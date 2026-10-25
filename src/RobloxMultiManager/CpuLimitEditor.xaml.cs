using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class CpuLimitEditor : System.Windows.Controls.UserControl
{
    private int _global = 100;
    private bool _allowInheritance;
    private bool _updating = true;
    private readonly SettingsSaveQueue _autosave = new();
    private bool _subscribed;
    public bool IsBusy => _autosave.IsBusy;
    public bool IsUsingGlobal { get; private set; }
    public Func<int?, Task>? SaveRequested { get; set; }
    public event EventHandler? Completed;

    public CpuLimitEditor()
    {
        InitializeComponent();
        _updating = false;
        Loaded += (_, _) => { if (!_subscribed) { L.LanguageChanged += LanguageChanged; _subscribed = true; } };
        Unloaded += (_, _) => { if (_subscribed) { L.LanguageChanged -= LanguageChanged; _subscribed = false; } };
    }

    public void Configure(int global, int? individual, bool allowInheritance)
    {
        _global = CpuLimitPolicy.Effective(global, null);
        _allowInheritance = allowInheritance;
        ApplyButton.Visibility = allowInheritance ? Visibility.Visible : Visibility.Collapsed;
        IsUsingGlobal = allowInheritance && (individual is null || !CpuLimitPolicy.IsValid(individual.Value));
        SetValue(CpuLimitPolicy.Effective(global, individual));
        UseGlobalButton.Visibility = InheritanceHint.Visibility = allowInheritance ? Visibility.Visible : Visibility.Collapsed;
        RefreshMode();
    }

    public void SetSessionState(bool running) => PendingSessionHint.Visibility = running ? Visibility.Collapsed : Visibility.Visible;

    private void SetValue(int value)
    {
        _updating = true;
        LimitSlider.Value = value;
        PercentInput.Text = value.ToString(CultureInfo.InvariantCulture);
        _updating = false;
        ErrorText.Text = SavedText.Text = "";
        ApplyButton.IsEnabled = true;
    }

    private void LimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating || PercentInput is null) return;
        IsUsingGlobal = false;
        SetValue((int)Math.Round(e.NewValue));
        RefreshMode(); SaveChanges();
    }

    private void PercentInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updating || ApplyButton is null) return;
        IsUsingGlobal = false;
        var valid = TryRead(out var percent);
        ApplyButton.IsEnabled = valid;
        ErrorText.Text = valid ? "" : L.T("Informe uma porcentagem inteira entre 1 e 100.");
        SavedText.Text = "";
        if (valid) { _updating = true; LimitSlider.Value = percent; _updating = false; }
        RefreshMode(); if (valid) SaveChanges();
    }

    private bool TryRead(out int percent) => int.TryParse(PercentInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out percent) && CpuLimitPolicy.IsValid(percent);
    private void UseGlobal_Click(object sender, RoutedEventArgs e)
    {
        IsUsingGlobal = true;
        SetValue(_global);
        RefreshMode(); SaveChanges();
    }
    private void LanguageChanged(object? sender, EventArgs e)
    {
        RefreshMode();
        if (ErrorText.Text.Length > 0) ErrorText.Text = TryRead(out _) ? L.T("Não foi possível salvar o limite. Tente novamente.") : L.T("Informe uma porcentagem inteira entre 1 e 100.");
        if (SavedText.Text.Length > 0) SavedText.Text = L.T("Preferência salva. Confira o estado aplicado nas contas abertas.");
    }
    public void RefreshMode()
    {
        var percent = TryRead(out var value) ? value : (int)LimitSlider.Value;
        ModeText.Text = !_allowInheritance ? L.F("Limite global: {0}%", percent)
            : IsUsingGlobal ? L.F("Usando global: {0}%", _global) : L.F("Limite próprio: {0}%", percent);
    }
    private void SaveChanges()
    {
        if (!TryRead(out var percent) || SaveRequested is null) return;
        var value = _allowInheritance && IsUsingGlobal ? (int?)null : percent;
        _autosave.Enqueue("cpu", async () =>
        {
            try { await SaveRequested(value); SavedText.Text = L.T("Preferência salva. Confira o estado aplicado nas contas abertas."); }
            catch (Exception) { ErrorText.Text = L.T("Não foi possível salvar o limite. Tente novamente."); }
        });
    }
    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        await _autosave.Completion;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
