using System.Windows;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private AutomationPanel? _automationPanel;
    private PreferenceFile<AutomationPreferences>? _automationStore;
    private PreferenceFile<AutomationPreferences> AutomationStore => _automationStore ??= new(_paths, "automation.json", () => AutomationPreferences.Empty, AutomationPreferences.Validate);
    private readonly DispatcherTimer _automationTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly WindowActivityPulse _activityPulse = new();
    private CancellationTokenSource? _automationCancellation;
    private Task _automationWork = Task.CompletedTask;
    private bool _automationTickBusy, _automationTransaction, _automationDispatch, _autoOwnsBatch, _rejoinInProgress;
    private async Task InitializeAutomationAsync()
    {
        try { await AutomationStore.LoadAsync(); }
        catch (Exception) { return; }
        _automationPanel = new AutomationPanel(AutomationStore, () => _workspaceLibrary.Presets, RunRecipeAsync, StopAutomation);
        AutomationSettingsHost.Content = _automationPanel;
        _automationTimer.Tick += async (_, _) => await TickAutomationsAsync();
        _automationTimer.Start();
    }
    private bool AutomationIdle => !_vaultLocked && _expansionPanel?.IsBusy != true && _loaded && !_shutdownStarted && !_handoffPrepared && !_storageMaintenanceRequested
        && !_applyingPreset && _activeLaunches == 0 && _launchBatchCancellation is null && _launchingProfiles.Count == 0
        && !_reconnectCoordinator.HasWork && _reconnectPump.IsCompleted && _reconnectInspections == 0
        && DrawerHost.Visibility != Visibility.Visible && !OwnedWindows.Cast<Window>().Any(w => w.IsVisible);
    private async Task TickAutomationsAsync()
    {
        if (_automationTickBusy || _automationCancellation is not null || !AutomationIdle) return;
        _automationTickBusy = true;
        try
        {
            var data = await AutomationStore.LoadAsync(); var now = DateTime.UtcNow;
            var due = AutomationPreferences.NextDue(data, now); if (due is null) return;
            await AutomationStore.UpdateAsync(current => AutomationPreferences.Claim(current, due.Id, now));
            // Do not replay missed rejoin/close operations after a shutdown or clock jump.
            if (now - due.NextRunUtc > TimeSpan.FromMinutes(5)) return;
            var recipe = new AutomationRecipe(1, Guid.NewGuid(), due.Name, due.AllowRejoin, [new(due.Action, due.PresetId)]);
            await RunRecipeAsync(recipe);
        }
        catch (Exception) { /* A failed schedule does not retry destructive actions or spam a modal dialog. */ }
        finally { _automationTickBusy = false; }
    }
    private Task RunRecipeAsync(AutomationRecipe recipe)
    {
        if (_automationCancellation is not null || !AutomationIdle) throw new InvalidOperationException(L.T("Aguarde as operações atuais antes de executar uma automação."));
        RecipeRunner.Validate(recipe);
        _automationCancellation = new CancellationTokenSource();
        _automationWork = ExecuteRecipeAsync(recipe, _automationCancellation);
        return _automationWork;
    }
    private async Task ExecuteRecipeAsync(AutomationRecipe recipe, CancellationTokenSource cancellation)
    {
        try { await RecipeRunner.RunAsync(recipe, DispatchAutomationStepAsync, cancellation.Token); }
        finally { _automationCancellation = null; cancellation.Dispose(); }
    }
    private void StopAutomation()
    {
        _automationCancellation?.Cancel();
        if (_autoOwnsBatch && !_rejoinInProgress) _launchBatchCancellation?.Cancel();
    }
    private async Task DispatchAutomationStepAsync(AutomationStep step, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!AutomationIdle) throw new InvalidOperationException();
        if (step.Action == "arrange") { ArrangeWindows(logResult: false); return; }
        var preset = _workspaceLibrary.Presets.FirstOrDefault(p => p.Id == step.PresetId);
        if (step.Action is "launch" or "rejoin" or "pulse" && preset is null) throw new InvalidOperationException();
        var ids = preset?.Accounts.ToHashSet() ?? _sequenceIds.ToHashSet();
        var owned = Instances.Where(i => _launchOwnership.TryGetValue(i.Key, out var owner) && ids.Contains(owner.ProfileId)).ToArray();
        if (step.Action == "pulse")
        { foreach (var session in owned) _activityPulse.Send(session); return; }
        if (step.Action == "trim")
        { await _tuningWork; await Task.Run(() => { foreach (var session in owned) _tuning.TrimMemory(session); }, token); return; }
        if (preset is null || preset.Accounts.Any(id => Profiles.All(p => p.Id != id || !p.IsSignedIn))) throw new InvalidOperationException();
        if (!await LoadLaunchPresetAsync(preset, showErrors: false)) throw new InvalidOperationException();
        _automationTransaction = true;
        try
        {
            Exception? closeFailure = null;
            var closedIds = new HashSet<Guid>();
            if (step.Action == "rejoin")
            {
                _rejoinInProgress = true;
                foreach (var session in owned)
                {
                    _intentionalClosures.Add(session.Key);
                    try
                    {
                        var owner = _launchOwnership[session.Key].ProfileId;
                        var result = await Task.Run(() => _layout.ForceTerminate(session));
                        if (!result.Succeeded) throw new InvalidOperationException();
                        closedIds.Add(owner);
                    }
                    catch (Exception ex) { _intentionalClosures.Remove(session.Key); closeFailure = ex; break; }
                }
                try { await _processMonitor.RefreshNowAsync(); } catch (Exception ex) { closeFailure ??= ex; }
            }
            else token.ThrowIfCancellationRequested();
            // Complete an in-flight rejoin before honoring Stop; recover only closed accounts after a partial failure.
            if (_shutdownStarted) return;
            _automationDispatch = _autoOwnsBatch = true;
            RobloxLaunchTarget? target = preset.Destination == "https://www.roblox.com/home" ? null : RobloxLinkParser.Parse(preset.Destination);
            var reopen = closeFailure is null ? preset.Accounts : preset.Accounts.Where(closedIds.Contains).ToArray();
            var profiles = reopen.Select(id => Profiles.Single(p => p.Id == id)).ToArray();
            if (profiles.Length > 0) await RunLaunchSequenceAsync(new(profiles, target));
            if (closeFailure is not null) throw new InvalidOperationException(L.T("Algumas sessões não puderam ser fechadas. As contas já fechadas foram enviadas para reabertura."), closeFailure);
        }
        finally { _automationTransaction = _automationDispatch = _autoOwnsBatch = _rejoinInProgress = false; }
    }
}
