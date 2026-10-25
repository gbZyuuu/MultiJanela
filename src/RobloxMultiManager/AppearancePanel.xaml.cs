using System.Windows;
using System.IO;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class AppearancePanel : System.Windows.Controls.UserControl
{
    private Dictionary<string, string>? _pendingDraft;
    private static readonly string[] DraftFields = ["Theme", "Scale", "Font", "Accent", "Privacy"];
    internal Dictionary<string, string> CaptureDraft() => FormDrafts.Capture(this, DraftFields);
    internal void RestoreDraft(Dictionary<string, string>? draft) { _pendingDraft = draft; ApplyDraft(); }
    private void ApplyDraft() { if (!IsLoaded || _pendingDraft is null) return; FormDrafts.Restore(this, _pendingDraft, DraftFields); _pendingDraft = null; ChangeAppearance(); }
    private readonly AppearancePreferencesStore _store;
    private readonly SettingsSaveQueue _autosave = new();
    private bool _restoring;
    public bool IsBusy => _autosave.IsBusy;
    public AppearancePanel(AppearancePreferencesStore store)
    {
        _store = store; InitializeComponent(); BuildThemeTiles(); var initialized = false; Loaded += (_, _) => { if (!initialized) { SetValues(AppearanceService.Current); initialized = true; } ApplyDraft(); };
        SettingsSaveQueue.Watch(this, ChangeAppearance, Theme, Scale, Font, Accent, Privacy);
    }
    private void BuildThemeTiles()
    {
        foreach (var preset in ThemePresets.All)
        {
            Theme.Items.Add(new System.Windows.Controls.ComboBoxItem { Tag = preset.Id, Content = L.T(preset.Name) });
            var tile = new System.Windows.Controls.Button { Tag = preset.Id, Width = 150, Height = 100, Margin = new Thickness(0,0,10,10), Padding = new Thickness(10), Background = ThemePresets.Brush(preset.Background) };
            var sample = new System.Windows.Controls.StackPanel();
            var strips = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0,0,0,12) };
            foreach (var color in new[] { preset.Surface, preset.Raised, preset.Accent }) strips.Children.Add(new System.Windows.Controls.Border { Background = ThemePresets.Brush(color), Width = 32, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(0,0,6,0) });
            sample.Children.Add(strips);
            var title = new System.Windows.Controls.TextBlock { Foreground = ThemePresets.Brush(preset.Light ? "#202936" : "#F5F6F8"), FontWeight = FontWeights.SemiBold };
            title.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TranslationSource.Language)) { Source = TranslationSource.Instance, Converter = new TrExtension(preset.Name) });
            sample.Children.Add(title); tile.Content = sample;
            tile.Click += (_, _) => { _restoring = true; Theme.SelectedValue = preset.Id; Accent.Text = preset.Accent; _restoring = false; MarkTheme(); ChangeAppearance(); };
            ThemeTiles.Children.Add(tile);
        }
        Theme.SelectionChanged += (_, _) => MarkTheme();
    }
    private void MarkTheme()
    {
        foreach (var tile in ThemeTiles.Children.OfType<System.Windows.Controls.Button>())
        { var selected = Equals(tile.Tag, Theme.SelectedValue); tile.BorderThickness = new Thickness(selected ? 2 : 1); tile.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, selected ? "Accent" : "Hairline"); }
    }
    private AppearancePreferences Values() => AppearancePreferences.Validate(new(Theme.SelectedValue?.ToString() ?? "midnight",
        Accent.Text.Trim(), int.TryParse(Scale.SelectedValue?.ToString(), out var scale) ? scale : 100, Font.SelectedValue?.ToString() ?? "Segoe UI", Privacy.IsChecked == true));
    public void RefreshLanguage() => Status.Text = "";
    private void SetValues(AppearancePreferences values)
    { _restoring = true; Theme.SelectedValue = values.Theme; Accent.Text = values.Accent; Scale.SelectedValue = values.TextScale.ToString(); Font.SelectedValue = values.Font; Privacy.IsChecked = values.StreamerMode; _restoring = false; }
    private void ChangeAppearance()
    {
        if (_restoring || !IsLoaded) return;
        try
        {
            var values = Values(); AppearanceService.Apply(values);
            Status.Text = L.T("Alterações salvas automaticamente.");
            _autosave.Enqueue("appearance", async () =>
            {
                try { await _store.SaveAsync(values); }
                catch (Exception) { Status.Text = L.T("Não foi possível aplicar a aparência. Verifique a cor e o acesso ao armazenamento."); }
            });
        }
        catch (Exception) { Status.Text = L.T("Não foi possível aplicar a aparência. Verifique a cor e o acesso ao armazenamento."); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.json", FileName = "MultiJanela-theme.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, AppearancePreferencesStore.Export(Values())); Status.Text = L.T("Tema exportado."); }
        catch (Exception) { Status.Text = L.T("Não foi possível exportar o tema."); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 16384) throw new InvalidDataException();
            SetValues(AppearancePreferencesStore.Import(await File.ReadAllTextAsync(dialog.FileName)));
            ChangeAppearance();
        }
        catch (Exception) { Status.Text = L.T("Arquivo de tema inválido."); }
    }
}
