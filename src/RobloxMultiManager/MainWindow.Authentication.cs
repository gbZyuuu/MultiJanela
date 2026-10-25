using System.Windows;
using System.Windows.Controls;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private readonly RobloxAuthenticationFailureReader _authenticationFailures = new();
    private readonly HashSet<string> _authenticationRejected = [];
    private bool _nativeSessionRecoveryInProgress;

    private bool ReportAuthenticationRejection(RobloxLaunchReceipt receipt, AccountProfile profile, bool showFailure)
    {
        _authenticationRejected.Add(receipt.ProcessKey);
        NotifyEvent("authentication");
        foreach (var row in WorkspaceAccounts.Where(r => r.Key == receipt.ProcessKey)) row.SetAuthenticationRejected(true);
        UpdateConfirmation();
        AppendLog(L.T("O Roblox recusou a autenticação no cliente (HTTP 403). Verifique o login ou a sessão local do Roblox."));
        if (!showFailure || DrawerHost.Visibility == Visibility.Visible) return false;
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = L.T("O Roblox recusou a autorização temporária. Atualize o login. Se a mesma conta funciona no aplicativo, mas falha pelo site e pelo gerenciador, tente reparar a sessão local com todas as janelas do Roblox fechadas."),
            TextWrapping = TextWrapping.Wrap, FontSize = 16, Margin = new Thickness(0, 0, 0, 24)
        });
        var refresh = new System.Windows.Controls.Button { Content = L.T("Login / atualizar sessão"), Height = 42, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        refresh.Click += (_, e) =>
        {
            CloseDrawer();
            ProfilesGrid.SelectedItem = Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (ProfilesGrid.SelectedItem is not null) LoginProfileButton_Click(refresh, e);
        };
        body.Children.Add(refresh);
        var repair = new System.Windows.Controls.Button { Content = L.T("Reparar sessão local do Roblox"), Height = 42, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        repair.Click += RepairNativeSession_Click;
        body.Children.Add(repair);
        OpenDrawer(L.T("Autenticação recusada (403)"), profile.PublicName, body);
        return false;
    }
    private async void RepairNativeSession_Click(object sender, RoutedEventArgs e)
    {
        if (_nativeSessionRecoveryInProgress) return;
        if (_launchBatchCancellation is not null || _activeLaunches > 0 || _launchingProfiles.Count > 0)
        {
            AppDialog.Show(this, L.T("Aguarde ou cancele as aberturas antes de reparar a sessão local."),
                L.T("Reparar sessão local do Roblox"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AppDialog.Show(this,
                L.T("Feche todas as janelas do Roblox e do Studio. A sessão local compartilhada será guardada em backup e o Roblox poderá pedir login novamente. Contas salvas, perfis de navegador e configurações do MultiJanela serão preservados. Continuar?"),
                L.T("Reparar sessão local do Roblox"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _nativeSessionRecoveryInProgress = true;
        var button = sender as System.Windows.Controls.Button;
        if (button is not null) button.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => new RobloxLocalSessionRecovery().Repair());
            var message = NativeSessionRecoveryMessage(result.Status);
            if (result.Status == LocalSessionRecoveryStatus.Repaired) AppendLog(message);
            AppDialog.Show(this, message, L.T("Reparar sessão local do Roblox"), MessageBoxButton.OK,
                result.Status == LocalSessionRecoveryStatus.Repaired ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            _nativeSessionRecoveryInProgress = false;
            if (button is not null) button.IsEnabled = true;
        }
    }

    internal static string NativeSessionRecoveryMessage(LocalSessionRecoveryStatus status) => status switch
    {
        LocalSessionRecoveryStatus.Repaired => L.T("Sessão local do Roblox guardada em backup. Tente abrir a conta novamente. Nenhuma conta salva do MultiJanela foi removida."),
        LocalSessionRecoveryStatus.ClientsRunning => L.T("Feche todas as janelas e processos do Roblox e do Studio antes de reparar. Nenhum arquivo foi alterado."),
        LocalSessionRecoveryStatus.AlreadyClean => L.T("O arquivo de sessão local já está ausente. Tente abrir a conta; não é necessário repetir o reparo."),
        LocalSessionRecoveryStatus.Busy => L.T("Outro reparo está em andamento. Aguarde terminar."),
        LocalSessionRecoveryStatus.UnsafePath => L.T("O local da sessão usa um redirecionamento de arquivos. O reparo foi cancelado para proteger seus dados."),
        _ => L.T("Não foi possível guardar a sessão local em backup. Feche o Roblox e verifique as permissões antes de tentar novamente.")
    };

}
