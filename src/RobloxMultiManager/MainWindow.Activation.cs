using System.Windows;
using System.Windows.Input;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private bool _handoffPrepared;
    private readonly TaskCompletionSource _startupCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task StartupCompletion => _startupCompletion.Task;
    private ManagerWorkspaceStore WorkspaceStore => new(_paths.Root);

    internal void ActivateExistingWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal async Task<ManagerOpenReply> PrepareManagerOpenAsync(ManagerOpenRequest request)
    {
        if (!ManagerInstanceCoordinator.IsDifferentVersion(request.EffectiveVersion, typeof(App).Assembly.GetName().Version!.ToString()))
        {
            ActivateExistingWindow();
            return new("activated");
        }
        if(new AccountVault(_paths.Root).Exists && Version.TryParse(request.Version,out var incoming) && incoming<new Version(2,9,0,0))return new("vault-incompatible");
        // Never abandon a queue, browser, editor, download or reconnect in flight.
        if (HandoffWorkPending())
            return new("busy");
        _handoffPrepared = true;
        IsEnabled = false;
        try
        {
            await HomeCpuSlider.FlushAsync(finishInteraction: true); await HomeGraphics.FlushAsync();
            await _cpuWork;
            await _tuningWork;
            await _preferencesWrite;
            await _historyWrite;
            await SaveProfilesAsync(throwOnError: true);
            await SaveSettingsAsync(throwOnError: true);
            await SaveOwnershipAsync(throwOnError: true);
            await SaveWorkspaceAsync();
            return new("ready");
        }
        catch (Exception)
        {
            AbortManagerHandoff();
            return new("save-failed");
        }
    }

    private bool HandoffWorkPending(bool ignoreUpdate = false) => _vaultLocked || GlobalCpuEditor.IsBusy || DrawerBody.Content is CpuLimitEditor { IsBusy: true } || _expansionPanel?.IsBusy == true || HomeGraphics.IsBusy || !ignoreUpdate && _updatesPanel?.IsBusy == true || _apiApplying || _localApiPanel?.IsBusy == true || _automationCancellation is not null || _automationTickBusy || _automationPanel?.IsBusy == true || _notificationsPanel?.IsBusy == true || _tuningPanel?.IsBusy == true || _graphicsPanel?.IsBusy == true || _storageMaintenanceRequested || _backupPanel?.IsBusy == true || _appearancePanel?.IsBusy == true || _workspacesPanel?.IsBusy == true || _applyingPreset || _accountPortal?.BlocksHandoff == true || _gamesHub?.IsBusy == true || !_loaded || _shutdownStarted || _handoffPrepared || _launchBatchCancellation is not null
            || _activeLaunches > 0 || _launchingProfiles.Count > 0 || _reconnectCoordinator.HasWork
            || !_reconnectPump.IsCompleted || _reconnectInspections > 0 || _nativeSessionRecoveryInProgress
            || DrawerHost.Visibility == Visibility.Visible
            || OwnedWindows.Cast<Window>().Any(w => w.IsVisible) || IsKeyboardFocusWithin && Mouse.Captured is not null;

    internal void AbortManagerHandoff() { _handoffPrepared = false; IsEnabled = true; }
    internal Task CommitManagerHandoffAsync() => ExitApplicationAsync(handoff: true);

    private Task SaveWorkspaceAsync() => WorkspaceStore.SaveAsync(new(
        _sequenceIds.ToArray(), LaunchLinkInput.Text, (SavedDestinationCombo.SelectedItem as SavedLaunchDestination)?.Id,
        (LaunchLinkProfileCombo.SelectedItem as AccountProfile)?.Id, AccountSearchInput.Text, _workspacePage.ToString(),
        WindowState == WindowState.Normal ? Width : RestoreBounds.Width,
        WindowState == WindowState.Normal ? Height : RestoreBounds.Height, WindowState == WindowState.Maximized,
        InstancesGrid.SelectedItems.Cast<RobloxInstanceInfo>().Select(s => s.Key).ToArray(),
        _reconnectCoordinator.ExportBudgets(),
        FinitePosition(WindowState == WindowState.Normal ? Left : RestoreBounds.Left),
        FinitePosition(WindowState == WindowState.Normal ? Top : RestoreBounds.Top),
        (ProfilesGrid.SelectedItem as AccountProfile)?.Id, CaptureFormDrafts()));

    private Dictionary<string, Dictionary<string, string>> _formDrafts = [];
    private Dictionary<string, Dictionary<string, string>> CaptureFormDrafts()
    {
        if (_accountPortal is not null) _formDrafts["portal"] = _accountPortal.CaptureDraft();
        if (_gamesHub is not null) _formDrafts["games"] = _gamesHub.CaptureDraft();
        if (_expansionPanel is not null) _formDrafts["tools"] = _expansionPanel.CaptureDraft();
        _formDrafts["shell"] = new() { ["group"] = _accountGroupId?.ToString() ?? "" };
        if (_graphicsPanel is not null) _formDrafts["graphics"] = _graphicsPanel.CaptureDraft();
        if (_appearancePanel is not null) _formDrafts["appearance"] = _appearancePanel.CaptureDraft();
        if (_automationPanel is not null) _formDrafts["automation"] = _automationPanel.CaptureDraft();
        if (_workspacesPanel is not null) _formDrafts["workspaces"] = _workspacesPanel.CaptureDraft();
        return _formDrafts;
    }
    private static double? FinitePosition(double value) => double.IsFinite(value) ? value : null;

    private async Task RestoreWorkspaceAsync()
    {
        var state = await WorkspaceStore.LoadAsync();
        if (state is null) return;
        _formDrafts = state.Drafts ?? [];
        if (Guid.TryParse(_formDrafts.GetValueOrDefault("shell")?.GetValueOrDefault("group"), out var group)) { _accountGroupId = group; RefreshAccountGroups(); }
        _expansionPanel?.RestoreDraft(_formDrafts.GetValueOrDefault("tools"));
        _graphicsPanel?.RestoreDraft(_formDrafts.GetValueOrDefault("graphics"));
        _appearancePanel?.RestoreDraft(_formDrafts.GetValueOrDefault("appearance"));
        _automationPanel?.RestoreDraft(_formDrafts.GetValueOrDefault("automation"));
        _sequenceIds.Clear();
        _sequenceIds.AddRange((state.Sequence ?? []).Distinct().Where(id => Profiles.Any(p => p.Id == id)));
        UpdateQueuePreview();
        SavedDestinationCombo.SelectedItem = SavedDestinations.FirstOrDefault(d => d.Id == state.SavedDestination);
        LaunchLinkInput.Text = state.Destination ?? "";
        LaunchLinkProfileCombo.SelectedItem = Profiles.FirstOrDefault(p => p.Id == state.SelectedAccount)
            ?? Profiles.FirstOrDefault(p => p.IsSignedIn);
        AccountSearchInput.Text = state.Search ?? "";
        if (Enum.TryParse<WorkspacePage>(state.Page, out var page) && Enum.IsDefined(page)) ShowWorkspacePage(page);
        var area = SystemParameters.WorkArea;
        if (double.IsFinite(state.Width)) Width = Math.Clamp(state.Width, MinWidth, Math.Max(MinWidth, area.Width));
        if (double.IsFinite(state.Height)) Height = Math.Clamp(state.Height, MinHeight, Math.Max(MinHeight, area.Height));
        if (state.Left is double left && double.IsFinite(left))
            Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft,
                Math.Max(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        if (state.Top is double top && double.IsFinite(top))
            Top = Math.Clamp(top, SystemParameters.VirtualScreenTop,
                Math.Max(SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
        ProfilesGrid.SelectedItem = Profiles.FirstOrDefault(p => p.Id == state.LibrarySelection) ?? ProfilesGrid.SelectedItem;
        if (state.Maximized) WindowState = WindowState.Maximized;
        var selected = (state.SelectedSessions ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var instance in Instances.Where(i => selected.Contains(i.Key))) InstancesGrid.SelectedItems.Add(instance);
        _reconnectCoordinator.ImportBudgets(state.ReconnectBudgets ?? []);
    }
}
