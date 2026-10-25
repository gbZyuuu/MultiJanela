using System.Diagnostics;
using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class UpdatesPanel : System.Windows.Controls.UserControl
{
    private readonly ManagerUpdateService _service;
    private readonly string _cache;
    private ManagerRelease? _available;
    private string? _executable;
    private CancellationTokenSource? _downloadCancellation;
    private DateTime _lastCheck;
    private string _state = "idle";
    public bool IsBusy { get; private set; }
    public bool HasUpdate { get; private set; }
    public string Summary => Status.Text;
    public event EventHandler? StateChanged;
    public Func<bool>? CanApply { get; set; }
    public UpdatesPanel(ManagerUpdateService service, string cache)
    { _service = service; _cache = cache; InitializeComponent(); RefreshLanguage(); }
    public void RefreshLanguage()
    {
        CurrentVersion.Text = L.F("MultiJanela · versão {0}", typeof(App).Assembly.GetName().Version!.ToString(3));
        if (_state == "checking") Status.Text = L.T("Verificando atualizações...");
        else if (_state == "error") Status.Text = L.T("Não foi possível verificar as atualizações agora.");
        else if (_state == "checked") Status.Text = HasUpdate && _available is not null ? L.F("Nova versão: {0} · {1} MB", _available.Version, (_available.Bytes / 1048576d).ToString("0.0")) : L.T("Você já usa esta versão ou uma versão local mais recente.");
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task CheckForUpdatesAsync()
    {
        if (IsBusy || DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(30)) return;
        _lastCheck = DateTime.UtcNow; IsBusy = true; _state = "checking"; Download.IsEnabled = false; RefreshLanguage();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var found = await _service.CheckAsync(timeout.Token);
            if (_available?.Sha256 != found.Sha256) { _executable = null; Open.IsEnabled = false; }
            _available = found;
            var current = typeof(App).Assembly.GetName().Version!;
            HasUpdate = Version.Parse(found.Version) > new Version(current.Major, current.Minor, current.Build);
            _state = "checked";
        }
        catch (Exception) { _state = "error"; }
        finally { IsBusy = false; Download.IsEnabled = HasUpdate; RefreshLanguage(); }
    }
    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync();
    private async void Download_Click(object sender, RoutedEventArgs e) => await DownloadAndApplyAsync();
    public async Task DownloadAndApplyAsync()
    {
        if (IsBusy || !HasUpdate || _available is null) return; IsBusy = true; Download.IsEnabled = false; StateChanged?.Invoke(this, EventArgs.Empty);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10)); _downloadCancellation = cancellation; CancelDownload.IsEnabled = true;
        try
        {
            _executable = await _service.DownloadAsync(_available, _cache, new Progress<double>(value => { Progress.Value = value; Status.Text = L.F("Baixando atualização: {0}%", (value * 100).ToString("0")); StateChanged?.Invoke(this, EventArgs.Empty); }), cancellation.Token);
            Open.IsEnabled = true; Status.Text = L.T("Download verificado. Abrindo a atualização e transferindo sua sessão...");
            if (CanApply?.Invoke() == false) { Status.Text = L.T("Atualização pronta. Conclua ou salve a operação atual e clique em atualizar novamente."); return; }
            var installedRoot = System.IO.Path.GetFullPath(UserInstallationService.DefaultRoot) + System.IO.Path.DirectorySeparatorChar;
            var target = _executable;
            if (Environment.ProcessPath?.StartsWith(installedRoot, StringComparison.OrdinalIgnoreCase) == true)
                target = await new UserInstallationService().InstallAsync(System.IO.Path.GetDirectoryName(_executable)!, false);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = false });
        }
        catch (OperationCanceledException) { Status.Text = L.T("Operação cancelada."); }
        catch (Exception) { Status.Text = L.T("O download não pôde ser concluído ou validado. A versão atual foi preservada."); }
        finally { IsBusy = false; Download.IsEnabled = HasUpdate; _downloadCancellation = null; CancelDownload.IsEnabled = false; StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();
    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_executable is null || _available is null || IsBusy) return; IsBusy = true; StateChanged?.Invoke(this, EventArgs.Empty);
        try { _executable = await _service.DownloadAsync(_available, _cache); Process.Start(new ProcessStartInfo(_executable) { UseShellExecute = false }); }
        catch (Exception) { Status.Text = L.T("Não foi possível abrir o aplicativo. Verifique os arquivos e tente novamente."); }
        finally { IsBusy = false; StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        try { var process = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }; process.ArgumentList.Add("--install"); Process.Start(process); }
        catch (Exception) { Status.Text = L.T("Não foi possível abrir o aplicativo. Verifique os arquivos e tente novamente."); }
    }
}
