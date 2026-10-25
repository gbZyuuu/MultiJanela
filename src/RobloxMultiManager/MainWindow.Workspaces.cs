using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private WorkspaceLibrary _workspaceLibrary = WorkspaceLibrary.Empty;
    private WorkspacesPanel? _workspacesPanel;
    private AccountPortal? _accountPortal;
    private Guid? _accountGroupId;
    private Dictionary<Guid, AccountMetadata> _metadataByProfile = [];
    private bool _applyingPreset;
    private sealed record GroupChoice(Guid? Id, string Name);

    private async Task LoadWorkspaceLibraryAsync()
    {
        try { _workspaceLibrary = await new WorkspaceLibraryStore(_paths).LoadAsync(); }
        catch (Exception) { AppendLog(L.T("Não foi possível carregar os grupos salvos. O arquivo original foi preservado.")); }
        RefreshAccountGroups();
    }
    private void RefreshAccountGroups()
    {
        _metadataByProfile = _workspaceLibrary.Metadata.ToDictionary(m => m.ProfileId);
        var selected = _accountGroupId;
        AccountGroupFilter.ItemsSource = new[] { new GroupChoice(null, L.T("Todas as contas")) }
            .Concat(_workspaceLibrary.Groups.Select(g => new GroupChoice(g.Id, g.Name))).ToArray();
        AccountGroupFilter.SelectedItem = ((IEnumerable<GroupChoice>)AccountGroupFilter.ItemsSource).FirstOrDefault(c => c.Id == selected) ?? AccountGroupFilter.Items[0];
        AccountGroupFilter.Visibility = _workspaceLibrary.Groups.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountProfilesView.Refresh();
    }
    private void AccountGroup_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { _accountGroupId = (AccountGroupFilter.SelectedItem as GroupChoice)?.Id; AccountProfilesView?.Refresh(); }
    private bool InSelectedGroup(AccountProfile profile) => _accountGroupId is null
        || _workspaceLibrary.Groups.Any(g => g.Id == _accountGroupId && g.Accounts.Contains(profile.Id));
    private WorkspacesPanel EnsureWorkspacesPanel()
    {
        if (_workspacesPanel is null)
        {
            _workspacesPanel = new WorkspacesPanel(new WorkspaceLibraryStore(_paths), Profiles, CaptureLaunchPreset,
                ApplyLaunchPreset, () => _sequenceIds.ToArray(), ApplyAccountGroup,
                data => { _workspaceLibrary = data; RefreshAccountGroups(); });
            _workspacesPanel.RestoreDraft(_formDrafts.GetValueOrDefault("workspaces"));
            WorkspacesPageHost.Content = _workspacesPanel;
        }
        return _workspacesPanel;
    }
    private void Workspaces_Click(object sender, RoutedEventArgs e) { EnsureWorkspacesPanel(); ShowWorkspacePage(WorkspacePage.Workspaces); }
    private LaunchPreset CaptureLaunchPreset(string name, Guid id, Guid[] accounts)
    {
        if (accounts.Length == 0) throw new ArgumentException(L.T("Adicione contas à sequência antes de salvar um perfil."));
        return new(id, name, accounts.ToArray(), string.IsNullOrWhiteSpace(LaunchLinkInput.Text) ? "https://www.roblox.com/home" : LaunchLinkInput.Text.Trim(),
            _settings.LaunchDelaySeconds, _settings.GlobalCpuLimitPercent, _settings.SelectedMonitor, _settings.AutoArrange);
    }
    private void ApplyAccountGroup(Guid[] accounts)
    {
        if (_launchBatchCancellation is not null || _applyingPreset || _handoffPrepared) return;
        _sequenceIds.Clear(); _sequenceIds.AddRange(accounts.Where(id => Profiles.Any(p => p.Id == id)).Distinct());
        UpdateQueuePreview(); ShowComfortLaunchPanel(replace: true);
    }
    private async void ApplyLaunchPreset(LaunchPreset preset) => await LoadLaunchPresetAsync(preset);
    private async Task<bool> LoadLaunchPresetAsync(LaunchPreset preset, bool showErrors = true)
    {
        if (_launchBatchCancellation is not null || _activeLaunches > 0 || _applyingPreset || _handoffPrepared || _storageMaintenanceRequested || _automationTransaction) return false;
        if (preset.Accounts.Any(id => Profiles.All(p => p.Id != id)))
        { if (showErrors) AppDialog.Show(this, L.T("Este perfil contém contas que não estão mais salvas. Atualize o perfil antes de carregar."), L.T("Carregar perfil"), MessageBoxButton.OK, MessageBoxImage.Information); return false; }
        _applyingPreset = true; IsEnabled = false;
        var original = _settings;
        try
        {
            await HomeCpuSlider.FlushAsync(finishInteraction: true); await HomeGraphics.FlushAsync(); await _cpuWork;
            _settings = _settings with { LaunchDelaySeconds = preset.DelaySeconds, GlobalCpuLimitPercent = preset.GlobalCpuPercent,
                SelectedMonitor = preset.Monitor, AutoArrange = preset.AutoArrange };
            await SaveSettingsAsync(throwOnError: true);
            _sequenceIds.Clear(); _sequenceIds.AddRange(preset.Accounts);
            LaunchLinkInput.Text = preset.Destination == "https://www.roblox.com/home" ? "" : preset.Destination;
            ApplySettingsToUi(); UpdateQueuePreview(); ConfigureGlobalCpuEditor(); QueueCpuReconcile(retryFailures: true);
            ShowComfortLaunchPanel(replace: true);
            return true;
        }
        catch (Exception) { _settings = original; if (showErrors) AppDialog.Show(this, L.T("Não foi possível carregar o perfil. As preferências anteriores foram preservadas."), L.T("Carregar perfil"), MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
        finally { _applyingPreset = false; IsEnabled = true; }
    }
    private bool _portalCloseHookAttached;
    private AccountPortal EnsureAccountPortal()
    {
        if (_accountPortal is null)
        {
            _accountPortal = new AccountPortal(_paths, Profiles, (account, destination) =>
            {
                LaunchLinkProfileCombo.SelectedItem = Profiles.FirstOrDefault(p => p.Id == account.Id);
                UseLibraryDestination(destination, false);
            }, OpenAccountHttpAsync, PlayFriendServer);
            _accountPortal.RestoreDraft(_formDrafts.GetValueOrDefault("portal"));
            _accountPortal.AvatarChanged += id => _ = RefreshAccountPortraitAsync(id);
            if (!_portraitRefreshHookAttached) { Closed += (_, _) => { _portraitRefreshLifetime.Cancel(); _portraitRefreshLifetime.Dispose(); }; _portraitRefreshHookAttached = true; }
            AccountToolsPageHost.Content = _accountPortal;
            _accountPortal.SetCovered(DrawerHost.Visibility == Visibility.Visible);
            if (!_portalCloseHookAttached) { Closed += (_, _) => _accountPortal?.Dispose(); _portalCloseHookAttached = true; }
        }
        return _accountPortal;
    }
    private void PlayFriendServer(AccountProfile account, string destination)
    {
        if (_launchBatchCancellation is not null || _handoffPrepared || _shutdownStarted) return;
        var current = Profiles.FirstOrDefault(p => p.Id == account.Id);
        if (current is null) return;
        LaunchLinkProfileCombo.SelectedItem = current;
        LaunchLinkInput.Text = destination;
        LaunchLinkButton_Click(this, new RoutedEventArgs());
    }
    private void AccountTools_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPrivacy.Enabled) { AppDialog.Show(this, L.T("Desative o modo de gravação para abrir páginas que exibem dados da conta."), L.T("Modo de gravação"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        EnsureAccountPortal(); ShowWorkspacePage(WorkspacePage.AccountTools);
    }
}
