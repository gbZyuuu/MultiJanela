using System.IO;
using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class HistoryPanel : System.Windows.Controls.UserControl
{
    private readonly SessionHistoryStore _store;
    private readonly ResourceHistory _resources;
    private readonly Func<IEnumerable<WorkspaceAccountRow>> _sessions;
    private SessionHistoryEntry[] _events = [];
    public HistoryPanel(SessionHistoryStore store, ResourceHistory resources, Func<IEnumerable<WorkspaceAccountRow>> sessions)
    { _store = store; _resources = resources; _sessions = sessions; InitializeComponent(); Loaded += async (_, _) => { RefreshResources(); await RefreshHistoryAsync(); }; }
    public void RefreshResources()
    {
        var selected = (Sessions.SelectedItem as WorkspaceAccountRow)?.Key;
        Sessions.ItemsSource = _sessions().ToArray();
        Sessions.SelectedItem = ((WorkspaceAccountRow[])Sessions.ItemsSource).FirstOrDefault(s => s.Key == selected);
        if (Sessions.SelectedItem is null) Sessions.SelectedIndex = 0;
        RefreshChart();
    }
    private void Chart_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) { if (Chart is not null) RefreshChart(); }
    public void RefreshPrivacy() { Events.Items.Refresh(); Sessions.Items.Refresh(); RefreshChart(); }
    private void RefreshChart()
    {
        if (Sessions.SelectedItem is not WorkspaceAccountRow row) { Chart.SetValues([]); ResourceCaption.Text = L.T("Abra uma sessão para acompanhar os recursos."); return; }
        var data = _resources.Get(row.Key);
        double? Value(ProcessResourceUsage item) => Metric.SelectedIndex switch { 0 => item.CpuPercent, 1 => item.ResidentBytes / 1048576d, _ => item.DedicatedGpuBytes / 1048576d };
        Chart.SetValues(data.Select(Value), Metric.SelectedIndex == 0 ? 100 : null);
        var current = data.LastOrDefault(); var value = current is null ? null : Value(current);
        ResourceCaption.Text = value is null ? L.T("Aguardando uma leitura disponível...") : L.F("Atual: {0} · últimas {1} amostras", value.Value.ToString("0.0") + (Metric.SelectedIndex == 0 ? "%" : " MB"), data.Length);
    }
    private async Task RefreshHistoryAsync()
    {
        try { _events = await _store.LoadAsync(); Events.ItemsSource = _events; Status.Text = L.F("{0} evento(s) · até 500 registros locais", _events.Length); }
        catch (Exception) { Status.Text = L.T("Não foi possível carregar o histórico. O arquivo original foi preservado."); }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshHistoryAsync();
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "CSV|*.csv", FileName = "MultiJanela-sessions.csv" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, SessionHistoryStore.ExportCsv(_events), new System.Text.UTF8Encoding(true)); Status.Text = L.T("Histórico exportado com nomes de contas ocultos."); }
        catch (Exception) { Status.Text = L.T("Não foi possível exportar o histórico."); }
    }
    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Show(Window.GetWindow(this), L.T("Excluir o histórico local de sessões?"), L.T("Limpar histórico"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _store.ClearAsync(); _resources.Clear(); RefreshChart(); await RefreshHistoryAsync(); }
        catch (Exception) { Status.Text = L.T("Não foi possível limpar o histórico."); }
    }
}
