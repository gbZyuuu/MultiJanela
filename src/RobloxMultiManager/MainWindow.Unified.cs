using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using RobloxMultiManager.Core;
using Button = System.Windows.Controls.Button;

namespace RobloxMultiManager;

public sealed class WorkspaceAccountRow(string key, Action<WorkspaceAccountRow, bool> select) : INotifyPropertyChanged
{
    public string Key { get; } = key;
    public AccountProfile? Profile { get; private set; }
    public RobloxInstanceInfo? Session { get; private set; }
    public string Name => DisplayPrivacy.Enabled ? Profile?.PublicName ?? L.T("Sessão") : Profile?.PublicName ?? Session?.ProfileText ?? RobloxMultiManager.Core.L.T("Conta");
    public string? AvatarUrl => Profile?.PublicAvatarUrl;
    public string? PublicAvatarUrl => AvatarUrl;
    public bool AuthenticationRejected { get; private set; }
    public void SetAuthenticationRejected(bool rejected) { if (AuthenticationRejected == rejected) return; AuthenticationRejected = rejected; Changed(); }
    public string Status => AuthenticationRejected ? L.T("Autenticação recusada (403)") : Session?.StatusText ?? (Profile?.IsSignedIn == true ? RobloxMultiManager.Core.L.T("Pronta para abrir") : RobloxMultiManager.Core.L.T("Faça login"));
    public string Pid => Session is {} s ? $"PID {s.ProcessId}" : "—";
    public string Version => Session?.FileVersion ?? "—";
    private int _cpuPercent = 100;
    private bool _cpuInherited = true;
    private CpuLimitResult? _cpuResult;
    public bool CanConfigureCpu => Profile is not null;
    public bool CpuHasError => HasSession && _cpuResult is { Success: false };
    public string CpuLimitLabel => CpuHasError ? "CPU !" : HasSession && _cpuResult is null && _cpuPercent < 100 ? "CPU …"
        : !HasSession && Profile is not null && _cpuInherited ? L.T("CPU global") : L.F("CPU {0}%", _cpuPercent);
    public string CpuLimitDescription
    {
        get
        {
            var source = _cpuInherited ? L.F("Usando global: {0}%", _cpuPercent) : L.F("Limite próprio: {0}%", _cpuPercent);
            if (!HasSession) return source + L.T("\nSerá aplicado quando esta conta abrir.");
            if (_cpuResult is null) return source + L.T("\nAguardando aplicação ao processo.");
            if (!_cpuResult.Success) return source + L.F("\nNão aplicado: {0} (Windows {1}).", _cpuResult.Code, _cpuResult.WindowsError?.ToString() ?? "—");
            return source + (_cpuPercent == 100 ? L.T("\nSem limite de CPU deste aplicativo.") : L.F("\nLimite aplicado ao PID {0}.", Session!.ProcessId));
        }
    }
    public void UpdateCpu(int percent, bool inherited, CpuLimitResult? result)
    {
        if (_cpuPercent == percent && _cpuInherited == inherited && Equals(_cpuResult, result)) return;
        _cpuPercent = percent; _cpuInherited = inherited; _cpuResult = result; Changed();
    }

    private ProcessResourceUsage? _resources;
    private static string Bytes(long? bytes) => bytes is not { } value ? "—" : value >= 1024L * 1024 * 1024
        ? (value / (1024d * 1024 * 1024)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GiB"
        : Math.Round(value / (1024d * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " MiB";
    public string RamText => "RAM " + Bytes(_resources?.ResidentBytes);
    public string VramText => "VRAM " + Bytes(_resources?.DedicatedGpuBytes);
    public string ResourceDescription => L.F("RAM residente: {0}\nMemória privada comprometida: {1}\nGPU dedicada: {2}\nGPU compartilhada: {3}\nLeitura do Windows a cada 2 s. GPU pode estar indisponível ou ser imprecisa, dependendo do driver.",
        Bytes(_resources?.ResidentBytes), Bytes(_resources?.PrivateBytes), Bytes(_resources?.DedicatedGpuBytes), Bytes(_resources?.SharedGpuBytes));
    public void UpdateResources(ProcessResourceUsage? usage)
    {
        if (Equals(_resources, usage)) return;
        _resources = usage;
        foreach (var name in new[] { nameof(RamText), nameof(VramText), nameof(ResourceDescription) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    public DateTime? StartTimeUtc => Session?.StartTimeUtc;
    public bool HasSession => Session is not null;
    public bool CanOpen => Session is {} s ? s.HasWindow : Profile?.IsSignedIn == true;
    public string PrimaryAction => HasSession ? RobloxMultiManager.Core.L.T("Focar") : RobloxMultiManager.Core.L.T("Abrir");
    private bool _selected;
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; select(this, value); Changed(); } }
    public void Update(AccountProfile? profile, RobloxInstanceInfo? session, bool selected)
    {
        if (Equals(Profile, profile) && Equals(Session, session) && _selected == selected) return;
        Profile = profile; Session = session; _selected = selected; Changed();
    }
    private RobloxGraphicsPreferences _graphics = new(-1, 0);
    private bool _graphicsInherited = true, _graphicsEnabled;
    public string FpsLabel => _graphics.FpsCap == -1 ? L.T("FPS padrão") : L.F("FPS {0}", _graphics.FpsCap);
    public string QualityLabel => _graphics.Quality == 0 ? L.T("Qualidade auto") : L.F("Qualidade {0}", _graphics.Quality);
    public string GraphicsSource => !_graphicsEnabled ? L.T("Gráficos salvos · aplicação desligada") : _graphicsInherited ? L.T("Gráficos globais") : L.T("Gráficos desta conta");
    public string GraphicsDescription => GraphicsSource + "\n" + L.T("FPS e qualidade serão usados na próxima abertura desta conta, quando Aplicar ao abrir estiver ativo.");
    public void UpdateGraphics(GraphicsLaunchPolicy policy)
    {
        var value = Profile is { } p ? policy.ForAccount(p.Id) : new(policy.GlobalFps, policy.GlobalQuality);
        var inherited = Profile is null || !((policy.Accounts ?? []).Any(a => a.ProfileId == Profile.Id));
        if (_graphics == value && _graphicsInherited == inherited && _graphicsEnabled == policy.Enabled) return;
        _graphics = value; _graphicsInherited = inherited; _graphicsEnabled = policy.Enabled; Changed();
    }
    public void RefreshLanguage() => Changed();
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow
{
    public ObservableCollection<WorkspaceAccountRow> WorkspaceAccounts { get; } = [];
    public ObservableCollection<WorkspaceAccountRow> SavedAccounts { get; } = [];
    private bool _workspaceRefreshPending;
    private void InitializeUnifiedWorkspace()
    {
        Profiles.CollectionChanged += (_, _) => { ScheduleWorkspaceRefresh(); QueueCpuReconcile(); };
        Instances.CollectionChanged += (_, _) => ScheduleWorkspaceRefresh();
        ((System.Collections.Specialized.INotifyCollectionChanged)AccountProfilesView).CollectionChanged += (_, _) => ScheduleWorkspaceRefresh();
        LanguageCombo.SelectedValue = L.Language;
        MotionCheck.IsChecked = UiPreferences.Current.Animations;
        RefreshWorkspaceAccounts();
        ConfigureGlobalCpuEditor();
        _interfacePreferencesReady = true;
        L.LanguageChanged += InterfaceLanguageChanged;
        Closed += (_, _) => L.LanguageChanged -= InterfaceLanguageChanged;
    }
    private void ScheduleWorkspaceRefresh()
    {
        if (_workspaceRefreshPending) return;
        _workspaceRefreshPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => { _workspaceRefreshPending = false; RefreshWorkspaceAccounts(); }));
    }
    private void RefreshWorkspaceAccounts()
    {
        var visible = AccountProfilesView.Cast<AccountProfile>().Select(p => p.Id).ToHashSet();
        var byUserId = Profiles.Where(p => p.RobloxUserId is > 0).GroupBy(p => p.RobloxUserId!.Value)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : null);
        var byName = Profiles.GroupBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count() == 1 ? g.First() : null, StringComparer.Ordinal);
        var sessionsByProfile = new Dictionary<Guid, List<RobloxInstanceInfo>>();
        var external = new List<RobloxInstanceInfo>();
        foreach (var session in Instances)
        {
            // Never attach an ambiguous name or guessed username to a saved account.
            AccountProfile? profile = null;
            if (session.AccountUserId is > 0) byUserId.TryGetValue(session.AccountUserId.Value, out profile);
            else if (session.ProfileName is not null) byName.TryGetValue(session.ProfileName, out profile);
            if (profile is null) { external.Add(session); continue; }
            if (!sessionsByProfile.TryGetValue(profile.Id, out var list)) sessionsByProfile[profile.Id] = list = [];
            list.Add(session);
        }
        var desired = new List<(string Key, AccountProfile? Profile, RobloxInstanceInfo? Session)>();
        foreach (var profile in Profiles.Where(p => visible.Contains(p.Id)))
        {
            if (sessionsByProfile.TryGetValue(profile.Id, out var sessions))
                desired.AddRange(sessions.Select(s => (s.Key, (AccountProfile?)profile, (RobloxInstanceInfo?)s)));

        }
        desired.AddRange(external.Where(s => !_onlyReadyAccounts && (string.IsNullOrEmpty(_accountQuery)
            || s.ProfileText.Contains(_accountQuery, StringComparison.OrdinalIgnoreCase)
            || s.ProcessId.ToString().Contains(_accountQuery, StringComparison.Ordinal))).Select(s => (s.Key, (AccountProfile?)null, (RobloxInstanceInfo?)s)));
        var selected = InstancesGrid.SelectedItems.OfType<RobloxInstanceInfo>().Select(s => s.Key).ToHashSet();
        ReconcileRows(WorkspaceAccounts, desired, selected);
        ReconcileRows(SavedAccounts, Profiles.Where(p => visible.Contains(p.Id))
            .Select(p => ($"account:{p.Id}", (AccountProfile?)p, (RobloxInstanceInfo?)null)).ToList(), []);
        _authenticationRejected.IntersectWith(Instances.Select(instance => instance.Key));
        foreach (var row in WorkspaceAccounts) row.SetAuthenticationRejected(_authenticationRejected.Contains(row.Key));
        RefreshCpuRows();
        RefreshResourceRows(); RefreshGraphicsRows();
    }
    private void ReconcileRows(ObservableCollection<WorkspaceAccountRow> rows,
        List<(string Key, AccountProfile? Profile, RobloxInstanceInfo? Session)> desired, HashSet<string> selected)
    {
        var old = rows.ToDictionary(r => r.Key);
        var keys = desired.Select(d => d.Key).ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--) if (!keys.Contains(rows[i].Key)) rows.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            var entry = desired[i];
            if (!old.TryGetValue(entry.Key, out var row)) row = new WorkspaceAccountRow(entry.Key, SelectWorkspaceSession);
            row.Update(entry.Profile, entry.Session, entry.Session is {} session && selected.Contains(session.Key));
            if (i < rows.Count && ReferenceEquals(rows[i], row)) continue;
            var previous = rows.IndexOf(row);
            if (previous >= 0) rows.Move(previous, i); else rows.Insert(i, row);
        }
    }
    private void SelectWorkspaceSession(WorkspaceAccountRow row, bool selected)
    {
        var current = Instances.FirstOrDefault(s => s.Key == row.Session?.Key);
        if (current is null) return;
        if (selected && !InstancesGrid.SelectedItems.Contains(current)) InstancesGrid.SelectedItems.Add(current);
        else if (!selected) InstancesGrid.SelectedItems.Remove(current);
    }
    private void UnifiedOpen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WorkspaceAccountRow row }) return;
        if (row.Session is {} session) CanvasFocusRow_Click(new Button { DataContext = session }, e);
        else if (row.Profile is {} profile) LaunchAccountRowButton_Click(new Button { DataContext = profile }, e);
    }
    private void UnifiedClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceAccountRow { Session: {} session } })
            CanvasCloseRow_Click(new Button { DataContext = session }, e);
    }
    private void UnifiedMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkspaceAccountRow row, ContextMenu: {} menu } button) return;
        ProfilesGrid.SelectedItem = row.Profile;
        menu.DataContext = row;
        foreach (var item in menu.Items.OfType<MenuItem>()) item.IsEnabled = row.Profile is not null;
        menu.PlacementTarget = button; menu.IsOpen = true;
    }
    private void UnifiedDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WorkspaceAccountRow { Session: {} session } })
            CanvasDetails_Click(new Button { DataContext = session }, e);
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Settings);
    private bool _interfacePreferencesReady;
    private bool _updatingInterfaceLanguage;
    private string _interfacePreferencesPath = UiPreferences.DefaultPath;
    private Task _preferencesWrite = Task.CompletedTask;
    private int _preferencesRevision;

    private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_interfacePreferencesReady || _updatingInterfaceLanguage || LanguageCombo.SelectedValue is not string language) return;
        L.Language = language;
        await SaveInterfacePreferencesAsync();
    }

    private async void MotionCheck_Changed(object sender, RoutedEventArgs e) { if (_interfacePreferencesReady) await SaveInterfacePreferencesAsync(); }

    private async Task SaveInterfacePreferencesAsync()
    {
        var next = new UiPreferences(LanguageCombo.SelectedValue as string ?? L.Language, MotionCheck.IsChecked == true);
        UiPreferences.Apply(next);
        var revision = ++_preferencesRevision;
        // Serialize disk writes off the UI thread; the newest choice always wins.
        var write = _preferencesWrite.ContinueWith(_ => UiPreferences.Save(next, _interfacePreferencesPath, applyCurrent: false),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _preferencesWrite = write;
        try
        {
            await write;
            if (revision == _preferencesRevision) PreferencesStatus.Text = L.T("Preferências salvas.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            if (revision == _preferencesRevision) PreferencesStatus.Text = L.T("Não foi possível salvar as preferências. Verifique o acesso à pasta local.");
        }
    }

    private void InterfaceLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => InterfaceLanguageChanged(sender, e)); return; }
        _updatingInterfaceLanguage = true;
        try
        {
            LanguageCombo.SelectedValue = L.Language;
            RefreshCpuStatus();
            RefreshAccountGroups();
            _historyPanel?.RefreshPrivacy();
            _graphicsPanel?.RefreshLanguage();
            _tuningPanel?.RefreshLanguage();
            _notificationsPanel?.RefreshLanguage();
            _automationPanel?.RefreshLanguage();
            _localApiPanel?.RefreshLanguage();
            _updatesPanel?.RefreshLanguage(); RefreshUpdateHeader();
            _appearancePanel?.RefreshLanguage();
            _workspacesPanel?.RefreshLanguage();
            _backupPanel?.RefreshLanguage();
            foreach (var row in WorkspaceAccounts.Concat(SavedAccounts)) row.RefreshLanguage();
            UpdateAccountResults();
            CanvasSelection_Changed(this, null!);
            UpdateQueuePreview();
            UpdateConfirmation();
            UpdateInstallationLabel(); // Cached state only: no scan, version pin or network request.
            if (_lastGuardResult is {} guard) UpdateGuardUi(guard);
            if (MonitorCombo.ItemsSource is IEnumerable<MonitorChoice> choices)
            {
                var selected = MonitorCombo.SelectedValue;
                MonitorCombo.ItemsSource = choices.Select(c => c.Index == -1 ? c with { Name = L.T("Todos os monitores") } : c).ToArray();
                MonitorCombo.SelectedValue = selected;
            }
            if (_notifyIcon?.ContextMenuStrip is {} menu)
            {
                menu.Items[0].Text = L.T("Mostrar MultiJanela");
                menu.Items[1].Text = L.T("Organizar Roblox");
                menu.Items[3].Text = L.T("Sair");
                _notifyIcon.Text = L.T("MultiJanela para Roblox");
            }
            PreferencesStatus.Text = string.Empty;
        }
        finally { _updatingInterfaceLanguage = false; }
    }
}
