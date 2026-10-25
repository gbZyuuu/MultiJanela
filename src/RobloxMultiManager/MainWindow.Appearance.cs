using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private AppearancePanel? _appearancePanel;
    private async Task InitializeAppearanceAsync()
    {
        var store = new AppearancePreferencesStore(_paths);
        try { AppearanceService.Apply(await store.LoadAsync()); }
        catch (Exception) { AppearanceService.Apply(new()); }
        _appearancePanel = new AppearancePanel(store); AppearanceSettingsHost.Content = _appearancePanel;
        AppearanceService.Changed += AppearanceChanged;
        Closed += (_, _) => AppearanceService.Changed -= AppearanceChanged;
        ApplyDisplayPrivacy();
    }
    private void AppearanceChanged(object? sender, EventArgs e) => ApplyDisplayPrivacy();
    private void ApplyDisplayPrivacy()
    {
        foreach (var row in WorkspaceAccounts.Concat(SavedAccounts)) row.RefreshLanguage();
        LaunchLinkProfileCombo.Items.Refresh(); AccountProfilesView.Refresh(); UpdateQueuePreview();
        var hidden = DisplayPrivacy.Enabled ? Visibility.Hidden : Visibility.Visible;
        LaunchLinkInput.Visibility = hidden; SavedDestinationCombo.Visibility = hidden; LogText.Visibility = hidden;
        if (DisplayPrivacy.Enabled && _workspacePage == WorkspacePage.AccountTools) ShowWorkspacePage(WorkspacePage.Accounts);
        _gamesHub?.RefreshPrivacy();
        _historyPanel?.RefreshPrivacy();
        _workspacesPanel?.RefreshPrivacy();
        _graphicsPanel?.RefreshPrivacy();
    }
}
