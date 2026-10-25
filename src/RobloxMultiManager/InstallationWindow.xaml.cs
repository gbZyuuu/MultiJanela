using System.Diagnostics;
using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class InstallationWindow : Window
{
    private readonly bool _uninstall;
    private bool _busy;
    public InstallationWindow(bool uninstall)
    {
        _uninstall = uninstall; InitializeComponent();
        if (uninstall) { Heading.Text = L.T("Desinstalar MultiJanela"); ActionButton.Content = L.T("Desinstalar"); Desktop.Visibility = Visibility.Collapsed; }
        Closing += (_, e) => e.Cancel = _busy;
    }
    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return; _busy = true; ActionButton.IsEnabled = false; Status.Text = L.T("Processando...");
        try
        {
            var installer = new UserInstallationService();
            if (_uninstall)
            {
                var removed = await installer.UninstallAsync(Environment.ProcessId);
                Status.Text = removed ? L.T("MultiJanela desinstalado. Seus dados de contas foram preservados.") : L.T("Os arquivos do aplicativo foram removidos. Arquivos adicionais foram preservados na pasta.");
                ActionButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                var desktop = Desktop.IsChecked == true;
                var launcher = await Task.Run(() => installer.InstallAsync(AppContext.BaseDirectory, desktop));
                Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false });
                _busy = false; Close();
            }
        }
        catch (InvalidOperationException ex) { Status.Text = ex.Message; }
        catch (Exception) { Status.Text = L.T("Não foi possível concluir. Verifique os arquivos do pacote e se o gerenciador instalado está fechado."); }
        finally { _busy = false; ActionButton.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) { if (!_busy) Close(); }
}
