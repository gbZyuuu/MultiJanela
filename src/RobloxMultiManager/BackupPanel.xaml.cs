using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class BackupPanel : System.Windows.Controls.UserControl
{
    private readonly EncryptedBackupService _service;
    private readonly Func<Func<Task>, bool, Task> _maintain;
    public bool IsBusy { get; private set; }
    public void RefreshLanguage() { if (!IsBusy) Status.Text = ""; }
    public BackupPanel(EncryptedBackupService service, Func<Func<Task>, bool, Task> maintain)
    { _service = service; _maintain = maintain; InitializeComponent(); }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = L.T("Backup criptografado") + "|*.mjb", FileName = "MultiJanela-backup.mjb" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunAsync(async () =>
        {
            var password = Password.Password; var browsers = IncludeBrowsers.IsChecked == true;
            await _maintain(async () => { await Task.Run(() => _service.ExportAsync(dialog.FileName, password, browsers)); }, false);
            Status.Text = L.T("Backup criptografado salvo.");
        });
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Backup criptografado") + "|*.mjb" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await RunAsync(async () =>
        {
            var password = Password.Password;
            var inventory = await Task.Run(() => _service.InspectAsync(dialog.FileName, password));
            if (AppDialog.Show(Window.GetWindow(this), L.F("Restaurar {0} arquivo(s)? Uma cópia criptografada dos dados atuais será salva antes. O gerenciador será reiniciado; os jogos continuarão abertos.", inventory.Files),
                    L.T("Restaurar backup"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _maintain(() => Task.Run(() => _service.RestoreAsync(dialog.FileName, password)), true);
        });
    }
    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true; Status.Text = L.T("Processando backup...");
        try { await action(); }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
        catch (System.Security.Cryptography.CryptographicException) { Status.Text = L.T("Senha incorreta ou backup danificado."); }
        catch (Exception) { Status.Text = L.T("Não foi possível concluir. Verifique a senha, o espaço disponível e se as páginas de contas estão fechadas."); }
        finally { IsBusy = false; Password.Clear(); }
    }
}
