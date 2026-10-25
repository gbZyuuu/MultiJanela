using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private readonly RobloxSessionLogReader _sessionLogs = new();
    private readonly CancellationTokenSource _reconnectStop = new();
    private readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ReconnectCoordinator _reconnectCoordinator = null!;
    private Task _reconnectPump = Task.CompletedTask;
    private int _reconnectInspections;
    private void InitializeReconnect()
    {
        _reconnectCoordinator = new ReconnectCoordinator(
            request => !Profiles.Any(p => p.Id == request.ProfileId)
                || Instances.Any(i => i.Key != request.SourceProcessKey && CpuProfile(i)?.Id == request.ProfileId),
            request => !_vaultLocked && !_storageMaintenanceRequested && !_shutdownStarted && !_handoffPrepared && _settings.AutoReconnect
                && !_launchingProfiles.Contains(request.ProfileId) && _launchingProfiles.Count < MaxConcurrentLaunches,
            LaunchReconnectAsync);
        _reconnectTimer.Tick += (_, _) =>
        {
            if (_reconnectPump.IsCompleted) _reconnectPump = PumpReconnectAsync();
        };
    }
    private async Task PumpReconnectAsync()
    {
        try
        {
            if (!_settings.AutoReconnect || _shutdownStarted) _reconnectCoordinator.CancelAll();
            else await _reconnectCoordinator.TickAsync(_reconnectStop.Token);
        }
        catch (Exception ex) { AppendLog(L.F("Reconexão interrompida: {0}.", ex.GetType().Name)); }
        finally { if (!_reconnectCoordinator.HasWork) _reconnectTimer.Stop(); }
    }
    private void EvaluateReconnect(RobloxInstanceInfo stale, ProcessOwnershipEntry? owner)
    {
        var exitCode = TryReadRetainedExitCode(stale.Key);
        if (!_settings.AutoReconnect || _shutdownStarted || owner is null || _intentionalClosures.Contains(stale.Key)) return;
        var profile = Profiles.FirstOrDefault(p => p.Id == owner.ProfileId);
        if (profile is null) return;
        _reconnectInspections++;
        _ = InspectReconnectAsync(stale, owner, profile.RobloxUserId, exitCode);
    }
    private async Task InspectReconnectAsync(RobloxInstanceInfo stale, ProcessOwnershipEntry owner, long? userId, int? exitCode)
    {
        try
        {
            var evidence = await Task.Run(() => _sessionLogs.Read(stale.StartTimeUtc, userId));
            if (_shutdownStarted || !_settings.AutoReconnect) return;
            if (!ShouldReconnect(true, true, false, exitCode, evidence?.FailureKind, 0, 1)) return;
            var target = ReconnectTargetPolicy.Resolve(owner.LaunchTargetUrl, evidence);
            if (owner.LaunchTargetUrl is not null && target is null)
            { AppendLog(L.T("Reconexão pausada: o destino salvo não é válido.")); return; }
            var request = new ReconnectRequest(owner.ProfileId, stale.Key, target);
            if (_reconnectCoordinator.Enqueue(request, Math.Clamp(_settings.AutoReconnectMaxAttempts, 1, 10),
                DateTime.UtcNow - stale.StartTimeUtc >= TimeSpan.FromMinutes(1)))
            {
                AppendLog(L.T("Reconexão agendada para o último destino disponível."));
                _reconnectTimer.Start();
            }
        }
        catch (Exception ex) { AppendLog(L.F("Não foi possível preparar a reconexão: {0}.", ex.GetType().Name)); }
        finally { _reconnectInspections--; }
    }
    private async Task<bool> LaunchReconnectAsync(ReconnectRequest request, CancellationToken token)
    {
        var profile = Profiles.FirstOrDefault(p => p.Id == request.ProfileId);
        if (profile is null || !_settings.AutoReconnect || !TryBeginLaunch(profile, out _)) return false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!await ActivateGuardAsync(interactive: false)) return false;
            token.ThrowIfCancellationRequested();
            var target = _reconnectCoordinator.Attempts(profile.Id) > 1 ? ReconnectTargetPolicy.WithoutServer(request.Target) : request.Target;
            var receipt = await LaunchProfileAsync(profile, cancellationToken: token, launchTarget: target);
            if (receipt is null) return false;
            return await WaitForLaunchedInstanceAsync(receipt, profile, token, showFailure: false);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { AppendLog(L.F("Falha na tentativa de reconexão: {0}.", ex.GetType().Name)); return false; }
        finally { EndLaunch(profile); }
    }
}
