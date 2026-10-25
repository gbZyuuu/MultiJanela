using System.Windows;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private enum WorkspacePage { Accounts, Launch, Settings, Games, Workspaces, AccountTools, History, Tools }
    private WorkspacePage _workspacePage;
    private bool _launchPageVisible => _workspacePage == WorkspacePage.Launch;
    private void ComfortAccounts_Click(object sender, RoutedEventArgs e) => SetComfortPage(false);
    private void ComfortLibrary_Click(object sender, RoutedEventArgs e) => ShowLibraryPage();
    private void ComfortLaunch_Click(object sender, RoutedEventArgs e) => ShowComfortLaunchPanel();
    private void SetComfortPage(bool launch) => ShowWorkspacePage(launch ? WorkspacePage.Launch : WorkspacePage.Accounts);
    private void ShowLibraryPage() => SetComfortPage(false);
    private void ShowWorkspacePage(WorkspacePage page)
    {
        if (page != WorkspacePage.AccountTools && _accountPortal is not null)
        { _accountPortal.Dispose(); _accountPortal = null; AccountToolsPageHost.Content = null; }
        if (page == WorkspacePage.AccountTools) EnsureAccountPortal();
        if (page == WorkspacePage.Workspaces) EnsureWorkspacesPanel();
        if (page == WorkspacePage.Tools) EnsureExpansionPanel();
        ToolsPageHost.Visibility = page == WorkspacePage.Tools ? Visibility.Visible : Visibility.Collapsed;
        var changed = _workspacePage != page;
        _workspacePage = page;
        NexusBody.Visibility = page == WorkspacePage.Accounts ? Visibility.Visible : Visibility.Collapsed;
        LaunchPageHost.Visibility = page == WorkspacePage.Launch ? Visibility.Visible : Visibility.Collapsed;
        HistoryPageHost.Visibility = page == WorkspacePage.History ? Visibility.Visible : Visibility.Collapsed;
        if (page == WorkspacePage.History) EnsureHistoryPanel();
        WorkspacesPageHost.Visibility = page == WorkspacePage.Workspaces ? Visibility.Visible : Visibility.Collapsed;
        AccountToolsPageHost.Visibility = page == WorkspacePage.AccountTools ? Visibility.Visible : Visibility.Collapsed;
        GamesPageHost.Visibility = page == WorkspacePage.Games ? Visibility.Visible : Visibility.Collapsed;
        if (page == WorkspacePage.Games) EnsureGamesHub();
        SettingsPageHost.Visibility = page == WorkspacePage.Settings ? Visibility.Visible : Visibility.Collapsed;
        RefreshTopNavigation();
        if (changed) UiMotion.Reveal(page switch
        {
            WorkspacePage.Launch => (FrameworkElement)LaunchPageHost,
            WorkspacePage.Settings => SettingsPageHost,
            WorkspacePage.Games => GamesPageHost,
            WorkspacePage.Workspaces => WorkspacesPageHost,
            WorkspacePage.AccountTools => AccountToolsPageHost,
            WorkspacePage.History => HistoryPageHost,
            WorkspacePage.Tools => ToolsPageHost,
            _ => NexusBody
        });
    }
    private void ShowComfortLaunchPanel(bool replace = false)
    {
        if (!replace && DrawerHost.Visibility == Visibility.Visible) return;
        if (replace && DrawerHost.Visibility == Visibility.Visible) CloseDrawer();
        SetComfortPage(true);
    }
    private void ComfortSessionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        { menu.PlacementTarget = button; menu.IsOpen = true; }
    }
}
