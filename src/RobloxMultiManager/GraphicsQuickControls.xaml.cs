using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RobloxMultiManager.Core;
namespace RobloxMultiManager;
public partial class GraphicsQuickControls : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _sync, _ready;
    private (int Fps, int Quality, bool Enabled)? _pending;
    private Task _saving = Task.CompletedTask;
    private GraphicsLaunchPolicy _saved = new();
    public Func<int, int, bool, Task>? SaveRequested { get; set; }
    public bool IsBusy => _timer.IsEnabled || !_saving.IsCompleted || _pending is not null;
    public GraphicsQuickControls() { InitializeComponent(); _timer.Tick += async (_, _) => { _timer.Stop(); await FlushAsync(); }; Synchronize(new()); }
    public void Synchronize(GraphicsLaunchPolicy policy)
    {
        _saved = policy;
        if (IsBusy) return;
        _sync = true; Fps.SelectedValue = policy.GlobalFps.ToString(); Quality.SelectedValue = policy.GlobalQuality.ToString(); Enabled.IsChecked = policy.Enabled; _sync = false; _ready = true;
    }
    private void Value_Changed(object sender, SelectionChangedEventArgs e) => Queue();
    private void Enabled_Changed(object sender, RoutedEventArgs e) => Queue();
    private void Queue()
    {
        if (!_ready || _sync || !int.TryParse(Fps.SelectedValue?.ToString(), out var fps) || !int.TryParse(Quality.SelectedValue?.ToString(), out var quality)) return;
        _pending = (fps, quality, Enabled.IsChecked == true); _timer.Stop(); _timer.Start(); Status.Text = "";
    }
    public async Task FlushAsync()
    {
        _timer.Stop();
        if (!_saving.IsCompleted) { await _saving; if (_pending is null) return; }
        _saving = SaveLoopAsync(); await _saving;
    }
    private async Task SaveLoopAsync()
    {
        while (_pending is { } value)
        {
            _pending = null;
            try { if (SaveRequested is { } save) await save(value.Fps, value.Quality, value.Enabled); _saved = _saved with { GlobalFps = value.Fps, GlobalQuality = value.Quality, Enabled = value.Enabled }; }
            catch (Exception)
            {
                if (_pending is null) { _sync = true; Fps.SelectedValue = _saved.GlobalFps.ToString(); Quality.SelectedValue = _saved.GlobalQuality.ToString(); Enabled.IsChecked = _saved.Enabled; _sync = false; }
                Status.Text = L.T("Não foi possível salvar.");
            }
        }
    }
}
