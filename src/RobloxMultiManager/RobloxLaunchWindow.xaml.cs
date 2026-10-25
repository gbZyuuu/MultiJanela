using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class RobloxLaunchWindow : Window
{
    private const string RobloxHomeUrl = "https://www.roblox.com/home";
    private const string AuthenticatedUserUrl =
        "https://users.roblox.com/v1/users/authenticated";
    private const string ClientAssertionUrl =
        "https://auth.roblox.com/v1/client-assertion/";
    private const string AuthenticationTicketUrl =
        "https://auth.roblox.com/v1/authentication-ticket/";

    /// <summary>
    /// Resolves the newer <c>/share?code=...&amp;type=Server</c> invite format.
    /// </summary>
    /// <remarks>
    /// A share link carries neither a place id nor a private-server link code, so
    /// it cannot build a launcher URL on its own.  The previous approach navigated
    /// the protected session to the share page and waited for Roblox to redirect
    /// to a concrete game URL; that redirect leaves <c>*.roblox.com</c>, the origin
    /// guard cancelled it, and the launch died with "a autorização tentou sair do
    /// domínio oficial".  Resolving through the API keeps the session on one
    /// trusted page.  Measured: the trailing-slash form of this path returns 404,
    /// and the bare form answers 403 with an <c>x-csrf-token</c> header, the same
    /// challenge the ticket endpoint uses.
    /// </remarks>
    private const string ShareLinkResolveUrl =
        "https://apis.roblox.com/sharelinks/v1/resolve-link";
    private const string MessageSource = "RobloxMultiManager.TicketResult";
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(30);

    private readonly AccountProfile _profile;
    private readonly AppPaths _paths;
    private readonly RobloxInstallationInfo _installation;
    private readonly Guid _attemptId;
    private readonly string? _launchChannel;
    private RobloxLaunchTarget? _launchTarget;
    private readonly string _messageNonce = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Completion signal used instead of <c>DialogResult</c>.
    /// </summary>
    /// <remarks>
    /// This window used to be shown with <c>ShowDialog()</c>, which blocked the
    /// whole UI on a nested dispatcher frame and made exactly one launch possible
    /// at a time.  <c>DialogResult</c> throws when the window was shown with
    /// <c>Show()</c>, so the result is reported through this instead and the caller
    /// awaits <see cref="Completion"/>.
    /// </remarks>
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _requestGeneration;
    private ulong? _homeNavigationId;
    private bool _homeNavigationPending;
    private bool _messageHandled;
    private bool _terminal;
    private bool _closing;

    public RobloxLaunchWindow(
        AccountProfile profile,
        AppPaths paths,
        RobloxInstallationInfo installation,
        Guid attemptId,
        string? launchChannel,
        RobloxLaunchTarget? launchTarget = null)
    {
        _profile = profile.EnsureBrowserTrackerId();
        _paths = paths;
        _installation = installation;
        _attemptId = attemptId;
        _launchChannel = launchChannel;
        _launchTarget = launchTarget;
        InitializeComponent();
        DataContext = profile;
        TitleText.Text = RobloxMultiManager.Core.L.F("Abrindo Roblox como @{0}",DisplayPrivacy.Enabled ? profile.PublicName : profile.RobloxUsername ?? profile.Name);
        Loaded += RobloxLaunchWindow_Loaded;
        Closed += (_, _) =>
        {
            _closing = true;
            Browser.Dispose();
            // Closing by the title bar has to release the caller too, otherwise a
            // non-modal launch would leave an awaiter hanging forever.
            SafeError ??= RobloxMultiManager.Core.L.T("Fechado pelo usuário");
            _completion.TrySetResult(false);
        };
    }

    public string? SafeError { get; private set; }
    public string? SafeDetail { get; private set; }
    public RobloxLaunchReceipt? LaunchReceipt { get; private set; }

    /// <summary>
    /// Completes when the launch has succeeded, failed or been closed.
    /// </summary>
    public Task<bool> Completion => _completion.Task;

    /// <summary>
    /// The share code still waiting to be resolved into a place id, if any.
    /// </summary>
    private string? PendingShareServerCode =>
        _launchTarget is
        {
            Kind: RobloxLaunchTargetKind.PrivateServer,
            PlaceId: null
        }
            ? _launchTarget.PrivateServerCode
            : null;

    private async void RobloxLaunchWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_profile.RobloxUserId is null)
        {
            Fail(RobloxMultiManager.Core.L.T("Sessão ausente"), RobloxMultiManager.Core.L.T("Faça login nesta conta antes de abrir o Roblox."));
            return;
        }

        if (_installation.ExecutablePath is null
            || !File.Exists(_installation.ExecutablePath)
            || !Path.GetFileName(_installation.ExecutablePath).Equals(
                "RobloxPlayerBeta.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            Fail(
                RobloxMultiManager.Core.L.T("Cliente ausente"),
                RobloxMultiManager.Core.L.T("O executável oficial selecionado não está mais disponível."));
            return;
        }

        StatusText.Text = RobloxMultiManager.Core.L.T("Iniciando a sessão protegida...");
        var generation = ++_requestGeneration;
        _ = MonitorTimeoutAsync(generation);
        try
        {
            var profilePath = _paths.BrowserProfilePath(_profile);
            Directory.CreateDirectory(profilePath);
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profilePath);
            await Browser.EnsureCoreWebView2Async(environment);
            if (_terminal || _closing)
            {
                return;
            }

            Browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            Browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            Browser.CoreWebView2.Settings.IsWebMessageEnabled = true;
            Browser.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            Browser.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
            Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            Browser.CoreWebView2.NewWindowRequested += (_, args) => args.Handled = true;
            Browser.CoreWebView2.ProcessFailed += (_, _) =>
            {
                if (!_closing)
                {
                    Dispatcher.BeginInvoke(() => Fail(
                        RobloxMultiManager.Core.L.T("Navegador interrompido"),
                        RobloxMultiManager.Core.L.T("A sessão protegida parou. Feche esta janela e tente novamente.")));
                }
            };

            StatusText.Text = RobloxMultiManager.Core.L.T("Validando a sessão da conta...");
            _homeNavigationPending = true;
            Browser.CoreWebView2.Navigate(
                ResolveInitialNavigationUrl(_launchTarget, PendingShareServerCode));
        }
        catch
        {
            Fail(
                RobloxMultiManager.Core.L.T("WebView2 indisponível"),
                RobloxMultiManager.Core.L.T("Não foi possível abrir a sessão protegida. Repare o Microsoft Edge WebView2."));
        }
    }

    private void CoreWebView2_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!AccountLoginWindow.IsAllowedRobloxUri(e.Uri))
        {
            e.Cancel = true;
            // Only the host is reported: a blocked URI can carry an invite code or
            // a token, and neither belongs in the UI or the log.
            Fail(
                RobloxMultiManager.Core.L.F("Origem inválida ({0})",DescribeBlockedHost(e.Uri)),
                RobloxMultiManager.Core.L.T("A autorização tentou sair do domínio oficial do Roblox e foi ")
                + RobloxMultiManager.Core.L.F("bloqueada. Destino recusado: {0}.",DescribeBlockedHost(e.Uri)));
            return;
        }

        if (_homeNavigationPending
            && _homeNavigationId is null
            && IsRequestedInitialPage(e.Uri))
        {
            _homeNavigationId = e.NavigationId;
        }
    }

    private async void CoreWebView2_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_terminal
            || _closing
            || !_homeNavigationPending
            || _homeNavigationId is null
            || e.NavigationId != _homeNavigationId)
        {
            return;
        }

        _homeNavigationPending = false;
        if (!e.IsSuccess)
        {
            Fail(
                RobloxMultiManager.Core.L.F("Navegação falhou ({0})",e.WebErrorStatus),
                RobloxMultiManager.Core.L.T("A página inicial oficial não respondeu. Verifique a internet e tente novamente."));
            return;
        }

        if (Browser.Source is null || !IsTrustedLaunchOrigin(Browser.Source.ToString()))
        {
            Fail(
                RobloxMultiManager.Core.L.T("Origem final inesperada"),
                RobloxMultiManager.Core.L.T("A página inicial saiu da origem oficial esperada. Use Login / atualizar e tente novamente."));
            return;
        }

        if (_launchTarget is not null
            && RobloxLinkParser.TryParse(
                Browser.Source.ToString(),
                out var resolvedTarget,
                out _)
            && resolvedTarget.PlaceId is > 0)
        {
            // New share links omit the place id. The official page normally
            // redirects to a concrete game URL; preserve the invite code while
            // adopting that resolved place id.
            if (_launchTarget.PrivateServerCode is { } privateCode
                && resolvedTarget.PrivateServerCode is null)
            {
                resolvedTarget = new RobloxLaunchTarget(
                    RobloxLaunchTargetKind.PrivateServer,
                    resolvedTarget.PlaceId,
                    $"https://www.roblox.com/games/start?placeId="
                    + $"{resolvedTarget.PlaceId.Value}&privateServerLinkCode="
                    + Uri.EscapeDataString(privateCode),
                    privateCode);
            }

            _launchTarget = resolvedTarget;
        }

        SetLaunchPhase(2);
        StatusText.Text = RobloxMultiManager.Core.L.T("Criando autorização temporária...");
        try
        {
            // This script returns synchronously after starting the async work.
            // The result arrives through WebMessageReceived instead of relying
            // on ExecuteScriptAsync to serialize a Promise.
            await Browser.CoreWebView2.ExecuteScriptAsync(
                BuildTicketFetchScript(
                    _messageNonce,
                    _profile.RobloxUserId!.Value,
                    PendingShareServerCode));
        }
        catch
        {
            Fail(
                RobloxMultiManager.Core.L.T("Falha ao iniciar autorização"),
                RobloxMultiManager.Core.L.T("A página oficial não conseguiu iniciar a autorização temporária."));
        }
    }

    private async void CoreWebView2_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_terminal
            || _closing
            || _messageHandled
            || !IsTrustedLaunchOrigin(e.Source)
            || Browser.Source is null
            || !IsTrustedLaunchOrigin(Browser.Source.ToString())
            || !TryParseTicketMessage(
                e.WebMessageAsJson,
                _messageNonce,
                _profile.RobloxUserId!.Value,
                out var result))
        {
            return;
        }

        _messageHandled = true;

        // A share invite is resolved before the ticket is evaluated, so an expired
        // or revoked link is reported as such instead of as an authorization
        // problem, and no one-time ticket is spent on it.
        if (PendingShareServerCode is not null)
        {
            var share = EvaluateShareResolution(
                result.ShareStatus,
                result.SharePlaceId,
                result.ShareLinkCode);
            if (!share.Ok)
            {
                Fail(share.Code, share.Message);
                return;
            }

            _launchTarget = new RobloxLaunchTarget(
                RobloxLaunchTargetKind.PrivateServer,
                share.PlaceId,
                "https://www.roblox.com/games/start?placeId="
                + share.PlaceId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + "&privateServerLinkCode="
                + Uri.EscapeDataString(share.LinkCode!),
                share.LinkCode);
        }

        var decision = EvaluateTicketFetchResponse(
            result.SessionState,
            result.StatusCode,
            result.Ticket);
        if (decision.Action == TicketFetchAction.LaunchPlayer)
        {
            await LaunchPlayerAsync(decision.Ticket!);
            return;
        }

        Fail(
            decision.ErrorCode ?? RobloxMultiManager.Core.L.T("Autorização recusada"),
            decision.UserMessage
            ?? RobloxMultiManager.Core.L.T("A sessão não autorizou a abertura. Faça Login / atualizar novamente."));
    }

    private async Task LaunchPlayerAsync(string ticket)
    {
        // The ticket has already been granted at this point, so nothing below is
        // a Roblox refusal.  The previous version wrapped this whole method in a
        // bare catch that reported "o Roblox não aceitou a solicitação" for every
        // local failure, which sent every diagnosis in the wrong direction.
        var startedPath = Path.GetFullPath(_installation.ExecutablePath!);
        Process process;
        try
        {
            var launchUri = _launchTarget is null
                ? BuildHomeLaunchUri(
                    ticket,
                    _profile.BrowserTrackerId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    _launchChannel)
                : BuildGameLaunchUri(
                    ticket,
                    _profile.BrowserTrackerId,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    _launchTarget,
                    _launchChannel);
            var start = new ProcessStartInfo
            {
                FileName = startedPath,
                UseShellExecute = false
            };
            start.ArgumentList.Add(launchUri);
            process = Process.Start(start)
                      ?? throw new InvalidOperationException(
                          RobloxMultiManager.Core.L.T("Process.Start não retornou um processo."));
        }
        catch (Exception ex)
        {
            Fail(
                RobloxMultiManager.Core.L.F("Cliente não iniciou ({0})",ex.GetType().Name),
                RobloxMultiManager.Core.L.T("O Windows não conseguiu iniciar o executável selecionado.\n\n")
                + Describe(ex, ticket));
            return;
        }

        try
        {
            var startedUtc = ReadStartTimeUtc(process);

            // The path handed to Process.Start is the authority.  Reading
            // MainModule immediately after start races the loader: it throws
            // Win32Exception ("Only part of a ReadProcessMemory ... request was
            // completed") while the process is still initializing, and
            // InvalidOperationException once it exits.  Letting that decide the
            // launch turned a working start into a reported Roblox refusal.
            var observedPath = TryReadMainModulePath(process);
            if (!RobloxClientExecutableInspector.TryInspect(
                    observedPath ?? startedPath,
                    out var actualClient)
                && !RobloxClientExecutableInspector.TryInspect(
                    startedPath,
                    out actualClient))
            {
                Fail(
                    RobloxMultiManager.Core.L.T("Executável não verificável"),
                    RobloxMultiManager.Core.L.T("O cliente iniciou, mas o caminho não passou na verificação:\n")
                    + $"{observedPath ?? startedPath}\n\n"
                    + RobloxMultiManager.Core.L.T("Uma versão só é aceita dentro de ")
                    + RobloxMultiManager.Core.L.T("%LOCALAPPDATA%\\Roblox\\Versions ou ")
                    + @"%LOCALAPPDATA%\RobloxMultiManager\Versions.");
                TryKill(process);
                process.Dispose();
                return;
            }

            LaunchReceipt = new RobloxLaunchReceipt(
                _attemptId,
                _profile.Id,
                process.Id,
                startedUtc,
                startedPath,
                _installation.VersionId!,
                _installation.Version,
                _launchChannel,
                actualClient);
            if (!LaunchReceipt.InitialClientMatches)
            {
                Fail(
                    RobloxMultiManager.Core.L.T("Executável inicial divergente"),
                    RobloxMultiManager.Core.L.T("O processo iniciado não corresponde à versão selecionada. ")
                    + RobloxMultiManager.Core.L.T("A abertura não será confirmada."));
                process.Dispose();
                return;
            }

            // The authorization is complete; the network timeout must not win
            // a race while the native client is starting.
            ++_requestGeneration;
            SetLaunchPhase(3);
            StatusText.Text = RobloxMultiManager.Core.L.T("Solicitação enviada ao Roblox. Aguardando a janela...");
            await Task.Delay(800);
            if (process.HasExited)
            {
                // This is the one case that really does mean the client refused,
                // and it is worth naming, because the usual cause is another
                // instance or another multi-instance manager owning the lock.
                var exitCode = TryReadExitCode(process);
                Fail(
                    RobloxMultiManager.Core.L.F("Cliente encerrou sozinho (código {0})",exitCode),
                    RobloxMultiManager.Core.L.T("O Roblox iniciou e fechou sozinho antes de abrir a janela")
                    + RobloxMultiManager.Core.L.F(" (código {0}).\n\n",exitCode)
                    + RobloxMultiManager.Core.L.T("Normalmente é o próprio cliente recusando uma segunda ")
                    + RobloxMultiManager.Core.L.T("instância: confirme que a trava está ativa e que nenhum ")
                    + RobloxMultiManager.Core.L.T("outro gerenciador (Bloxstrap, Fishstrap, Roblox Account ")
                    + RobloxMultiManager.Core.L.T("Manager) está aberto."));
                process.Dispose();
                return;
            }

            _terminal = true;
            ++_requestGeneration;
            process.Dispose();
            CompleteAndClose(true);
        }
        catch (Exception ex)
        {
            Fail(
                RobloxMultiManager.Core.L.F("Falha após iniciar o cliente ({0})",ex.GetType().Name),
                RobloxMultiManager.Core.L.T("O cliente foi iniciado, mas a verificação seguinte falhou.\n\n")
                + Describe(ex, ticket));
            process.Dispose();
        }
    }

    private static DateTime ReadStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // A process that exits immediately no longer reports StartTime, and
            // the launch identity only needs a lower bound here.
            return DateTime.UtcNow;
        }
    }

    private static string? TryReadMainModulePath(Process process)
    {
        // Three quick attempts: the module table is not readable during the first
        // moments of a large client's startup.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }

            Thread.Sleep(120);
        }

        return null;
    }

    private static string TryReadExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return RobloxMultiManager.Core.L.T("desconhecido");
        }
    }

    /// <summary>
    /// Renders an exception for display, with the temporary ticket stripped.
    /// </summary>
    /// <remarks>
    /// The launch URI carries the ticket, so an exception raised while handling it
    /// could otherwise surface that value in the UI or in the activity log, which
    /// the app promises never to record.
    /// </remarks>
    private static string Describe(Exception exception, string ticket)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        if (exception.InnerException is not null)
        {
            text += $" -> {exception.InnerException.GetType().Name}: "
                    + exception.InnerException.Message;
        }

        return ticket.Length >= 8
            ? text.Replace(ticket, "<ticket>", StringComparison.Ordinal)
            : text;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // A client that cannot be stopped is reported by the divergence
            // checks in the main window instead.
        }
    }

    private async Task MonitorTimeoutAsync(int generation)
    {
        await Task.Delay(AuthorizationTimeout);
        if (_closing || _terminal || generation != _requestGeneration)
        {
            return;
        }

        Fail(
            RobloxMultiManager.Core.L.T("Tempo esgotado"),
            RobloxMultiManager.Core.L.F("A autorização demorou mais de {0} segundos. ",(int)AuthorizationTimeout.TotalSeconds)
            + RobloxMultiManager.Core.L.T("Confirme o login e tente novamente."));
    }

    internal static string BuildTicketFetchScript(
        string nonce,
        long expectedUserId,
        string? shareServerCode = null)
    {
        if (!IsSafeNonce(nonce))
        {
            throw new ArgumentException(RobloxMultiManager.Core.L.T("Nonce inválido."), nameof(nonce));
        }

        if (expectedUserId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedUserId));
        }

        if (shareServerCode is not null && !IsSafeShareCode(shareServerCode))
        {
            throw new ArgumentException(
                RobloxMultiManager.Core.L.T("Código de convite inválido."),
                nameof(shareServerCode));
        }

        var nonceJson = JsonSerializer.Serialize(nonce);
        var sourceJson = JsonSerializer.Serialize(MessageSource);
        var expectedUserIdJson = JsonSerializer.Serialize(
            expectedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var authenticatedUserUrlJson = JsonSerializer.Serialize(AuthenticatedUserUrl);
        var assertionUrlJson = JsonSerializer.Serialize(ClientAssertionUrl);
        var ticketUrlJson = JsonSerializer.Serialize(AuthenticationTicketUrl);
        var shareUrlJson = JsonSerializer.Serialize(ShareLinkResolveUrl);
        var shareCodeJson = shareServerCode is null
            ? "null"
            : JsonSerializer.Serialize(shareServerCode);
        return $$"""
            (() => {
                const messageNonce = {{nonceJson}};
                const messageSource = {{sourceJson}};
                const expectedUserId = {{expectedUserIdJson}};
                const authenticatedUserUrl = {{authenticatedUserUrlJson}};
                const assertionUrl = {{assertionUrlJson}};
                const ticketUrl = {{ticketUrlJson}};
                const shareResolveUrl = {{shareUrlJson}};
                const shareCode = {{shareCodeJson}};
                let shareStatus = null;
                let sharePlaceId = null;
                let shareLinkCode = null;
                const sendResult = value => window.chrome.webview.postMessage({
                    ...value,
                    shareStatus,
                    sharePlaceId,
                    shareLinkCode,
                    source: messageSource,
                    nonce: messageNonce
                });
                const readCachedCsrf = () => {
                    try {
                        return localStorage.getItem("x-csrf-token")
                            || document.querySelector('meta[name="csrf-token"]')
                                ?.dataset?.token
                            || null;
                    } catch {
                        return null;
                    }
                };
                const cacheCsrf = value => {
                    try {
                        localStorage.setItem("x-csrf-token", value);
                    } catch {
                        // O fluxo continua sem o cache local.
                    }
                };
                const resolveShare = async () => {
                    const send = csrf => {
                        const headers = {
                            "Accept": "application/json",
                            "Content-Type": "application/json"
                        };
                        if (csrf) {
                            headers["X-CSRF-TOKEN"] = csrf;
                        }
                        return fetch(shareResolveUrl, {
                            method: "POST",
                            mode: "cors",
                            credentials: "include",
                            cache: "no-store",
                            headers,
                            body: JSON.stringify({
                                linkId: shareCode,
                                linkType: "Server"
                            })
                        });
                    };
                    let response = await send(readCachedCsrf());
                    if (response.status === 403) {
                        const csrf = response.headers.get("x-csrf-token");
                        if (csrf) {
                            cacheCsrf(csrf);
                            response = await send(csrf);
                        }
                    }
                    if (!response.ok) {
                        shareStatus = "http-" + response.status;
                        return;
                    }
                    const data = await response.json();
                    const invite = data?.privateServerInviteData;
                    if (invite == null) {
                        shareStatus = "missing";
                        return;
                    }
                    shareStatus = invite.status == null
                        ? "unknown"
                        : String(invite.status);
                    if (invite.placeId != null) {
                        sharePlaceId = String(invite.placeId);
                    }
                    if (invite.linkCode != null) {
                        shareLinkCode = String(invite.linkCode);
                    }
                };
                const requestTicket = async (assertion, csrf) => {
                    const headers = {
                        "Accept": "application/json",
                        "Content-Type": "application/json"
                    };
                    if (csrf) {
                        headers["X-CSRF-TOKEN"] = csrf;
                    }
                    return fetch(ticketUrl, {
                        method: "POST",
                        mode: "cors",
                        credentials: "include",
                        headers,
                        body: JSON.stringify(assertion ?? {})
                    });
                };
                (async () => {
                    let authenticatedUserId = null;
                    try {
                        const identityResponse = await fetch(authenticatedUserUrl, {
                            method: "GET",
                            mode: "cors",
                            credentials: "include",
                            cache: "no-store",
                            headers: { "Accept": "application/json" }
                        });
                        if (!identityResponse.ok) {
                            sendResult({
                                kind: "ticket-result",
                                session: "not-authenticated",
                                authenticatedUserId: null,
                                status: identityResponse.status,
                                ticket: null
                            });
                            return;
                        }

                        const identity = await identityResponse.json();
                        authenticatedUserId = identity?.id == null
                            ? null
                            : String(identity.id);
                        if (!authenticatedUserId) {
                            sendResult({
                                kind: "ticket-result",
                                session: "not-authenticated",
                                authenticatedUserId: null,
                                status: 422,
                                ticket: null
                            });
                            return;
                        }
                        if (authenticatedUserId !== expectedUserId) {
                            sendResult({
                                kind: "ticket-result",
                                session: "account-mismatch",
                                authenticatedUserId,
                                status: 409,
                                ticket: null
                            });
                            return;
                        }
                    } catch {
                        sendResult({
                            kind: "ticket-result",
                            session: "not-authenticated",
                            authenticatedUserId: null,
                            status: 0,
                            ticket: null
                        });
                        return;
                    }

                    // Resolved before the ticket is requested: a bad invite must
                    // not consume a one-time authorization.
                    if (shareCode) {
                        try {
                            await resolveShare();
                        } catch {
                            shareStatus = "network";
                        }
                        if (shareStatus !== "Valid") {
                            sendResult({
                                kind: "ticket-result",
                                session: "verified",
                                authenticatedUserId,
                                status: 0,
                                ticket: null
                            });
                            return;
                        }
                    }

                    let assertion = {};
                    try {
                        const assertionResponse = await fetch(assertionUrl, {
                            method: "GET",
                            mode: "cors",
                            credentials: "include",
                            headers: { "Accept": "application/json" }
                        });
                        if (assertionResponse.ok) {
                            assertion = (await assertionResponse.json()) ?? {};
                        }
                    } catch {
                        assertion = {};
                    }

                    try {
                        const initialCsrf =
                            localStorage.getItem("x-csrf-token")
                            || document.querySelector('meta[name="csrf-token"]')
                                ?.dataset?.token
                            || null;
                        let response = await requestTicket(assertion, initialCsrf);
                        if (response.status === 403) {
                            const csrf = response.headers.get("x-csrf-token");
                            if (csrf) {
                                try {
                                    localStorage.setItem("x-csrf-token", csrf);
                                } catch {
                                    // A autorização ainda pode continuar sem cache local.
                                }
                                response = await requestTicket(assertion, csrf);
                            }
                        }
                        sendResult({
                            kind: "ticket-result",
                            session: "verified",
                            authenticatedUserId,
                            status: response.status,
                            ticket: response.headers.get("rbx-authentication-ticket") || null
                        });
                    } catch {
                        sendResult({
                            kind: "ticket-result",
                            session: "verified",
                            authenticatedUserId,
                            status: 0,
                            ticket: null
                        });
                    }
                })();
                return true;
            })();
            """;
    }

    internal static bool TryParseTicketMessage(
        string? json,
        string expectedNonce,
        long expectedUserId,
        out TicketFetchResult result)
    {
        result = new TicketFetchResult(string.Empty, 0, null);
        if (!IsSafeNonce(expectedNonce)
            || expectedUserId <= 0
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.String
                || !string.Equals(
                    source.GetString(),
                    MessageSource,
                    StringComparison.Ordinal)
                || !root.TryGetProperty("nonce", out var nonce)
                || nonce.ValueKind != JsonValueKind.String
                || !string.Equals(
                    nonce.GetString(),
                    expectedNonce,
                    StringComparison.Ordinal)
                || !root.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String
                || !string.Equals(
                    kind.GetString(),
                    "ticket-result",
                    StringComparison.Ordinal)
                || !root.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.Number
                || !status.TryGetInt32(out var statusCode)
                || statusCode is < 0 or > 599
                || !root.TryGetProperty("session", out var session)
                || session.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var sessionState = session.GetString();
            if (sessionState is not ("verified"
                or "not-authenticated"
                or "account-mismatch"))
            {
                return false;
            }

            string? authenticatedUserId = null;
            if (root.TryGetProperty(
                    "authenticatedUserId",
                    out var authenticatedUserIdElement)
                && authenticatedUserIdElement.ValueKind == JsonValueKind.String)
            {
                authenticatedUserId = authenticatedUserIdElement.GetString();
            }

            var expectedUserIdText = expectedUserId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (sessionState == "verified"
                && !string.Equals(
                    authenticatedUserId,
                    expectedUserIdText,
                    StringComparison.Ordinal))
            {
                return false;
            }

            string? ticket = null;
            if (root.TryGetProperty("ticket", out var ticketElement)
                && ticketElement.ValueKind == JsonValueKind.String)
            {
                ticket = ticketElement.GetString();
            }

            result = new TicketFetchResult(
                sessionState,
                statusCode,
                ticket,
                ReadOptionalString(root, "shareStatus"),
                ReadOptionalString(root, "sharePlaceId"),
                ReadOptionalString(root, "shareLinkCode"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Names a rejected navigation target by scheme and host only.
    /// </summary>
    internal static string DescribeBlockedHost(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RobloxMultiManager.Core.L.T("destino vazio");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return RobloxMultiManager.Core.L.T("destino não reconhecido");
        }

        return uri.IsAbsoluteUri && !uri.IsFile && uri.Host.Length > 0
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}:";
    }

    private static string? ReadOptionalString(
        JsonElement root,
        string propertyName) =>
        root.TryGetProperty(propertyName, out var element)
        && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    /// <summary>
    /// Turns the share-link resolution into a launchable place id plus link code,
    /// or into a reason the invite cannot be used.
    /// </summary>
    /// <remarks>
    /// Roblox reports invite state in the body, not the HTTP status: a resolved
    /// call returns 200 with <c>privateServerInviteData.status</c> set to
    /// <c>Valid</c>, <c>Expired</c> or <c>Invalid</c>.  An unrecognised state is
    /// reported verbatim rather than guessed at, because Roblox can add states.
    /// </remarks>
    internal static ShareResolution EvaluateShareResolution(
        string? status,
        string? placeIdText,
        string? linkCode)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Convite não resolvido"),
                RobloxMultiManager.Core.L.T("O Roblox não respondeu sobre este convite. Tente novamente."));
        }

        if (status.Equals("Expired", StringComparison.OrdinalIgnoreCase))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Convite expirado"),
                RobloxMultiManager.Core.L.T("Este link de servidor privado expirou. Peça um link novo ao dono ")
                + RobloxMultiManager.Core.L.T("do servidor."));
        }

        if (status.Equals("Invalid", StringComparison.OrdinalIgnoreCase))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Convite inválido"),
                RobloxMultiManager.Core.L.T("O Roblox não reconhece este código de convite. Ele pode ter sido ")
                + RobloxMultiManager.Core.L.T("revogado ou o link foi copiado incompleto."));
        }

        if (status.Equals("missing", StringComparison.Ordinal))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Link não é de servidor privado"),
                RobloxMultiManager.Core.L.T("Este link compartilhado não aponta para um servidor privado."));
        }

        if (status.Equals("network", StringComparison.Ordinal))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Falha de rede ao resolver o convite"),
                RobloxMultiManager.Core.L.T("Não foi possível contatar o serviço de convites do Roblox."));
        }

        if (status.StartsWith("http-", StringComparison.Ordinal))
        {
            var code = status[5..];
            return Unusable(
                RobloxMultiManager.Core.L.F("Convite recusado (HTTP {0})",code),
                RobloxMultiManager.Core.L.F("O Roblox recusou a consulta do convite (HTTP {0}). ",code)
                + RobloxMultiManager.Core.L.T("Use Login / atualizar nesta conta e tente novamente."));
        }

        if (!status.Equals("Valid", StringComparison.OrdinalIgnoreCase))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Estado de convite inesperado"),
                RobloxMultiManager.Core.L.F("O Roblox respondeu com um estado não previsto: {0}.",status));
        }

        if (!long.TryParse(
                placeIdText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var placeId)
            || placeId <= 0)
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Convite sem PlaceId"),
                RobloxMultiManager.Core.L.T("O convite foi aceito, mas não trouxe o jogo correspondente."));
        }

        if (!IsSafeShareCode(linkCode))
        {
            return Unusable(
                RobloxMultiManager.Core.L.T("Convite sem código utilizável"),
                RobloxMultiManager.Core.L.T("O convite foi aceito, mas o código do servidor não pôde ser ")
                + RobloxMultiManager.Core.L.T("validado."));
        }

        return new ShareResolution(
            true,
            placeId,
            linkCode,
            string.Empty,
            string.Empty);

        static ShareResolution Unusable(string code, string message) =>
            new(false, 0, null, code, message);
    }

    internal static bool IsSafeShareCode(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && value.All(character =>
            char.IsAsciiLetterOrDigit(character)
            || character is '-' or '_' or '.' or '~');

    internal static TicketFetchDecision EvaluateTicketFetchResponse(
        string sessionState,
        int statusCode,
        string? ticket)
    {
        if (sessionState == "account-mismatch")
        {
            return TicketFetchDecision.Fail(
                RobloxMultiManager.Core.L.T("Conta diferente no perfil"),
                RobloxMultiManager.Core.L.T("Esta sessão pertence a outra conta. Use Login / atualizar no perfil selecionado."));
        }

        if (sessionState == "not-authenticated")
        {
            return TicketFetchDecision.Fail(
                statusCode == 401
                    ? RobloxMultiManager.Core.L.T("Sessão expirada (HTTP 401)")
                    : RobloxMultiManager.Core.L.F("Sessão não autenticada (HTTP {0})",statusCode),
                RobloxMultiManager.Core.L.T("A sessão desta conta expirou. Use Login / atualizar e tente novamente."));
        }

        if (sessionState != "verified")
        {
            return TicketFetchDecision.Fail(
                RobloxMultiManager.Core.L.T("Estado de sessão inválido"),
                RobloxMultiManager.Core.L.T("A validação da conta retornou um estado inesperado."));
        }

        if (statusCode is >= 200 and < 300 && IsSafeTicket(ticket))
        {
            return new TicketFetchDecision(
                TicketFetchAction.LaunchPlayer,
                ticket,
                null,
                null);
        }

        if (statusCode == 0)
        {
            return TicketFetchDecision.Fail(
                RobloxMultiManager.Core.L.T("Falha de rede na autorização"),
                RobloxMultiManager.Core.L.T("A página oficial não conseguiu contatar o serviço de autorização."));
        }

        if (statusCode == 401)
        {
            return TicketFetchDecision.Fail(
                RobloxMultiManager.Core.L.T("Sessão expirada (HTTP 401)"),
                RobloxMultiManager.Core.L.T("A sessão da conta expirou. Use Login / atualizar e tente novamente."));
        }

        if (statusCode == 429)
        {
            return TicketFetchDecision.Fail(
                RobloxMultiManager.Core.L.T("Limite temporário (HTTP 429)"),
                RobloxMultiManager.Core.L.T("O Roblox limitou as tentativas. Aguarde um minuto e tente novamente."));
        }

        if (statusCode is >= 200 and < 300)
        {
            return TicketFetchDecision.Fail(
                RobloxMultiManager.Core.L.F("Ticket ausente (HTTP {0})",statusCode),
                RobloxMultiManager.Core.L.T("O Roblox respondeu sem a autorização temporária esperada."));
        }

        return TicketFetchDecision.Fail(
            RobloxMultiManager.Core.L.F("Autorização recusada (HTTP {0})",statusCode),
            RobloxMultiManager.Core.L.F("O Roblox recusou a autorização temporária (HTTP {0}). ",statusCode)
            + RobloxMultiManager.Core.L.T("Use Login / atualizar e tente novamente."));
    }

    internal static bool IsRequestedHomePage(string? value) =>
        IsTrustedLaunchOrigin(value)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.TrimEnd('/').Equals(
            "/home",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The page the protected session opens to obtain the authorization ticket.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A destination that already carries a concrete place id must NOT be visited.
    /// Reported with
    /// <c>https://www.roblox.com/games/2753915549?privateServerLinkCode=...</c>:
    /// the canonical form of that link is <c>/games/start?placeId=...</c>, and the
    /// entire purpose of that page is to hand the join to the installed client by
    /// navigating to <c>roblox://experiences</c>.  The origin guard correctly refuses
    /// to follow a non-HTTPS scheme, so the launch died with "a autorização tentou
    /// sair do domínio oficial do Roblox ... Destino recusado: roblox://experiences".
    /// </para>
    /// <para>
    /// Visiting it was never necessary: the ticket comes from
    /// <c>auth.roblox.com</c> and works from any official page, and this app builds
    /// the <c>roblox-player:</c> URI itself from
    /// <see cref="RobloxLaunchTarget.PlaceId"/>.  So the home page is used whenever
    /// the target is already complete.
    /// </para>
    /// <para>
    /// The one case that still needs the official page is a <c>/share</c> experience
    /// link, which carries no place id and no invite code: there the redirect is the
    /// only thing that reveals the place id.  A <c>/share</c> SERVER invite does not
    /// need it either, because it is resolved through the API instead.
    /// </para>
    /// </remarks>
    internal static string ResolveInitialNavigationUrl(
        RobloxLaunchTarget? target,
        string? pendingShareServerCode,
        string homeUrl = RobloxHomeUrl)
    {
        if (target is null || pendingShareServerCode is not null)
        {
            return homeUrl;
        }

        return target.PlaceId is > 0 ? homeUrl : target.LauncherUrl;
    }

    private bool IsRequestedInitialPage(string? value) =>
        IsRequestedHomePage(value)
        || (_launchTarget is not null
            && RobloxLinkParser.TryParse(value, out _, out _));

    internal static bool IsTrustedLaunchOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);

    internal static string BuildHomeLaunchUri(
        string ticket,
        long browserTrackerId,
        long launchTimeMilliseconds,
        string? channel = null)
    {
        if (!IsSafeTicket(ticket))
        {
            throw new ArgumentException(RobloxMultiManager.Core.L.T("Ticket temporário inválido."), nameof(ticket));
        }

        if (browserTrackerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(browserTrackerId));
        }

        return "roblox-player:1"
               + "+launchmode:app"
               + $"+gameinfo:{Uri.EscapeDataString(ticket)}"
               + $"+launchtime:{launchTimeMilliseconds}"
               + $"+browsertrackerid:{browserTrackerId}"
               + "+robloxLocale:pt_br"
               + "+gameLocale:pt_br"
               + BuildChannelSegment(channel);
    }

    internal static string BuildGameLaunchUri(
        string ticket,
        long browserTrackerId,
        long launchTimeMilliseconds,
        RobloxLaunchTarget target,
        string? channel = null)
    {
        if (!IsSafeTicket(ticket))
        {
            throw new ArgumentException(RobloxMultiManager.Core.L.T("Ticket temporário inválido."), nameof(ticket));
        }

        if (browserTrackerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(browserTrackerId));
        }

        if (!RobloxLinkParser.TryParse(
                target.LauncherUrl,
                out var normalizedTarget,
                out var error))
        {
            throw new ArgumentException(error, nameof(target));
        }

        var launcherUrl = BuildProtocolLauncherUrl(
            normalizedTarget,
            browserTrackerId);
        return "roblox-player:1"
               + "+launchmode:play"
               + $"+gameinfo:{Uri.EscapeDataString(ticket)}"
               + $"+launchtime:{launchTimeMilliseconds}"
               + $"+placelauncherurl:{Uri.EscapeDataString(launcherUrl)}"
               + $"+browsertrackerid:{browserTrackerId}"
               + "+robloxLocale:pt_br"
               + "+gameLocale:pt_br"
               + BuildChannelSegment(channel);
    }

    private static string BuildProtocolLauncherUrl(
        RobloxLaunchTarget target,
        long browserTrackerId)
    {
        if (target.PlaceId is not > 0)
        {
            // A new share link may still be waiting for the official page to
            // resolve its place id. Keep that official URL as a bounded
            // fallback; concrete game links use the API endpoint below.
            return target.LauncherUrl;
        }

        var request = target.Kind == RobloxLaunchTargetKind.PrivateServer
            ? "RequestPrivateGame"
            : target.GameInstanceId is not null ? "RequestGameJob" : "RequestGame";
        var builder = new System.Text.StringBuilder()
            .Append("https://assetgame.roblox.com/game/PlaceLauncher.ashx")
            .Append("?request=")
            .Append(request)
            .Append("&browserTrackerId=")
            .Append(browserTrackerId)
            .Append("&placeId=")
            .Append(target.PlaceId.Value)
            .Append("&isPlayTogetherGame=false");
        if (target.Kind == RobloxLaunchTargetKind.PrivateServer
            && target.PrivateServerCode is { } privateCode)
        {
            builder.Append("&linkCode=")
                .Append(Uri.EscapeDataString(privateCode));
        }

        if (target.Kind == RobloxLaunchTargetKind.Game && target.GameInstanceId is { } job)
            builder.Append("&gameId=").Append(Uri.EscapeDataString(job));
        return builder.ToString();
    }

    private static string BuildChannelSegment(string? channel)
    {
        if (channel is null)
        {
            return string.Empty;
        }

        var normalized = RobloxVersionChecker.NormalizeChannel(channel);
        var protocolChannel = normalized.Equals(
            "LIVE",
            StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalized;
        return $"+channel:{Uri.EscapeDataString(protocolChannel)}";
    }

    private static bool IsSafeNonce(string? value) =>
        value is { Length: 32 }
        && value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static bool IsSafeTicket(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length is < 16 or > 8192)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character)
                || char.IsWhiteSpace(character))
            {
                return false;
            }
        }

        return true;
    }

    private void Fail(string code, string userMessage)
    {
        if (_terminal || _closing)
        {
            return;
        }

        _terminal = true;
        ++_requestGeneration;
        SafeError = code;
        // The detail is what actually identifies the failure, so it has to reach
        // the activity log too and not only this dialog, which the user closes.
        SafeDetail = userMessage;
        LaunchProgress.IsIndeterminate = false;
        LaunchHeadingText.Text = RobloxMultiManager.Core.L.T("Não foi possível abrir.");
        StatusText.Text = userMessage;
        CloseButton.Content = RobloxMultiManager.Core.L.T("Fechar");
        CloseButton.IsEnabled = true;
    }

    private void SetLaunchPhase(int phase)
    {
        StageOne.Foreground = (System.Windows.Media.Brush)FindResource(phase == 1 ? "Accent" : "Success");
        StageTwo.Foreground = (System.Windows.Media.Brush)FindResource(phase < 2 ? "TextFaint" : phase == 2 ? "Accent" : "Success");
        StageThree.Foreground = (System.Windows.Media.Brush)FindResource(phase == 3 ? "Accent" : "TextFaint");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        SafeError ??= RobloxMultiManager.Core.L.T("Cancelado");
        _terminal = true;
        ++_requestGeneration;
        CompleteAndClose(false);
    }

    /// <summary>
    /// Publishes the outcome and closes the window.
    /// </summary>
    private void CompleteAndClose(bool launched)
    {
        _completion.TrySetResult(launched);
        if (!_closing)
        {
            Close();
        }
    }

    internal enum TicketFetchAction
    {
        Fail,
        LaunchPlayer
    }

    internal sealed record TicketFetchResult(
        string SessionState,
        int StatusCode,
        string? Ticket,
        string? ShareStatus = null,
        string? SharePlaceId = null,
        string? ShareLinkCode = null);

    internal sealed record ShareResolution(
        bool Ok,
        long PlaceId,
        string? LinkCode,
        string Code,
        string Message);

    internal sealed record TicketFetchDecision(
        TicketFetchAction Action,
        string? Ticket,
        string? ErrorCode,
        string? UserMessage)
    {
        public static TicketFetchDecision Fail(
            string errorCode,
            string userMessage) =>
            new(TicketFetchAction.Fail, null, errorCode, userMessage);
    }
}
