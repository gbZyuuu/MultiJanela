using System.Text.Json;
using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class AutomationPanel : System.Windows.Controls.UserControl
{
    private Dictionary<string, string>? _pendingDraft;
    private static readonly string[] DraftFields = ["Schedules", "Recipes", "Action", "Preset", "NameInput", "Date", "Time", "Interval", "Enabled", "AllowClose", "RecipeJson"];
    internal Dictionary<string, string> CaptureDraft() => FormDrafts.Capture(this, DraftFields);
    internal void RestoreDraft(Dictionary<string, string>? draft) { _pendingDraft = draft; ApplyDraft(); }
    private void ApplyDraft() { if (!IsLoaded || _pendingDraft is null) return; FormDrafts.Restore(this, _pendingDraft, DraftFields); _pendingDraft = null; }
    private readonly PreferenceFile<AutomationPreferences> _store;
    private readonly Func<IEnumerable<LaunchPreset>> _presets;
    private readonly Func<AutomationRecipe, Task> _run;
    private readonly Action _stop;
    public bool IsBusy { get; private set; }
    public AutomationPanel(PreferenceFile<AutomationPreferences> store, Func<IEnumerable<LaunchPreset>> presets, Func<AutomationRecipe, Task> run, Action stop)
    {
        _store = store; _presets = presets; _run = run; _stop = stop; InitializeComponent();
        var initialized = false; Loaded += async (_, _) => { if (!initialized) { Preset.ItemsSource = _presets().ToArray(); NoPresets.Visibility = Preset.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; await ReloadAsync(); initialized = true; } ApplyDraft(); };
        Preset.DropDownOpened += (_, _) => { var id = (Preset.SelectedItem as LaunchPreset)?.Id; Preset.ItemsSource = _presets().ToArray(); NoPresets.Visibility = Preset.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed; Preset.SelectedItem = _presets().FirstOrDefault(p => p.Id == id); };
        New_Click(this, new());
        RefreshCalendarLanguage();
    }
    public void RefreshLanguage() { Status.Text = ""; RefreshCalendarLanguage(); }
    private void RefreshCalendarLanguage()
    {
        Date.Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);
        if (Date.Template?.FindName("PART_Popup", Date) is System.Windows.Controls.Primitives.Popup { Child: System.Windows.Controls.Calendar calendar }) calendar.Language = Date.Language;
    }
    private async Task ReloadAsync()
    { try { var data = await _store.LoadAsync(); Schedules.ItemsSource = data.Schedules; Recipes.ItemsSource = data.Recipes; } catch (Exception) { Status.Text = L.T("Não foi possível carregar as automações. O arquivo original foi preservado."); } }
    private void New_Click(object sender, RoutedEventArgs e)
    { Schedules.SelectedItem = null; NameInput.Clear(); Date.SelectedDate = DateTime.Now.AddMinutes(2).Date; Time.Text = DateTime.Now.AddMinutes(2).ToString("HH:mm"); Interval.Text = "0"; Enabled.IsChecked = false; AllowClose.IsChecked = false; }
    private void Schedule_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Schedules.SelectedItem is not ScheduledManagerAction item) return;
        NameInput.Text = item.Name; Action.SelectedValue = item.Action; Preset.SelectedItem = _presets().FirstOrDefault(p => p.Id == item.PresetId);
        Date.SelectedDate = item.NextRunUtc.ToLocalTime().Date; Time.Text = item.NextRunUtc.ToLocalTime().ToString("HH:mm"); Interval.Text = item.IntervalMinutes.ToString(); Enabled.IsChecked = item.Enabled; AllowClose.IsChecked = item.AllowRejoin;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return; IsBusy = true;
        try
        {
            if (Date.SelectedDate is not DateTime day || !TimeSpan.TryParseExact(Time.Text, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time)
                || !int.TryParse(Interval.Text, out var minutes)) throw new ArgumentException();
            var original = Schedules.SelectedItem as ScheduledManagerAction;
            var id = original?.Id ?? Guid.NewGuid();
            var local = DateTime.SpecifyKind(day.Date + time, DateTimeKind.Local);
            var schedule = new ScheduledManagerAction(id, NameInput.Text.Trim(), Action.SelectedValue?.ToString() ?? "launch", (Preset.SelectedItem as LaunchPreset)?.Id,
                local.ToUniversalTime(), minutes, Enabled.IsChecked == true, AllowClose.IsChecked == true);
            await _store.UpdateAsync(d =>
            {
                var current = d.Schedules.FirstOrDefault(s => s.Id == id);
                if (original is not null && current is not null)
                {
                    if (schedule.NextRunUtc.ToLocalTime().ToString("yyyyMMddHHmm") == original.NextRunUtc.ToLocalTime().ToString("yyyyMMddHHmm")) schedule = schedule with { NextRunUtc = current.NextRunUtc };
                    if (schedule.Enabled == original.Enabled) schedule = schedule with { Enabled = current.Enabled };
                }
                return d with { Schedules = d.Schedules.Where(s => s.Id != id).Append(schedule).ToArray() };
            });
            await ReloadAsync(); Status.Text = L.T("Agendamento salvo.");
        }
        catch (Exception) { Status.Text = L.T("Verifique nome, horário, intervalo, perfil e permissão para reabrir sessões."); }
        finally { IsBusy = false; }
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Schedules.SelectedItem is not ScheduledManagerAction selected || IsBusy) return;
        if (AppDialog.Show(Window.GetWindow(this), L.F("Excluir ‘{0}’?", selected.Name), L.T("Excluir"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        IsBusy = true;
        try { await _store.UpdateAsync(d => d with { Schedules = d.Schedules.Where(s => s.Id != selected.Id).ToArray() }); await ReloadAsync(); }
        catch (Exception) { Status.Text = L.T("Não foi possível excluir o agendamento."); }
        finally { IsBusy = false; }
    }
    private void Example_Click(object sender, RoutedEventArgs e) => RecipeJson.Text = JsonSerializer.Serialize(new AutomationRecipe(1, Guid.NewGuid(), L.T("Minha receita"), false,
        [new("arrange"), new("delay", Seconds: 2), new("arrange")]), new JsonSerializerOptions { WriteIndented = true });
    private void Recipe_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    { if (Recipes.SelectedItem is AutomationRecipe recipe) RecipeJson.Text = JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }); }
    private async void SaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return; IsBusy = true;
        try
        {
            var recipe = RecipeRunner.Parse(RecipeJson.Text);
            if (recipe.AllowRejoin && AllowClose.IsChecked != true) throw new ArgumentException();
            await _store.UpdateAsync(d => d with { Recipes = d.Recipes.Where(r => r.Id != recipe.Id).Append(recipe).ToArray() });
            await ReloadAsync(); Status.Text = L.T("Receita salva.");
        }
        catch (Exception) { Status.Text = L.T("Receita inválida ou sem permissão para reabrir sessões."); }
        finally { IsBusy = false; }
    }
    private async void RunRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        try
        {
            var recipe = RecipeRunner.Parse(RecipeJson.Text);
            if (recipe.AllowRejoin && AllowClose.IsChecked != true) throw new ArgumentException();
            if (AppDialog.Show(Window.GetWindow(this), L.F("Executar ‘{0}’ com {1} passo(s)?", recipe.Name, recipe.Steps.Length), L.T("Executar receita"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await _run(recipe); Status.Text = L.T("Receita concluída.");
        }
        catch (OperationCanceledException) { Status.Text = L.T("Operação cancelada."); }
        catch (Exception) { Status.Text = L.T("Não foi possível executar. Verifique os perfis, as permissões e as operações em andamento."); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { _stop(); Status.Text = L.T("Cancelamento solicitado."); }
}
