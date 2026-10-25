using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class LocalApiPanel : System.Windows.Controls.UserControl
{
    private LocalApiPreferences _preferences;
    private readonly Func<LocalApiPreferences, bool, Task<LocalApiPreferences>> _save;
    private readonly SettingsSaveQueue _autosave = new();
    private bool _rotating;
    public bool IsBusy => _autosave.IsBusy || _rotating;
    public LocalApiPanel(LocalApiPreferences preferences, Func<LocalApiPreferences, bool, Task<LocalApiPreferences>> save)
    { _preferences = preferences; _save = save; InitializeComponent(); Enabled.IsChecked = preferences.Enabled; Port.Text = preferences.Port.ToString(); Actions.IsChecked = preferences.AllowActions; Rejoin.IsChecked = preferences.AllowRejoin; Names.IsChecked = preferences.IncludeNames; SettingsSaveQueue.Watch(this, SaveChanges, Enabled, Port, Actions, Rejoin, Names); }
    public void RefreshLanguage() => Status.Text = "";
    private void SaveChanges()
    {
        if (!int.TryParse(Port.Text, out var port)) return;
        try
        {
            var next = LocalApiPreferences.Validate(new(Enabled.IsChecked == true, port, Actions.IsChecked == true, Rejoin.IsChecked == true, Names.IsChecked == true, _preferences.ProtectedToken));
            _autosave.Enqueue("api", async () => await PersistAsync(next, false));
        }
        catch (Exception) { Status.Text = L.T("Não foi possível ativar ou salvar. Verifique a porta, as permissões e se ela já está em uso."); }
    }
    private async Task PersistAsync(LocalApiPreferences next, bool rotate)
    {
        try
        {
            _preferences = await _save(next with { ProtectedToken = _preferences.ProtectedToken }, rotate);
            Status.Text = _preferences.Enabled ? L.F("API ativa em 127.0.0.1:{0}.", _preferences.Port) : L.T("API local desativada.");
        }
        catch (Exception) { Status.Text = L.T("Não foi possível ativar ou salvar. Verifique a porta, as permissões e se ela já está em uso."); }
    }
    private async void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (AppDialog.Show(Window.GetWindow(this), L.T("Gerar uma nova chave? Integrações com a chave antiga perderão acesso."), L.T("Gerar nova chave"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) { _rotating = true; try { await PersistAsync(_preferences, true); } finally { _rotating = false; } }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_preferences.ProtectedToken)) { Status.Text = L.T("Salve as preferências para gerar uma chave."); return; }
        try { System.Windows.Clipboard.SetText(LocalSecretProtection.Unprotect(_preferences.ProtectedToken)); Status.Text = L.T("Chave copiada. Mantenha-a privada."); }
        catch (Exception) { Status.Text = L.T("Não foi possível copiar a chave deste Windows."); }
    }
}
