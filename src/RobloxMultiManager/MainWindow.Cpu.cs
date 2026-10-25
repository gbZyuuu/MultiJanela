using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private readonly CpuLimitService _cpuLimits = new();
    private readonly Dictionary<string, CpuLimitResult> _cpuResults = [];
    private Task _cpuWork = Task.CompletedTask;
    private bool _cpuRefreshRequested;
    private bool _cpuRetryRequested;
    private bool _cpuUpdateFailed;

    private void ConfigureGlobalCpuEditor()
    {
        GlobalCpuEditor.Configure(_settings.GlobalCpuLimitPercent, null, allowInheritance: false);
        GlobalCpuEditor.SaveRequested = SaveGlobalCpuLimitAsync;
        HomeCpuSlider.SaveRequested = SaveGlobalCpuLimitAsync;
        HomeCpuSlider.Synchronize(_settings.GlobalCpuLimitPercent);
    }

    private async Task SaveGlobalCpuLimitAsync(int? percent)
    {
        if (percent is { } value && !CpuLimitPolicy.IsValid(value)) throw new ArgumentOutOfRangeException(nameof(percent));
        var previous = _settings.GlobalCpuLimitPercent;
        _settings = _settings with { GlobalCpuLimitPercent = percent ?? 100 };
        try { await SaveSettingsAsync(throwOnError: true); }
        catch { _settings = _settings with { GlobalCpuLimitPercent = previous }; QueueCpuReconcile(); throw; }
        ConfigureGlobalCpuEditor();
        RefreshCpuRows();
        QueueCpuReconcile(retryFailures: true);
    }

    private void AccountCpu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceAccountRow { Profile: { } profile } row }) return;
        if (DrawerHost.Visibility == Visibility.Visible) return;
        var editor = new CpuLimitEditor();
        editor.Configure(_settings.GlobalCpuLimitPercent, profile.CpuLimitPercent, allowInheritance: true);
        editor.SetSessionState(Instances.Any(session => CpuProfile(session)?.Id == profile.Id));
        editor.SaveRequested = value => SaveAccountCpuLimitAsync(profile.Id, value);
        editor.Completed += (_, _) => { if (ReferenceEquals(DrawerBody.Content, editor)) CloseDrawer(); };
        OpenDrawer(L.T("Limite de CPU"), profile.PublicName, editor);
    }

    private async Task SaveAccountCpuLimitAsync(Guid profileId, int? percent)
    {
        if (percent is { } value && !CpuLimitPolicy.IsValid(value)) throw new ArgumentOutOfRangeException(nameof(percent));
        var index = Profiles.ToList().FindIndex(profile => profile.Id == profileId);
        if (index < 0) throw new InvalidOperationException("profile-not-found");
        var previous = Profiles[index].CpuLimitPercent;
        var selected = (ProfilesGrid.SelectedItem as AccountProfile)?.Id;
        var quick = (LaunchLinkProfileCombo.SelectedItem as AccountProfile)?.Id;
        Profiles[index] = Profiles[index] with { CpuLimitPercent = percent };
        try { await SaveProfilesAsync(throwOnError: true); }
        catch
        {
            index = Profiles.ToList().FindIndex(profile => profile.Id == profileId);
            if (index >= 0) Profiles[index] = Profiles[index] with { CpuLimitPercent = previous };
            RefreshCpuRows(); QueueCpuReconcile(); throw;
        }
        ProfilesGrid.SelectedItem = Profiles.FirstOrDefault(profile => profile.Id == selected);
        LaunchLinkProfileCombo.SelectedItem = Profiles.FirstOrDefault(profile => profile.Id == quick);
        UpdateQueuePreview();
        RefreshWorkspaceAccounts();
        QueueCpuReconcile(retryFailures: true);
    }

    private AccountProfile? CpuProfile(RobloxInstanceInfo instance)
    {
        if (_launchOwnership.TryGetValue(instance.Key, out var owner))
            return Profiles.FirstOrDefault(profile => profile.Id == owner.ProfileId);
        var matches = instance.AccountUserId is > 0
            ? Profiles.Where(profile => profile.RobloxUserId == instance.AccountUserId).Take(2).ToArray()
            : Profiles.Where(profile => instance.ProfileName is not null && profile.Name == instance.ProfileName).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void RefreshCpuRows()
    {
        foreach (var row in WorkspaceAccounts.Concat(SavedAccounts))
        {
            var profile = row.Session is { } session ? CpuProfile(session) : row.Profile;
            var percent = CpuLimitPolicy.Effective(_settings.GlobalCpuLimitPercent, profile?.CpuLimitPercent);
            var result = row.Session is { } active && _cpuResults.TryGetValue(active.Key, out var status)
                && status.RequestedPercent == percent ? status : null;
            row.UpdateCpu(percent, profile?.CpuLimitPercent is not { } own || !CpuLimitPolicy.IsValid(own), result);
        }
    }

    private void QueueCpuReconcile(bool retryFailures = false)
    {
        RefreshCpuRows();
        if (!_loaded || _shutdownStarted) return;
        _cpuRefreshRequested = true;
        _cpuRetryRequested |= retryFailures;
        if (_cpuWork.IsCompleted) _cpuWork = RunCpuReconcileAsync();
    }

    private void RefreshCpuStatus()
    {
        if (_cpuUpdateFailed) { CpuLimitStatus.Visibility = Visibility.Visible; CpuLimitStatus.Text = L.T("Não foi possível atualizar os limites de CPU."); return; }
        var failed = _cpuResults.Values.Count(result => !result.Success);
        CpuLimitStatus.Visibility = failed == 0 ? Visibility.Collapsed : Visibility.Visible;
        CpuLimitStatus.Text = failed == 0
            ? L.F("CPU: {0} processo(s) verificado(s).", _cpuResults.Count)
            : L.F("CPU: o Windows recusou o limite em {0} processo(s). Confira o aviso na conta.", failed);
    }

    private async Task RunCpuReconcileAsync()
    {
        try
        {
            while (_cpuRefreshRequested && !_shutdownStarted)
            {
                _cpuRefreshRequested = false;
                var retry = _cpuRetryRequested; _cpuRetryRequested = false;
                var requests = Instances.Select(instance => new CpuLimitRequest(instance.ProcessId, instance.StartTimeUtc,
                    CpuLimitPolicy.Effective(_settings.GlobalCpuLimitPercent, CpuProfile(instance)?.CpuLimitPercent))).ToArray();
                var results = await Task.Run(() => _cpuLimits.Reconcile(requests, retry));
                if (_shutdownStarted) return;
                _cpuResults.Clear();
                foreach (var result in results) _cpuResults[result.Key] = result;
                RefreshCpuRows();
                _cpuUpdateFailed = false;
                RefreshCpuStatus();
            }
        }
        catch (Exception ex)
        {
            _cpuUpdateFailed = true;
            RefreshCpuStatus();
            AppendLog(L.F("Limite de CPU: falha ({0}).", ex.GetType().Name));
        }
    }
}
