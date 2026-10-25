using System.Drawing;
using System.Text.Json.Serialization;

namespace RobloxMultiManager.Core;

public sealed record AccountProfile(
    Guid Id,
    string Name,
    string GameUrl,
    long? RobloxUserId = null,
    string? RobloxUsername = null,
    string? RobloxDisplayName = null,
    string? AvatarUrl = null,
    long BrowserTrackerId = 0,
    int? CpuLimitPercent = null,
    string? RobloxVersionId = null)
{
    public override string ToString() => PublicName;

    [JsonIgnore]
    public string PublicName => DisplayPrivacy.AccountName(Id, Name);

    [JsonIgnore]
    public string? PublicAvatarUrl => DisplayPrivacy.Enabled ? null : AvatarUrl;

    [JsonIgnore]
    public string AccountDisplay => DisplayPrivacy.Enabled ? PublicName : RobloxUserId is > 0
        ? $"{RobloxDisplayName ?? RobloxUsername ?? RobloxMultiManager.Core.L.T("Conta")}  (@{RobloxUsername ?? "?"})"
        : RobloxMultiManager.Core.L.T("Nenhuma conta conectada");

    [JsonIgnore]
    public string SessionStatus => DisplayPrivacy.Enabled ? RobloxMultiManager.Core.L.T("Conta") : RobloxUserId is > 0
        ? RobloxMultiManager.Core.L.F("Pronta · ID {0}",RobloxUserId)
        : RobloxMultiManager.Core.L.T("Faça login");

    /// <summary>
    /// Whether this profile can actually open a client.
    /// </summary>
    /// <remarks>
    /// Exposed so the per-row open button can be disabled instead of launching and
    /// then refusing with a dialog, which is what the shared toolbar button did.
    /// </remarks>
    [JsonIgnore]
    public bool IsSignedIn => RobloxUserId is > 0;

    public static AccountProfile Create(string name, string gameUrl) =>
        new(
            Guid.NewGuid(),
            name.Trim(),
            gameUrl.Trim(),
            BrowserTrackerId: CreateBrowserTrackerId());

    public static AccountProfile Create(string name) =>
        Create(name, "https://www.roblox.com/home");

    public AccountProfile WithIdentity(AccountIdentity identity) =>
        this with
        {
            RobloxUserId = identity.UserId,
            RobloxUsername = identity.Username,
            RobloxDisplayName = identity.DisplayName,
            AvatarUrl = identity.AvatarUrl,
            BrowserTrackerId = BrowserTrackerId > 0
                ? BrowserTrackerId
                : CreateBrowserTrackerId()
        };

    public AccountProfile EnsureBrowserTrackerId() =>
        BrowserTrackerId > 0
            ? this
            : this with { BrowserTrackerId = CreateBrowserTrackerId() };

    private static long CreateBrowserTrackerId() =>
        Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);
}

public sealed record AccountIdentity(
    long UserId,
    string Username,
    string DisplayName,
    string? AvatarUrl = null);

public sealed record AppSettings(
    int TargetInstances = 5,
    bool AutoArrange = true,
    int SelectedMonitor = -1,
    bool CompatibilityRiskAccepted = false,
    int LaunchDelaySeconds = 7,
    string? SelectedRobloxVersionId = null,
    string? SelectedRobloxChannel = null,
    bool AutoReconnect = false,
    int AutoReconnectMaxAttempts = 3,
    int GlobalCpuLimitPercent = 100);

public enum GuardState
{
    Stopped,
    Starting,
    Active,
    Contested,
    PlayersAlreadyRunning,
    Faulted
}

public sealed record GuardResult(
    GuardState State,
    string Message,
    bool CreatedNew = false,
    bool RecoveredAbandoned = false,
    bool OwnsMutex = false,
    bool Degraded = false)
{
    public bool IsActive => State == GuardState.Active;

    /// <summary>
    /// Active, but the singleton name could not be given the shape a second client
    /// needs, so opening another window may still be refused by the client itself.
    /// </summary>
    /// <remarks>
    /// This exists so the app can proceed and say what it knows, instead of the
    /// previous behaviour of refusing outright and demanding that a running client
    /// be closed.  Nothing here promises the next launch will work; it reports the
    /// measured state of the Windows names.
    /// </remarks>
    public bool IsFullyHeld => IsActive && !Degraded;
}

public enum RobloxInstanceState
{
    Starting,
    WindowReady,
    Running,
    NotResponding
}

public sealed record RobloxInstanceInfo(
    int ProcessId,
    DateTime StartTimeUtc,
    nint WindowHandle,
    string WindowTitle,
    RobloxInstanceState State,
    bool IsResponsive,
    string? ExecutablePath = null,
    string? VersionId = null,
    string? FileVersion = null,
    string? ProfileName = null,
    string? RequestedVersionId = null,
    string? DeploymentId = null,
    long? AccountUserId = null,
    string? AccountUsername = null,
    bool AccountIsExact = false)
{
    public string Key => $"{ProcessId}:{StartTimeUtc.Ticks}";
    public bool HasWindow => WindowHandle != nint.Zero;

    /// <summary>
    /// Whether the client sits in a deployment folder this app validates.
    /// </summary>
    /// <remarks>
    /// A client started by another launcher (Fishstrap, Bloxstrap) runs from that
    /// launcher's own <c>Versions</c> folder, so it is a real Roblox client with a
    /// real deployment id that this app simply did not verify.  That is a
    /// different thing from a client whose path could not be read at all, and
    /// collapsing the two was what made every external client read "Não
    /// verificado" and block launches.
    /// </remarks>
    public bool IsVerified => VersionId is not null;

    /// <summary>
    /// The deployment this process is running, verified or not.
    /// </summary>
    public string? EffectiveDeploymentId => VersionId ?? DeploymentId;

    public string StatusText => State switch
    {
        RobloxInstanceState.Starting => RobloxMultiManager.Core.L.T("Abrindo janela"),
        RobloxInstanceState.WindowReady => RobloxMultiManager.Core.L.T("Janela pronta"),
        RobloxInstanceState.Running => RobloxMultiManager.Core.L.T("Em execução"),
        RobloxInstanceState.NotResponding => RobloxMultiManager.Core.L.T("Não respondendo"),
        _ => State.ToString()
    };

    /// <summary>
    /// Who this client belongs to, as far as anything can tell.
    /// </summary>
    /// <remarks>
    /// A client this app launched carries its profile name.  For one it did not,
    /// the account is read from Roblox's own artifacts, so it shows the real
    /// username instead of admitting nothing.  Only a client with no identity on
    /// disk yet, which happens before it joins a place, falls through to the last
    /// case.
    /// </remarks>
    public string ProfileText => ProfileName
        ?? (AccountUsername is { Length: > 0 }
            ? $"@{AccountUsername}"
            : AccountUserId is { } id
                ? $"ID {id}"
                : RobloxMultiManager.Core.L.T("Aberto fora do app"));

    /// <summary>
    /// Whether this client was matched to a registered profile.
    /// </summary>
    public bool IsAttributed => ProfileName is not null;

    public string ClientText => VersionId is not null
        ? $"{FileVersion ?? RobloxMultiManager.Core.L.T("versão n/d")} · {VersionId}"
        : DeploymentId is not null
            ? $"{FileVersion ?? RobloxMultiManager.Core.L.T("versão n/d")} · {DeploymentId}"
            : ExecutablePath is not null
                ? RobloxMultiManager.Core.L.F("{0} · pasta não reconhecida",FileVersion ?? RobloxMultiManager.Core.L.T("versão n/d"))
                : RobloxMultiManager.Core.L.T("caminho não legível");

    /// <summary>
    /// Where the client came from, for the row's second line.
    /// </summary>
    public string OriginText
    {
        get
        {
            // For a client this app did not launch, where the account came from
            // matters more than the folder: an inferred match can be wrong when
            // several clients start seconds apart, and the user has to be able to
            // see the difference rather than trust a guess silently.
            if (ProfileName is null && AccountUserId is not null)
            {
                return AccountIsExact
                    ? RobloxMultiManager.Core.L.T("conta identificada pelo PID")
                    : RobloxMultiManager.Core.L.T("conta inferida pelo horário");
            }

            return VersionId is not null
                ? RequestedVersionId is null
                    ? RobloxMultiManager.Core.L.T("instalação verificada")
                    : RobloxMultiManager.Core.L.F("solicitada {0}",RequestedVersionId)
                : DeploymentId is not null
                    ? RobloxMultiManager.Core.L.T("fora das pastas do app")
                    : ExecutablePath is not null
                        ? RobloxMultiManager.Core.L.T("outro instalador")
                        : RobloxMultiManager.Core.L.T("sem permissão de leitura");
        }
    }

    public string VersionMatchText =>
        RequestedVersionId is null
            ? RobloxMultiManager.Core.L.T("Sem solicitação")
            : EffectiveDeploymentId is null
                ? RobloxMultiManager.Core.L.T("Não verificada")
                : EffectiveDeploymentId.Equals(
                    RequestedVersionId,
                    StringComparison.OrdinalIgnoreCase)
                    ? RobloxMultiManager.Core.L.T("Corresponde")
                    : RobloxMultiManager.Core.L.T("Divergente");
}

/// <summary>
/// Records which profile a running client belongs to, across app restarts.
/// </summary>
/// <remarks>
/// Attribution used to live only in memory, so restarting the manager turned
/// every client it had opened into an unattributed row.  <see cref="ProcessKey"/>
/// is <c>pid:startTicks</c>, the same identity the termination guard uses, so a
/// recycled process id can never inherit a stale entry.
/// </remarks>
/// <param name="LaunchTargetUrl">
/// The canonical launcher URL this client was opened with, or null for the home
/// screen.  Recorded so a reconnect can return to the same place instead of only
/// reopening the account; it is the already-validated
/// <see cref="RobloxLaunchTarget.LauncherUrl"/>, never raw user input, and it
/// deliberately excludes nothing else about the session.
/// </param>
public sealed record ProcessOwnershipEntry(
    string ProcessKey,
    Guid ProfileId,
    string ProfileName,
    string RequestedVersionId,
    string AttemptTag,
    DateTime RecordedAtUtc,
    string? LaunchTargetUrl = null);

public sealed record RobloxClientExecutableMetadata(
    string ExecutablePath,
    string VersionId,
    string? FileVersion,
    string SafeDisplayPath);

public sealed record RobloxLaunchReceipt(
    Guid AttemptId,
    Guid ProfileId,
    int ProcessId,
    DateTime StartTimeUtc,
    string RequestedExecutablePath,
    string RequestedVersionId,
    string? RequestedFileVersion,
    string? RequestedChannel,
    RobloxClientExecutableMetadata ActualClient)
{
    public string AttemptTag => AttemptId.ToString("N")[..8];

    public string ProcessKey => $"{ProcessId}:{StartTimeUtc.Ticks}";

    public bool InitialClientMatches =>
        ProcessId > 0
        && string.Equals(
            RequestedExecutablePath,
            ActualClient.ExecutablePath,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            RequestedVersionId,
            ActualClient.VersionId,
            StringComparison.OrdinalIgnoreCase);
}

public sealed record MonitorDescriptor(
    nint Handle,
    string Name,
    Rectangle WorkingArea,
    bool IsPrimary)
{
    public string DisplayName =>
        $"{(IsPrimary ? RobloxMultiManager.Core.L.T("Principal") : "Monitor")} — {WorkingArea.Width}×{WorkingArea.Height}";
}

public sealed record RobloxInstallationInfo(
    string? ExecutablePath,
    string? ProtocolCommand,
    string? Version,
    string? Publisher,
    bool AppearsSigned,
    string Source,
    string? VersionId = null,
    string? Channel = null)
{
    public bool IsFound => !string.IsNullOrWhiteSpace(ExecutablePath);
}

public sealed record RobloxInstalledVersionInfo(
    string VersionId,
    string ExecutablePath,
    string? FileVersion,
    DateTime LastWriteTimeUtc,
    string? Publisher,
    bool AppearsSigned,
    bool IsProtocolTarget,
    bool IsEnvironmentCurrent,
    bool IsRunning,
    bool IsManaged = false)
{
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var suffix = Badges.Count == 0
                ? string.Empty
                : $" · {string.Join(", ", Badges)}";
            return $"{FileVersion ?? RobloxMultiManager.Core.L.T("versão desconhecida")} · {VersionId}{suffix}";
        }
    }

    [JsonIgnore]
    public string StatusText =>
        Badges.Count == 0
            ? IsManaged ? RobloxMultiManager.Core.L.T("Baixada pelo app") : RobloxMultiManager.Core.L.T("Instalada")
            : string.Join(", ", Badges);

    [JsonIgnore]
    private List<string> Badges
    {
        get
        {
            var badges = new List<string>();
            if (IsProtocolTarget)
            {
                badges.Add("handler");
            }

            if (IsEnvironmentCurrent)
            {
                badges.Add(RobloxMultiManager.Core.L.T("ambiente"));
            }

            if (IsRunning)
            {
                badges.Add(RobloxMultiManager.Core.L.T("em execução"));
            }

            if (IsManaged)
            {
                badges.Add(RobloxMultiManager.Core.L.T("baixada pelo app"));
            }

            return badges;
        }
    }

    [JsonIgnore]
    public string SignatureText =>
        AppearsSigned ? RobloxMultiManager.Core.L.T("Roblox válida") : RobloxMultiManager.Core.L.T("Não confirmada");

    [JsonIgnore]
    public string InstalledAtText =>
        LastWriteTimeUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
}

public sealed record RobloxRemoteVersionInfo(
    string Channel,
    string Version,
    string VersionId,
    DateTime CheckedAtUtc,
    string Source);

public sealed record DiagnosticReport(
    string Text,
    DateTime GeneratedAtUtc);
