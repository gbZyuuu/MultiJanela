using System.Windows;
using System.Windows.Input;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private bool _navigationExpanded;
    private double _navigationRestWidth = 164;
    private void Shell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ShellNavigationColumn is null || TopNavBar is null) return;
        _navigationRestWidth = e.NewSize.Width < 1080 ? 64 : 164;
        ShellNavigationColumn.Width = new GridLength(_navigationRestWidth);
        UpdateNavigationWidth();
    }
    private void UpdateNavigationWidth()
    {
        var width = _navigationExpanded ? Math.Max(220, 220 * AppearanceService.Current.TextScale / 100d) : _navigationRestWidth;
        if (TopNavBar.Width != width) TopNavBar.Width = width;
        var labels = _navigationExpanded || _navigationRestWidth > 64 ? Visibility.Visible : Visibility.Collapsed;
        if (!Equals(Resources["SidebarLabelVisibility"], labels)) Resources["SidebarLabelVisibility"] = labels;
    }
    private void Navigation_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_navigationExpanded) return;
        _navigationExpanded = true; UpdateNavigationWidth();
        _accountPortal?.SetCovered(true);
        UiMotion.Animate(TopNavBar, OpacityProperty, .92, 1, 100, UiMotion.Enabled);
    }
    private void CollapseNavigation()
    {
        if (!_navigationExpanded) return;
        _navigationExpanded = false; UpdateNavigationWidth();
        _accountPortal?.SetCovered(DrawerHost.Visibility == Visibility.Visible || CommandPaletteHost.Visibility == Visibility.Visible);
    }
    private void Navigation_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => CollapseNavigation();
    private void Navigation_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) { if (!TopNavBar.IsMouseOver) CollapseNavigation(); }

    private void InitializeNavigation()
    {
        Deactivated += (_, _) => CollapseNavigation();
        PreviewMouseMove += (_, _) => { if (_navigationExpanded && !TopNavBar.IsMouseOver) CollapseNavigation(); };
        PreviewMouseDown += (_, _) => { if (!TopNavBar.IsMouseOver) CollapseNavigation(); };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            WorkspacePage? page = e.Key switch { Key.D1 => WorkspacePage.Accounts, Key.D2 => WorkspacePage.Launch, Key.D3 => WorkspacePage.Games,
                Key.D4 => WorkspacePage.AccountTools, Key.D5 => WorkspacePage.Workspaces, Key.OemComma => WorkspacePage.Settings, _ => null };
            if (page is null) return;
            if (page == WorkspacePage.AccountTools) AccountTools_Click(this, new RoutedEventArgs()); else ShowWorkspacePage(page.Value);
            e.Handled = true;
        };
        DrawerHost.IsVisibleChanged += (_, _) => _accountPortal?.SetCovered(DrawerHost.Visibility == Visibility.Visible);
        CommandPaletteHost.IsVisibleChanged += (_, _) => _accountPortal?.SetCovered(CommandPaletteHost.Visibility == Visibility.Visible || DrawerHost.Visibility == Visibility.Visible);
        RefreshTopNavigation();
    }
    private void RefreshTopNavigation()
    {
        foreach (var (button, page) in new[] { (AccountsNavButton, WorkspacePage.Accounts), (LaunchNavButton, WorkspacePage.Launch),
            (GamesNavButton, WorkspacePage.Games), (AccountToolsNavButton, WorkspacePage.AccountTools), (WorkspacesNavButton, WorkspacePage.Workspaces), (SettingsButton, WorkspacePage.Settings), (ToolsNavButton, WorkspacePage.Tools) })
            NavigationState.SetActive(button, _workspacePage == page);
    }
}
