using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class ProcessTuningPanel : System.Windows.Controls.UserControl
{
    private readonly Func<ProcessTuningPreferences, Task> _save;
    private readonly Func<Task<int>> _trim;
    private readonly SettingsSaveQueue _autosave = new();
    private bool _trimming;
    public bool IsBusy => _autosave.IsBusy || _trimming;
    public ProcessTuningPanel(ProcessTuningPreferences preferences, Func<ProcessTuningPreferences, Task> save, Func<Task<int>> trim)
    { _save = save; _trim = trim; InitializeComponent(); Enabled.IsChecked = preferences.Enabled; Priority.IsChecked = preferences.LowerPriority; Eco.IsChecked = preferences.Eco; OnlyBackgroundCheck.IsChecked = preferences.OnlyBackground; SettingsSaveQueue.Watch(this, SaveChanges, Enabled, Priority, Eco, OnlyBackgroundCheck); }
    public void RefreshLanguage() => Status.Text = "";
    private void SaveChanges()
    {
        var next = new ProcessTuningPreferences(Enabled.IsChecked == true, Priority.IsChecked == true, Eco.IsChecked == true, OnlyBackgroundCheck.IsChecked == true);
        _autosave.Enqueue("process", async () =>
        {
            try { await _save(next); Status.Text = L.T("Preferências salvas."); }
            catch (Exception) { Status.Text = L.T("Não foi possível aplicar os ajustes de processo."); }
        });
    }
    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (AppDialog.Show(Window.GetWindow(this), L.T("Liberar páginas de memória das sessões atuais? O Windows poderá carregá-las novamente quando necessário."), L.T("Liberar memória de trabalho"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _trimming = true;
        try { Status.Text = L.F("{0} processo(s) atualizado(s).", await _trim()); }
        catch (Exception) { Status.Text = L.T("Não foi possível liberar a memória de trabalho."); }
        finally { _trimming = false; }
    }
}
