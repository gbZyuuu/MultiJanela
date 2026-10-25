using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RobloxMultiManager.Core;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using TextBox = System.Windows.Controls.TextBox;

namespace RobloxMultiManager;

internal enum LaunchObservationDecision
{
    WaitingForProcess,
    WaitingForUpdater,
    Stabilizing,
    Confirmed,
    Diverged,
    InitialProcessExited,
    UpdaterStillRunning,
    ReadinessTimedOut
}

public partial class MainWindow : Window
{
    internal const int MaxPendingLaunchVerifications = 2;

    /// <summary>
    /// Upper bound on simultaneous launch windows.
    /// </summary>
    /// <remarks>
    /// Each one runs its own WebView2, so this is a memory and rate-limit ceiling,
    /// not a correctness one.
    /// </remarks>
    internal const int MaxConcurrentLaunches = 4;
    private static readonly TimeSpan BatchLaunchStagger =
        TimeSpan.FromSeconds(1);
    private static readonly HttpClient VersionHttpClient = new();
    private readonly AppPaths _paths = new();
    private readonly LocalStore _store;
    private readonly MultiInstanceGuard _guard = new();
    private readonly RobloxProcessMonitor _processMonitor = new();
    private readonly WindowLayoutService _layout = new();
    private readonly RobloxInstallationLocator _installationLocator = new();
    private readonly RobloxVersionChecker _versionChecker =
        new(VersionHttpClient);
    private readonly RobloxDeploymentCatalog _deploymentCatalog =
        new(VersionHttpClient);
    private readonly RobloxDeploymentDownloader _deploymentDownloader =
        new(VersionHttpClient);

    /// <summary>
    /// Resolves the Roblox headshot picture shown on each account row.
    /// </summary>
    /// <remarks>
    /// Shares the one process-wide HttpClient with the version and catalog services
    /// so avatars do not open their own connection pool.
    /// </remarks>
    private readonly RobloxAvatarService _avatarService = new(VersionHttpClient);
    private readonly SemaphoreSlim _settingsSaveGate = new(1, 1);
    private readonly SemaphoreSlim _profilesSaveGate = new(1, 1);
    private readonly Dictionary<string, ProcessOwnershipEntry> _launchOwnership =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _ownershipSaveGate = new(1, 1);
    private readonly RobloxAccountResolver _accountResolver = new();
    private readonly RobloxClientFailureReader _failureReader = new();
    private readonly RobloxSelfUpdateBlocker _selfUpdateBlocker = new();

    /// <summary>
    /// Cached account lookups for clients this app did not launch, by process
    /// identity, with the number of attempts made.
    /// </summary>
    private readonly Dictionary<string, (RobloxClientAccount? Account, int Attempts)>
        _accountCache = new(StringComparer.Ordinal);

    private const int AccountResolveAttempts = 8;
    private readonly Dictionary<string, RobloxInstanceInfo>
        _lastObservedProcesses = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _confirmationTimer;
    private readonly DispatcherTimer _arrangeTimer;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly string _logFile;

    private AppSettings _settings = new();
    private RobloxInstallationInfo? _installation;
    private DateTime? _targetStableSinceUtc;
    private CancellationTokenSource? _launchBatchCancellation;

    /// <summary>
    /// Completed when a drawer that has an awaiting caller is dismissed.
    /// </summary>
    /// <remarks>
    /// Null for a fire-and-forget drawer such as Diagnóstico. It is what lets the
    /// versions panel be awaited the way the old modal window was, without a nested
    /// dispatcher frame.
    /// </remarks>
    private TaskCompletionSource? _drawerDismissal;

    /// <summary>
    /// The last guard result seen, so the partial state can be recognised later.
    /// </summary>
    private GuardResult? _lastGuardResult;

    /// <summary>
    /// Live process handles for clients this app launched, by process identity.
    /// </summary>
    /// <remarks>
    /// Kept open for one reason only: a process's exit code is unreadable once every
    /// handle to it is closed, and the exit code is what separates a crash from the
    /// player quitting.  The monitor deliberately disposes its handles each sweep, so
    /// this is a separate, small set covering only the clients this app owns.
    /// </remarks>
    private readonly Dictionary<string, ProcessExitObserver> _ownedProcessHandles =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Clients this app terminated, so their disappearance is never a crash.
    /// </summary>
    private readonly HashSet<string> _intentionalClosures = new(StringComparer.Ordinal);

    /// <summary>
    /// Headshot URLs already resolved this session, keyed by Roblox user id.
    /// </summary>
    /// <remarks>
    /// The thumbnail endpoint is only asked once per id per run: the URL is also
    /// persisted in the profile, so the common case does no network work at all.
    /// </remarks>
    private readonly HashSet<long> _avatarsRequested = [];
    private RobloxInstallationInfo? _activeBatchInstallation;
    private readonly HashSet<string> _activeBatchVersions=new(StringComparer.OrdinalIgnoreCase);
    private bool _batchDivergenceReported;
    private bool _loaded;
    private bool _allowClose;
    private bool _shutdownStarted;
    private string _lastArrangedRoster = string.Empty;

    /// <summary>
    /// Launches currently in flight, and the profiles they belong to.
    /// </summary>
    /// <remarks>
    /// This replaces a single boolean that refused any second launch. Two launches
    /// of the SAME profile still cannot overlap: both would open a WebView2 on the
    /// one user-data folder that profile owns, and that folder cannot be shared.
    /// Different profiles are independent and may run together.
    /// </remarks>
    private readonly HashSet<Guid> _launchingProfiles = [];
    private int _activeLaunches;
    private bool _guardActivationInFlight;
    private bool _arrangeInFlight;
    private bool _arrangeAgain;
    private bool _arrangeLogRequested;
    private AccountProfile? _pendingAccountDraft;

    private sealed record PendingLaunchVerification(
        AccountProfile Profile,
        RobloxLaunchReceipt Receipt,
        Task<bool> Verification);

    public MainWindow()
    {
        InitializeComponent();
        _paths.EnsureCreated();
        _store = new LocalStore(_paths);
        _logFile = Path.Combine(
            _paths.LogsRoot,
            $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        Profiles = [];
        Instances = [];
        AccountProfilesView = CreateAccountView();
        DataContext = this;
        InitializeWorkspace();
        InitializeNavigation();
        InitializeReconnect();

        _guard.StateChanged += Guard_StateChanged;
        _processMonitor.SnapshotChanged += ProcessMonitor_SnapshotChanged;
        _processMonitor.ResourcesChanged += ProcessMonitor_ResourcesChanged;

        _confirmationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _confirmationTimer.Tick += (_, _) => UpdateConfirmation();

        _arrangeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900)
        };
        _arrangeTimer.Tick += (_, _) =>
        {
            _arrangeTimer.Stop();
            if (_settings.AutoArrange)
            {
                ArrangeWindows(logResult: false);
            }
        };

        _notifyIcon = BuildNotifyIcon();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    public ObservableCollection<AccountProfile> Profiles { get; }
    public ObservableCollection<RobloxInstanceInfo> Instances { get; }

    private Forms.NotifyIcon BuildNotifyIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var show = new Forms.ToolStripMenuItem(RobloxMultiManager.Core.L.T("Mostrar MultiJanela"));
        show.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var arrange = new Forms.ToolStripMenuItem(RobloxMultiManager.Core.L.T("Organizar Roblox"));
        arrange.Click += (_, _) => Dispatcher.Invoke(() => ArrangeWindows(logResult: true));
        var exit = new Forms.ToolStripMenuItem(RobloxMultiManager.Core.L.T("Sair"));
        exit.Click += async (_, _) => await Dispatcher.InvokeAsync(() => ExitApplicationAsync());
        menu.Items.Add(show);
        menu.Items.Add(arrange);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        var icon = new Forms.NotifyIcon
        {
            // The app's own icon, read from the executable, so the tray matches the
            // taskbar instead of showing the generic Windows application glyph.
            Icon = TryLoadAppIcon() ?? Drawing.SystemIcons.Application,
            Text = RobloxMultiManager.Core.L.T("MultiJanela para Roblox"),
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        return icon;
    }

    /// <summary>
    /// The icon embedded in this executable, or null when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Returns null instead of throwing so a missing or malformed icon degrades to
    /// the stock glyph rather than taking the tray, and with it the multi-instance
    /// lock, down with it.
    /// </remarks>
    private static Drawing.Icon? TryLoadAppIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executable)
                ? null
                : Drawing.Icon.ExtractAssociatedIcon(executable);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = await _store.LoadSettingsAsync();
            await LoadSavedDestinationsAsync();
            var profiles = await _store.LoadProfilesAsync();
            var migratedProfiles = false;
            foreach (var profile in profiles)
            {
                var migrated = profile.EnsureBrowserTrackerId();
                Profiles.Add(migrated);
                migratedProfiles |= migrated != profile;
            }

            if (migratedProfiles)
            {
                await SaveProfilesAsync();
            }

            await LoadWorkspaceLibraryAsync();
            await InitializeAppearanceAsync();
            InitializeBackups();
            await InitializeGraphicsAsync();
            await InitializeTuningAsync();
            await InitializeNotificationsAsync();
            await InitializeAutomationAsync();
            await InitializeLocalApiAsync();
            await InitializeExpansionAsync();
            InitializeManagerUpdates();
            ApplySettingsToUi();
            LaunchLinkProfileCombo.SelectedItem = Profiles.FirstOrDefault(
                profile => profile.RobloxUserId is > 0);

            // Something is always selected, so Editar / Remover / Login stop opening
            // "Selecione um perfil na lista." on a list the user can plainly see.
            ProfilesGrid.SelectedItem ??= Profiles.FirstOrDefault(
                profile => profile.RobloxUserId is > 0) ?? Profiles.FirstOrDefault();
            UpdateQueueScopeText();
            RefreshMonitorChoices();
            RefreshInstallationInfo();

            _processMonitor.Start();
            var firstSnapshot = await _processMonitor.RefreshNowAsync();
            await RestoreOwnershipAsync(firstSnapshot);
            await RestoreWorkspaceAsync();
            _confirmationTimer.Start();
            _loaded = true;
            QueueCpuReconcile();

            AppendLog(
                RobloxMultiManager.Core.L.T("Aplicativo iniciado. Senhas, cookies, CSRF e tickets não são gravados no cadastro ou log."));

            // Detached: an avatar is decoration, and startup must not wait on
            // thumbnails.roblox.com to take the multi-instance lock.
            _ = RefreshAvatarsAsync();
            AppendLog(_installation?.IsFound == true
                ? RobloxMultiManager.Core.L.F("Cliente oficial localizado por: {0}.",_installation.Source)
                : RobloxMultiManager.Core.L.T("Cliente Roblox não localizado; use o diagnóstico para detalhes."));

            // Multi-window is what this app is for, so the lock is taken now rather
            // than waiting for the user to find a button. The consent below is a
            // one-time disclosure, not a per-session switch.
            if (!_settings.CompatibilityRiskAccepted)
            {
                _ = await ActivateGuardAsync();
            }
            else
            {
                // Running clients used to short-circuit this into a synthetic
                // "they hold the lock, close them" message without even trying.
                // The attempt is cheap and now succeeds in a shared mode, so it is
                // always made and the real measured state is what gets reported.
                await EnsureGuardHeldAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog(RobloxMultiManager.Core.L.F("Falha na inicialização: {0}",ex.Message));
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("O aplicativo iniciou com uma falha:\n\n{0}",ex.Message),
                "MultiJanela",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally { _startupCompletion.TrySetResult(); }
    }

    /// <summary>
    /// Reloads attribution for clients that are still running, and drops the rest.
    /// </summary>
    /// <remarks>
    /// Pruning against the first real snapshot is what keeps the file from growing
    /// forever.  A stale entry could not be misapplied anyway, because the key
    /// carries the process creation time, but there is no reason to keep it.
    /// </remarks>
    private async Task RestoreOwnershipAsync(
        IReadOnlyList<RobloxInstanceInfo> snapshot)
    {
        IReadOnlyList<ProcessOwnershipEntry> stored;
        try
        {
            stored = await _store.LoadOwnershipAsync();
        }
        catch (Exception ex)
        {
            AppendLog(RobloxMultiManager.Core.L.F("Atribuição de processos não pôde ser lida: {0}",ex.Message));
            return;
        }

        if (stored.Count == 0)
        {
            return;
        }

        var live = snapshot
            .Select(instance => instance.Key)
            .ToHashSet(StringComparer.Ordinal);
        var restored = 0;
        foreach (var entry in stored)
        {
            if (!live.Contains(entry.ProcessKey))
            {
                continue;
            }

            _launchOwnership[entry.ProcessKey] = entry;
            var instance = snapshot.First(item => item.Key == entry.ProcessKey);
            RetainExitObserver(instance.Key, instance.ProcessId, instance.StartTimeUtc);
            restored++;
        }

        if (restored > 0)
        {
            AppendLog(
                RobloxMultiManager.Core.L.F("[process] ATRIBUIÇÃO RESTAURADA: {0} de ",restored)
                + RobloxMultiManager.Core.L.F("{0} registro(s) correspondem a processos ainda ativos.",stored.Count));
        }

        if (restored != stored.Count)
        {
            await SaveOwnershipAsync();
        }

        ProcessMonitor_SnapshotChanged(this, snapshot);
    }

    /// <summary>
    /// Resolves the account of a client this app did not launch, with caching.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot arrives once a second and resolution reads files off disk, so
    /// results are cached per process identity.  A miss is retried a bounded number
    /// of times rather than never or forever: a client that has not joined a place
    /// yet has no identity on disk, and that changes after a few seconds.
    /// </para>
    /// <para>
    /// This runs inline, on the UI thread, and that is deliberate.  Measured against
    /// the ten real Roblox logs on this machine, including five-megabyte ones, the
    /// slowest single resolution was 34 ms and six clients cost 28 ms in total, so
    /// the bounded worst case is about 1.6 s of work spread across several seconds.
    /// A background queue was written and then removed: it bought nothing measurable
    /// and it delayed the account appearing on the row.  Do not "optimise" this
    /// without measuring first.
    /// </para>
    /// </remarks>
    private RobloxClientAccount? ResolveAccount(RobloxInstanceInfo instance)
    {
        if (_accountCache.TryGetValue(instance.Key, out var cached))
        {
            if (cached.Account is not null || cached.Attempts >= AccountResolveAttempts)
            {
                return cached.Account;
            }
        }

        var resolved = _accountResolver.Resolve(
            instance.ProcessId,
            instance.StartTimeUtc);
        _accountCache[instance.Key] = (resolved, cached.Attempts + 1);
        if (resolved is not null)
        {
            AppendLog(
                RobloxMultiManager.Core.L.F("[process] CONTA IDENTIFICADA pid={0}; ",instance.ProcessId)
                + $"userId={resolved.UserId}; "
                + RobloxMultiManager.Core.L.F("usuario=@{0}; ",SanitizeLogValue(resolved.Username ?? "n/d"))
                + $"origem={(resolved.IsExact ? RobloxMultiManager.Core.L.T("arquivo do PID") : RobloxMultiManager.Core.L.T("log por horário"))}.");
        }

        return resolved;
    }

    private void ApplySettingsToUi()
    {
        var target = Math.Clamp(_settings.TargetInstances, 1, 100);
        TargetInput.Text = target.ToString();
        TargetCountText.Text = target.ToString();
        TargetProgress.Maximum = target;
        AutoArrangeCheck.IsChecked = _settings.AutoArrange;
        AutoReconnectCheck.IsChecked = _settings.AutoReconnect;
        ConfigureGlobalCpuEditor();
    }

    private void RefreshMonitorChoices()
    {
        var choices = new List<MonitorChoice>
        {
            new(-1, RobloxMultiManager.Core.L.T("Todos os monitores"))
        };
        var monitors = _layout.GetMonitors();
        for (var index = 0; index < monitors.Count; index++)
        {
            choices.Add(new MonitorChoice(
                index,
                $"{index + 1}. {monitors[index].DisplayName}"));
        }

        MonitorCombo.ItemsSource = choices;
        MonitorCombo.SelectedValue = choices.Any(choice => choice.Index == _settings.SelectedMonitor)
            ? _settings.SelectedMonitor
            : -1;
    }

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        await ActivateGuardAsync();
    }

    /// <summary>
    /// Keeps the multi-instance lock held, without asking.
    /// </summary>
    /// <remarks>
    /// Opening several windows is the entire point of this app, so the lock is not
    /// a setting to remember to switch on: it is taken at startup and re-taken
    /// whenever it becomes possible.  It can only be taken while no client is
    /// running, because a running client owns the lock and it cannot be taken away,
    /// so this runs on every snapshot where the process list is empty.
    /// </remarks>
    private async Task EnsureGuardHeldAsync()
    {
        if (_guardActivationInFlight || !_settings.CompatibilityRiskAccepted)
        {
            return;
        }

        // A fully held lock has nothing to do. A degraded one does: the singleton
        // name is the client's Event and can only be re-shaped once that client is
        // gone, so the guard is restarted to claim the name the moment it frees up.
        // Without this the partial state would persist for the whole session.
        var upgrading = _guard.IsActive && _lastGuardResult?.Degraded == true;
        if (_guard.IsActive && !upgrading)
        {
            return;
        }

        if (upgrading && RobloxProcessMonitor.CountPlayerProcesses() > 0)
        {
            return;
        }

        _guardActivationInFlight = true;
        try
        {
            if (upgrading)
            {
                await _guard.StopAsync();
            }

            // Running clients are no longer a reason to refuse. Ownership of the
            // mutex belongs to whoever started first and cannot be taken, but a live
            // handle is what keeps the name alive for the next client, so the
            // attempt is always made.
            var result = await _guard.StartAsync(requireNoRobloxProcesses: false);
            _lastGuardResult = result;
            UpdateGuardUi(result);
            if (result.IsActive && (!upgrading || !result.Degraded))
            {
                AppendLog(result.Message);
            }
        }
        finally
        {
            _guardActivationInFlight = false;
        }
    }

    /// <summary>
    /// Makes sure the lock is in a state where launching will not cost the client
    /// that is already open.
    /// </summary>
    /// <param name="interactive">
    /// False for automatic callers such as auto reconnect, which must never put a
    /// modal question on screen and must simply decline when the state is unsafe.
    /// </param>
    private async Task<bool> ActivateGuardAsync(bool interactive = true)
    {
        if (_guard.IsActive)
        {
            if (_lastGuardResult?.Degraded != true)
            {
                return true;
            }

            // Measured: launching in the degraded state terminated the client that
            // was already open. Log of 2026-08-26 16:42, with no FECHANDO and no
            // FINALIZAÇÃO line, so this app killed nothing:
            //   16:42:14 INICIADO pid=18448
            //   16:42:23 CONFIRMADO pid=18448 window=ready
            //   16:42:23 ENCERRADO perfil="nova_demo" pid=10760   (51 min old)
            // The new client found ROBLOX_singletonEvent, which the old client had
            // created as an Event, signalled it, and the old client honoured the
            // handoff and exited. So a launch here is destructive and has to be
            // stopped before it starts.
            return interactive && await OfferToFreeSingletonNameAsync();
        }

        if (!_settings.CompatibilityRiskAccepted)
        {
            var answer = AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("Este modo ocupa a trava de instância única usada pelo cliente, como fazia o Roblox Account Manager.\n\n")
                + RobloxMultiManager.Core.L.T("Esta versão protege o mutex e o singletonEvent no próprio gerenciador. ")
                + RobloxMultiManager.Core.L.T("Ela não modifica o executável, não injeta código e não fecha handles dentro do Roblox, ")
                + RobloxMultiManager.Core.L.T("mas continua sendo um modo de compatibilidade não oficial. ")
                + RobloxMultiManager.Core.L.T("Uma atualização pode interrompê-lo, o Roblox pode limitar lançamentos e software que interfere ")
                + RobloxMultiManager.Core.L.T("no cliente pode gerar alerta ou restrição.\n\n")
                + RobloxMultiManager.Core.L.T("Você aceita ativar por sua conta e risco?"),
                RobloxMultiManager.Core.L.T("Aviso do modo multi-instância"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                AppendLog(RobloxMultiManager.Core.L.T("Ativação cancelada: o aviso de compatibilidade não foi aceito."));
                return false;
            }

            _settings = _settings with { CompatibilityRiskAccepted = true };
            await SaveSettingsAsync();
        }

        ActivateButton.IsEnabled = false;
        UpdateGuardUi(new GuardResult(GuardState.Starting, RobloxMultiManager.Core.L.T("Ativando a trava de compatibilidade...")));
        // Running clients are not a reason to refuse any more, so the lock is simply
        // attempted. Closing anything is now only proposed when acquisition really
        // fails, which means something else owns the name.
        var result = await _guard.StartAsync(requireNoRobloxProcesses: false);
        _lastGuardResult = result;
        UpdateGuardUi(result);
        AppendLog(result.Message);

        // Degraded still counts as usable here. Refusing would put the app back to
        // demanding that an open client be closed before it will even try, and the
        // client's own refusal is detected and explained afterwards if it happens.
        if (result.IsActive)
        {
            return true;
        }

        // Reaching here means the name is genuinely owned by another process, and a
        // mutex cannot be taken from its owner. The blockers are in the list with the
        // account each one belongs to, so closing them is offered instead of
        // reporting a dead end.
        if (result.State == GuardState.PlayersAlreadyRunning
            || result.State == GuardState.Contested)
        {
            return await OfferToCloseBlockersAsync(result);
        }

        AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("O Roblox não foi aberto porque a trava de compatibilidade não ")
            + RobloxMultiManager.Core.L.F("pôde ser ativada.\n\n{0}",result.Message),
            RobloxMultiManager.Core.L.T("Trava de compatibilidade"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// Offers to close the clients holding the lock, then takes it.
    /// </summary>
    /// <remarks>
    /// This force-terminates a running game, so it is never silent: the accounts
    /// are named and the user has to confirm.  A client this app launched is listed
    /// with its profile; one opened elsewhere is listed with the account read from
    /// Roblox's own artifacts.
    /// </remarks>
    /// <summary>
    /// Explains that launching now would cost the open client, and offers the one
    /// action that actually fixes it.
    /// </summary>
    /// <remarks>
    /// The name the client reads can only be given the right shape while no client
    /// holds it, so closing the open one is not a workaround here, it is the fix:
    /// after it exits the guard claims the name as a Mutex and every launch from
    /// then on is additive.  Before 1.7.6 this same closing achieved nothing,
    /// because the guard re-opened the name as an Event and stayed broken.
    /// </remarks>
    private async Task<bool> OfferToFreeSingletonNameAsync()
    {
        await _processMonitor.RefreshNowAsync();
        var blockers = Instances.ToArray();
        if (blockers.Length == 0)
        {
            // The name is held by something that is not a visible client, so there
            // is nothing to offer closing.
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("O nome de instância única do Roblox está ocupado por outro programa ")
                + $"({MultiInstanceGuard.DescribeNamedObject(MultiInstanceGuard.DefaultEventName)}), "
                + RobloxMultiManager.Core.L.T("e nenhum cliente Roblox aparece na lista.\n\n")
                + RobloxMultiManager.Core.L.T("Feche outros gerenciadores (Bloxstrap, Fishstrap, Roblox Account ")
                + RobloxMultiManager.Core.L.T("Manager) ou reinicie o Windows."),
                RobloxMultiManager.Core.L.T("Multi-janela indisponível"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var list = string.Join(
            "\n",
            blockers.Select(instance =>
                $"PID {instance.ProcessId} · {instance.ProfileText}"));
        var answer = AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("Abrir agora fecharia o Roblox que já está aberto, e não seria este ")
            + RobloxMultiManager.Core.L.T("aplicativo fazendo isso.\n\n")
            + RobloxMultiManager.Core.L.T("Medido nesta máquina: o cliente novo encontra o nome ")
            + RobloxMultiManager.Core.L.T("ROBLOX_singletonEvent, que o cliente já aberto criou, avisa essa ")
            + RobloxMultiManager.Core.L.T("instância e ela mesma se encerra. Foi o que aconteceu às 16:42: a ")
            + RobloxMultiManager.Core.L.T("janela nova ficou pronta e a antiga saiu no mesmo segundo, sem ")
            + RobloxMultiManager.Core.L.T("nenhum comando de encerramento partir daqui.\n\n")
            + RobloxMultiManager.Core.L.T("Para o modo multi-janela funcionar, este aplicativo precisa ocupar ")
            + RobloxMultiManager.Core.L.T("esse nome, e isso só é possível enquanto nenhum Roblox o detém. ")
            + RobloxMultiManager.Core.L.T("Fechar o que está aberto agora é o que libera o nome — depois disso ")
            + RobloxMultiManager.Core.L.T("as próximas janelas somam em vez de substituir.\n\n")
            + RobloxMultiManager.Core.L.F("Fechar {0} Roblox aberto(s) e assumir o nome?\n\n",blockers.Length)
            + list
            + RobloxMultiManager.Core.L.T("\n\nO jogo perderá qualquer estado não salvo. Se preferir manter ")
            + RobloxMultiManager.Core.L.T("essa janela, cancele: nada será aberto e nada será fechado."),
            RobloxMultiManager.Core.L.T("Abrir agora custaria o Roblox já aberto"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            AppendLog(
                RobloxMultiManager.Core.L.T("[guard] Abertura cancelada pelo usuário: o Roblox já aberto foi ")
                + RobloxMultiManager.Core.L.T("mantido e o nome de instância única continua com ele."));
            return false;
        }

        return await CloseBlockersAndRetakeAsync(blockers);
    }

    private async Task<bool> OfferToCloseBlockersAsync(GuardResult result)
    {
        await _processMonitor.RefreshNowAsync();
        var blockers = Instances.ToArray();
        if (blockers.Length == 0)
        {
            AppDialog.Show(
                this,
                result.Message,
                RobloxMultiManager.Core.L.T("Trava de compatibilidade"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var list = string.Join(
            "\n",
            blockers.Select(instance =>
                $"PID {instance.ProcessId} · {instance.ProfileText}"));
        var answer = AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("Para abrir várias janelas, o gerenciador precisa segurar a trava de ")
            + RobloxMultiManager.Core.L.T("instância única, e ela pertence ao Roblox que já está aberto. Não é ")
            + RobloxMultiManager.Core.L.T("possível tirá-la dele.\n\n")
            + RobloxMultiManager.Core.L.F("Fechar {0} Roblox aberto(s) e ativar agora?\n\n",blockers.Length)
            + list
            + RobloxMultiManager.Core.L.T("\n\nO jogo perderá qualquer estado não salvo."),
            RobloxMultiManager.Core.L.T("Fechar o Roblox aberto e ativar"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            AppendLog(
                RobloxMultiManager.Core.L.T("[guard] Ativação recusada pelo usuário: os Roblox abertos foram mantidos."));
            return false;
        }

        return await CloseBlockersAndRetakeAsync(blockers);
    }

    /// <summary>
    /// Closes the clients holding the singleton name, then claims it.
    /// </summary>
    private async Task<bool> CloseBlockersAndRetakeAsync(
        IReadOnlyList<RobloxInstanceInfo> blockers)
    {

        foreach (var instance in blockers)
        {
            _intentionalClosures.Add(instance.Key);
            var outcome = _layout.ForceTerminate(instance);
            AppendLog(
                RobloxMultiManager.Core.L.F("[guard] FECHANDO PID {0} para ativar a trava; ",instance.ProcessId)
                + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(instance.ProfileText))
                + $"code={outcome.Code}; "
                + $"confirmed={outcome.Succeeded.ToString().ToLowerInvariant()}.");
        }

        // The lock cannot be taken until the processes are actually gone.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (RobloxProcessMonitor.CountPlayerProcesses() == 0)
            {
                break;
            }

            await Task.Delay(150);
        }

        await _processMonitor.RefreshNowAsync();
        // Stopped first: the guard may already be holding the names in the shared
        // mode, and StartAsync short-circuits on an active guard, which would report
        // success without ever re-claiming the freed name.
        await _guard.StopAsync();
        var retry = await _guard.StartAsync(requireNoRobloxProcesses: true);
        _lastGuardResult = retry;
        UpdateGuardUi(retry);
        AppendLog(retry.Message);

        // IsFullyHeld, not IsActive: the shared mode is exactly the state where
        // launching costs the open client, so returning true for it would send the
        // caller straight back into the behaviour this closing was meant to avoid.
        if (!retry.IsFullyHeld)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("O nome de instância única ainda não pôde ser assumido, então abrir ")
                + RobloxMultiManager.Core.L.T("outra janela continuaria fechando a anterior.\n\n")
                + retry.Message,
                RobloxMultiManager.Core.L.T("Multi-janela indisponível"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return retry.IsFullyHeld;
    }

    private void Guard_StateChanged(object? sender, GuardResult result)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _lastGuardResult = result;
            UpdateGuardUi(result);
        });
    }

    /// <summary>
    /// Reflects the measured lock state, including the partial one.
    /// </summary>
    /// <remarks>
    /// Active and fully held are now different things.  Active means the guard is
    /// holding what it can and launches may proceed; degraded means the singleton
    /// name still has the shape the client reads as "another instance owns the
    /// app", which it only loses when the client that created it exits.  Showing
    /// that as a warning rather than as a refusal is the point: the app no longer
    /// demands that a running client be closed before it will try.
    /// </remarks>
    private void UpdateGuardUi(GuardResult result)
    {
        GuardStatusText.Text = result.Message;
        GuardDot.Fill = result switch
        {
            { State: GuardState.Active, Degraded: false } =>
                (Brush)FindResource("Success"),
            // Danger, not warning: in this state a launch destroys the open client,
            // so it is a blocked state and not a cosmetic degradation.
            { State: GuardState.Active, Degraded: true } =>
                (Brush)FindResource("Danger"),
            { State: GuardState.Contested or GuardState.Faulted } =>
                (Brush)FindResource("Danger"),
            _ => (Brush)FindResource("Warning")
        };
        ActivateButton.Content = result switch
        {
            { State: GuardState.Active, Degraded: false } => RobloxMultiManager.Core.L.T("Multi-janela ativo"),
            { State: GuardState.Active, Degraded: true } => RobloxMultiManager.Core.L.T("Liberar multi-janela"),
            { State: GuardState.PlayersAlreadyRunning or GuardState.Contested } =>
                RobloxMultiManager.Core.L.T("Assumir a trava"),
            { State: GuardState.Starting } => RobloxMultiManager.Core.L.T("Ativando..."),
            _ => RobloxMultiManager.Core.L.T("Ativar agora")
        };
        // Enabled while degraded too: pressing it is the way to ask the app to close
        // the client that owns the name, which is the only thing that frees it.
        ActivateButton.IsEnabled = result.State != GuardState.Starting
                                   && !result.IsFullyHeld;
    }

    private void ProcessMonitor_SnapshotChanged(
        object? sender,
        IReadOnlyList<RobloxInstanceInfo> snapshot)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Every row object is replaced on each refresh, so the selection has to
            // be re-established by identity. All selected keys are kept, not just
            // the focused one, otherwise a multi-row selection was destroyed about
            // once a second.
            var displayedSnapshot = new List<RobloxInstanceInfo>();
            foreach (var instance in snapshot)
            {
                if (_launchOwnership.TryGetValue(
                        instance.Key,
                        out var ownership))
                {
                    displayedSnapshot.Add(instance with
                    {
                        ProfileName = ownership.ProfileName,
                        RequestedVersionId = ownership.RequestedVersionId
                    });
                    continue;
                }

                // Not launched here, so ask Roblox's own artifacts who it is. When
                // the account matches a registered profile the row is adopted and
                // counts toward the target like any other.
                var account = ResolveAccount(instance);
                var adopted = account is null
                    ? null
                    : Profiles.FirstOrDefault(profile =>
                        profile.RobloxUserId == account.UserId);
                displayedSnapshot.Add(instance with
                {
                    ProfileName = adopted?.Name,
                    AccountUserId = account?.UserId,
                    AccountUsername = account?.Username,
                    AccountIsExact = account?.IsExact ?? false
                });
            }

            RecordSessionChanges(displayedSnapshot);
            var ownershipChanged = false;
            var currentKeys = displayedSnapshot
                .Select(instance => instance.Key)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _lastObservedProcesses
                         .Where(pair => !currentKeys.Contains(pair.Key))
                         .Select(pair => pair.Value)
                         .ToArray())
            {
                if (stale.ProfileName is not null)
                {
                    AppendLog(
                        RobloxMultiManager.Core.L.T("[process] ENCERRADO perfil=\"")
                        + $"{SanitizeLogValue(stale.ProfileName)}\"; "
                        + $"pid={stale.ProcessId}; actual={stale.VersionId ?? RobloxMultiManager.Core.L.T("não-verificado")}; "
                        + $"fileVersion={stale.FileVersion ?? "n/d"}; "
                        + $"startUtc={stale.StartTimeUtc:O}.");
                }

                // Evaluated BEFORE the ownership entry is dropped: that entry carries
                // the profile and the destination a reconnect needs, and removing it
                // first is what would make reopening impossible.
                _launchOwnership.TryGetValue(stale.Key, out var owner);
                EvaluateReconnect(stale, owner);

                if (_launchOwnership.Remove(stale.Key))
                {
                    ownershipChanged = true;
                }

                _accountCache.Remove(stale.Key);
                _intentionalClosures.Remove(stale.Key);
                if (_ownedProcessHandles.Remove(stale.Key, out var handle))
                {
                    handle.Dispose();
                }
            }

            if (ownershipChanged)
            {
                _ = SaveOwnershipAsync();
            }

            _lastObservedProcesses.Clear();
            foreach (var instance in displayedSnapshot)
            {
                _lastObservedProcesses[instance.Key] = instance;
            }

            MergeInstances(displayedSnapshot);
            QueueCpuReconcile();

            if (_activeBatchInstallation?.VersionId is { } expectedVersionId
                && !_batchDivergenceReported)
            {
                var divergent = snapshot.FirstOrDefault(instance =>
                    instance.VersionId is not null
                    && !_activeBatchVersions.Contains(instance.VersionId));
                if (divergent is not null)
                {
                    _batchDivergenceReported = true;
                    AppendLog(
                        RobloxMultiManager.Core.L.F("[batch] DIVERGÊNCIA: requested={0}; ",expectedVersionId)
                        + $"observedPid={divergent.ProcessId}; actual={divergent.VersionId}; "
                        + RobloxMultiManager.Core.L.T("fila=cancelada. O atualizador oficial substituiu o deployment."));
                    _launchBatchCancellation?.Cancel();
                }
            }

            UpdateConfirmation();

            // Re-take the lock the moment it becomes possible, so the user never
            // has to remember to switch the mode on after closing a client.
            if (_loaded && displayedSnapshot.Count == 0)
            {
                _ = EnsureGuardHeldAsync();
            }

            // Auto-arrange only reacts to the set of windows changing. It used to
            // run on any snapshot change, and since Roblox retitles its window
            // whenever the player joins or leaves a place, that silently undid
            // manual window placement during normal play.
            var roster = string.Join(
                "|",
                displayedSnapshot
                    .Where(item => item.HasWindow)
                    .Select(item => $"{item.Key}:{item.WindowHandle}")
                    .OrderBy(value => value, StringComparer.Ordinal));
            if (_settings.AutoArrange
                && roster.Length > 0
                && !roster.Equals(_lastArrangedRoster, StringComparison.Ordinal))
            {
                _lastArrangedRoster = roster;
                _arrangeTimer.Stop();
                _arrangeTimer.Start();
            }
            else if (roster.Length == 0)
            {
                _lastArrangedRoster = string.Empty;
            }
        });
    }

    /// <summary>
    /// Reconciles the bound instance list with a fresh snapshot, in place.
    /// </summary>
    /// <remarks>
    /// This replaces a Clear-then-refill, which was the actual reason a multi-row
    /// selection could not be held: clearing the collection destroys every
    /// container, and with them the ListBox anchor that Shift-click and Ctrl-click
    /// extend from.  Re-adding the keys afterwards restored the highlight but not
    /// the anchor, so the selection collapsed about once a second.
    ///
    /// <see cref="RobloxInstanceInfo"/> is a record, so an unchanged row compares
    /// equal and is skipped entirely.  In the steady state this touches nothing,
    /// which is also why the list stopped re-rendering every second.
    /// </remarks>
    private void MergeInstances(IReadOnlyList<RobloxInstanceInfo> snapshot)
    {
        var incoming = snapshot
            .Select((instance, index) => (instance, index))
            .ToDictionary(
                pair => pair.instance.Key,
                pair => pair,
                StringComparer.Ordinal);

        for (var index = Instances.Count - 1; index >= 0; index--)
        {
            if (!incoming.ContainsKey(Instances[index].Key))
            {
                Instances.RemoveAt(index);
            }
        }

        for (var target = 0; target < snapshot.Count; target++)
        {
            var desired = snapshot[target];
            var current = -1;
            for (var index = 0; index < Instances.Count; index++)
            {
                if (string.Equals(
                        Instances[index].Key,
                        desired.Key,
                        StringComparison.Ordinal))
                {
                    current = index;
                    break;
                }
            }

            if (current < 0)
            {
                Instances.Insert(Math.Min(target, Instances.Count), desired);
                continue;
            }

            if (current != target)
            {
                Instances.Move(current, target);
            }

            // Value equality: only a row whose displayed data actually changed is
            // replaced, so a selected row keeps its container and its selection.
            if (Instances[target] != desired)
            {
                var wasSelected = InstancesGrid.SelectedItems.Contains(
                    Instances[target]);
                Instances[target] = desired;
                if (wasSelected)
                {
                    InstancesGrid.SelectedItems.Add(desired);
                }
            }
        }
    }

    private void UpdateConfirmation()
    {
        var target = GetTarget();
        var processes = Instances.Count;
        // A client opened outside the app counts too, as long as it is on the same
        // deployment. It used to be excluded because its version was unreadable,
        // which made the target permanently unreachable when one was running.
        var ready = Instances.Count(instance =>
            !_authenticationRejected.Contains(instance.Key)
            && instance.HasWindow
            && instance.IsResponsive
            && instance.State != RobloxInstanceState.NotResponding
            && _installation?.VersionId is not null
            && instance.EffectiveDeploymentId is not null
            && instance.EffectiveDeploymentId.Equals(
                _installation!.VersionId,
                StringComparison.OrdinalIgnoreCase));

        TargetCountText.Text = target.ToString();
        ProcessCountText.Text = processes.ToString();
        WindowCountText.Text = ready.ToString();
        TargetProgress.Maximum = target;
        TargetProgress.Value = Math.Min(ready, target);

        if (ready >= target)
        {
            _targetStableSinceUtc ??= DateTime.UtcNow;
            var stableFor = DateTime.UtcNow - _targetStableSinceUtc.Value;
            if (stableFor >= TimeSpan.FromSeconds(10))
            {
                ConfirmationText.Text = RobloxMultiManager.Core.L.F("{0}/{1} confirmadas e estáveis",ready,target);
                ConfirmationText.Foreground = (Brush)FindResource("Success");
            }
            else
            {
                var remaining = Math.Max(0, 10 - (int)stableFor.TotalSeconds);
                ConfirmationText.Text = RobloxMultiManager.Core.L.F("{0}/{1} abertas — validando por {2}s",ready,target,remaining);
                ConfirmationText.Foreground = (Brush)FindResource("Warning");
            }
        }
        else
        {
            _targetStableSinceUtc = null;
            ConfirmationText.Text = RobloxMultiManager.Core.L.F("{0}/{1} confirmadas",ready,target);
            ConfirmationText.Foreground = (Brush)FindResource(
                processes > 0 ? "Warning" : "TextMuted");
        }
    }

    /// <summary>
    /// Adds an account by logging in on Roblox's own site.
    /// </summary>
    /// <remarks>
    /// Adding used to open a dialog that only collected an arbitrary local label,
    /// leaving the profile with no Roblox identity until the user separately
    /// pressed "Login / atualizar".  The name now comes from the account itself, so
    /// the list always shows the real username.  A cancelled login adds nothing;
    /// the WebView2 session folder it may have created is kept on purpose, so a
    /// retry does not have to sign in from scratch.
    /// </remarks>
    private async void AddProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPrivacy.Enabled) { AppDialog.Show(this, L.T("Desative o modo de gravação para abrir páginas que exibem dados da conta."), L.T("Modo de gravação"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var draft = _pendingAccountDraft ??= AccountProfile.Create(RobloxMultiManager.Core.L.T("Nova conta"));
        var login = new AccountLoginWindow(draft, _paths) { Owner = this };
        if (ShowOverlayWindow(login) != true || login.Result is null)
        {
            AppendLog(
                RobloxMultiManager.Core.L.T("Adição de conta cancelada ou não confirmada ")
                + $"({login.SafeFailureCode ?? RobloxMultiManager.Core.L.T("sem identidade")}).");
            return;
        }

        var identity = login.Result;
        var duplicate = Profiles.FirstOrDefault(profile =>
            profile.RobloxUserId == identity.UserId);
        if (duplicate is not null)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("A conta @{0} já está na lista como ",identity.Username)
                + $"“{duplicate.Name}”.",
                RobloxMultiManager.Core.L.T("Conta já cadastrada"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            AppendLog(
                RobloxMultiManager.Core.L.F("Conta @{0} não foi adicionada: já cadastrada.",SanitizeLogValue(identity.Username)));
            ProfilesGrid.SelectedItem = duplicate;
            _pendingAccountDraft = null;
            return;
        }

        var added = (draft with { Name = identity.Username })
            .WithIdentity(identity);
        Profiles.Add(added);
        ProfilesGrid.SelectedItem = added;
        LaunchLinkProfileCombo.SelectedItem ??= added;
        await SaveProfilesAsync();
        _pendingAccountDraft = null;
        AppendLog(
            RobloxMultiManager.Core.L.F("Conta adicionada: {0} ",SanitizeLogValue(identity.DisplayName))
            + $"(@{SanitizeLogValue(identity.Username)}), ID {identity.UserId}.");
        UpdateQueueScopeText();
        await RefreshAvatarsAsync();
    }

    private async void EditProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPrivacy.Enabled) { AppDialog.Show(this, L.T("Desative o modo de gravação para abrir páginas que exibem dados da conta."), L.T("Modo de gravação"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (ProfilesGrid.SelectedItem is not AccountProfile selected)
        {
            ShowSelectProfileMessage();
            return;
        }

        var dialog = new ProfileDialog(selected) { Owner = this };
        if (ShowOverlayWindow(dialog) != true || dialog.Result is null)
        {
            return;
        }

        var index = Profiles.IndexOf(selected);
        Profiles[index] = dialog.Result;
        ProfilesGrid.SelectedItem = dialog.Result;
        await SaveProfilesAsync();
        AppendLog(RobloxMultiManager.Core.L.F("Perfil “{0}” atualizado.",dialog.Result.Name));
    }

    private async void RemoveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not AccountProfile selected)
        {
            ShowSelectProfileMessage();
            return;
        }

        var answer = AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.F("Remover “{0}” da lista?\n\n",selected.Name)
            + RobloxMultiManager.Core.L.T("A sessão WebView2 permanecerá no disco para evitar perda acidental."),
            RobloxMultiManager.Core.L.T("Remover perfil"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        Profiles.Remove(selected);
        await SaveProfilesAsync();
        AppendLog(RobloxMultiManager.Core.L.F("Perfil “{0}” removido da lista; sessão WebView2 preservada.",selected.Name));
    }

    private async void LoginProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (DisplayPrivacy.Enabled) { AppDialog.Show(this, L.T("Desative o modo de gravação para abrir páginas que exibem dados da conta."), L.T("Modo de gravação"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        if (ProfilesGrid.SelectedItem is not AccountProfile selected)
        {
            ShowSelectProfileMessage();
            return;
        }

        var dialog = new AccountLoginWindow(selected, _paths)
        {
            Owner = this
        };
        if (ShowOverlayWindow(dialog) != true || dialog.Result is null)
        {
            AppendLog(
                RobloxMultiManager.Core.L.F("Login de “{0}” cancelado ou não confirmado ",selected.Name)
                + $"({dialog.SafeFailureCode ?? RobloxMultiManager.Core.L.T("sem identidade")}).");
            return;
        }

        var identity = dialog.Result;
        var duplicate = Profiles.FirstOrDefault(profile =>
            profile.Id != selected.Id
            && profile.RobloxUserId == identity.UserId);
        if (duplicate is not null)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("A conta @{0} já pertence ao perfil “{1}”.",identity.Username,duplicate.Name),
                RobloxMultiManager.Core.L.T("Conta já cadastrada"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            AppendLog(RobloxMultiManager.Core.L.F("Login duplicado rejeitado para o perfil “{0}”.",selected.Name));
            return;
        }

        var updated = selected.WithIdentity(identity);
        if (ShouldAdoptAccountName(selected, identity))
        {
            updated = updated with { Name = identity.Username };
        }

        var index = Profiles.IndexOf(selected);
        Profiles[index] = updated;
        ProfilesGrid.SelectedItem = updated;
        await SaveProfilesAsync();
        AppendLog(
            RobloxMultiManager.Core.L.F("Perfil “{0}” conectado como {1} (@{2}), ",updated.Name,identity.DisplayName,identity.Username)
            + $"ID {identity.UserId}.");

        // A re-login may be a different account on the same row, so a stale URL has
        // to be dropped before asking again.
        _avatarsRequested.Remove(identity.UserId);
        await RefreshAvatarsAsync();
    }

    /// <summary>
    /// Whether a re-login may replace the profile label with the account username.
    /// </summary>
    /// <remarks>
    /// A label the user chose deliberately is left alone.  Only the placeholders
    /// this app itself produced, and a label that already mirrored the previous
    /// username, are adopted, so the list converges on real usernames without
    /// overwriting anyone's own naming.
    /// </remarks>
    internal static bool ShouldAdoptAccountName(
        AccountProfile profile,
        AccountIdentity identity)
    {
        var name = profile.Name?.Trim() ?? string.Empty;
        if (name.Length == 0
            || name.Equals(RobloxMultiManager.Core.L.T("Nova conta"), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (profile.RobloxUsername is { } previous
            && name.Equals(previous, StringComparison.OrdinalIgnoreCase))
        {
            return !name.Equals(identity.Username, StringComparison.Ordinal);
        }

        // The seeded defaults are "Conta 1".."Conta 5".
        return name.StartsWith(RobloxMultiManager.Core.L.T("Conta "), StringComparison.OrdinalIgnoreCase)
               && int.TryParse(name[6..], out _);
    }

    /// <summary>
    /// Whether another launch may start right now, and why not if it may not.
    /// </summary>
    /// <remarks>
    /// NotNullWhen(false) states the contract the body already follows: every
    /// early return sets a reason. Without it the callers get CS8604 when they
    /// pass the reason straight to the dialog, which is the only thing they ever
    /// want to do with it.
    /// </remarks>
    private bool TryBeginLaunch(
        AccountProfile profile,
        [NotNullWhen(false)] out string? refusal)
    {
        if (_vaultLocked || (_automationTransaction && !_automationDispatch) || _storageMaintenanceRequested || _handoffPrepared || _shutdownStarted)
        {
            refusal = L.T("Aguarde a transferência do gerenciador terminar.");
            return false;
        }
        if (_nativeSessionRecoveryInProgress)
        {
            refusal = L.T("Aguarde o reparo da sessão local terminar antes de abrir contas.");
            return false;
        }
        if (_launchBatchCancellation is not null)
        {
            refusal = RobloxMultiManager.Core.L.T("Uma sequência está em andamento. Aguarde terminar ou cancele a sequência.");
            return false;
        }

        if (_launchingProfiles.Contains(profile.Id))
        {
            refusal =
                RobloxMultiManager.Core.L.F("A conta “{0}” já está sendo aberta. Aguarde essa ",profile.Name)
                + RobloxMultiManager.Core.L.T("janela terminar; a mesma conta não pode abrir duas sessões ao ")
                + RobloxMultiManager.Core.L.T("mesmo tempo.");
            return false;
        }

        if (_activeLaunches >= MaxConcurrentLaunches)
        {
            refusal =
                RobloxMultiManager.Core.L.F("Já existem {0} aberturas em andamento. Aguarde ",_activeLaunches)
                + RobloxMultiManager.Core.L.T("uma delas terminar.");
            return false;
        }

        _launchingProfiles.Add(profile.Id);
        _activeLaunches++;
        refusal = null;
        return true;
    }

    private void EndLaunch(AccountProfile profile)
    {
        _launchingProfiles.Remove(profile.Id);
        _activeLaunches = Math.Max(0, _activeLaunches - 1);
    }

    /// <summary>
    /// Opens the client for the account whose row was clicked.
    /// </summary>
    /// <remarks>
    /// The profile comes from the button's own DataContext, not from the list
    /// selection, so this cannot act on a different account than the one the user
    /// pointed at.  That ambiguity is why the single toolbar button was replaced.
    /// </remarks>
    private async void LaunchAccountRowButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AccountProfile selected })
        {
            return;
        }

        // Keeps the row the user acted on visibly current, and gives the toolbar
        // buttons a sensible subject afterwards.
        ProfilesGrid.SelectedItem = selected;
        await LaunchSingleProfileAsync(selected);
    }

    private async void LaunchProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not AccountProfile selected)
        {
            ShowSelectProfileMessage();
            return;
        }

        await LaunchSingleProfileAsync(selected);
    }

    /// <summary>
    /// One launch, with the concurrency gate and the guard the batch path also uses.
    /// </summary>
    private async Task LaunchSingleProfileAsync(AccountProfile selected)
    {
        if (!TryBeginLaunch(selected, out var refusal))
        {
            AppDialog.Show(
                this,
                refusal,
                RobloxMultiManager.Core.L.T("Abertura em andamento"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!await ActivateGuardAsync())
            {
                return;
            }

            var receipt = await LaunchProfileAsync(selected);
            if (receipt is not null)
            {
                await WaitForLaunchedInstanceAsync(
                    receipt,
                    selected,
                    CancellationToken.None,
                    showFailure: true);
            }
        }
        finally
        {
            EndLaunch(selected);
        }
    }

    private async void LaunchLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launchBatchCancellation is not null)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("Uma fila de abertura está em andamento. Cancele a fila antes de ")
                + RobloxMultiManager.Core.L.T("abrir um link."),
                RobloxMultiManager.Core.L.T("Fila em andamento"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!RobloxLinkParser.TryParse(
                LaunchLinkInput.Text,
                out var target,
                out var parseError))
        {
            AppDialog.Show(
                this,
                parseError,
                RobloxMultiManager.Core.L.T("Link do Roblox inválido"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (LaunchLinkProfileCombo.SelectedItem is not AccountProfile selected)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("Selecione a conta que entrará no link."),
                RobloxMultiManager.Core.L.T("Conta do link"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (selected.RobloxUserId is null)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("Faça login no perfil “{0}” antes de abrir um link.",selected.Name),
                RobloxMultiManager.Core.L.T("Sessão não identificada"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!TryBeginLaunch(selected, out var refusal))
        {
            AppDialog.Show(
                this,
                refusal,
                RobloxMultiManager.Core.L.T("Abertura em andamento"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!await ActivateGuardAsync())
            {
                return;
            }

            AppendLog(
                RobloxMultiManager.Core.L.F("[link] SOLICITADO perfil=\"{0}\"; ",SanitizeLogValue(selected.Name))
                + RobloxMultiManager.Core.L.F("usuario=@{0}; ",SanitizeLogValue(selected.RobloxUsername ?? "n/d"))
                + RobloxMultiManager.Core.L.F("target={0}; código privado não registrado.",target.SafeSummary));
            var receipt = await LaunchProfileAsync(
                selected,
                launchTarget: target);
            if (receipt is not null)
            {
                await WaitForLaunchedInstanceAsync(
                    receipt,
                    selected,
                    CancellationToken.None,
                    showFailure: true);
            }
        }
        finally
        {
            EndLaunch(selected);
        }
    }

    private async void LaunchAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launchBatchCancellation is not null) { _launchBatchCancellation.Cancel(); return; }
        if (!TryCreateSequencePlan(false, out var plan, out var error))
        { AppDialog.Show(this, error, RobloxMultiManager.Core.L.T("Abrir sequência"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await RunLaunchSequenceAsync(plan);
    }

    private async Task RunLaunchSequenceAsync(LaunchSequencePlan plan)
    {
        if (_vaultLocked || (_automationTransaction && !_automationDispatch) || _storageMaintenanceRequested || _launchBatchCancellation is not null) return;
        if (_activeLaunches > 0)
        {
            AppDialog.Show(this, RobloxMultiManager.Core.L.T("Aguarde as aberturas individuais terminarem antes de iniciar a sequência."), RobloxMultiManager.Core.L.T("Abertura em andamento"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Own the batch before the first await. The immutable click-time plan is
        // shared by every launch, even if a profile, input or selection changes.
        _launchBatchCancellation = new CancellationTokenSource();
        UpdateQueuePreview();
        var pending = new List<PendingLaunchVerification>();
        using var verificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_launchBatchCancellation.Token);
        var completed = true;
        try
        {
            if (!await ActivateGuardAsync()) return;
            _launchBatchCancellation.Token.ThrowIfCancellationRequested();
            var selectedVersion = plan.Profiles.All(p=>p.RobloxVersionId is not null)?plan.Profiles[0].RobloxVersionId:_settings.SelectedRobloxVersionId;
            var batchInstallation = await Task.Run(() => _installationLocator.Resolve(selectedVersion));
            _launchBatchCancellation.Token.ThrowIfCancellationRequested();
            if (!IsOfficialInstallation(batchInstallation)) { ShowLaunchError(CreateInstallationException(batchInstallation)); return; }
            var batchChannel = GetConfiguredLaunchChannel(batchInstallation);
            await _processMonitor.RefreshNowAsync();
            _launchBatchCancellation.Token.ThrowIfCancellationRequested();
            if (!EnsureRunningClientsMatch(batchInstallation, showFailure: true)) return;
            var activeProfileIds = _processMonitor.Current.Select(instance =>
                _launchOwnership.TryGetValue(instance.Key, out var ownership) ? ownership.ProfileId : (Guid?)null)
                .Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
            var profiles = plan.Profiles.Where(profile => !activeProfileIds.Contains(profile.Id)).ToArray();
            if (profiles.Length == 0)
            {
                AppDialog.Show(this, RobloxMultiManager.Core.L.T("As contas desta sequência já estão abertas. Feche as sessões antes de abrir novamente."), RobloxMultiManager.Core.L.T("Sequência já aberta"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (profiles.Length < plan.Profiles.Length)
                AppendLog(RobloxMultiManager.Core.L.F("[batch] {0} conta(s) já aberta(s) foram ignoradas; a ordem das demais foi mantida.",plan.Profiles.Length - profiles.Length));
            _activeBatchInstallation = batchInstallation;
            _activeBatchVersions.Clear();foreach(var version in profiles.Select(p=>p.RobloxVersionId??batchInstallation.VersionId).Concat(Instances.Select(i=>i.EffectiveDeploymentId)).OfType<string>())_activeBatchVersions.Add(version);
            _batchDivergenceReported = false;
            AppendLog(RobloxMultiManager.Core.L.F("[batch] Sequência iniciada para {0} conta(s); target={1}; requested={2}; channel={3}.",profiles.Length,plan.Target?.SafeSummary ?? "home",batchInstallation.VersionId,batchChannel ?? "automatic"));
            for (var index = 0; index < profiles.Length; index++)
            {
                _launchBatchCancellation.Token.ThrowIfCancellationRequested();
                if (!await DrainBatchVerificationsAsync(
                        pending,
                        verificationCancellation,
                        waitForOne: false))
                {
                    completed = false;
                    break;
                }

                while (!HasBatchVerificationCapacity(pending.Count))
                {
                    if (!await DrainBatchVerificationsAsync(
                            pending,
                            verificationCancellation,
                            waitForOne: true))
                    {
                        completed = false;
                        break;
                    }
                }

                if (!completed)
                {
                    break;
                }

                var receipt = await LaunchProfileAsync(
                    profiles[index],
                    batchInstallation,
                    batchChannel,
                    _launchBatchCancellation.Token,
                    launchTarget: plan.Target);
                if (receipt is null)
                {
                    completed = false;
                    AppendLog(
                        RobloxMultiManager.Core.L.T("[batch] Fila interrompida no perfil ")
                        + $"\"{SanitizeLogValue(profiles[index].Name)}\": "
                        + RobloxMultiManager.Core.L.T("nenhum PID inicial confiável foi obtido."));
                    break;
                }

                pending.Add(new PendingLaunchVerification(
                    profiles[index],
                    receipt,
                    WaitForLaunchedInstanceAsync(
                        receipt,
                        profiles[index],
                        verificationCancellation.Token,
                        showFailure: false)));
                AppendLog(
                    RobloxMultiManager.Core.L.F("[batch] PID INICIAL VALIDADO attempt={0}; ",receipt.AttemptTag)
                    + RobloxMultiManager.Core.L.F("pid={0}; pendentes={1}/",receipt.ProcessId,pending.Count)
                    + $"{MaxPendingLaunchVerifications}; "
                    + RobloxMultiManager.Core.L.T("próxima conta liberada sem aguardar a janela completa."));

                if (index + 1 < profiles.Length)
                {
                    await Task.Delay(
                        BatchLaunchStagger,
                        _launchBatchCancellation.Token);
                }
            }

            while (completed && pending.Count > 0)
            {
                completed = await DrainBatchVerificationsAsync(
                    pending,
                    verificationCancellation,
                    waitForOne: true);
            }

            if (completed)
            {
                AppendLog(
                    RobloxMultiManager.Core.L.T("[batch] Fila concluída com todos os players confirmados em ")
                    + $"{batchInstallation.VersionId}.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog(RobloxMultiManager.Core.L.T("Sequência de abertura cancelada."));
        }
        catch (Exception ex)
        {
            ShowLaunchError(ex);
        }
        finally
        {
            verificationCancellation.Cancel();
            await ObservePendingBatchVerificationsAsync(pending);
            if (completed && _activeBatchInstallation is not null) NotifyEvent("launch-complete");
            _activeBatchInstallation = null;_activeBatchVersions.Clear();
            _batchDivergenceReported = false;
            _launchBatchCancellation.Dispose();
            _launchBatchCancellation = null;
            UpdateQueuePreview();
        }
    }

    /// <summary>Reconnect owned failures while preserving intentional exits and updater handoffs.</summary>
    internal static bool ShouldReconnect(
        bool enabled,
        bool launchedByApp,
        bool closedByApp,
        int? exitCode,
        string? clientFailureKind,
        int attemptsSoFar,
        int maxAttempts)
    {
        if (!enabled || closedByApp || attemptsSoFar >= maxAttempts)
        {
            return false;
        }

        // Only a client this app opened can be reopened: for any other one the
        // account and the destination are not known.
        if (!launchedByApp)
        {
            return false;
        }

        // The official updater already relaunches the client it replaced.
        if (string.Equals(clientFailureKind, "self-update", StringComparison.Ordinal))
        {
            return false;
        }

        if (clientFailureKind is "crash" or "single-instance-handoff") return true;
        return exitCode is { } code && code != 0;
    }

    internal static bool HasBatchVerificationCapacity(int pendingCount) =>
        pendingCount < MaxPendingLaunchVerifications;

    internal static int CalculateRemainingBatchLaunches(
        int target,
        int matchingRunningCount,
        int availableProfiles) =>
        Math.Min(
            Math.Max(0, availableProfiles),
            Math.Max(0, target - Math.Max(0, matchingRunningCount)));

    private async Task<bool> DrainBatchVerificationsAsync(
        List<PendingLaunchVerification> pending,
        CancellationTokenSource verificationCancellation,
        bool waitForOne)
    {
        if (pending.Count == 0)
        {
            return true;
        }

        if (waitForOne && pending.All(item => !item.Verification.IsCompleted))
        {
            _ = await Task.WhenAny(
                pending.Select(item => item.Verification));
        }

        var finished = pending
            .Where(item => item.Verification.IsCompleted)
            .ToArray();
        foreach (var item in finished)
        {
            pending.Remove(item);
            if (await item.Verification)
            {
                continue;
            }

            AppendLog(
                RobloxMultiManager.Core.L.F("[batch] Fila interrompida; attempt={0}; ",item.Receipt.AttemptTag)
                + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(item.Profile.Name))
                + RobloxMultiManager.Core.L.T("nenhum ticket adicional será solicitado."));
            verificationCancellation.Cancel();
            return false;
        }

        return true;
    }

    private static async Task ObservePendingBatchVerificationsAsync(
        IReadOnlyList<PendingLaunchVerification> pending)
    {
        foreach (var item in pending)
        {
            try
            {
                _ = await item.Verification;
            }
            catch (OperationCanceledException)
            {
                // A fila já registrou o cancelamento ou a falha que interrompeu o lote.
            }
        }
    }

    private async Task<bool> WaitForLaunchedInstanceAsync(
        RobloxLaunchReceipt receipt,
        AccountProfile profile,
        CancellationToken cancellationToken,
        bool showFailure)
    {
        var timeoutSeconds = Math.Max(
            30,
            Math.Clamp(_settings.LaunchDelaySeconds, 3, 30) * 3);
        var normalTimeout = TimeSpan.FromSeconds(timeoutSeconds);
        var activeUpdaterTimeout = TimeSpan.FromMinutes(4);
        var updaterOverallTimeout = TimeSpan.FromMinutes(4)
            + TimeSpan.FromSeconds(15);
        var stopwatch = Stopwatch.StartNew();
        var authenticationRejected = false;
        TimeSpan? stableSince = null;
        TimeSpan? updaterLastSeen = null;
        var updaterObserved = false;
        var updaterWasActive = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updaterBeforeSnapshot = IsRobloxUpdaterRunning();
            var snapshot = await _processMonitor.RefreshNowAsync();
            var updaterRunning =
                updaterBeforeSnapshot || IsRobloxUpdaterRunning();
            var observed = snapshot.FirstOrDefault(instance =>
                IsSameProcessIdentity(receipt, instance));
            var elapsed = stopwatch.Elapsed;

            if (updaterRunning)
            {
                updaterLastSeen = elapsed;
                if (!updaterObserved)
                {
                    updaterObserved = true;
                    AppendLog(
                        RobloxMultiManager.Core.L.F("[launch {0}] UPDATER OBSERVADO ",receipt.AttemptTag)
                        + $"pid={receipt.ProcessId}; requested={receipt.RequestedVersionId}; "
                        + RobloxMultiManager.Core.L.T("ação=aguardar e revalidar; divergência=false."));
                }

                updaterWasActive = true;
            }
            else if (updaterWasActive)
            {
                updaterWasActive = false;
                AppendLog(
                    RobloxMultiManager.Core.L.F("[launch {0}] UPDATER FINALIZADO ",receipt.AttemptTag)
                    + RobloxMultiManager.Core.L.F("pid={0}; ação=revalidar PID, caminho e versão.",receipt.ProcessId));
            }

            var observedMatches = observed is not null
                && DoesReceiptMatchObservedClient(receipt, observed);
            var observedReady = observedMatches
                && observed!.HasWindow
                && observed.IsResponsive;
            if (updaterRunning || !observedReady)
            {
                stableSince = null;
            }
            else
            {
                stableSince ??= elapsed;
            }

            var readyStableFor = stableSince is null
                ? TimeSpan.Zero
                : elapsed - stableSince.Value;
            var timedOut = updaterObserved
                ? updaterRunning
                    ? elapsed >= activeUpdaterTimeout
                    : elapsed >= Min(
                        updaterOverallTimeout,
                        (updaterLastSeen ?? elapsed) + normalTimeout)
                : elapsed >= normalTimeout;
            var processAlive = observed is not null
                || IsReceiptProcessAlive(receipt);
            var decision = ClassifyLaunchObservation(
                receipt,
                observed,
                processAlive,
                updaterRunning,
                readyStableFor,
                timedOut);

            if (decision is LaunchObservationDecision.Confirmed or LaunchObservationDecision.InitialProcessExited or LaunchObservationDecision.ReadinessTimedOut)
            {
                // Recheck the final response: an earlier challenge may have recovered.
                authenticationRejected = await Task.Run(() => _authenticationFailures.WasRejected(receipt.StartTimeUtc), cancellationToken);
                if (authenticationRejected) return ReportAuthenticationRejection(receipt, profile, showFailure);
            }

            switch (decision)
            {
                case LaunchObservationDecision.Diverged:
                    return ReportLaunchDivergence(
                        receipt,
                        profile,
                        observed,
                        RobloxMultiManager.Core.L.T("o PID inicial executa outro deployment"),
                        showFailure);
                case LaunchObservationDecision.InitialProcessExited:
                {
                    var replacement = snapshot
                        .Where(instance =>
                            instance.ProcessId != receipt.ProcessId
                            && instance.StartTimeUtc >= receipt.StartTimeUtc)
                        .OrderBy(instance => instance.StartTimeUtc)
                        .FirstOrDefault();
                    return ReportInitialProcessExited(
                        receipt,
                        profile,
                        replacement,
                        showFailure);
                }
                case LaunchObservationDecision.Confirmed:
                {
                    AppendLog(
                        RobloxMultiManager.Core.L.F("[launch {0}] CONFIRMADO ",receipt.AttemptTag)
                        + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(profile.Name))
                        + RobloxMultiManager.Core.L.F("usuario=@{0}; ",SanitizeLogValue(profile.RobloxUsername ?? "n/d"))
                        + $"pid={observed!.ProcessId}; actual={observed.VersionId}; "
                        + $"fileVersion={observed.FileVersion ?? "n/d"}; "
                        + $"window=ready; match=true; "
                        + $"updaterObserved={updaterObserved.ToString().ToLowerInvariant()}.");
                    return true;
                }
                case LaunchObservationDecision.UpdaterStillRunning:
                {
                    return ReportUpdaterStillRunning(
                        receipt,
                        profile,
                        observed!,
                        showFailure);
                }
                case LaunchObservationDecision.ReadinessTimedOut:
                    return ReportLaunchReadinessTimeout(
                        receipt,
                        profile,
                        updaterObserved,
                        showFailure);
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private async Task<RobloxLaunchReceipt?> LaunchProfileAsync(
        AccountProfile profile,
        RobloxInstallationInfo? frozenInstallation = null,
        string? frozenChannel = null,
        CancellationToken cancellationToken = default,
        RobloxLaunchTarget? launchTarget = null)
    {
        if (_nativeSessionRecoveryInProgress) return null;
        if (profile.RobloxUserId is null)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("Faça login no perfil “{0}” antes de abrir o Roblox.",profile.Name),
                RobloxMultiManager.Core.L.T("Sessão não identificada"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return null;
        }

        if (!await WaitForUpdaterBeforeLaunchAsync(cancellationToken))
        {
            return null;
        }

        // Resolved into a local, not the shared _installation field. That field is
        // what the readout and the confirmation count read from; overwriting it per
        // launch meant two concurrent launches clobbered each other's version, and
        // a link launch could repoint it in the middle of a queue.
        if (profile.RobloxVersionId is { } ownVersion)
        {
            frozenInstallation = await Task.Run(() => _installationLocator.Resolve(ownVersion), cancellationToken);
            frozenChannel = GetConfiguredLaunchChannel(frozenInstallation);
        }
        var installation = frozenInstallation is null
            ? _installationLocator.Resolve(_settings.SelectedRobloxVersionId)
            : _installationLocator.Resolve(frozenInstallation.VersionId);
        if (frozenInstallation is not null
            && !string.Equals(
                installation.ExecutablePath,
                frozenInstallation.ExecutablePath,
                StringComparison.OrdinalIgnoreCase))
        {
            installation = new RobloxInstallationInfo(
                null,
                frozenInstallation.ProtocolCommand,
                null,
                null,
                false,
                RobloxMultiManager.Core.L.T("A versão congelada para a fila mudou"),
                frozenInstallation.VersionId,
                frozenInstallation.Channel);
        }

        if (!IsOfficialInstallation(installation))
        {
            ShowLaunchError(CreateInstallationException(installation));
            return null;
        }

        await _processMonitor.RefreshNowAsync();
        if (!EnsureRunningClientsMatch(
                installation,
                showFailure: true))
        {
            return null;
        }

        IAsyncDisposable? graphicsLease;
        try { graphicsLease = await PrepareLaunchGraphicsAsync(profile, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ShowLaunchError(ex); return null; }
        await using var restoreGraphics = graphicsLease;

        var attemptId = Guid.NewGuid();
        var attemptTag = attemptId.ToString("N")[..8];
        var launchChannel = frozenInstallation is null
            ? GetConfiguredLaunchChannel(installation)
            : frozenChannel;
        var mode = string.IsNullOrWhiteSpace(
            _settings.SelectedRobloxVersionId)
            ? "automatic"
            : "fixed";
        AppendLog(
            RobloxMultiManager.Core.L.F("[launch {0}] SOLICITADO ",attemptTag)
            + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(profile.Name))
            + RobloxMultiManager.Core.L.F("usuario=@{0}; ",SanitizeLogValue(profile.RobloxUsername ?? "n/d"))
            + $"userId={profile.RobloxUserId}; mode={mode}; "
            + $"requested={installation.VersionId}; "
            + $"fileVersion={installation.Version ?? "n/d"}; "
            + $"channel={launchChannel ?? "automatic"}; "
            + $"path={GetSafeClientPath(installation.ExecutablePath)}; "
            + $"target={(launchTarget?.SafeSummary ?? "home")}.");

        // Shown non-modally: ShowDialog ran a nested dispatcher frame that froze
        // the rest of the UI and made a second launch impossible until this one
        // finished.
        var dialog = new RobloxLaunchWindow(
            profile,
            _paths,
            installation,
            attemptId,
            launchChannel,
            launchTarget)
        {
            Owner = this
        };
        dialog.Show();
        bool launched;
        try { launched = await dialog.Completion.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { dialog.Close(); throw; }
        var receipt = dialog.LaunchReceipt;
        if (receipt is not null)
        {
            _launchOwnership[receipt.ProcessKey] = new ProcessOwnershipEntry(
                receipt.ProcessKey,
                profile.Id,
                profile.Name,
                receipt.RequestedVersionId,
                receipt.AttemptTag,
                DateTime.UtcNow,
                launchTarget?.LauncherUrl);
            RetainProcessHandle(receipt);
            await SaveOwnershipAsync();
            AppendLog(
                RobloxMultiManager.Core.L.F("[launch {0}] INICIADO ",receipt.AttemptTag)
                + $"pid={receipt.ProcessId}; "
                + $"startUtc={receipt.StartTimeUtc:O}; "
                + $"requested={receipt.RequestedVersionId}; "
                + $"actual={receipt.ActualClient.VersionId}; "
                + $"fileVersion={receipt.ActualClient.FileVersion ?? "n/d"}; "
                + $"channel={receipt.RequestedChannel ?? "automatic"}; "
                + $"path={receipt.ActualClient.SafeDisplayPath}; "
                + $"match={receipt.InitialClientMatches.ToString().ToLowerInvariant()}.");
        }

        if (launched && receipt?.InitialClientMatches == true)
        {
            _ = RecordGameLaunchAsync(launchTarget?.PlaceId);
            return receipt;
        }

        if (!string.IsNullOrWhiteSpace(dialog.SafeError))
        {
            // The detail carries the exception type, the exit code and the path
            // that actually identify the failure.  Logging only the short reason
            // made every local problem read as a Roblox refusal.
            var detail = string.IsNullOrWhiteSpace(dialog.SafeDetail)
                ? string.Empty
                : $"detail=\"{SanitizeLogValue(
                    dialog.SafeDetail
                        .Replace("\r", " ", StringComparison.Ordinal)
                        .Replace("\n", " | ", StringComparison.Ordinal),
                    400)}\"; ";
            AppendLog(
                RobloxMultiManager.Core.L.F("[launch {0}] FALHOU perfil=\"",attemptTag)
                + $"{SanitizeLogValue(profile.Name)}\"; "
                + $"reason={SanitizeLogValue(dialog.SafeError)}; "
                + detail
                + RobloxMultiManager.Core.L.T("nenhum ticket foi salvo."));
        }

        return null;
    }

    private static bool IsRobloxUpdaterRunning()
    {
        var processes = Process.GetProcessesByName("RobloxPlayerInstaller")
            .Concat(Process.GetProcessesByName("RobloxPlayerLauncher"))
            .ToArray();
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return !process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            });
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private async Task<bool> WaitForUpdaterBeforeLaunchAsync(
        CancellationToken cancellationToken)
    {
        if (!IsRobloxUpdaterRunning())
        {
            return true;
        }

        AppendLog(
            RobloxMultiManager.Core.L.T("[updater] ABERTURA EM ESPERA: o atualizador oficial está ativo; ")
            + RobloxMultiManager.Core.L.T("nenhum ticket será solicitado até ele terminar."));
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromMinutes(4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(500, cancellationToken);
            if (!IsRobloxUpdaterRunning())
            {
                AppendLog(
                    RobloxMultiManager.Core.L.T("[updater] FINALIZADO: executáveis em execução serão ")
                    + RobloxMultiManager.Core.L.T("revalidados antes da próxima abertura."));
                return true;
            }
        }

        AppendLog(
            RobloxMultiManager.Core.L.T("[updater] TIMEOUT: o atualizador continua ativo após 4 minutos; ")
            + RobloxMultiManager.Core.L.T("abertura cancelada sem solicitar ticket."));
        AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("O atualizador oficial ainda está em execução após 4 minutos.\n\n")
            + RobloxMultiManager.Core.L.T("Nenhuma troca de versão foi atribuída à conta. Aguarde o processo ")
            + RobloxMultiManager.Core.L.T("terminar e tente novamente."),
            RobloxMultiManager.Core.L.T("Atualização ainda em andamento"),
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// Refuses a launch only when a running client is provably on another
    /// deployment.
    /// </summary>
    /// <remarks>
    /// This used to reject any client whose version could not be read, which made
    /// a single client started by another bootstrapper block every launch: those
    /// live outside the folders this app validates, so their version was always
    /// unreadable.  A deployment folder is now compared even when the path is not
    /// one this app manages, and a genuinely unreadable client is reported and
    /// allowed instead of blocking, because a mismatch that cannot be read has not
    /// been demonstrated.
    /// </remarks>
    private bool EnsureRunningClientsMatch(
        RobloxInstallationInfo installation,
        bool showFailure)
    {
        var unreadable = new List<int>();
        RobloxInstanceInfo? mismatch = null;
        foreach (var instance in _processMonitor.Current)
        {
            var deployment = instance.EffectiveDeploymentId;
            if (deployment is null)
            {
                unreadable.Add(instance.ProcessId);
                continue;
            }

            var owned = _launchOwnership.GetValueOrDefault(instance.Key);
            if(owned is not null && deployment.Equals(owned.RequestedVersionId,StringComparison.OrdinalIgnoreCase))continue;
            var attributed = owned?.ProfileId;
            var configured = Profiles.FirstOrDefault(p => p.Id == attributed)?.RobloxVersionId;
            // An explicit per-account deployment is an intentional coexistence,
            // while an unassigned external mismatch remains protected.
            if (configured is not null && deployment.Equals(configured, StringComparison.OrdinalIgnoreCase)) continue;
            if (!deployment.Equals(
                    installation.VersionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (Profiles.Any(p => p.RobloxVersionId == installation.VersionId)
                    && deployment.Equals(_installation?.VersionId, StringComparison.OrdinalIgnoreCase)) continue;
                mismatch = instance;
                break;
            }
        }

        if (unreadable.Count > 0)
        {
            AppendLog(
                RobloxMultiManager.Core.L.F("[launch] AVISO: {0} processo(s) sem versão legível ",unreadable.Count)
                + RobloxMultiManager.Core.L.F("(pid={0}); a abertura continua porque ",string.Join(",", unreadable))
                + RobloxMultiManager.Core.L.T("nenhuma divergência pôde ser comprovada."));
        }

        if (mismatch is null)
        {
            return true;
        }

        AppendLog(
            RobloxMultiManager.Core.L.F("[launch] BLOQUEADO: já existe pid={0}; ",mismatch.ProcessId)
            + $"requested={installation.VersionId}; "
            + $"actual={mismatch.EffectiveDeploymentId}; "
            + RobloxMultiManager.Core.L.F("verificado={0}; ",mismatch.IsVerified.ToString().ToLowerInvariant())
            + RobloxMultiManager.Core.L.T("todos os players precisam usar o mesmo deployment."));
        if (showFailure)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("O PID {0} está usando ",mismatch.ProcessId)
                + RobloxMultiManager.Core.L.F("{0}, mas a seleção atual é ",mismatch.EffectiveDeploymentId)
                + $"{installation.VersionId}.\n\n"
                + RobloxMultiManager.Core.L.T("Feche esse Roblox antes de abrir outra conta. O app não misturará versões."),
                RobloxMultiManager.Core.L.T("Versões diferentes"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    internal static bool IsSameProcessIdentity(
        RobloxLaunchReceipt receipt,
        RobloxInstanceInfo instance) =>
        receipt.ProcessId == instance.ProcessId
        && receipt.StartTimeUtc == instance.StartTimeUtc;

    internal static bool DoesReceiptMatchObservedClient(
        RobloxLaunchReceipt receipt,
        RobloxInstanceInfo instance) =>
        IsSameProcessIdentity(receipt, instance)
        && instance.VersionId is not null
        && instance.ExecutablePath is not null
        && instance.VersionId.Equals(
            receipt.RequestedVersionId,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            instance.ExecutablePath,
            receipt.RequestedExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    internal static LaunchObservationDecision ClassifyLaunchObservation(
        RobloxLaunchReceipt receipt,
        RobloxInstanceInfo? observed,
        bool initialProcessAlive,
        bool updaterRunning,
        TimeSpan readyStableFor,
        bool timedOut)
    {
        if (observed is not null
            && !DoesReceiptMatchObservedClient(receipt, observed))
        {
            return LaunchObservationDecision.Diverged;
        }

        if (observed is null && !initialProcessAlive)
        {
            return LaunchObservationDecision.InitialProcessExited;
        }

        if (timedOut)
        {
            return updaterRunning && observed is not null
                ? LaunchObservationDecision.UpdaterStillRunning
                : LaunchObservationDecision.ReadinessTimedOut;
        }

        if (observed is null)
        {
            return LaunchObservationDecision.WaitingForProcess;
        }

        if (updaterRunning)
        {
            return LaunchObservationDecision.WaitingForUpdater;
        }

        if (!observed.HasWindow || !observed.IsResponsive)
        {
            return LaunchObservationDecision.WaitingForProcess;
        }

        return readyStableFor >= TimeSpan.FromSeconds(5)
            ? LaunchObservationDecision.Confirmed
            : LaunchObservationDecision.Stabilizing;
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private static bool IsReceiptProcessAlive(RobloxLaunchReceipt receipt)
    {
        try
        {
            using var process = Process.GetProcessById(receipt.ProcessId);
            return !process.HasExited
                   && process.StartTime.ToUniversalTime()
                       == receipt.StartTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private bool ReportLaunchDivergence(
        RobloxLaunchReceipt receipt,
        AccountProfile profile,
        RobloxInstanceInfo? observed,
        string reason,
        bool showFailure)
    {
        AppendLog(
            RobloxMultiManager.Core.L.F("[launch {0}] DIVERGÊNCIA ",receipt.AttemptTag)
            + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(profile.Name))
            + $"initialPid={receipt.ProcessId}; "
            + $"requested={receipt.RequestedVersionId}; "
            + $"observedPid={(observed?.ProcessId.ToString() ?? RobloxMultiManager.Core.L.T("nenhum"))}; "
            + $"actual={observed?.VersionId ?? RobloxMultiManager.Core.L.T("nenhum")}; "
            + $"reason={SanitizeLogValue(reason)}; "
            + "confirmed=false.");
        if (showFailure)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("A abertura iniciou o PID {0} em ",receipt.ProcessId)
                + RobloxMultiManager.Core.L.F("{0}, mas ele não permaneceu nessa versão.\n\n",receipt.RequestedVersionId)
                + RobloxMultiManager.Core.L.F("{0}. O atualizador oficial do Roblox pode trocar o deployment ",reason)
                + RobloxMultiManager.Core.L.T("conforme o canal atribuído à conta. Nenhuma outra versão foi atribuída ")
                + RobloxMultiManager.Core.L.T("automaticamente ao perfil, e a próxima conta não será aberta."),
                RobloxMultiManager.Core.L.T("Roblox trocou a versão"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private bool ReportInitialProcessExited(
        RobloxLaunchReceipt receipt,
        AccountProfile profile,
        RobloxInstanceInfo? unassignedPlayer,
        bool showFailure)
    {
        var extra = unassignedPlayer is null
            ? RobloxMultiManager.Core.L.T("nenhum novo player foi observado")
            : RobloxMultiManager.Core.L.F("surgiu pid={0}, mas ele não é atribuível à tentativa",unassignedPlayer.ProcessId);

        // The client writes why it died to its own log. Reporting only what this app
        // saw from outside names no cause and suggests no action.
        var failure = _failureReader.Read(receipt.StartTimeUtc);
        AppendLog(
            RobloxMultiManager.Core.L.F("[launch {0}] ENCERRADO ",receipt.AttemptTag)
            + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(profile.Name))
            + $"initialPid={receipt.ProcessId}; "
            + $"requested={receipt.RequestedVersionId}; "
            + $"observedPid={(unassignedPlayer?.ProcessId.ToString() ?? RobloxMultiManager.Core.L.T("nenhum"))}; "
            + $"actual={unassignedPlayer?.VersionId ?? RobloxMultiManager.Core.L.T("nenhum")}; "
            + RobloxMultiManager.Core.L.F("reason=o PID inicial encerrou antes da confirmação e {0}; ",extra)
            + $"clienteMotivo={failure?.Kind ?? RobloxMultiManager.Core.L.T("não registrado no log do cliente")}"
            + (failure?.Win32Error is { } code ? RobloxMultiManager.Core.L.F("; clienteErro={0}",code) : "")
            + (failure is null ? "" : RobloxMultiManager.Core.L.F("; clienteLog={0}",failure.LogFileName))
            + RobloxMultiManager.Core.L.T("; divergência=não-comprovada; confirmed=false."));

        if (!showFailure)
        {
            return false;
        }

        var observed = unassignedPlayer is null
            ? RobloxMultiManager.Core.L.T("Nenhum Player substituto foi observado.")
            : RobloxMultiManager.Core.L.F("O PID {0} apareceu, mas não pode ser atribuído com segurança a esta conta.",unassignedPlayer.ProcessId);

        if (failure is null)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("O PID inicial {0} encerrou antes de concluir a confirmação.\n\n",receipt.ProcessId)
                + observed
                + RobloxMultiManager.Core.L.T("\n\nO cliente não deixou nenhum motivo no log dele, então não há ")
                + RobloxMultiManager.Core.L.T("causa a apontar aqui. Isso não foi classificado como troca de ")
                + RobloxMultiManager.Core.L.T("versão, e a próxima conta não será aberta automaticamente."),
                RobloxMultiManager.Core.L.T("Player inicial encerrado"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (failure.BlockingSelfUpdateMayFix)
        {
            ReportSelfUpdateAndEnforcePin(receipt, failure);
            return false;
        }

        if (!failure.ClosingAllClientsMayFix)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("O PID inicial {0} encerrou antes de concluir a confirmação.\n\n",receipt.ProcessId)
                + observed
                + RobloxMultiManager.Core.L.F("\n\nMotivo registrado pelo próprio cliente:\n{0}",failure.Summary)
                + RobloxMultiManager.Core.L.T("\n\nA próxima conta não será aberta automaticamente."),
                RobloxMultiManager.Core.L.T("Player inicial encerrado"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var answer = AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.F("O PID inicial {0} encerrou antes de concluir a confirmação.\n\n",receipt.ProcessId)
            + RobloxMultiManager.Core.L.F("Motivo registrado pelo próprio cliente:\n{0}",failure.Summary)
            + RobloxMultiManager.Core.L.T("\n\nFechar agora todos os processos Roblox e liberar esse estado? ")
            + RobloxMultiManager.Core.L.T("Isso encerra qualquer Roblox aberto, inclusive os que estão em jogo, ")
            + RobloxMultiManager.Core.L.T("e não abre nada de volta sozinho."),
            RobloxMultiManager.Core.L.T("O Roblox travou na trava de instância única"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes)
        {
            CloseEveryRobloxProcess();
        }

        return false;
    }

    /// <summary>
    /// Reports that the pinned deployment replaced itself and re-applies the pin.
    /// </summary>
    /// <remarks>
    /// This used to ask whether to block the updater.  It no longer asks: a version
    /// the user pinned is the version that must run, so the block is policy, applied
    /// by <see cref="EnforceVersionPin"/> the moment a version is selected.  Reaching
    /// here means the pin was not in force, normally because Roblox reinstalled that
    /// folder and put its updater back, so the pin is simply re-applied.
    /// </remarks>
    private void ReportSelfUpdateAndEnforcePin(
        RobloxLaunchReceipt receipt,
        RobloxClientFailure failure)
    {
        var applied = EnforceVersionPin();
        var tail = applied switch
        {
            SelfUpdateState.Blocked =>
                RobloxMultiManager.Core.L.T("A atualização automática desta versão foi bloqueada agora. ")
                + RobloxMultiManager.Core.L.T("Tente abrir novamente."),
            SelfUpdateState.NoUpdaterPresent =>
                RobloxMultiManager.Core.L.F("Esta pasta de versão não tem {0}, ",RobloxSelfUpdateBlocker.UpdaterFileName)
                + RobloxMultiManager.Core.L.T("então a substituição veio de outro caminho e vale olhar o log do ")
                + RobloxMultiManager.Core.L.T("cliente."),
            _ =>
                RobloxMultiManager.Core.L.T("Não foi possível aplicar a fixação desta versão; o motivo está no ")
                + RobloxMultiManager.Core.L.T("log de atividade.")
        };

        AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.F("O PID inicial {0} encerrou antes de concluir a confirmação.\n\n",receipt.ProcessId)
            + RobloxMultiManager.Core.L.F("Motivo registrado pelo próprio cliente:\n{0}\n\n",failure.Summary)
            + tail,
            RobloxMultiManager.Core.L.T("A versão fixada se atualizou sozinha"),
            MessageBoxButton.OK,
            applied == SelfUpdateState.Blocked
                ? MessageBoxImage.Information
                : MessageBoxImage.Warning);
    }

    /// <summary>
    /// Makes the selected version actually stay selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Policy, not a preference: when a deployment is pinned, the client's own
    /// <c>RobloxPlayerInstaller.exe</c> is renamed away inside that version folder,
    /// so the client cannot ask the server what is current and replace itself.  This
    /// runs on every selection change and on startup, because Roblox restores that
    /// file whenever it reinstalls the folder.
    /// </para>
    /// <para>
    /// The complement matters as much: on "Automático" the updater is put back, so
    /// automatic mode can still update.  Blocking there would silently freeze the
    /// app on whatever version happened to be installed.
    /// </para>
    /// </remarks>
    private SelfUpdateState EnforceVersionPin()
    {
        var folder = Path.GetDirectoryName(_installation?.ExecutablePath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return SelfUpdateState.NoUpdaterPresent;
        }

        var pinned = !string.IsNullOrWhiteSpace(_settings.SelectedRobloxVersionId);
        var current = _selfUpdateBlocker.Inspect(folder);
        if (pinned == (current == SelfUpdateState.Blocked))
        {
            return current;
        }

        var (state, message) = pinned
            ? _selfUpdateBlocker.Block(folder)
            : _selfUpdateBlocker.Unblock(folder);
        AppendLog(
            $"[version] {(pinned ? RobloxMultiManager.Core.L.T("FIXADA") : RobloxMultiManager.Core.L.T("AUTOMÁTICA"))} "
            + RobloxMultiManager.Core.L.F("{0}: estado={1}; ",_installation?.VersionId ?? "n/d",state)
            + $"{SanitizeLogValue(message)}");
        return state;
    }

    /// <summary>
    /// Closes every Roblox process, the clients and the crash handler alike.
    /// </summary>
    /// <remarks>
    /// This is the remedy measured for the single-instance handoff crash, and it is
    /// the whole point that it is not selective: with only the crash handler left
    /// behind, launching still failed.  It is destructive and only ever runs from an
    /// explicit confirmation.
    /// </remarks>
    private void CloseEveryRobloxProcess()
    {
        var closed = 0;
        var failed = 0;
        foreach (var name in (string[])["RobloxPlayerBeta", "RobloxCrashHandler"])
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                        closed++;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AppendLog(
                            RobloxMultiManager.Core.L.F("[process] LIMPEZA FALHOU {0} pid={1}: ",name,process.Id)
                            + $"{ex.GetType().Name}: {SanitizeLogValue(ex.Message)}.");
                    }
                }
            }
        }

        AppendLog(
            RobloxMultiManager.Core.L.F("[process] LIMPEZA DA TRAVA: {0} processo(s) Roblox encerrado(s)",closed)
            + (failed > 0 ? RobloxMultiManager.Core.L.F("; {0} não puderam ser encerrados",failed) : "")
            + RobloxMultiManager.Core.L.T(". Tente abrir novamente."));
    }

    private bool ReportUpdaterStillRunning(
        RobloxLaunchReceipt receipt,
        AccountProfile profile,
        RobloxInstanceInfo observed,
        bool showFailure)
    {
        AppendLog(
            RobloxMultiManager.Core.L.F("[launch {0}] UPDATER PENDENTE ",receipt.AttemptTag)
            + RobloxMultiManager.Core.L.F("perfil=\"{0}\"; ",SanitizeLogValue(profile.Name))
            + $"pid={observed.ProcessId}; requested={receipt.RequestedVersionId}; "
            + $"actual={observed.VersionId}; match=true; confirmed=false; "
            + RobloxMultiManager.Core.L.T("reason=atualizador ativo após o limite de 4 minutos."));
        if (showFailure)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.F("O PID {0} ainda corresponde a ",observed.ProcessId)
                + RobloxMultiManager.Core.L.F("{0}, mas o atualizador oficial ",receipt.RequestedVersionId)
                + RobloxMultiManager.Core.L.T("não terminou dentro de 4 minutos.\n\n")
                + RobloxMultiManager.Core.L.T("Isso não foi classificado como troca de versão. A janela pode ")
                + RobloxMultiManager.Core.L.T("continuar aberta; aguarde a atualização terminar e tente novamente."),
                RobloxMultiManager.Core.L.T("Atualização ainda em andamento"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private bool ReportLaunchReadinessTimeout(
        RobloxLaunchReceipt receipt,
        AccountProfile profile,
        bool updaterObserved,
        bool showFailure)
    {
        AppendLog(
            RobloxMultiManager.Core.L.F("[launch {0}] TIMEOUT perfil=\"",receipt.AttemptTag)
            + $"{SanitizeLogValue(profile.Name)}\"; initialPid={receipt.ProcessId}; "
            + $"requested={receipt.RequestedVersionId}; "
            + $"updaterObserved={updaterObserved.ToString().ToLowerInvariant()}; "
            + RobloxMultiManager.Core.L.T("janela não confirmada; divergência=false."));
        if (showFailure)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("O PID inicial não manteve uma janela responsiva pelo período de confirmação. ")
                + RobloxMultiManager.Core.L.T("Nenhuma troca de versão foi comprovada nem atribuída automaticamente à conta."),
                RobloxMultiManager.Core.L.T("Player não confirmado"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private static string GetSafeClientPath(string? executablePath) =>
        RobloxClientExecutableInspector.TryInspect(
            executablePath,
            out var client)
            ? client.SafeDisplayPath
            : RobloxMultiManager.Core.L.T("indisponível");

    private string? GetConfiguredLaunchChannel(
        RobloxInstallationInfo installation)
    {
        if (string.IsNullOrWhiteSpace(
                _settings.SelectedRobloxVersionId))
        {
            return null;
        }

        try
        {
            return RobloxVersionChecker.NormalizeChannel(
                _settings.SelectedRobloxChannel ?? "LIVE");
        }
        catch (ArgumentException)
        {
            AppendLog(
                RobloxMultiManager.Core.L.T("[version] Canal fixado inválido; launch bloqueado para ")
                + $"{installation.VersionId}.");
            return "LIVE";
        }
    }

    /// <summary>
    /// Makes a value safe for one log line.
    /// </summary>
    /// <remarks>
    /// The default cap suits short fields like names and version ids.  A failure
    /// detail carries an exception type, an exit code and a path, and truncating
    /// that at 160 characters cut off the part that identifies the problem, so
    /// those call sites pass a larger cap.
    /// </remarks>
    internal static string SanitizeLogValue(string? value, int maxLength = 160)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "n/d";
        }

        var sanitized = new string(value
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray())
            .Trim();
        return sanitized.Length <= maxLength
            ? sanitized
            : sanitized[..maxLength];
    }

    private static bool IsOfficialInstallation(RobloxInstallationInfo? installation)
    {
        if (installation?.ExecutablePath is null
            || !File.Exists(installation.ExecutablePath)
            || installation.AppearsSigned != true
            || !RobloxInstallationLocator.IsValidVersionId(installation.VersionId)
            || !Path.GetFileName(installation.ExecutablePath).Equals(
                "RobloxPlayerBeta.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Two roots are accepted: the one Roblox maintains, and the app-owned one
        // that holds versions downloaded on request.  The Authenticode
        // requirement above is what makes a client official, not the folder it
        // sits in, so only the prefix is relaxed here.
        var executable = Path.GetFullPath(installation.ExecutablePath);
        var roots = new[]
        {
            Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Roblox",
                "Versions")),
            Path.GetFullPath(RobloxInstallationLocator.ManagedVersionsRoot)
        };
        return roots.Any(root => executable.StartsWith(
                   root.TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase))
            && new DirectoryInfo(Path.GetDirectoryName(executable)!).Name.Equals(
                installation.VersionId,
                StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException CreateInstallationException(
        RobloxInstallationInfo? installation)
    {
        var reason = installation?.Source ?? RobloxMultiManager.Core.L.T("Cliente não encontrado");
        return new InvalidOperationException(
            RobloxMultiManager.Core.L.F("{0}. Abra Versões e escolha um cliente com assinatura Roblox válida, ",reason)
            + RobloxMultiManager.Core.L.T("ou baixe o deployment desejado por hash. ")
            + RobloxMultiManager.Core.L.T("As instalações oficiais em %LOCALAPPDATA%\\Roblox nunca são alteradas."));
    }

    private void ArrangeButton_Click(object sender, RoutedEventArgs e)
    {
        ArrangeWindows(logResult: true);
    }

    /// <summary>
    /// Opens the deployment browser in the sliding panel.
    /// </summary>
    /// <remarks>
    /// It used to be a modal window that appeared centred, after a visible delay
    /// spent creating the window.  The panel is a control now, so it slides in over
    /// the dimmed page like Diagnóstico does.  The await is on the panel's own
    /// close request rather than on ShowDialog, which is why this no longer runs a
    /// nested dispatcher frame.
    /// </remarks>
    private async void VersionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DrawerHost.Visibility == Visibility.Visible) return;
        var canChangeSelection =
            _launchBatchCancellation is null && Instances.Count == 0;
        var panel = new VersionsPanel(
            _installationLocator,
            _versionChecker,
            _deploymentCatalog,
            _deploymentDownloader,
            _settings.SelectedRobloxVersionId,
            _settings.SelectedRobloxChannel,
            canChangeSelection);

        var dismissed = new TaskCompletionSource();
        panel.CloseRequested += (_, _) => dismissed.TrySetResult();
        OpenDrawer(
            RobloxMultiManager.Core.L.T("Versões do Roblox"),
            RobloxMultiManager.Core.L.T("Todos os deployments publicados, do mais recente ao mais antigo. Escolha ")
            + RobloxMultiManager.Core.L.T("um instalado ou baixe qualquer hash direto da CDN da Roblox."),
            panel);
        _drawerDismissal = dismissed;
        await dismissed.Task;
        _drawerDismissal = null;
        CloseDrawer();

        if (!panel.Confirmed)
        {
            return;
        }

        var dialog = panel;

        if (_launchBatchCancellation is not null || Instances.Count > 0)
        {
            AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("A versão não foi alterada porque uma fila ou janela do Roblox está ativa. ")
                + RobloxMultiManager.Core.L.T("Feche as instâncias e tente novamente."),
                RobloxMultiManager.Core.L.T("Versões"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _settings = _settings with
        {
            SelectedRobloxVersionId = dialog.SelectedVersionId,
            SelectedRobloxChannel = dialog.SelectedChannel
        };
        await SaveSettingsAsync();
        RefreshInstallationInfo();
        AppendLog(
            RobloxMultiManager.Core.L.T("[version] SELEÇÃO APLICADA mode=")
            + (string.IsNullOrWhiteSpace(dialog.SelectedVersionId)
                ? "automatic"
                : "fixed")
            + $"; requested={_installation?.VersionId ?? RobloxMultiManager.Core.L.T("indisponível")}; "
            + $"fileVersion={_installation?.Version ?? "n/d"}; "
            + $"channel={_settings.SelectedRobloxChannel ?? "automatic"}; "
            + $"path={GetSafeClientPath(_installation?.ExecutablePath)}.");
        AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("Seleção aplicada:\n\n")
            + RobloxMultiManager.Core.L.F("Versão: {0}\n",_installation?.Version ?? "n/d")
            + $"Deployment: {_installation?.VersionId ?? RobloxMultiManager.Core.L.T("indisponível")}\n"
            + RobloxMultiManager.Core.L.F("Canal: {0}\n",_settings.SelectedRobloxChannel ?? RobloxMultiManager.Core.L.T("Automático"))
            + RobloxMultiManager.Core.L.F("Modo: {0}\n\n",(string.IsNullOrWhiteSpace(dialog.SelectedVersionId) ? RobloxMultiManager.Core.L.T("Automático") : "Fixado"))
            + RobloxMultiManager.Core.L.T("Cada abertura registrará o PID e verificará se o Roblox permaneceu nessa versão."),
            RobloxMultiManager.Core.L.T("Versão aplicada"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RefreshInstallationInfo()
    {
        _installation = _installationLocator.Resolve(
            _settings.SelectedRobloxVersionId);
        UpdateInstallationLabel();
        // Keep version policy in the explicit installation refresh, never in a language change.
        if (_installation.IsFound) _ = EnforceVersionPin();
    }

    private void UpdateInstallationLabel()
    {
        if (_installation?.IsFound != true)
        {
            InstallText.Text = RobloxMultiManager.Core.L.T("Roblox indisponível");
            return;
        }

        var mode = string.IsNullOrWhiteSpace(
            _settings.SelectedRobloxVersionId)
            ? "Auto"
            : RobloxMultiManager.Core.L.T("Fixada");
        var channel = string.IsNullOrWhiteSpace(
            _settings.SelectedRobloxVersionId)
            ? string.Empty
            : $" · {_settings.SelectedRobloxChannel ?? "LIVE"}";
        InstallText.Text =
            $"{mode}{channel} · Roblox {_installation.Version ?? _installation!.VersionId}";

    }

    /// <summary>
    /// Tiles every controllable window inside the chosen monitor's work area.
    /// </summary>
    /// <remarks>
    /// The log reports containment separately from movement.  Being positioned and
    /// being on screen are different claims: a client that refuses to shrink into
    /// its cell is pulled back to the screen edge, and if it cannot fit the monitor
    /// at all that has to be said rather than counted as a clean success.
    /// </remarks>
    private async void ArrangeWindows(bool logResult)
    {
        if(_vaultLocked)return;
        _arrangeLogRequested |= logResult;
        if (_arrangeInFlight) { _arrangeAgain = true; return; }
        _arrangeInFlight = true;
        try
        {
            do
            {
                _arrangeAgain = false;
                var windows = Instances.ToArray();
                var monitor = _settings.SelectedMonitor;
                // Native restores and placement waits never block WPF input.
                var report = await Task.Run(() => _layout.ArrangeWithReport(windows, monitor));
                if (_shutdownStarted) return;
                if (_arrangeLogRequested)
                {
                    _arrangeLogRequested = false;
                    LogArrangement(report);
                }
            } while (_arrangeAgain && !_shutdownStarted);
        }
        catch (Exception ex)
        {
            if (!_shutdownStarted) AppendLog(L.F("Não foi possível organizar as janelas ({0}).", ex.GetType().Name));
        }
        finally { _arrangeInFlight = false; }
    }

    private void LogArrangement(ArrangeReport report)
    {
        if (report.Moved == 0)
        {
            AppendLog(RobloxMultiManager.Core.L.T("Nenhuma janela controlável foi encontrada para organizar."));
            return;
        }

        var message = RobloxMultiManager.Core.L.F("{0} janela(s) organizadas",report.Moved);
        if (report.Overflowing > 0)
        {
            message +=
                RobloxMultiManager.Core.L.F("; {0} não caberam na tela porque o cliente recusou ",report.Overflowing)
                + RobloxMultiManager.Core.L.T("ficar menor, e foram encostadas na borda");
        }

        if (report.Overlapping > 0)
            message += L.F("; {0} se sobrepõem porque o tamanho mínimo do cliente não cabe nesta grade", report.Overlapping);

        var unverifiable = report.Moved - report.Contained - report.Overflowing;
        if (unverifiable > 0)
        {
            message += RobloxMultiManager.Core.L.F("; {0} sem geometria legível",unverifiable);
        }

        AppendLog($"{message}.");
    }

    private void FocusInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (InstancesGrid.SelectedItem is not RobloxInstanceInfo selected)
        {
            ShowSelectInstanceMessage();
            return;
        }

        if (!_layout.Focus(selected))
        {
            AppendLog(RobloxMultiManager.Core.L.F("A janela do PID {0} não pôde receber foco.",selected.ProcessId));
        }
    }

    /// <summary>
    /// Force-terminates every selected client.
    /// </summary>
    /// <remarks>
    /// The selection is copied before anything else happens: the instance list is
    /// rebuilt roughly once a second, and reading
    /// <see cref="System.Windows.Controls.DataGrid.SelectedItems"/> lazily would
    /// lose rows mid-loop.  Each item still goes through the per-process identity
    /// gauntlet in <see cref="WindowLayoutService.ForceTerminate"/>, so a recycled
    /// process id is rejected individually rather than taken down with the batch.
    /// </remarks>
    /// <summary>
    /// Terminates the client whose row X was clicked.
    /// </summary>
    /// <remarks>
    /// The row's own DataContext is the subject, which is what makes closing one
    /// client independent of the selection surviving the once-a-second refresh.
    /// When the clicked row is part of a multi-row selection the whole selection is
    /// taken, so closing a group is still one action.
    /// </remarks>
    private void CloseInstanceRowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: RobloxInstanceInfo clicked
            })
        {
            return;
        }

        var selection = InstancesGrid.SelectedItems
            .OfType<RobloxInstanceInfo>()
            .ToArray();
        var subjects = selection.Any(instance =>
            string.Equals(instance.Key, clicked.Key, StringComparison.Ordinal))
            ? selection
            : [clicked];
        TerminateInstances(subjects);
    }

    private void CloseInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        var selection = InstancesGrid.SelectedItems
            .OfType<RobloxInstanceInfo>()
            .ToArray();
        if (selection.Length == 0)
        {
            ShowSelectInstanceMessage();
            return;
        }

        TerminateInstances(selection);
    }

    private void TerminateInstances(IReadOnlyList<RobloxInstanceInfo> requested)
    {
        if(_vaultLocked)return;
        var selection = requested
            .OrderBy(instance => instance.StartTimeUtc)
            .ThenBy(instance => instance.ProcessId)
            .ToArray();
        if (selection.Length == 0)
        {
            return;
        }

        var detail = selection.Length == 1
            ? $"PID {selection[0].ProcessId}\n"
              + RobloxMultiManager.Core.L.F("Perfil: {0}\n",selection[0].ProfileText)
              + RobloxMultiManager.Core.L.F("Início UTC: {0:O}\n",selection[0].StartTimeUtc)
              + RobloxMultiManager.Core.L.F("Versão: {0}",selection[0].FileVersion ?? selection[0].EffectiveDeploymentId ?? RobloxMultiManager.Core.L.T("não verificada"))
            : string.Join(
                "\n",
                selection.Select(instance =>
                    $"PID {instance.ProcessId} · {instance.ProfileText}"));
        var answer = AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.F("Encerrar à força {0} tarefa(s) do Roblox?\n\n",selection.Length)
            + detail
            + RobloxMultiManager.Core.L.T("\n\nO jogo perderá qualquer estado não salvo. Somente os PIDs ")
            + RobloxMultiManager.Core.L.T("listados são afetados."),
            selection.Length == 1
                ? RobloxMultiManager.Core.L.T("Encerrar tarefa do Roblox")
                : RobloxMultiManager.Core.L.F("Encerrar {0} tarefas do Roblox",selection.Length),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = 0;
        var failures = new List<string>();
        foreach (var instance in selection)
        {
            // Recorded before the kill: the monitor may notice the disappearance on
            // the very next sweep, and auto reconnect must never reopen something
            // the user just asked to close.
            _intentionalClosures.Add(instance.Key);
            var result = _layout.ForceTerminate(instance);
            AppendLog(
                RobloxMultiManager.Core.L.F("[process] FINALIZAÇÃO PID {0}; ",instance.ProcessId)
                + $"code={result.Code}; requested={result.TerminationRequested.ToString().ToLowerInvariant()}; "
                + $"confirmed={result.Succeeded.ToString().ToLowerInvariant()}; "
                + $"message={SanitizeLogValue(result.Message)}.");
            if (result.Succeeded)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"PID {instance.ProcessId}: {result.Message}");
            }
        }

        if (selection.Length > 1)
        {
            AppendLog(
                RobloxMultiManager.Core.L.F("[process] FINALIZAÇÃO EM LOTE: {0} de ",succeeded)
                + RobloxMultiManager.Core.L.F("{0} confirmada(s).",selection.Length));
        }

        if (failures.Count > 0)
        {
            AppDialog.Show(
                this,
                string.Join("\n", failures),
                failures.Count == selection.Length
                    ? RobloxMultiManager.Core.L.T("Não foi possível encerrar a tarefa")
                    : RobloxMultiManager.Core.L.T("Algumas tarefas não foram encerradas"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void TargetInput_LostFocus(object sender, RoutedEventArgs e)
    {
        var target = GetTarget();
        TargetInput.Text = target.ToString();
        _settings = _settings with { TargetInstances = target };
        await SaveSettingsAsync();
        UpdateConfirmation();
    }

    private async void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _updatingInterfaceLanguage || MonitorCombo.SelectedItem is not MonitorChoice choice)
        {
            return;
        }

        _settings = _settings with { SelectedMonitor = choice.Index };
        await SaveSettingsAsync();
        if (_settings.AutoArrange)
        {
            ArrangeWindows(logResult: false);
        }
    }

    private async void AutoArrangeCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _settings = _settings with { AutoArrange = AutoArrangeCheck.IsChecked == true };
        await SaveSettingsAsync();
        if (_settings.AutoArrange)
        {
            ArrangeWindows(logResult: false);
        }
    }

    private int GetTarget()
    {
        if (!int.TryParse(TargetInput.Text, out var target))
        {
            target = _settings.TargetInstances;
        }

        return Math.Clamp(target, 1, 100);
    }

    private async void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (DrawerHost.Visibility == Visibility.Visible) return;
        var loading = new ReportPanel(RobloxMultiManager.Core.L.T("DIAGNÓSTICO"), RobloxMultiManager.Core.L.T("Conhecendo seu ambiente…"), RobloxMultiManager.Core.L.T("Consultando instalações e assinatura do cliente."), RobloxMultiManager.Core.L.T("O relatório aparecerá aqui quando a leitura terminar."), []);
        OpenDrawer(RobloxMultiManager.Core.L.T("Diagnóstico"), RobloxMultiManager.Core.L.T("Estado do ambiente e das sessões."), loading);
        var selectedVersion = _settings.SelectedRobloxVersionId;
        try
        {
            var snapshot = await Task.Run(() => _installationLocator.Inspect(selectedVersion));
            if (!ReferenceEquals(DrawerBody.Content, loading) || DrawerHost.Visibility != Visibility.Visible) return;
            _installation = snapshot.Effective;
            var report = BuildDiagnosticReport(snapshot.Installed);
            ShowDiagnosticWindow(report);
            AppendLog(RobloxMultiManager.Core.L.T("Relatório de diagnóstico gerado sem dados de autenticação."));
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(DrawerBody.Content, loading))
                DrawerBody.Content = new ReportPanel(RobloxMultiManager.Core.L.T("DIAGNÓSTICO"), RobloxMultiManager.Core.L.T("Leitura indisponível"), RobloxMultiManager.Core.L.T("Feche o painel e tente novamente."), SanitizeLogValue(ex.Message), []);
        }
    }

    private string BuildDiagnosticReport(IReadOnlyList<RobloxInstalledVersionInfo> installedSnapshot)
    {
        var monitors = _layout.GetMonitors();
        var builder = new StringBuilder();
        builder.AppendLine(RobloxMultiManager.Core.L.T("MULTIJANELA PARA ROBLOX — DIAGNÓSTICO"));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Gerado: {0:yyyy-MM-dd HH:mm:ss zzz}",DateTimeOffset.Now));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Sistema: {0}",RuntimeInformation.OSDescription));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Arquitetura: {0}",RuntimeInformation.OSArchitecture));
        builder.AppendLine($"Runtime: .NET {Environment.Version}");
        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.F("Trava: {0}",_guard.State));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Posse do mutex: {0}",_guard.OwnsMutex));
        builder.AppendLine(
            RobloxMultiManager.Core.L.F("Multi-janela completo: {0}",_lastGuardResult?.IsFullyHeld.ToString() ?? "n/d"));

        // The object TYPE behind these names is what decides whether a second client
        // opens, so it belongs in the report: a future occurrence then carries its
        // own evidence instead of needing a separate probe.
        foreach (var name in (string[])
                 [
                     MultiInstanceGuard.DefaultMutexName,
                     MultiInstanceGuard.DefaultEventName
                 ])
        {
            builder.AppendLine(
                $"  {name}: {MultiInstanceGuard.DescribeNamedObject(name)}");
        }

        builder.AppendLine(RobloxMultiManager.Core.L.F("Processos Roblox: {0}",Instances.Count));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Janelas prontas: {0}",Instances.Count(item => item.HasWindow && item.IsResponsive)));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Alvo configurado: {0}",GetTarget()));
        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.F("Cliente encontrado: {0}",_installation?.IsFound == true));
        builder.AppendLine(
            RobloxMultiManager.Core.L.F("Seleção: {0}",(string.IsNullOrWhiteSpace(_settings.SelectedRobloxVersionId) ? RobloxMultiManager.Core.L.T("Automático") : _settings.SelectedRobloxVersionId)));
        builder.AppendLine(
            RobloxMultiManager.Core.L.F("Canal fixado: {0}",_settings.SelectedRobloxChannel ?? RobloxMultiManager.Core.L.T("automático")));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Origem: {0}",_installation?.Source ?? "n/d"));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Executável: {0}",_installation?.ExecutablePath ?? "n/d"));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Versão: {0}",_installation?.Version ?? "n/d"));
        builder.AppendLine($"Deployment: {_installation?.VersionId ?? "n/d"}");
        builder.AppendLine(RobloxMultiManager.Core.L.F("Canal detectado: {0}",_installation?.Channel ?? "n/d"));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Publicador: {0}",_installation?.Publisher ?? "n/d"));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Assinatura aparenta Roblox: {0}",_installation?.AppearsSigned == true));
        builder.AppendLine($"Handler: {_installation?.ProtocolCommand ?? "n/d"}");
        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.T("Instalações locais:"));
        foreach (var installed in installedSnapshot)
        {
            builder.AppendLine(
                $"  {installed.VersionId} | {installed.FileVersion ?? "n/d"} | "
                + RobloxMultiManager.Core.L.F("{0} | assinatura: {1}",installed.StatusText,installed.SignatureText));
        }
        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.T("Processos observados:"));
        if (Instances.Count == 0)
        {
            builder.AppendLine(RobloxMultiManager.Core.L.T("  nenhum"));
        }
        else
        {
            foreach (var instance in Instances)
            {
                builder.AppendLine(
                    RobloxMultiManager.Core.L.F("  PID {0} | início {1:O} | ",instance.ProcessId,instance.StartTimeUtc)
                    + RobloxMultiManager.Core.L.F("perfil {0} | ",SanitizeLogValue(instance.ProfileText))
                    + RobloxMultiManager.Core.L.F("solicitada {0} | ",instance.RequestedVersionId ?? "n/d")
                    + RobloxMultiManager.Core.L.F("execução {0} | ",instance.VersionId ?? RobloxMultiManager.Core.L.T("não verificada"))
                    + RobloxMultiManager.Core.L.F("versão {0} | ",instance.FileVersion ?? "n/d")
                    + RobloxMultiManager.Core.L.F("compatibilidade {0} | ",instance.VersionMatchText)
                    + RobloxMultiManager.Core.L.F("janela {0}",(instance.HasWindow ? "sim" : RobloxMultiManager.Core.L.T("não"))));
            }
        }
        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.T("Sessões de conta: WebView2 isolado por perfil"));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Perfis cadastrados: {0}",Profiles.Count));
        builder.AppendLine(RobloxMultiManager.Core.L.F("Dados locais: {0}",_paths.Root));
        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.F("Monitores: {0}",monitors.Count));
        for (var index = 0; index < monitors.Count; index++)
        {
            var monitor = monitors[index];
            builder.AppendLine(
                $"  {index + 1}: {monitor.Name} {monitor.WorkingArea.Width}x{monitor.WorkingArea.Height}"
                + (monitor.IsPrimary ? RobloxMultiManager.Core.L.T(" (principal)") : string.Empty));
        }

        builder.AppendLine();
        builder.AppendLine(RobloxMultiManager.Core.L.T("O relatório não inclui senha, cookie, ticket, link privado ou linha de comando do cliente."));
        return builder.ToString();
    }

    /// <summary>
    /// Shows the diagnostic report in the side drawer instead of a second window.
    /// </summary>
    /// <remarks>
    /// It used to open a 760x590 OS window on top of the app. A drawer keeps the
    /// report in the same surface as everything else, and the report stays readable
    /// while the activity log behind it keeps updating.
    /// </remarks>
    private void ShowDiagnosticWindow(string report)
    {
        var content = new ReportPanel(RobloxMultiManager.Core.L.T("DIAGNÓSTICO / LOCAL"), RobloxMultiManager.Core.L.T("Seu ambiente, em detalhes."),
            RobloxMultiManager.Core.L.T("Estado medido nesta consulta. O relatório não inclui dados de autenticação."), report,
            [new(RobloxMultiManager.Core.L.T("PROCESSOS"), Instances.Count.ToString()), new(RobloxMultiManager.Core.L.T("JANELAS"), Instances.Count(i => i.HasWindow).ToString()), new(RobloxMultiManager.Core.L.T("CONTAS"), Profiles.Count.ToString())]);
        if (DrawerHost.Visibility == Visibility.Visible) DrawerBody.Content = content;
        else OpenDrawer(RobloxMultiManager.Core.L.T("Diagnóstico"), RobloxMultiManager.Core.L.T("Estado do ambiente e das sessões."), content);
    }

    /// <summary>
    /// Slides a panel in from the right, over a dimmed application.
    /// </summary>
    private int _drawerGeneration;
    private System.Windows.IInputElement? _drawerFocusReturn;
    private void OpenDrawer(string title, string subtitle, UIElement content)
    {
        var generation = ++_drawerGeneration;
        if (DrawerHost.Visibility != Visibility.Visible)
            _drawerFocusReturn = System.Windows.Input.Keyboard.FocusedElement;
        _drawerDismissal = null;
        DrawerTitle.Text = title;
        DrawerSubtitle.Text = subtitle;
        DrawerBody.Content = content;
        DrawerHost.Visibility = Visibility.Visible;
        ShowScrim(true);
        UiMotion.Animate(DrawerShift, TranslateTransform.XProperty, 24, 0, 180, UiMotion.Enabled && IsVisible);
        if (IsVisible) Dispatcher.BeginInvoke(() =>
        {
            if (generation == _drawerGeneration && DrawerHost.IsVisible)
                DrawerHost.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        }, DispatcherPriority.Input);
    }

    private void CloseDrawer()
    {
        if (DrawerHost.Visibility != Visibility.Visible) return;
        var generation = ++_drawerGeneration;
        UiMotion.Animate(DrawerShift, TranslateTransform.XProperty, DrawerShift.X, 24, 140,
            UiMotion.Enabled && IsVisible, () =>
            {
                if (generation != _drawerGeneration) return;
                DrawerHost.Visibility = Visibility.Collapsed;
                DrawerBody.Content = null;
                if (_drawerFocusReturn is UIElement { IsVisible: true, IsEnabled: true } previous) previous.Focus();
                _drawerFocusReturn = null;
            });
        ShowScrim(false);
    }

    /// <summary>
    /// Dismisses whatever the drawer is showing.
    /// </summary>
    /// <remarks>
    /// A panel that owns state, like Versões, must be allowed to refuse: it asks
    /// before abandoning a running download.  So the request goes to the panel
    /// first when there is one, and only a panel-less drawer closes immediately.
    /// </remarks>
    private void RequestDrawerDismissal()
    {
        if (DrawerBody.Content is DestinationEditorPanel { IsSaving: true }) return;
        if (_drawerDismissal is not null)
        {
            if (DrawerBody.Content is VersionsPanel panel)
            {
                panel.RequestCloseFromHost();
                return;
            }

            _drawerDismissal.TrySetResult();
            return;
        }

        CloseDrawer();
    }

    private void DrawerClose_Click(object sender, RoutedEventArgs e) =>
        RequestDrawerDismissal();

    /// <summary>
    /// Clicking the dimmed area behind a panel dismisses it.
    /// </summary>
    private void Scrim_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DrawerHost.Visibility == Visibility.Visible)
        {
            RequestDrawerDismissal();
        }
    }

    /// <summary>
    /// Fades the dimming layer that sits behind anything modal.
    /// </summary>
    /// <remarks>
    /// Also used around the windows that are still separate windows, so the app
    /// behind them reads as inactive instead of looking like it could be clicked.
    /// </remarks>
    private void ShowScrim(bool visible)
    {
        if (visible) Scrim.Visibility = Visibility.Visible;
        UiMotion.Animate(Scrim, OpacityProperty, Scrim.Opacity, visible ? 1 : 0, 140,
            UiMotion.Enabled && IsVisible, () =>
            {
                if (!visible && Scrim.Opacity <= 0.01) Scrim.Visibility = Visibility.Collapsed;
            });
    }

    /// <summary>
    /// Runs a still-separate window as a modal, with the app dimmed behind it.
    /// </summary>
    private bool? ShowOverlayWindow(Window window)
    {
        ShowScrim(true);
        try
        {
            return window.ShowDialog();
        }
        finally
        {
            ShowScrim(false);
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ExitApplicationAsync();
    }

    private async Task ExitApplicationAsync(bool handoff = false)
    {
        if(_vaultLocked)handoff=true;
        if (_shutdownStarted)
        {
            return;
        }

        if (!handoff && Instances.Count > 0)
        {
            var answer = AppDialog.Show(
                this,
                RobloxMultiManager.Core.L.T("Ainda há Roblox aberto. Sair libera a trava e impede novas instâncias, ")
                + RobloxMultiManager.Core.L.T("mas não força o fechamento das janelas atuais.\n\nDeseja sair?"),
                RobloxMultiManager.Core.L.T("Sair do MultiJanela"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (!handoff)
        {
            try { await SaveWorkspaceAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        await SettingsSaveQueue.FlushAllAsync();
        _shutdownStarted = true;
        _automationTimer.Stop(); StopAutomation();
        _launchBatchCancellation?.Cancel();
        if (_localApi is not null) await _localApi.DisposeAsync();
        try { await _automationWork; } catch (Exception) { }
        await _historyWrite;
        try { await _preferencesWrite; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        _confirmationTimer.Stop();
        _arrangeTimer.Stop();
        await DisposeExpansionAsync();
        await _processMonitor.DisposeAsync();
        _reconnectTimer.Stop();
        _reconnectStop.Cancel();
        _reconnectCoordinator.CancelAll();
        await _reconnectPump;
        foreach (var observer in _ownedProcessHandles.Values) observer.Dispose();
        _ownedProcessHandles.Clear();
        await HomeCpuSlider.FlushAsync(finishInteraction: true); await HomeGraphics.FlushAsync();
        await _cpuWork;
        await _tuningWork;
        await Task.Run(() => _tuning.Dispose());
        try { await Task.Run(() => _cpuLimits.Dispose()); }
        catch (Exception) { AppendLog(L.T("Não foi possível remover todos os limites de CPU antes de sair. Reinicie as sessões afetadas.")); }
        await _guard.DisposeAsync();
        if (_notifications is not null) await _notifications.DisposeAsync();
        _notificationHttp?.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        _notifyIcon.ShowBalloonTip(
            2500,
            RobloxMultiManager.Core.L.T("MultiJanela continua ativo"),
            RobloxMultiManager.Core.L.T("O aplicativo foi mantido na área de notificação para conservar a trava multi-instância."),
            Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// The window draws its own title bar, so the three caption buttons are wired
    /// here.
    /// </summary>
    /// <remarks>
    /// Closing keeps the existing behaviour on purpose: it hides to the notification
    /// area rather than exiting, because the multi-instance lock only exists while
    /// this process is alive.  Leaving is the explicit "Sair" action.
    /// </remarks>
    private void ChromeMinimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void ChromeMaximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void ChromeClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task SaveProfilesAsync(bool throwOnError = false)
    {
        await _profilesSaveGate.WaitAsync();
        try
        {
            if (!_storageMaintenance) await _store.SaveProfilesAsync(Profiles);
        }
        catch (Exception ex)
        {
            AppendLog(RobloxMultiManager.Core.L.F("Não foi possível salvar perfis: {0}",ex.Message));
            if (throwOnError) throw;
        }
        finally { _profilesSaveGate.Release(); }
    }

    /// <summary>
    /// Persists which profile each running client belongs to.
    /// </summary>
    /// <remarks>
    /// Without this, restarting the manager relabelled every client it had opened
    /// as unattributed, because attribution lived only in memory.  The key is
    /// <c>pid:startTicks</c>, so a stale entry can never be applied to a recycled
    /// process id.
    /// </remarks>
    private async Task SaveOwnershipAsync(bool throwOnError = false)
    {
        var entries = _launchOwnership.Values.ToArray();
        await _ownershipSaveGate.WaitAsync();
        try
        {
            if (!_storageMaintenance) await _store.SaveOwnershipAsync(entries);
        }
        catch (Exception ex)
        {
            AppendLog(RobloxMultiManager.Core.L.F("Não foi possível salvar a atribuição de processos: {0}",ex.Message));
            if (throwOnError) throw;
        }
        finally
        {
            _ownershipSaveGate.Release();
        }
    }

    private async Task SaveSettingsAsync(bool throwOnError = false)
    {
        await _settingsSaveGate.WaitAsync();
        try
        {
            if (!_storageMaintenance) await _store.SaveSettingsAsync(_settings);
        }
        catch (Exception ex)
        {
            AppendLog(RobloxMultiManager.Core.L.F("Não foi possível salvar configurações: {0}",ex.Message));
            if (throwOnError) throw;
        }
        finally
        {
            _settingsSaveGate.Release();
        }
    }

    private void AppendLog(string message)
    {
        var line =
            $"[{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz}] {message}";
        LogText.AppendText(line + Environment.NewLine);
        LogText.ScrollToEnd();
        try
        {
            File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The in-memory UI log remains available.
        }
    }

    private void ShowLaunchError(Exception exception)
    {
        AppendLog(RobloxMultiManager.Core.L.F("Falha ao abrir perfil: {0}",exception.Message));
        AppDialog.Show(
            this,
            exception.Message,
            RobloxMultiManager.Core.L.T("Não foi possível abrir"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void MoveProfileUpButton_Click(object sender, RoutedEventArgs e) =>
        await MoveSelectedProfileAsync(-1);

    private async void MoveProfileDownButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await MoveSelectedProfileAsync(1);

    /// <summary>
    /// Moves the selected account one slot in the list and persists the new order.
    /// </summary>
    /// <remarks>
    /// The order is not decoration: the quick queue opens accounts in list order,
    /// so being able to change it is what makes the queue predictable.  The order
    /// is the order in profiles.json, so a Move plus a save is the whole feature.
    /// </remarks>
    private async Task MoveSelectedProfileAsync(int delta)
    {
        if (ProfilesGrid.SelectedItem is not AccountProfile selected)
        {
            ShowSelectProfileMessage();
            return;
        }

        var index = Profiles.IndexOf(selected);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Profiles.Count)
        {
            return;
        }

        Profiles.Move(index, target);
        ProfilesGrid.SelectedItem = selected;
        ProfilesGrid.ScrollIntoView(selected);
        await SaveProfilesAsync();
        AppendLog(
            RobloxMultiManager.Core.L.F("Ordem das contas alterada: “{0}” ",SanitizeLogValue(selected.Name))
            + RobloxMultiManager.Core.L.F("agora é a {0}ª da fila.",target + 1));
    }

    private void ProfilesGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) => UpdateQueueScopeText();

    private void UpdateQueueScopeText()
    {
        if (_orbitReady) ScheduleQueuePreview();
    }

    /// <summary>
    /// Fills in the account pictures that are missing.
    /// </summary>
    /// <remarks>
    /// Runs detached from startup on purpose: the picture is decoration, and the
    /// app must finish loading and take the multi-instance lock whether or not
    /// Roblox answers.  Profiles are records, so a resolved URL is applied by
    /// replacing the item, which is also what makes the row redraw.
    /// </remarks>
    private async Task RefreshAvatarsAsync()
    {
        try
        {
            var wanted = Profiles
                .Where(profile =>
                    profile.RobloxUserId is > 0
                    && string.IsNullOrWhiteSpace(profile.AvatarUrl)
                    && !_avatarsRequested.Contains(profile.RobloxUserId.Value))
                .Select(profile => profile.RobloxUserId!.Value)
                .Distinct()
                .ToArray();
            if (wanted.Length == 0)
            {
                return;
            }

            foreach (var userId in wanted)
            {
                _avatarsRequested.Add(userId);
            }

            var headshots = await _avatarService.FetchHeadshotsAsync(wanted);
            if (headshots.Count == 0)
            {
                return;
            }

            var applied = 0;
            for (var index = 0; index < Profiles.Count; index++)
            {
                var profile = Profiles[index];
                if (profile.RobloxUserId is not { } userId
                    || !headshots.TryGetValue(userId, out var url))
                {
                    continue;
                }

                if (string.Equals(profile.AvatarUrl, url, StringComparison.Ordinal))
                {
                    continue;
                }

                var wasSelected = ReferenceEquals(
                    ProfilesGrid.SelectedItem,
                    profile);
                var updated = profile with { AvatarUrl = url };
                Profiles[index] = updated;
                if (wasSelected)
                {
                    ProfilesGrid.SelectedItem = updated;
                }

                applied++;
            }

            if (applied > 0)
            {
                await SaveProfilesAsync();
                AppendLog(
                    RobloxMultiManager.Core.L.F("Foto de perfil obtida para {0} conta(s) ",applied)
                    + RobloxMultiManager.Core.L.T("em thumbnails.roblox.com."));
            }
        }
        catch (Exception ex)
        {
            // Never fatal: the monogram is the designed fallback.
            AppendLog(RobloxMultiManager.Core.L.F("Não foi possível obter as fotos de perfil: {0}",ex.Message));
        }
    }

    /// <summary>
    /// Keeps a handle to a launched client so its exit code stays readable.
    /// </summary>
    private void RetainProcessHandle(RobloxLaunchReceipt receipt) =>
        RetainExitObserver(receipt.ProcessKey, receipt.ProcessId, receipt.StartTimeUtc);

    private void RetainExitObserver(string key, int processId, DateTime startedUtc)
    {
        if (_ownedProcessHandles.ContainsKey(key)) return;
        try { _ownedProcessHandles[key] = ProcessExitObserver.Open(processId, startedUtc); }
        catch (Exception) { /* Verified client logs are the fallback. */ }
    }

    private int? TryReadRetainedExitCode(string key) =>
        _ownedProcessHandles.TryGetValue(key, out var observer) ? observer.ReadExitCode() : null;

    private async void AutoReconnectCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
        {
            return;
        }

        _settings = _settings with
        {
            AutoReconnect = AutoReconnectCheck.IsChecked == true
        };
        if (!_settings.AutoReconnect) { _reconnectCoordinator.CancelAll(); _reconnectTimer.Stop(); }
        await SaveSettingsAsync();
        AppendLog(_settings.AutoReconnect
            ? L.T("Reconexão após falha ativada.")
            : L.T("[reconnect] Desativado."));
    }

    private void ShowSelectProfileMessage() =>
        AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("Selecione um perfil na lista."),
            RobloxMultiManager.Core.L.T("Perfis"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void ShowSelectInstanceMessage() =>
        AppDialog.Show(
            this,
            RobloxMultiManager.Core.L.T("Selecione uma instância na lista."),
            RobloxMultiManager.Core.L.T("Instâncias"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private sealed record MonitorChoice(int Index, string Name);

}
