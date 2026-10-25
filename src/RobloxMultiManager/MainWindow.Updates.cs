using System.IO;
using System.Windows;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private UpdatesPanel? _updatesPanel;
    private void InitializeManagerUpdates()
    {
        _updatesPanel = new UpdatesPanel(new ManagerUpdateService(VersionHttpClient), Path.Combine(_paths.Root, "Updates"));
        UpdatesSettingsHost.Content = _updatesPanel;
        _updatesPanel.CanApply = () =>
        {
            HeaderUpdatePopup.IsOpen = false;
            var ready = !HandoffWorkPending(ignoreUpdate: true);
            if (!ready) HeaderUpdatePopup.IsOpen = true;
            return ready;
        };
        _updatesPanel.StateChanged += (_, _) => RefreshUpdateHeader();
        RefreshUpdateHeader();
        _ = _updatesPanel.CheckForUpdatesAsync();
    }
    private void RefreshUpdateHeader()
    {
        var available = _updatesPanel?.HasUpdate == true;
        HeaderUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        HeaderUpdateButton.IsEnabled = _updatesPanel?.IsBusy != true;
        HeaderUpdateDot.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        HeaderUpdateButton.ToolTip = L.T("Baixar e atualizar agora");
        HeaderUpdateStatus.Text = _updatesPanel?.Summary ?? "";
        HeaderUpdateCheckButton.IsEnabled = _updatesPanel?.IsBusy != true;
        HeaderUpdateCheckButton.ToolTip = _updatesPanel?.Summary is { Length: > 0 } summary ? summary : L.T("Verificar atualizações");
    }
    private async void HeaderCheckUpdates_Click(object sender, RoutedEventArgs e)
    { if (_updatesPanel is not null) { await _updatesPanel.CheckForUpdatesAsync(); HeaderUpdatePopup.IsOpen = true; } }
    private async void HeaderUpdates_Click(object sender, RoutedEventArgs e)
    { if (_updatesPanel is not null) { HeaderUpdatePopup.IsOpen = true; await _updatesPanel.DownloadAndApplyAsync(); } }
    private void HeaderUpdateHover(object sender, System.Windows.Input.MouseEventArgs e) { if (_updatesPanel?.HasUpdate == true) HeaderUpdatePopup.IsOpen = true; }
}
