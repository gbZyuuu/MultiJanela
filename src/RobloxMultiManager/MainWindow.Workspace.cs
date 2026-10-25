using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private DispatcherTimer? _accountSearchTimer;
    private string _accountQuery = string.Empty;
    private bool _onlyReadyAccounts;

    // Independent view: searching must never filter the quick-launch account selector.
    public ListCollectionView AccountProfilesView { get; }

    private ListCollectionView CreateAccountView() => new(Profiles)
    {
        Filter = item => item is AccountProfile profile
            && InSelectedGroup(profile)
            && (!_onlyReadyAccounts || profile.IsSignedIn)
            && AccountSearch.Matches(profile, _metadataByProfile.GetValueOrDefault(profile.Id), _workspaceLibrary.Groups, _accountQuery)
    };

    private void InitializeWorkspace()
    {
        _accountSearchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _accountSearchTimer.Tick += (_, _) =>
        {
            _accountSearchTimer.Stop();
            ApplyAccountFilter();
        };
        ((System.Collections.Specialized.INotifyCollectionChanged)AccountProfilesView).CollectionChanged += (_, _) => UpdateAccountResults();
        UpdateAccountResults();
        Closed += (_, _) => _accountSearchTimer.Stop();
        InitializeOrbit();
        InitializeUnifiedWorkspace();
    }

    private void AccountSearchInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_accountSearchTimer is null) return;
        _accountSearchTimer.Stop();
        _accountSearchTimer.Start();
    }

    private void AccountReadyFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_accountSearchTimer is null) return;
        _accountSearchTimer.Stop();
        ApplyAccountFilter();
    }

    private void ApplyAccountFilter()
    {
        _accountQuery = AccountSearchInput.Text.Trim();
        _onlyReadyAccounts = AccountReadyFilter.IsChecked == true;
        AccountProfilesView.Refresh();
        UpdateAccountResults();
    }

    private void UpdateAccountResults()
    {
        if (AccountResultsText is null) return;
        var visible = AccountProfilesView.Count;
        AccountResultsText.Text = visible == Profiles.Count
            ? RobloxMultiManager.Core.L.F("{0} conta{1} · Ctrl / Shift para selecionar",visible,(visible == 1 ? string.Empty : "s"))
            : RobloxMultiManager.Core.L.F("{0} de {1} contas · busca ativa",visible,Profiles.Count);
        AccountSearchEmptyState.Visibility = visible == 0 && Profiles.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WorkspaceClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryPage();
        AccountSearchInput.Clear();
        AccountReadyFilter.IsChecked = false;
        _accountSearchTimer?.Stop();
        ApplyAccountFilter();
        AccountSearchInput.Focus();
    }

    private void AccountSearchInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        WorkspaceClearSearch_Click(sender, e);
        e.Handled = true;
    }

    private void WorkspaceFind_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        ShowLibraryPage();
        AccountSearchInput.Focus();
        AccountSearchInput.SelectAll();
        e.Handled = true;
    }

    private void WorkspaceOverview_Click(object sender, RoutedEventArgs e)
    {
        SetComfortPage(false);
        ActivityExpander.IsExpanded = false;
        InstancesGrid.Focus();
    }

    private void WorkspaceAccounts_Click(object sender, RoutedEventArgs e)
    {
        ShowLibraryPage();
        AccountSearchInput.Focus();
        AccountSearchInput.SelectAll();
    }

    private void WorkspaceInstances_Click(object sender, RoutedEventArgs e)
    {
        SetComfortPage(false);
        InstancesGrid.Focus();
    }

    private void WorkspaceLaunch_Click(object sender, RoutedEventArgs e)
    {
        ShowComfortLaunchPanel();
    }

    private void WorkspaceActivity_Click(object sender, RoutedEventArgs e) =>
        ActivityExpander.IsExpanded = !ActivityExpander.IsExpanded;
}
