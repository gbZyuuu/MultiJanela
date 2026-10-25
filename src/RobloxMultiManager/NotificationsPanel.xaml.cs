using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class NotificationsPanel : System.Windows.Controls.UserControl
{
    private readonly Func<NotificationPreferences, Task> _save;
    private readonly Action _test;
    private readonly SettingsSaveQueue _autosave = new();
    public bool IsBusy => _autosave.IsBusy;
    public NotificationsPanel(NotificationPreferences preferences, Func<NotificationPreferences, Task> save, Action test)
    { _save = save; _test = test; InitializeComponent(); Desktop.IsChecked = preferences.Desktop; Webhook.IsChecked = preferences.Webhook; Address.Password = preferences.WebhookUrl; SettingsSaveQueue.Watch(this, SaveChanges, Desktop, Webhook, Address); }
    public void RefreshLanguage() => Status.Text = "";
    private void SaveChanges()
    {
        try
        {
            var next = NotificationPreferences.Validate(new(Desktop.IsChecked == true, Webhook.IsChecked == true, Address.Password.Trim()));
            _autosave.Enqueue("notifications", async () =>
            {
                try { await _save(next); Status.Text = L.T("Preferências salvas."); }
                catch (Exception) { Status.Text = L.T("Não foi possível salvar. Verifique o endereço HTTPS e o armazenamento local."); }
            });
        }
        catch (Exception) { Status.Text = L.T("Não foi possível salvar. Verifique o endereço HTTPS e o armazenamento local."); }
    }
    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Show(Window.GetWindow(this), L.T("Enviar um evento de teste usando as preferências já salvas?"), L.T("Enviar teste"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        { _test(); Status.Text = L.T("Evento de teste colocado na fila."); }
    }
}
