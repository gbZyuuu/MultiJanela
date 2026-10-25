using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class CpuGlobalSlider : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private int _saved = 100;
    private int? _pending;
    private bool _updating, _configured, _dragging;
    private Task _saveTask = Task.CompletedTask;
    public Func<int?, Task>? SaveRequested { get; set; }

    public CpuGlobalSlider()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (!_dragging) _ = FlushAsync(); };
        Unloaded += (_, _) => _debounce.Stop();
    }

    public void Synchronize(int percent)
    {
        _saved = CpuLimitPolicy.Effective(percent, null);
        _configured = true;
        // A completed older save must not move the thumb away from newer user input.
        if (_pending is null && !_dragging) SetValue(_saved);
    }
    private void SetValue(int value)
    {
        _updating = true;
        LimitSlider.Value = value;
        ValueText.Text = value.ToString(CultureInfo.InvariantCulture) + "%";
        _updating = false;
    }
    private void LimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_configured || _updating) return;
        var value = (int)Math.Round(e.NewValue);
        ValueText.Text = value.ToString(CultureInfo.InvariantCulture) + "%";
        _pending = value;
        SaveError.Visibility = Visibility.Collapsed;
        _debounce.Stop();
        if (!_dragging) _debounce.Start();
    }
    private void LimitSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _dragging = true;
        _debounce.Stop();
    }
    private void LimitSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        if (_pending is not null) _debounce.Start();
    }
    public Task FlushAsync(bool finishInteraction = false)
    {
        _debounce.Stop();
        if (finishInteraction) _dragging = false;
        if (_saveTask.IsCompleted) _saveTask = SavePendingAsync();
        return _saveTask;
    }
    private async Task SavePendingAsync()
    {
        if (SaveRequested is null) return;
        while (!_dragging && _pending is { } percent)
        {
            _pending = null;
            if (percent == _saved) continue;
            try
            {
                await SaveRequested(percent);
                _saved = percent;
                SaveError.Visibility = Visibility.Collapsed;
            }
            catch
            {
                SaveError.Visibility = Visibility.Visible;
                if (_pending is null && !_dragging) SetValue(_saved);
            }
        }
    }
}
