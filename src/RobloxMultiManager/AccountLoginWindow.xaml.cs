using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class AccountLoginWindow : Window
{
    private const string LoginUrl = "https://www.roblox.com/login";
    private const string AuthenticatedUserUrl =
        "https://users.roblox.com/v1/users/authenticated";
    private readonly AccountProfile _profile;
    private readonly AppPaths _paths;
    private bool _identityProbePending;
    private readonly List<LoginPopupWindow> _popups = [];
    private readonly Queue<string> _diagnostics = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CoreWebView2Environment? _environment;
    private bool _closing;

    public AccountLoginWindow(AccountProfile profile, AppPaths paths)
    {
        _profile = profile;
        _paths = paths;
        InitializeComponent();
        TitleText.Text = RobloxMultiManager.Core.L.F("Entrar em “{0}”",profile.Name);
        Loaded += AccountLoginWindow_Loaded;
        Closed += (_, _) =>
        {
            _closing = true;
            _lifetime.Cancel();
            foreach (var popup in _popups.ToArray()) popup.Close();
            IdentityBrowser.Dispose();
            Browser.Dispose();
            _lifetime.Dispose();
        };
    }

    public AccountIdentity? Result { get; private set; }
    public string? SafeFailureCode { get; private set; }

    private async void AccountLoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var profilePath = _paths.BrowserProfilePath(_profile);
            Directory.CreateDirectory(profilePath);
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profilePath);
            await Browser.EnsureCoreWebView2Async(_environment);
            if (_closing) return;

            ConfigureLoginBrowser(Browser.CoreWebView2);
            RecordDiagnostic("WebView2 " + _environment.BrowserVersionString);
            Browser.CoreWebView2.WebResourceResponseReceived += (_, response) =>
            {
                if (LoginBrowserPolicy.DescribeHttpFailure(response.Request.Uri, response.Response.StatusCode) is { } failure)
                    RecordDiagnostic(failure);
            };
            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.NewWindowRequested += CoreWebView2_NewWindowRequested;
            Browser.CoreWebView2.ProcessFailed += (_, _) =>
            {
                if (!_closing)
                {
                    Dispatcher.BeginInvoke(() =>
                        StatusText.Text = RobloxMultiManager.Core.L.T("O navegador do login parou. Feche esta janela e tente novamente."));
                }
            };

            DetectButton.IsEnabled = true;
            ReloadButton.IsEnabled = true;
            StatusText.Text = RobloxMultiManager.Core.L.T("Faça login e depois clique em Concluir login.");
            Browser.CoreWebView2.Navigate(LoginUrl);
        }
        catch (Exception ex)
        {
            RecordDiagnostic("browser-start-failed · " + ex.GetType().Name);
            StatusText.Text = L.T("WebView2 não pôde iniciar. Verifique a instalação do Microsoft Edge WebView2.");
            DetectButton.IsEnabled = false;
        }
    }

    private void CoreWebView2_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAllowedRobloxUri(e.Uri))
        {
            e.Cancel = true;
            StatusText.Text = RobloxMultiManager.Core.L.T("Navegação externa bloqueada. Use somente páginas oficiais roblox.com.");
            return;
        }

        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            HostText.Text = uri.Host;
        }
    }

    private void CoreWebView2_NavigationCompleted(
        object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_closing || Browser.Source is null) return;
        HostText.Text = Browser.Source.Host;
        if (!e.IsSuccess)
        {
            SafeFailureCode = "login-navigation-failed";
            RecordDiagnostic("navigation · " + e.WebErrorStatus);
            StatusText.Text = L.F("A página não carregou ({0}). Use Recarregar ou teste no navegador.", e.WebErrorStatus);
        }
    }

    private async void CoreWebView2_NewWindowRequested(
        object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (_closing || _environment is null) return;
        if (!LoginBrowserPolicy.IsLoginPopup(e.Uri) || _popups.Count >= 4)
        {
            RecordDiagnostic("popup-blocked");
            StatusText.Text = L.T("Uma janela externa não oficial foi bloqueada.");
            return;
        }

        using var deferral = e.GetDeferral();
        var popup = new LoginPopupWindow(this);
        _popups.Add(popup);
        popup.Closed += (_, _) => _popups.Remove(popup);
        try
        {
            popup.Show();
            var core = await popup.InitializeAsync(_environment, CoreWebView2_NewWindowRequested, RecordDiagnostic);
            if (!_closing && core is not null) e.NewWindow = core;
        }
        catch (Exception ex)
        {
            RecordDiagnostic("popup-start-failed · " + ex.GetType().Name);
            if (!_closing)
            {
                popup.Close();
                StatusText.Text = L.T("A janela de verificação não pôde abrir. Consulte o diagnóstico abaixo.");
            }
        }
    }

    internal static void ConfigureLoginBrowser(CoreWebView2 core)
    {
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closing || Browser.CoreWebView2 is null || _environment is null)
        {
            SafeFailureCode = "browser-not-ready";
            StatusText.Text = L.T("O navegador ainda não está pronto. Aguarde e tente novamente.");
            return;
        }
        if (_identityProbePending) return;
        _identityProbePending = true;
        SafeFailureCode = null;
        DetectButton.IsEnabled = false;
        StatusText.Text = L.T("Confirmando a conta conectada...");
        try
        {
            // The verification page stays exactly where the user left it. A
            // separate view on the SAME profile checks identity only on request.
            await IdentityBrowser.EnsureCoreWebView2Async(_environment);
            if (_closing) return;
            var core = IdentityBrowser.CoreWebView2;
            ConfigureLoginBrowser(core);
            var response = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<CoreWebView2NavigationStartingEventArgs> guard = (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri)
                    || !IsAuthenticatedUserEndpoint(uri))
                {
                    args.Cancel = true;
                    response.TrySetResult(null);
                }
            };
            EventHandler<CoreWebView2NavigationCompletedEventArgs> completed = async (_, args) =>
            {
                if (_closing || !args.IsSuccess)
                {
                    response.TrySetResult(null);
                    return;
                }
                try
                {
                    var encoded = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                    response.TrySetResult(JsonSerializer.Deserialize<string>(encoded));
                }
                catch (Exception ex) { response.TrySetException(ex); }
            };
            core.NavigationStarting += guard;
            core.NavigationCompleted += completed;
            string? json;
            try
            {
                core.Navigate(AuthenticatedUserUrl);
                json = await response.Task.WaitAsync(TimeSpan.FromSeconds(12), _lifetime.Token);
            }
            finally
            {
                if (!_closing)
                {
                    core.NavigationStarting -= guard;
                    core.NavigationCompleted -= completed;
                    core.Stop();
                }
            }
            if (_closing) return;
            if (!TryParseIdentity(json, out var identity))
            {
                SafeFailureCode = "not-authenticated";
                StatusText.Text = L.T("A sessão ainda não foi reconhecida. Termine o login ou a verificação na página e tente Concluir login novamente.");
                return;
            }
            Result = identity;
            StatusText.Text = L.F("Conta detectada: {0} (@{1}).", identity.DisplayName, identity.Username);
            DialogResult = true;
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (TimeoutException)
        {
            SafeFailureCode = "identity-timeout";
            StatusText.Text = L.T("A confirmação demorou mais de 12 segundos. A página foi preservada; tente novamente depois de concluir a verificação.");
        }
        catch (Exception ex)
        {
            if (_closing) return;
            SafeFailureCode = "identity-read-failed";
            RecordDiagnostic("identity-probe · " + ex.GetType().Name);
            StatusText.Text = L.T("Não foi possível confirmar a sessão. A página foi preservada; consulte o diagnóstico.");
        }
        finally
        {
            _identityProbePending = false;
            if (!_closing) DetectButton.IsEnabled = true;
        }
    }

    private void RecordDiagnostic(string value)
    {
        if (_closing) return;
        if (_diagnostics.LastOrDefault() == value) return;
        _diagnostics.Enqueue(value);
        while (_diagnostics.Count > 10) _diagnostics.Dequeue();
        DiagnosticText.Text = string.Join(Environment.NewLine, _diagnostics);
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2 is null || _identityProbePending) return;
        Browser.Reload(); // Keep this account's cookies and storage intact.
    }

    private void ExternalBrowserButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(LoginUrl) { UseShellExecute = true });
            StatusText.Text = L.T("Teste o login no navegador normal. Essa sessão é separada e não é importada para o MultiJanela.");
        }
        catch (Exception ex) { RecordDiagnostic("external-browser · " + ex.GetType().Name); }
    }

    private void CopyDiagnosticButton_Click(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(DiagnosticText.Text); }
        catch (Exception ex) { RecordDiagnostic("clipboard · " + ex.GetType().Name); }
    }

    internal static bool TryParseIdentity(string? json, out AccountIdentity identity)
    {
        identity = new AccountIdentity(0, string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("authenticated", out var authenticated)
                && authenticated.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            if (!root.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.Number
                || !idElement.TryGetInt64(out var userId)
                || userId <= 0
                || !root.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var username = nameElement.GetString()?.Trim();
            var displayName = root.TryGetProperty("displayName", out var displayElement)
                              && displayElement.ValueKind == JsonValueKind.String
                ? displayElement.GetString()?.Trim()
                : username;
            if (string.IsNullOrWhiteSpace(username)
                || username.Length > 40
                || string.IsNullOrWhiteSpace(displayName)
                || displayName.Length > 80)
            {
                return false;
            }

            identity = new AccountIdentity(userId, username, displayName);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsAllowedRobloxUri(string? value) => LoginBrowserPolicy.IsRobloxPage(value);

    private static bool IsAuthenticatedUserEndpoint(Uri uri) =>
        uri.Host.Equals("users.roblox.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.TrimEnd('/').Equals(
            "/v1/users/authenticated",
            StringComparison.OrdinalIgnoreCase);

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SafeFailureCode ??= "cancelled";
        DialogResult = false;
    }
}
