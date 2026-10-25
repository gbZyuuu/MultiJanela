using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RobloxMultiManager.Core;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace RobloxMultiManager;

public sealed record QueuePreviewEntry(int Number, AccountProfile Profile, bool CanMoveUp, bool CanMoveDown);
public sealed record WorkspaceCommand(string Label, string Detail, Action Execute);

public partial class MainWindow
{
    public ObservableCollection<QueuePreviewEntry> QueuePreview { get; } = [];
    private WorkspaceCommand[] _commandCatalog = [];
    private bool _orbitReady;
    private bool _queueRefreshPending;

    private void InitializeOrbit()
    {
        _orbitReady = true;
        Profiles.CollectionChanged += (_, _) => ScheduleQueuePreview();
        Instances.CollectionChanged += (_, _) => { ScheduleQueuePreview(); CanvasSelection_Changed(this, null!); };
        TargetInput.TextChanged += (_, _) => ScheduleQueuePreview();
        UpdateQueuePreview();
        CanvasSelection_Changed(this, null!);
    }

    private void ScheduleQueuePreview()
    {
        if (!_orbitReady || _queueRefreshPending) return;
        _queueRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _queueRefreshPending = false;
            UpdateQueuePreview();
        }));
    }

    private void OrbitAccountActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: AccountProfile profile, ContextMenu: { } menu } button) return;
        ProfilesGrid.SelectedItem = profile;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void OrbitPalette_Executed(object sender, ExecutedRoutedEventArgs e) { OpenCommandPalette(); e.Handled = true; }
    private void OrbitPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OpenCommandPalette()
    {
        if (DrawerHost.Visibility == Visibility.Visible) return;
        var items = new List<WorkspaceCommand>
        {
            new(RobloxMultiManager.Core.L.T("Buscar contas"), RobloxMultiManager.Core.L.T("Ir para seus perfis · Ctrl+F"), () => WorkspaceAccounts_Click(this, new RoutedEventArgs())),
            new(RobloxMultiManager.Core.L.T("Sessões abertas"), RobloxMultiManager.Core.L.T("Ver e gerenciar as sessões"), () => WorkspaceInstances_Click(this, new RoutedEventArgs())),
            new(RobloxMultiManager.Core.L.T("Organizar janelas"), RobloxMultiManager.Core.L.T("Distribuir no monitor escolhido"), () => ArrangeButton_Click(this, new RoutedEventArgs())),
            new(RobloxMultiManager.Core.L.T("Versões do Roblox"), RobloxMultiManager.Core.L.T("Escolher e gerenciar o cliente"), () => VersionsButton_Click(this, new RoutedEventArgs())),
            new(RobloxMultiManager.Core.L.T("Diagnóstico"), RobloxMultiManager.Core.L.T("Inspecionar o estado do aplicativo"), () => DiagnosticsButton_Click(this, new RoutedEventArgs())),
            new(RobloxMultiManager.Core.L.T("Mostrar atividade"), RobloxMultiManager.Core.L.T("Abrir o histórico desta sessão"), () => ActivityExpander.IsExpanded = true),
            new(RobloxMultiManager.Core.L.T("Preparar fila"), RobloxMultiManager.Core.L.T("Escolher contas e destino"), () => WorkspaceLaunch_Click(this, new RoutedEventArgs()))
        };
        items.AddRange(Profiles.Select(profile => new WorkspaceCommand(profile.PublicName, RobloxMultiManager.Core.L.F("Conta · {0}",profile.AccountDisplay), () => SelectCommandProfile(profile.Id))));
        _commandCatalog = items.ToArray();
        CommandPaletteHost.Visibility = Visibility.Visible;
        CommandSearchInput.Clear();
        FilterCommands();
        CommandSearchInput.Focus();
    }

    private void SelectCommandProfile(Guid id)
    {
        var profile = Profiles.FirstOrDefault(profile => profile.Id == id);
        if (profile is null) return;
        WorkspaceClearSearch_Click(this, new RoutedEventArgs());
        ProfilesGrid.SelectedItem = profile;
        ProfilesGrid.ScrollIntoView(profile);
    }

    private void OrbitCommandSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_orbitReady) FilterCommands();
    }

    private void FilterCommands()
    {
        var query = CommandSearchInput.Text.Trim();
        var matches = _commandCatalog.Where(item => item.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Detail.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(30).ToArray();
        CommandResults.ItemsSource = matches;
        CommandResults.SelectedIndex = matches.Length > 0 ? 0 : -1;
        CommandEmptyText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseCommandPalette()
    {
        CommandPaletteHost.Visibility = Visibility.Collapsed;
        _commandCatalog = [];
        CommandResults.ItemsSource = null;
        if (_launchPageVisible) WorkspaceLaunchTab.Focus();
        else if (_workspacePage == WorkspacePage.Accounts) AccountSearchInput.Focus();
        else WorkspaceSessionsTab.Focus();
    }

    private void OrbitCommandClose_Click(object sender, RoutedEventArgs e) => CloseCommandPalette();
    private void OrbitCommandBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, CommandPaletteHost)) CloseCommandPalette();
    }
    private void OrbitWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (CommandPaletteHost.Visibility != Visibility.Visible) return;
        if (e.Key == Key.Escape) { CloseCommandPalette(); e.Handled = true; }
        else if (e.Key == Key.Enter) { ExecuteCommand(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up)
        {
            if (CommandResults.Items.Count == 0) return;
            CommandResults.SelectedIndex = Math.Clamp(CommandResults.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, CommandResults.Items.Count - 1);
            CommandResults.ScrollIntoView(CommandResults.SelectedItem);
            e.Handled = true;
        }
    }
    private void OrbitCommandResult_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceCommand command })
        {
            CloseCommandPalette(); command.Execute();
        }
    }
    private void ExecuteCommand()
    {
        if (CommandResults.SelectedItem is not WorkspaceCommand command) return;
        CloseCommandPalette(); command.Execute();
    }
}
