using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RobloxMultiManager;
using RobloxMultiManager.Core;

internal static partial class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        L.Language = args.Contains("--pt") ? "pt-BR" : "en";
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../dist/_ui-preview/comfort-pid"));
        var projectRoot = Path.GetFullPath(Path.Combine(output, "../../.."));
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Load the real resources into a plain Application. Constructing App would
        // enqueue its singleton startup even without calling Run; subclassing App
        // cannot load its BAML across assembly boundaries.
        var source = System.Xml.Linq.XDocument.Load(Path.Combine(projectRoot, "src/RobloxMultiManager/App.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = source.Root!.Element(ns + "Application.Resources")!.Elements().Single();
        resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        resources.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "local", "clr-namespace:RobloxMultiManager;assembly=RobloxMultiManager");
        var context = new System.Windows.Markup.ParserContext
        {
            BaseUri = new Uri("pack://application:,,,/RobloxMultiManager;component/App.xaml")
        };
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString(), context);
        var window = new MainWindow(); // Never Show: no saved profiles or monitor startup.
        var tray = typeof(MainWindow).GetField("_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        tray.GetType().GetProperty("Visible")!.SetValue(tray, false);
        try
        {
            Directory.CreateDirectory(output);
            typeof(MainWindow).GetField("_logFile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, Path.Combine(output, "fixture-activity.log"));
            if (args.Contains("--comfort"))
            {
                var fixture = new Window { Width=900, Height=700, Left=-10000, Top=-10000, ShowActivated=false, ShowInTaskbar=false, Content=new Border() };
                try { fixture.Show(); RunComfortChecks(output,fixture); Console.WriteLine($"[PASS] {_checks} comfort checks in {L.Language}"); return 0; }
                finally { fixture.Close(); }
            }
            if (args.Contains("--studio"))
            {
                RunStudioChecks(window, output);
                Check((bool)typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == false, "Production startup never ran during Studio preview");
                Console.WriteLine($"[PASS] {_checks} Studio checks in {L.Language}; previews: {output}");
                return 0;
            }
            RunUnifiedChecks(window, output);
            RunLiveLanguageChecks(window, output);
            if (L.Language == "pt-BR")
            {
                var root = (FrameworkElement)window.Content;
                var profiles = Get<ListBox>(window, "ProfilesGrid");
                profiles.UnselectAll(); profiles.SelectedItems.Add(window.Profiles[0]); profiles.SelectedItems.Add(window.Profiles[1]);
                RunSequenceChecks(window, root, output);

                RunSavedDestinationChecks(window, root, output);
            }
            RunCpuGlobalSliderChecks(window);
            RunCpuResourceChecks(window, output);
            RunOfflineCpuChecks(window, output);
            RunPanelChecks(window, output);
            RunMotionChecks(window);
            RunAuthenticationRecoveryChecks(window, output);
            RunManagerWorkspaceChecks(window, output);
            RunFeaturePanelChecks(window, output);
            Check((bool)typeof(MainWindow).GetField("_loaded", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == false,
                "Production startup never ran during preview");
            Console.WriteLine($"[PASS] {_checks} checks in {L.Language}; previews: {output}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { ((IDisposable)tray).Dispose(); }
    }

    private static void RunManagerWorkspaceChecks(MainWindow window, string output)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var pathsField = typeof(MainWindow).GetField("_paths", flags)!;
        var storeField = typeof(MainWindow).GetField("_store", flags)!;
        var loadedField = typeof(MainWindow).GetField("_loaded", flags)!;
        var oldPaths = pathsField.GetValue(window);
        var oldStore = storeField.GetValue(window);
        var paths = new AppPaths(Path.Combine(output, "activation-fixture"));
        pathsField.SetValue(window, paths);
        storeField.SetValue(window, new LocalStore(paths));
        var sequence = (List<Guid>)typeof(MainWindow).GetField("_sequenceIds", flags)!.GetValue(window)!;
        Task<ManagerOpenReply> Prepare() => (Task<ManagerOpenReply>)InvokeResult(window, "PrepareManagerOpenAsync", new ManagerOpenRequest("99.0.0.0"))!;
        try
        {
            HideDrawer(window);
            var notLoaded = Prepare(); PumpUntil(() => notLoaded.IsCompleted);
            Check(notLoaded.Result.Action == "busy", "Version transfer waits for startup instead of abandoning initialization");
            loadedField.SetValue(window, true);
            typeof(MainWindow).GetField("_activeLaunches", flags)!.SetValue(window, 1);
            var busy = Prepare(); PumpUntil(() => busy.IsCompleted);
            Check(busy.Result.Action == "busy" && window.IsEnabled, "Version transfer leaves active launches and controls intact");
            typeof(MainWindow).GetField("_activeLaunches", flags)!.SetValue(window, 0);
            sequence.Clear(); sequence.Add(window.Profiles[0].Id);
            Get<TextBox>(window, "LaunchLinkInput").Text = "123456";
            Get<TextBox>(window, "AccountSearchInput").Text = "fixture-search";
            Get<ComboBox>(window, "LaunchLinkProfileCombo").SelectedItem = window.Profiles[0];
            Invoke(window, "ShowWorkspacePage", Enum.Parse(typeof(MainWindow).GetNestedType("WorkspacePage", BindingFlags.NonPublic)!, "Launch"));
            var prepared = Prepare(); PumpUntil(() => prepared.IsCompleted);
            Check(prepared.Result.Action == "ready" && !window.IsEnabled, "Transfer saves its state before acknowledging readiness");
            var saved = new ManagerWorkspaceStore(paths.Root).LoadAsync(); PumpUntil(() => saved.IsCompleted);
            Check(saved.Result!.Sequence.SequenceEqual(sequence) && saved.Result.Destination == "123456" && saved.Result.Search == "fixture-search",
                "Transfer checkpoint preserves the queue and unsaved destination/search input");
            Check(File.Exists(paths.ProfilesFile) && File.Exists(paths.SettingsFile) && File.Exists(paths.OwnershipFile),
                "Transfer flushes profiles, settings and PID ownership to its isolated store");
            Invoke(window, "AbortManagerHandoff");
            Check(window.IsEnabled, "Aborted transfer restores the current manager controls");
            sequence.Clear(); Get<TextBox>(window, "LaunchLinkInput").Clear(); Get<TextBox>(window, "AccountSearchInput").Clear();
            var restore = (Task)InvokeResult(window, "RestoreWorkspaceAsync")!; PumpUntil(() => restore.IsCompleted); restore.GetAwaiter().GetResult();
            Check(sequence.SequenceEqual(saved.Result.Sequence) && Get<TextBox>(window, "LaunchLinkInput").Text == "123456"
                && Get<TextBox>(window, "AccountSearchInput").Text == "fixture-search", "The replacement restores the saved queue and inputs");
            Check(ReferenceEquals(Get<ComboBox>(window, "LaunchLinkProfileCombo").SelectedItem, window.Profiles[0])
                && typeof(MainWindow).GetField("_workspacePage", flags)!.GetValue(window)!.ToString() == "Launch",
                "The replacement restores the launch account and active page");
        }
        finally
        {
            Invoke(window, "AbortManagerHandoff");
            typeof(MainWindow).GetField("_activeLaunches", flags)!.SetValue(window, 0);
            loadedField.SetValue(window, false);
            pathsField.SetValue(window, oldPaths); storeField.SetValue(window, oldStore);
        }
    }

    private static void RunAuthenticationRecoveryChecks(MainWindow window, string output)
    {
        HideDrawer(window); Invoke(window, "SetComfortPage", false);
        var row = window.WorkspaceAccounts[0]; var session = row.Session!; var profile = row.Profile!;
        var path = Path.Combine(output, "fixture-client.exe"); const string version = "version-1111111111111111";
        var receipt = new RobloxLaunchReceipt(Guid.NewGuid(), profile.Id, session.ProcessId, session.StartTimeUtc, path, version, "0.735.3.0", "LIVE", new RobloxClientExecutableMetadata(path, version, "0.735.3.0", "fixture-client.exe"));
        var confirmed = (bool)InvokeResult(window, "ReportAuthenticationRejection", receipt, profile, true)!;
        Check(!confirmed && row.AuthenticationRejected && row.Status == L.T("Autenticação recusada (403)"), "A native authorization error cannot be confirmed as a successful launch");
        var panel = (StackPanel)Get<ContentControl>(window, "DrawerBody").Content;
        Check(panel.Children.OfType<Button>().Any(b => b.Content?.ToString() == L.T("Login / atualizar sessão")), "Authentication recovery exposes a manual refresh action without repeating tickets");
        Check(panel.Children.OfType<Button>().Any(b => b.Content?.ToString() == L.T("Reparar sessão local do Roblox")), "Native 403 recovery offers an explicit backed-up repair");
        Check(Get<Button>(window, "RepairNativeSessionButton").Content?.ToString() == L.T("Reparar sessão local do Roblox"), "Settings keeps native repair available independently of log attribution");
        typeof(MainWindow).GetField("_nativeSessionRecoveryInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        object?[] repairLaunch = [profile, null];
        Check(!(bool)typeof(MainWindow).GetMethod("TryBeginLaunch", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, repairLaunch)!, "Recovery blocks simultaneous account launches");
        typeof(MainWindow).GetField("_nativeSessionRecoveryInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
        var shift = Get<TranslateTransform>(window, "DrawerShift"); shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = 0;
        Render((FrameworkElement)window.Content, 1080, 760, Path.Combine(output, "authentication-recovery.png"));
        HideDrawer(window);
        ((HashSet<string>)typeof(MainWindow).GetField("_authenticationRejected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Remove(row.Key);
        Invoke(window, "RefreshWorkspaceAccounts");
    }

    private static void RunCpuGlobalSliderChecks(MainWindow window)
    {
        var control = new CpuGlobalSlider();
        var slider = (Slider)control.FindName("LimitSlider");
        var saved = new List<int?>();
        control.SaveRequested = value => { saved.Add(value); return Task.CompletedTask; };
        control.Synchronize(35);
        Check(slider.Value == 35 && saved.Count == 0, "Home slider loads saved CPU without writing settings");
        Layout(control, 640, 76);
        var track = (System.Windows.Controls.Primitives.Track)slider.Template.FindName("PART_Track", slider);
        Check(track.Thumb.ActualWidth == 18 && track.Minimum == 1 && track.Maximum == 100, "Styled home slider retains the native interactive track");
        Slider.IncreaseSmall.Execute(null, slider);
        Check(slider.Value == 36, "Keyboard slider commands work with the custom theme");
        slider.Value = 40; slider.Value = 45; slider.Value = 55;
        Check(saved.Count == 0 && ((TextBlock)control.FindName("ValueText")).Text == "55%", "CPU thumb and percentage update immediately without writing on every movement");
        var flush = control.Dispatcher.Invoke(() => control.FlushAsync()); PumpUntil(() => flush.IsCompleted); flush.GetAwaiter().GetResult();
        Check(saved.SequenceEqual(new int?[] {55}), "Rapid home slider changes coalesce to one saved value");
        control.Synchronize(70);
        Check(slider.Value == 70 && saved.Count == 1, "Settings changes synchronize the same visible global slider");
        slider.Value = 71;
        PumpUntil(() => saved.Count == 2);
        Check(saved.Last() == 71, "Home slider saves automatically without an Apply button");
        slider.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
        slider.Value = 72; slider.Value = 73;
        flush = control.Dispatcher.Invoke(() => control.FlushAsync());
        Check(flush.IsCompleted && saved.Count == 2, "Dragging defers saves until the gesture ends");
        slider.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
        PumpUntil(() => saved.Count == 3);
        Check(saved.Last() == 73, "Releasing the slider saves the final drag value");
        var disk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.SaveRequested = value => { saved.Add(value); return value == 75 ? disk.Task : Task.CompletedTask; };
        slider.Value = 75;
        flush = control.Dispatcher.Invoke(() => control.FlushAsync());
        slider.Value = 76; slider.Value = 77;
        control.Synchronize(75);
        Check(slider.Value == 77 && !flush.IsCompleted, "A slow older save never pulls the CPU thumb away from newer input");
        disk.SetResult(); PumpUntil(() => flush.IsCompleted); flush.GetAwaiter().GetResult();
        Check(saved.TakeLast(2).SequenceEqual(new int?[] {75,77}), "CPU autosaves serialize and preserve the latest value");
        control.SaveRequested = _ => Task.FromException(new IOException("offline fixture"));
        slider.Value = 40;
        flush = control.Dispatcher.Invoke(() => control.FlushAsync()); PumpUntil(() => flush.IsCompleted);
        Check(slider.Value == 77 && ((TextBlock)control.FindName("SaveError")).Visibility == Visibility.Visible, "Failed autosave restores the persisted value and shows an error");
        control.SaveRequested = value => { saved.Add(value); return Task.CompletedTask; };
        slider.Value = 100;
        flush = control.Dispatcher.Invoke(() => control.FlushAsync(finishInteraction: true)); PumpUntil(() => flush.IsCompleted);
        Check(saved.Last() == 100 && slider.Value == 100, "Exit flush saves the last global value, including unlimited");
        Check(Get<CpuGlobalSlider>(window, "HomeCpuSlider").SaveRequested is not null, "Home slider is wired to the real global CPU setting");
    }

    private static void RunOfflineCpuChecks(MainWindow window, string output)
    {
        var storeField = typeof(MainWindow).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalStore = storeField.GetValue(window);
        var originalSettings = settingsField.GetValue(window);
        var paths = new AppPaths(Path.Combine(output, "offline-cpu-store"));
        var store = new LocalStore(paths);
        var root = (FrameworkElement)window.Content;
        var list = Get<ListBox>(window, "SavedAccountsList");
        var id = window.SavedAccounts.First(r => r.Profile is not null && !window.Instances.Any(session => session.ProfileName == r.Profile.Name)).Profile!.Id;
        var queueBefore = window.QueuePreview.Select(q => q.Profile.Id).ToArray();
        storeField.SetValue(window, store); // Only fictional preview profiles are ever saved here.
        CpuLimitEditor OpenOfflineEditor()
        {
            HideDrawer(window);
            Invoke(window, "SetComfortPage", false);
            Invoke(window, "RefreshWorkspaceAccounts");
            var row = window.SavedAccounts.Single(r => r.Profile?.Id == id);
            list.ScrollIntoView(row); Layout(root, 1080, 760);
            var button = Descendants<Button>(list).Single(b => ReferenceEquals(b.DataContext, row) && b.Name == "AccountCpuButton");
            Check(!row.HasSession && row.CanConfigureCpu && button.IsEnabled && button.IsHitTestVisible,
                "Closed saved account has an enabled, clickable CPU action");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Get<ContentControl>(window, "DrawerBody").Content is CpuLimitEditor,
                "The actual CPU button opens the editor without a running session");
            var opened = (CpuLimitEditor)Get<ContentControl>(window, "DrawerBody").Content;
            Check(((TextBlock)opened.FindName("PendingSessionHint")).Visibility == Visibility.Visible,
                "Closed account editor explains that the saved cap applies at next launch");
            return opened;
        }
        void Save(CpuLimitEditor editor)
        {
            ((Button)editor.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => ((TextBlock)editor.FindName("SavedText")).Text.Length > 0 || ((TextBlock)editor.FindName("ErrorText")).Text.Length > 0);
            Check(((TextBlock)editor.FindName("ErrorText")).Text.Length == 0, "Closed account CPU preference saves without a Roblox process");
        }
        try
        {
            Check(window.SavedAccounts.Single(r => r.Profile?.Id == id).CpuLimitLabel == L.T("CPU global"),
                "Closed inherited account shows a clear global CPU action instead of a dash");
            var editor = OpenOfflineEditor();
            Render((FrameworkElement)window.Content, 1080, 760, Path.Combine(output, "closed-account-cpu-editor.png"));
            ((TextBox)editor.FindName("PercentInput")).Text = "27"; Save(editor);
            var read = store.LoadProfilesAsync(); PumpUntil(() => read.IsCompleted);
            Check(read.GetAwaiter().GetResult().Single(p => p.Id == id).CpuLimitPercent == 27,
                "Individual CPU cap for a closed account persists to the isolated profile store");
            editor = OpenOfflineEditor();
            Check(((TextBox)editor.FindName("PercentInput")).Text == "27" && !editor.IsUsingGlobal,
                "Reopening a closed account restores its own saved cap");
            HideDrawer(window);
            var global = (Task)InvokeResult(window, "SaveGlobalCpuLimitAsync", (int?)40)!;
            PumpUntil(() => global.IsCompleted); global.GetAwaiter().GetResult();
            Invoke(window, "RefreshWorkspaceAccounts");
            Check(window.SavedAccounts.Single(r => r.Profile?.Id == id).CpuLimitLabel == "CPU 27%",
                "Global adjustment does not overwrite a closed account's own cap");
            editor = OpenOfflineEditor();
            ((Button)editor.FindName("UseGlobalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Save(editor);
            read = store.LoadProfilesAsync(); PumpUntil(() => read.IsCompleted);
            Check(read.GetAwaiter().GetResult().Single(p => p.Id == id).CpuLimitPercent is null,
                "Use global restores persisted inheritance while the account is closed");
            editor = OpenOfflineEditor();
            Check(editor.IsUsingGlobal && ((TextBox)editor.FindName("PercentInput")).Text == "40",
                "Closed inherited account immediately picks up the current global cap");
            ((TextBox)editor.FindName("PercentInput")).Text = "100"; Save(editor);
            read = store.LoadProfilesAsync(); PumpUntil(() => read.IsCompleted);
            Check(read.GetAwaiter().GetResult().Single(p => p.Id == id).CpuLimitPercent == 100,
                "Closed account can explicitly disable its own cap independently of global");
            Invoke(window, "RefreshWorkspaceAccounts");
            Check(window.SavedAccounts.Single(r => r.Profile?.Id == id).CpuLimitLabel == "CPU 100%",
                "An unlimited individual setting remains visible on a closed account");
            editor = OpenOfflineEditor();
            ((Button)editor.FindName("UseGlobalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Save(editor);
            Check(window.QueuePreview.Select(q => q.Profile.Id).SequenceEqual(queueBefore),
                "Editing CPU for a closed account preserves launch queue membership and order");
        }
        finally
        {
            HideDrawer(window);
            storeField.SetValue(window, originalStore);
            settingsField.SetValue(window, originalSettings);
            Invoke(window, "ConfigureGlobalCpuEditor"); Invoke(window, "RefreshWorkspaceAccounts");
            list.ScrollIntoView(window.SavedAccounts[0]); Layout(root, 1080, 760);
        }
    }

    private static void RunCpuResourceChecks(MainWindow window, string output)
    {
        var editor = new CpuLimitEditor();
        var saves = new List<int?>();
        editor.SaveRequested = value => { saves.Add(value); return Task.CompletedTask; };
        T Find<T>(string name) => (T)editor.FindName(name);
        editor.Configure(30, null, true);
        Check(editor.IsUsingGlobal && Find<Slider>("LimitSlider").Value == 30, "New account inherits global CPU cap");
        Find<Slider>("LimitSlider").Value = 45;
        Check(!editor.IsUsingGlobal && Find<TextBox>("PercentInput").Text == "45", "Moving CPU slider creates individual override");
        PumpUntil(() => !editor.IsBusy);
        Check(saves.SequenceEqual(new int?[] {45}), "Changing the slider automatically saves the CPU cap");
        Find<Button>("UseGlobalButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !editor.IsBusy);
        Check(saves.Last() is null && editor.IsUsingGlobal, "Use global saves inheritance instead of copying the number");
        editor.Configure(60, 25, true);
        Check(!editor.IsUsingGlobal && Find<TextBox>("PercentInput").Text == "25", "Global changes preserve individual cap");
        Find<TextBox>("PercentInput").Text = "0";
        Check(!Find<Button>("ApplyButton").IsEnabled && Find<TextBlock>("ErrorText").Text.Length > 0, "Invalid CPU values blocked");
        Find<TextBox>("PercentInput").Text = "100";
        PumpUntil(() => !editor.IsBusy);
        Check(saves.Last() == 100 && !editor.IsUsingGlobal, "Explicit unlimited is an individual override");
        var language = L.Language;
        L.Language = "en"; editor.RefreshMode();
        Check(Find<TextBlock>("ModeText").Text == "Custom limit: 100%", "CPU mode translated into English");
        L.Language = "pt-BR"; editor.RefreshMode();
        Check(Find<TextBlock>("ModeText").Text == "Limite próprio: 100%", "CPU mode translated into Portuguese");
        L.Language = language;
        editor.Configure(30, null, true);
        Render(editor, 480, 460, Path.Combine(output, "cpu-limit-editor.png"));
        var row = window.WorkspaceAccounts.First(r => r.HasSession);
        row.UpdateResources(new ProcessResourceUsage(row.Session!.Key, 1536L * 1024 * 1024, 2048L * 1024 * 1024, 512L * 1024 * 1024, 64L * 1024 * 1024, DateTime.UtcNow));
        Check(row.RamText == "RAM 1.5 GiB" && row.VramText == "VRAM 512 MiB", "RAM/VRAM units are readable per process");
        var resourceEvents = new List<string?>(); row.PropertyChanged += (_, e) => resourceEvents.Add(e.PropertyName);
        row.UpdateResources(null);
        Check(row.VramText == "VRAM —" && !resourceEvents.Contains(null), "Unavailable readings stay unknown and update only resource bindings");
        row.UpdateCpu(25, false, new(row.Session.Key, 25, null, false, "access-denied", 5));
        Check(row.CpuHasError && row.CpuLimitLabel == "CPU !", "Windows rejection is visible, not shown as applied");
        row.UpdateCpu(25, false, new(row.Session.Key, 25, 25, true, "applied"));
        Check(!row.CpuHasError && row.CpuLimitLabel == "CPU 25%", "Confirmed cap appears on account row");
        Invoke(window, "AccountCpu_Click", new Button { DataContext = row }, new RoutedEventArgs());
        Check(Get<ContentControl>(window, "DrawerBody").Content is CpuLimitEditor, "Account CPU action opens real editor");
        Invoke(window, "CloseDrawer");
    }

    private static void RunUnifiedChecks(MainWindow window, string output)
    {
        var root = (FrameworkElement)window.Content;
        var list = Get<InstanceMap>(window, "UnifiedAccountsList");
        var sessions = Get<ListBox>(window, "InstancesGrid");
        Render(root, 860, 640, Path.Combine(output, "empty.png"));
        Check(Descendants<TextBlock>(root).Any(t => t.Text == L.T("Nenhuma sessão aberta")), "Empty state is localized");
        var names = new[] { "Aurora", "Altair", "Atlas", "Nova", "Orbit", "Eclipse", "Luna", "Cosmo", "Zenith", "Vega", "Drift", "Sol" };
        for (var i = 0; i < names.Length; i++) window.Profiles.Add(new AccountProfile(Guid.NewGuid(), names[i], "https://www.roblox.com/home", i == 7 ? null : 720100L + i, $"{names[i].ToLowerInvariant()}_play", names[i], FixtureAvatar(output, i)));
        for (var i = 0; i < 3; i++) window.Instances.Add(new RobloxInstanceInfo(18240 + i * 24, DateTime.UtcNow.AddMinutes(-i * 7), (nint)(i + 1), "Roblox", RobloxInstanceState.Running, true, ProfileName: names[i], VersionId: "version-1111111111111111", FileVersion: "0.735.3.0", RequestedVersionId: "version-1111111111111111", AccountUserId: 720100L + i, AccountUsername: $"{names[i].ToLowerInvariant()}_play", AccountIsExact: true));
        Invoke(window, "RefreshWorkspaceAccounts");
        Render(root, 1080, 760, Path.Combine(output, "workspace.png"));
        Check(list.Items.Count == 3 && window.SavedAccounts.Count == 12 && window.WorkspaceAccounts.All(r => r.HasSession) && window.SavedAccounts.All(r => !r.HasSession),
            "Saved accounts and open sessions have separate collections on the same page");
        Check(window.WorkspaceAccounts[0].Pid == "PID 18240" && window.WorkspaceAccounts[0].Version == "0.735.3.0", "Live PID and version belong to the matching account");
        Check(window.SavedAccounts[3].PrimaryAction == L.T("Abrir") && window.WorkspaceAccounts[0].PrimaryAction == L.T("Focar"), "Saved accounts open while live sessions offer focus");
        Check(!window.SavedAccounts[7].CanOpen, "Unsigned accounts cannot launch");
        var selected = window.WorkspaceAccounts[0]; selected.IsSelected = true;
        Check(sessions.SelectedItems.Count == 1 && Get<Button>(window, "CanvasCloseSelectedButton").IsEnabled, "Row selection controls the guarded batch close command");
        Invoke(window, "CanvasClearSelection_Click", window, new RoutedEventArgs()); Invoke(window, "RefreshWorkspaceAccounts");
        Check(!selected.IsSelected && sessions.SelectedItems.Count == 0, "Clear selection updates the visible account row");
        Invoke(window, "CanvasSelectAll_Click", window, new RoutedEventArgs()); Invoke(window, "RefreshWorkspaceAccounts");
        Check(window.WorkspaceAccounts.Count(r => r.IsSelected) == 3, "Select all selects live sessions, not offline accounts");
        var resourceFixture = window.WorkspaceAccounts.Where(r => r.HasSession).ToDictionary(r => r.Session!.Key,
            r => new ProcessResourceUsage(r.Session!.Key, 1536L*1024*1024, 2048L*1024*1024, 512L*1024*1024, 64L*1024*1024, DateTime.UtcNow));
        Invoke(window, "ProcessMonitor_ResourcesChanged", window, resourceFixture);
        Render(root, 860, 640, Path.Combine(output, "workspace-compact.png"));
        var savedPane = Get<Grid>(window, "SavedAccountsSection"); var sessionPane = Get<Grid>(window, "OpenSessionsSection");
        var savedBounds = new Rect(savedPane.TranslatePoint(new Point(), root), savedPane.RenderSize);
        var sessionBounds = new Rect(sessionPane.TranslatePoint(new Point(), root), sessionPane.RenderSize);
        Check(!savedBounds.IntersectsWith(sessionBounds) && new Rect(root.RenderSize).Contains(savedBounds) && new Rect(root.RenderSize).Contains(sessionBounds), "Both account and session sections fit side by side at the minimum window size");
        Check(Get<ListBox>(window, "SavedAccountsList").Items.Count == 12 && list.Items.Count == 3, "Running accounts remain in the library without duplicating their session rows");
        var homeCpu = Get<CpuGlobalSlider>(window, "HomeCpuSlider");
        var homeCpuBounds = new Rect(homeCpu.TranslatePoint(new Point(), root), homeCpu.RenderSize);
        Check(new Rect(root.RenderSize).Contains(homeCpuBounds) && ((Slider)homeCpu.FindName("LimitSlider")).ActualWidth > 100,
            "Global CPU slider is fully visible and usable at 860x640");
        Check(homeCpuBounds.Bottom <= list.TranslatePoint(new Point(), root).Y, "Global CPU control stays above accounts without overlapping them");
        var cpuLabel = (TextBlock)homeCpu.FindName("ValueText");
        Console.WriteLine($"[CPU LABEL] text={cpuLabel.Text}, size={cpuLabel.RenderSize}, pos={cpuLabel.TranslatePoint(new Point(), root)}, opacity={cpuLabel.Opacity}, visibility={cpuLabel.Visibility}");
        Check(cpuLabel.Text == "100%" && cpuLabel.ActualWidth > 20 && cpuLabel.ActualHeight > 10, "Home CPU percentage is rendered beside the slider");
        foreach (var name in new[] { "CanvasArrangeAllButton", "CanvasCloseAllButton", "CanvasCloseSelectedButton", "SettingsButton" })
        {
            var button = Get<Button>(window, name);
            Check(new Rect(root.RenderSize).Contains(new Rect(button.TranslatePoint(new Point(), root), button.RenderSize)), $"{name} fits at 860x640");
            if (name != "SettingsButton") Check(button.TranslatePoint(new Point(0, button.ActualHeight), root).Y <= list.TranslatePoint(new Point(), root).Y, $"{name} stays above the account list");
        }
        var rows = Descendants<ListBoxItem>(list).ToArray();
        Check(rows.Length > 0 && rows.All(r => r.ActualHeight >= 70 && r.ActualWidth <= list.ActualWidth), "Unified rows fit the compact viewport");
        Check(Descendants<TextBlock>(list).Any(t => t.Text == "Aurora") && Descendants<TextBlock>(list).Any(t => t.Text == "PID 18240"), "Account identity and process information are visible together");
        Get<TextBox>(window, "AccountSearchInput").Text = "aurora"; Invoke(window, "ApplyAccountFilter"); Invoke(window, "RefreshWorkspaceAccounts");
        Check(window.WorkspaceAccounts.Count == 1 && window.WorkspaceAccounts[0].Session is not null, "Search filters the unified account and session row");
        Invoke(window, "WorkspaceClearSearch_Click", window, new RoutedEventArgs()); Invoke(window, "RefreshWorkspaceAccounts");
        var external = window.Instances[0] with { ProcessId = 99999, AccountUserId = 99999, ProfileName = null, AccountUsername = "External" };
        window.Instances.Add(external); Invoke(window, "RefreshWorkspaceAccounts");
        Check(window.WorkspaceAccounts.Count == 4 && window.SavedAccounts.Count == 12 && window.WorkspaceAccounts[^1].Profile is null, "Unattributed external sessions remain visible and closable");
        window.Instances.Remove(external); Invoke(window, "RefreshWorkspaceAccounts");
        var extraSession = window.Instances[0] with { ProcessId = 18888 };
        window.Instances.Add(extraSession); Invoke(window, "RefreshWorkspaceAccounts");
        Check(window.WorkspaceAccounts.Count == 4 && window.SavedAccounts.Count == 12 && window.WorkspaceAccounts.Count(r=>r.Profile?.Id==window.Profiles[0].Id)==2,
            "Two sessions for one account retain separate PIDs and independent actions");
        window.Instances.Remove(extraSession); Invoke(window, "RefreshWorkspaceAccounts");
        var ambiguous = window.Profiles[0] with { Id = Guid.NewGuid() };
        window.Profiles.Add(ambiguous); Invoke(window, "RefreshWorkspaceAccounts");
        Check(window.WorkspaceAccounts.Single(r=>r.Session?.ProcessId==18240).Profile is null,
            "An ambiguous saved identity is not silently attributed to the wrong account");
        window.Profiles.Remove(ambiguous); Invoke(window, "RefreshWorkspaceAccounts");
        var stale = new Button { DataContext = window.Instances[0] with { StartTimeUtc = DateTime.UtcNow.AddDays(-1) } };
        Check(InvokeResult(window,"CurrentCanvasSession", stale) is null, "Reused PIDs cannot target a stale session");

        Invoke(window, "AddSequenceAccounts", (object)window.Profiles.Take(5).ToArray());
        Get<TextBox>(window, "LaunchLinkInput").Text = "2753915549";
        Invoke(window, "ShowComfortLaunchPanel", false);
        Render(root, 860, 640, Path.Combine(output, "launch.png"));
        Check(Get<Button>(window, "LaunchDestinationSequenceButton").Content?.ToString() == L.T("Abrir no destino"), "Launch destination action uses the selected language");
        Invoke(window, "Settings_Click", window, new RoutedEventArgs());
        Render(root, 860, 640, Path.Combine(output, "settings.png"));
        Check(Get<Grid>(window, "SettingsPageHost").Visibility == Visibility.Visible && Get<Grid>(window, "NexusBody").Visibility == Visibility.Collapsed, "Gear opens settings without splitting accounts from sessions");
        Check(Get<ComboBox>(window, "LanguageCombo").Items.Count == 2 && Descendants<TextBlock>(root).Any(t => t.Text == L.T("Idioma")), "Settings offers English and Portuguese with localized labels");
        Check(Get<CheckBox>(window, "AutoArrangeCheck").ActualHeight > 0 && Get<CheckBox>(window, "MotionCheck").ActualHeight > 0, "Window and animation preferences are present in settings");
        Invoke(window, "SetComfortPage", false); Layout(root, 1080, 760);
        Check(window.QueuePreview.Count == 5 && sessions.SelectedItems.Count == 3, "Navigation preserves launch order and session selection");
        var prefPath = Path.Combine(output, "preferences-fixture.json");
        Check(UiPreferences.Read(Path.Combine(output,"absent-preferences.json")).Language == "en", "Fresh installations default to English");
        UiPreferences.Save(new UiPreferences("pt-BR", false), prefPath);
        Check(UiPreferences.Read(prefPath) is { Language: "pt-BR", Animations: false }, "Language and reduced-motion preferences persist atomically");
        UiPreferences.Save(new UiPreferences("en", true), prefPath);
        Check(UiPreferences.Read(prefPath).Language == "en", "English can be selected again");
        var culture = L.Language;
        var formats = L.Catalog.All(entry => System.Text.CompositeFormat.Parse(entry.Key).MinimumArgumentCount == System.Text.CompositeFormat.Parse(entry.Value).MinimumArgumentCount);
        Check(formats && L.Catalog.Count > 900, "Every localized format preserves its parameters across a catalog of over 900 strings");
        foreach (var pair in L.Catalog) CheckText(L.T(pair.Key) == (culture == "en" ? pair.Value : pair.Key), pair.Key);
        Check(L.T("Minha conta pessoal") == "Minha conta pessoal", "User-entered names are not translated");
        RobloxLinkParser.TryParse("", out _, out var error);
        Check(error == L.T("Cole um link de jogo ou de servidor privado."), "Validation errors are localized at their source");
        var more = Descendants<Button>(Get<ListBox>(window, "SavedAccountsList")).First(b => b.ContextMenu is not null);
        var menu = new ContextMenu { Style = (Style)window.FindResource(typeof(ContextMenu)), Resources = window.Resources };
        foreach (var item in more.ContextMenu!.Items.OfType<MenuItem>()) menu.Items.Add(new MenuItem { Header = item.Header });
        Render(menu, 310, 350, Path.Combine(output,"account-menu.png"));
        Check(menu.Items.OfType<MenuItem>().Any(i=>i.Header?.ToString()==L.T("Login / atualizar sessão")), "Account context menu is fully localized");
        // Stress the actual combined list, not the hidden selection backing controls.
        for (var i = 12; i < 500; i++) window.Profiles.Add(new AccountProfile(Guid.NewGuid(), $"Account {i}", "https://www.roblox.com/home", 900000L+i));
        Invoke(window, "RefreshWorkspaceAccounts"); Layout(root,1080,760);
        var savedList = Get<ListBox>(window, "SavedAccountsList");
        Check(savedList.Items.Count == 500 && Descendants<ListBoxItem>(savedList).Count() is >0 and <30, "500 saved accounts stay virtualized independently of open sessions");
        savedList.ScrollIntoView(window.SavedAccounts[^1]); Layout(root,1080,760);
        Check(savedList.ItemContainerGenerator.ContainerFromIndex(499) is not null && Descendants<ListBoxItem>(savedList).Count()<30, "The saved account library reaches its last row while recycling containers");
        while(window.Profiles.Count>12)window.Profiles.RemoveAt(window.Profiles.Count-1);
        Invoke(window,"RefreshWorkspaceAccounts"); list.ScrollIntoView(window.WorkspaceAccounts[0]); Layout(root,1080,760);
        Invoke(window,"CanvasClearSelection_Click",window,new RoutedEventArgs());
    }
    private static void RunLiveLanguageChecks(MainWindow window, string output)
    {
        var root = (FrameworkElement)window.Content;
        var initialLanguage = L.Language;
        var list = Get<InstanceMap>(window, "UnifiedAccountsList");
        // Finish the preceding fixture's deferred Clear Selection before selecting again.
        Layout(root, 1080, 760);
        var row = window.WorkspaceAccounts[0]; row.IsSelected = true;
        Check(Get<ListBox>(window, "InstancesGrid").SelectedItems.Contains(row.Session), "Live language fixture starts with a selected running session");
        var queue = window.QueuePreview.Select(q => q.Profile.Id).ToArray();
        var target = Get<TextBox>(window, "LaunchLinkInput"); target.Text = "https://www.roblox.com/games/1818";
        var more = Descendants<Button>(Get<ListBox>(window, "SavedAccountsList")).First(b => b.ContextMenu is not null);
        var existingMenu = more.ContextMenu!;
        var preferences = Path.Combine(output, "live-preferences-fixture.json");
        typeof(MainWindow).GetField("_interfacePreferencesPath", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, preferences);
        Invoke(window, "Settings_Click", window, new RoutedEventArgs());
        Layout(root, 860, 640);
        var settings = Get<Grid>(window, "SettingsPageHost");
        var rail = Get<Border>(window, "NavigationRail");
        var before = settings.TranslatePoint(new Point(), root);
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Invoke(window, "NavigationRail_MouseEnter", rail, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)); Layout(root, 860, 640);
        Check(rail.ActualWidth >= 220 && settings.TranslatePoint(new Point(), root) == before,
            "Expanded rail overlays the page without moving or remeasuring its content width");
        DependencyObject? hit = VisualTreeHelper.HitTest(root, rail.TranslatePoint(new Point(170, 50), root))?.VisualHit;
        while (hit is not null && !ReferenceEquals(hit, rail)) hit = VisualTreeHelper.GetParent(hit);
        Check(ReferenceEquals(hit, rail), "Expanded navigation is painted and hit-testable above the settings page beyond the 72px column");
        Render(root, 860, 640, Path.Combine(output, "rail-expanded.png"));
        Invoke(window, "NavigationRail_MouseLeave", rail, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)); Layout(root, 860, 640);
        Check(rail.ActualWidth == 72, "Rail collapses after pointer exit");
        var focusKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsKeyboardFocusWithinPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        rail.SetValue(focusKey, true);
        Invoke(window, "NavigationRail_MouseEnter", rail, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0));
        Invoke(window, "SetNavigationKeyboardMode", false);
        Invoke(window, "NavigationRail_MouseLeave", rail, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)); Layout(root, 860, 640);
        Check(rail.ActualWidth == 72, "Click focus does not pin the rail after pointer exit");
        Invoke(window, "SetNavigationKeyboardMode", true); Layout(root, 860, 640);
        Check(rail.ActualWidth >= 220, "Keyboard navigation can still reveal rail labels");
        Invoke(window, "NavigationWindow_Deactivated", window, EventArgs.Empty); Layout(root, 860, 640);
        Check(rail.ActualWidth == 72, "Switching away collapses stale hover and keyboard state");
        rail.SetValue(focusKey, false);


        // Simulate a slow preferences disk. Translation and dispatcher work must not wait for it.
        var disk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeField = typeof(MainWindow).GetField("_preferencesWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
        writeField.SetValue(window, disk.Task);
        var combo = Get<ComboBox>(window, "LanguageCombo");
        var other = initialLanguage == "en" ? "pt-BR" : "en";
        var clock = Stopwatch.StartNew();
        combo.SelectedValue = other; Layout(root, 860, 640); clock.Stop();
        Check(L.Language == other && Descendants<TextBlock>(settings).Any(t => t.Text == L.T("Idioma")),
            "Selecting the language updates existing settings immediately without Save or restart");
        Check(Descendants<TextBlock>(Get<Button>(window, "HeaderVersionsButton")).Any(t => t.Text == L.T("Versões"))
            && Get<Button>(window,"SettingsButton").ToolTip?.ToString() == L.T("Configurações"),
            "Existing header and navigation tooltip bindings switch languages");
        Check(existingMenu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == L.T("Login / atualizar sessão")),
            "An already-created account context menu updates while closed");
        Check(ReferenceEquals(row, window.WorkspaceAccounts[0]) && row.IsSelected && row.PrimaryAction == L.T("Focar"),
            "Language changes preserve live account objects and selection while translating computed actions");
        Check(window.QueuePreview.Select(q=>q.Profile.Id).SequenceEqual(queue) && target.Text == "https://www.roblox.com/games/1818"
            && settings.Visibility == Visibility.Visible, "Language changes preserve queue order, target input and current page");
        var heartbeat = false;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => heartbeat = true));
        PumpUntil(() => heartbeat);
        Check(!((Task)writeField.GetValue(window)!).IsCompleted && clock.ElapsedMilliseconds < 1000,
            $"Language rendering completes while disk is blocked and dispatcher stays responsive ({clock.ElapsedMilliseconds}ms)");
        Render(root,860,640,Path.Combine(output,"settings-live-switched.png"));
        combo.SelectedValue = initialLanguage; combo.SelectedValue = other; combo.SelectedValue = initialLanguage;
        disk.SetResult();
        PumpUntil(() => ((Task)writeField.GetValue(window)!).IsCompleted);
        Layout(root,860,640);
        Check(UiPreferences.Read(preferences).Language == initialLanguage && L.Language == initialLanguage,
            "Rapid language changes save the last choice in order without overwriting it with an older write");
        Check(Descendants<TextBlock>(settings).Any(t => t.Text == L.T("Idioma")), "Switching back updates the same existing settings controls");
        Invoke(window,"SetComfortPage", false); Layout(root,1080,760);
        Check(Descendants<TextBlock>(list).Any(t=>t.Text == L.T("Em execução")), "Returning to accounts shows translated running status without refreshing processes");
        Invoke(window,"CanvasClearSelection_Click",window,new RoutedEventArgs());
    }

    private static void CheckText(bool passed, string key) { if(!passed)throw new InvalidOperationException("Untranslated catalog entry: "+key); }

    private static void RunMenuChecks(MainWindow window, string output)
    {
        var actual = Descendants<Button>(Get<ListBox>(window, "ProfilesGrid")).First(button => button.ContextMenu is not null).ContextMenu!;
        var menu = new ContextMenu { Style = (Style)window.FindResource(typeof(ContextMenu)), Resources = window.Resources, Visibility = Visibility.Visible };
        foreach (var entry in actual.Items)
        {
            if (entry is MenuItem item) menu.Items.Add(new MenuItem { Header = item.Header });
            else if (entry is Separator) menu.Items.Add(new Separator());
        }
        Render(menu, 280, 320, Path.Combine(output, "account-menu.png"));
        Check(menu.Items.OfType<MenuItem>().Count() == 5 && Descendants<TextBlock>(menu).Count(text => !string.IsNullOrEmpty(text.Text)) >= 5,
            "Account action menu materializes every existing action");
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var header = Descendants<TextBlock>(item).First(text => text.Text == item.Header.ToString());
            var probe = new TextBlock { Text = header.Text, FontFamily = header.FontFamily, FontSize = header.FontSize, FontWeight = header.FontWeight };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var bounds = new Rect(header.TranslatePoint(new Point(), menu), header.RenderSize);
            Check(header.ActualWidth >= probe.DesiredSize.Width - 1 && bounds.Left >= 8 && bounds.Right < menu.ActualWidth,
                "Menu header fits without an overlapping native icon gutter: " + header.Text);
        }
    }
    private static void CheckWholeAccountRows(QueueAccountsPanel chooser)
    {
        var row = Descendants<CheckBox>(chooser).First(box => box.Focusable && box.IsEnabled && box.DataContext is QueueAccountChoice);
        var choice = (QueueAccountChoice)row.DataContext;
        var wasChecked = choice.IsChosen;
        foreach (var point in new[] { new Point(8, row.ActualHeight / 2), new Point(100, row.ActualHeight / 2), new Point(row.ActualWidth - 10, row.ActualHeight / 2) })
        {
            DependencyObject? hit = VisualTreeHelper.HitTest(row, point)?.VisualHit;
            while (hit is not null && !ReferenceEquals(hit, row)) hit = VisualTreeHelper.GetParent(hit);
            Check(ReferenceEquals(hit, row), "Padding, account name and status hit the same native whole-row checkbox");
        }
        var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(row);
        var provider = (System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle);
        provider.Toggle();
        Check(choice.IsChosen != wasChecked, "Whole-row native toggle updates the actual chosen-account model");
        provider.Toggle();
        Check(choice.IsChosen == wasChecked, "Whole-row selection toggles off without losing other selections");
        Check(chooser.Choices.Where(item => !item.CanChoose).All(item => !item.IsChosen), "Existing and unsigned accounts remain unselectable");
    }

    private static void RunSavedDestinationChecks(MainWindow window, FrameworkElement root, string output)
    {
        HideDrawer(window);
        Invoke(window, "ShowComfortLaunchPanel", false);
        Get<Expander>(window, "SingleAccountExpander").IsExpanded = true;
        var combo = Get<ComboBox>(window, "LaunchLinkProfileCombo");
        combo.SelectedItem = window.Profiles[1];
        Layout(root, 1080, 760);
        Check(Descendants<TextBlock>(combo).Any(text => text.Text == window.Profiles[1].Name)
            && !Descendants<TextBlock>(combo).Any(text => text.Text.Contains("AccountProfile {")),
            "Expanded individual-account selector displays the profile name instead of its record representation");
        typeof(MainWindow).GetField("_savedDestinationsReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        var saved = new SavedLaunchDestination(Guid.NewGuid(), "Meu servidor de teste", "https://www.roblox.com/games/2753915549?privateServerLinkCode=fixture-only");
        window.SavedDestinations.Add(saved);
        Get<ComboBox>(window, "SavedDestinationCombo").SelectedItem = saved;
        Layout(root, 1080, 760);
        Check(Get<TextBox>(window, "LaunchLinkInput").Text == saved.Link && Get<Button>(window, "SavedDestinationEditButton").IsEnabled,
            "Choosing a named saved destination restores its exact link and enables editing");
        Render(root, 1080, 760, Path.Combine(output, "saved-destinations.png"));
        Invoke(window, "EditSavedDestination_Click", window, new RoutedEventArgs());
        var actualEditor = (DestinationEditorPanel)Get<ContentControl>(window, "DrawerBody").Content;
        Check(((TextBox)actualEditor.FindName("NameInput")).Text == saved.Name && ((TextBox)actualEditor.FindName("LinkInput")).Text == saved.Link,
            "Editing a selected saved destination opens both its current name and link");
        HideDrawer(window); // Do not save through the production store in a preview.

        Invoke(window, "ShowComfortLaunchPanel", false);
        var savedCombo = Get<ComboBox>(window, "SavedDestinationCombo");
        var destinationInput = Get<TextBox>(window, "LaunchLinkInput");
        var plan = SequencePlan(window, true);
        Check(Equals(plan.GetType().GetProperty("Target")!.GetValue(plan), RobloxLinkParser.Parse(saved.Link)),
            "Opening a selected saved destination uses its full private-server target");
        destinationInput.Text = "1818";
        Layout(root, 1080, 760);
        Check(savedCombo.SelectedItem is null && !Get<Button>(window, "SavedDestinationEditButton").IsEnabled
            && Get<TextBlock>(window, "SavedDestinationDraftLabel").Visibility == Visibility.Visible,
            "Editing a saved link immediately becomes a new destination instead of retaining a stale saved selection");
        Check(window.SavedDestinations.Single() == saved,
            "Typing a new destination leaves the original saved name and link intact");
        plan = SequencePlan(window, true);
        Check(Equals(plan.GetType().GetProperty("Target")!.GetValue(plan), RobloxLinkParser.Parse("1818")),
            "Opening after an edit uses the visible draft instead of the previously saved private server");
        Invoke(window, "SaveCurrentDestination_Click", window, new RoutedEventArgs());
        actualEditor = (DestinationEditorPanel)Get<ContentControl>(window, "DrawerBody").Content;
        Check(((TextBox)actualEditor.FindName("NameInput")).Text.Length == 0
            && ((TextBox)actualEditor.FindName("LinkInput")).Text == "1818",
            "Saving an edited draft offers a new named destination with the edited link");
        HideDrawer(window);
        Invoke(window, "ShowComfortLaunchPanel", false);
        Check(destinationInput.Text == "1818" && savedCombo.SelectedItem is null,
            "Switching workspace tabs preserves the unsaved destination and its detached state");
        Render(root, 1080, 760, Path.Combine(output, "new-destination.png"));
        savedCombo.SelectedItem = saved;
        Check(destinationInput.Text == saved.Link,
            "Choosing the same saved destination after editing restores its link again");
        destinationInput.Clear();
        Check(savedCombo.SelectedItem is null && destinationInput.Text.Length == 0,
            "Clearing the destination also clears the saved selection without restoring old text");
        savedCombo.SelectedItem = saved;
        Check(destinationInput.Text == saved.Link,
            "Reselecting a saved destination after clearing supplies a usable launch target");
        destinationInput.AppendText(" ");
        Check(savedCombo.SelectedItem is null && destinationInput.Text == saved.Link + " ",
            "Even a single typed character detaches the draft without discarding the user's edit");
        savedCombo.SelectedItem = saved;

        var store = new SavedDestinationStore(new AppPaths(Path.Combine(output, "saved-links-fixture")));
        var committed = new List<SavedLaunchDestination>();
        async Task SaveFixture(SavedLaunchDestination item)
        {
            var next = committed.Where(old => old.Id != item.Id).Append(item).ToList();
            await store.SaveAsync(next); committed = next;
        }
        var editor = new DestinationEditorPanel(null, saved.Link, SaveFixture);
        ((TextBox)editor.FindName("NameInput")).Text = "Meu servidor";
        var completed = false; editor.Completed += (_, _) => completed = true;
        InvokeResult(editor, "Save_Click", editor, new RoutedEventArgs()); PumpUntil(() => completed && !editor.IsSaving);
        var read = store.LoadAsync(); PumpUntil(() => read.IsCompleted);
        Check(read.GetAwaiter().GetResult().Single() is { Name: "Meu servidor" } persisted && persisted.Link == saved.Link,
            "Destination editor persists the name and private link to isolated local storage");
        var first = committed.Single();
        editor = new DestinationEditorPanel(first, string.Empty, SaveFixture);
        ((TextBox)editor.FindName("NameInput")).Text = "Meu jogo renomeado";
        ((TextBox)editor.FindName("LinkInput")).Text = "1818";
        completed = false; editor.Completed += (_, _) => completed = true;
        InvokeResult(editor, "Save_Click", editor, new RoutedEventArgs()); PumpUntil(() => completed && !editor.IsSaving);
        read = store.LoadAsync(); PumpUntil(() => read.IsCompleted);
        Check(read.GetAwaiter().GetResult().Single() is { Name: "Meu jogo renomeado", Link: "1818" } updated && updated.Id == first.Id,
            "Editing both fields replaces the existing destination without duplicating it");
        Render(editor, 580, 480, Path.Combine(output, "destination-editor.png"));
        var calls = 0;
        var invalid = new DestinationEditorPanel(null, "https://example.com", _ => { calls++; return Task.CompletedTask; });
        ((TextBox)invalid.FindName("NameInput")).Text = "Inválido";
        InvokeResult(invalid, "Save_Click", invalid, new RoutedEventArgs());
        Check(calls == 0 && ((TextBlock)invalid.FindName("ErrorText")).Text.Length > 0, "Invalid saved links fail inline before writing any file");
        var failed = new DestinationEditorPanel(first, first.Link, _ => Task.FromException(new IOException("Fixture write failure")));
        InvokeResult(failed, "Save_Click", failed, new RoutedEventArgs());
        Check(!failed.IsSaving && ((TextBlock)failed.FindName("ErrorText")).Text.Length > 0 && ((Button)failed.FindName("SaveButton")).IsEnabled,
            "A failed destination write keeps the editor open and lets the user correct or retry");
        var isolatedStore = new SavedDestinationStore(new AppPaths(Path.Combine(output, "delete-links-fixture")));
        typeof(MainWindow).GetField("_savedDestinationStore", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, isolatedStore);
        var initialWrite = isolatedStore.SaveAsync(window.SavedDestinations.ToArray()); PumpUntil(() => initialWrite.IsCompleted);
        initialWrite.GetAwaiter().GetResult();
        var deletion = (Task)InvokeResult(window, "RemoveSavedDestinationAsync", saved)!;
        PumpUntil(() => deletion.IsCompleted); deletion.GetAwaiter().GetResult();
        var afterDelete = isolatedStore.LoadAsync(); PumpUntil(() => afterDelete.IsCompleted);
        Check(afterDelete.GetAwaiter().GetResult().Count == 0 && window.SavedDestinations.Count == 0
            && savedCombo.SelectedItem is null && destinationInput.Text == saved.Link,
            "Deleting a saved destination persists removal and retains the visible link as an unsaved draft");
        Get<Expander>(window, "SingleAccountExpander").IsExpanded = false;
        HideDrawer(window);
    }

    private static void RunMotionChecks(MainWindow window)
    {
        var shift = new TranslateTransform();
        var finished = false;
        UiMotion.Animate(shift, TranslateTransform.XProperty, 24, 0, 170, false, () => finished = true);
        Check(finished && shift.X == 0 && !shift.HasAnimatedProperties,
            "Reduced-motion path applies its final state immediately without an animation clock");
        // A live compositor is required for clock completion. Keep this independent
        // 1px fixture offscreen; never show MainWindow or start production services.
        using var compositor = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("MultiJanela motion fixture")
        { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x90000000) });
        compositor.RootVisual = new Border { Width = 1, Height = 1, Background = Brushes.Black, RenderTransform = shift };
        var obsoleteCompleted = false; finished = false;
        UiMotion.Animate(shift, TranslateTransform.XProperty, 24, 0, 800, true, () => obsoleteCompleted = true);
        UiMotion.Animate(shift, TranslateTransform.XProperty, shift.X, 12, 40, true, () => finished = true);
        PumpUntil(() => finished);
        Check(!obsoleteCompleted && shift.X == 12 && !shift.HasAnimatedProperties,
            $"Rapid transitions replace obsolete work, complete at the new position and release their clocks (obsolete={obsoleteCompleted}, X={shift.X}, animated={shift.HasAnimatedProperties})");
        var panel = new System.Windows.Controls.Grid();
        UiMotion.Reveal(panel);
        Check(panel.Opacity == 1 && panel.RenderTransform is TranslateTransform { Y: 0, HasAnimatedProperties: false },
            "Hidden pages skip transition work and retain a fully readable resting state");
        Invoke(window, "OpenDrawer", "First", "", new TextBlock { Text = "First" });
        Invoke(window, "CloseDrawer");
        var replacement = new TextBlock { Text = "Replacement" };
        Invoke(window, "OpenDrawer", "Second", "", replacement);
        Check(ReferenceEquals(Get<ContentControl>(window, "DrawerBody").Content, replacement)
            && Get<Border>(window, "DrawerHost").Visibility == Visibility.Visible,
            "Closing and reopening a panel preserves the latest panel content");
        Invoke(window, "CloseDrawer");
        Check(Get<ContentControl>(window, "DrawerBody").Content is null && Get<Border>(window, "Scrim").Visibility == Visibility.Collapsed,
            "Dismissal releases panel content and its background overlay");
    }

    private static void RunSequenceChecks(MainWindow window, FrameworkElement root, string output)
    {
        var library = Get<ListBox>(window, "ProfilesGrid");
        var destination = Get<TextBox>(window, "LaunchLinkInput");
        Invoke(window, "OrbitClearSelection_Click", window, new RoutedEventArgs());
        Layout(root, 1540, 1000);
        Check(window.QueuePreview.Count == 0 && library.SelectedItems.Count == 2 && !Get<Button>(window, "LaunchAllButton").IsEnabled,
            "Clear empties the sequence, preserves library selection and disables opening");
        Get<TextBox>(window, "TargetInput").Text = "8"; Layout(root, 1540, 1000);
        Check(window.QueuePreview.Count == 0, "Target and process updates never repopulate an empty sequence");
        var desired = new[] { window.Profiles[4], window.Profiles[2], window.Profiles[0] };
        Invoke(window, "AddSequenceAccounts", (object)desired);
        Invoke(window, "AddSequenceAccounts", (object)new[] { window.Profiles[4], window.Profiles[7] });
        Check(window.QueuePreview.Select(row => row.Profile.Id).SequenceEqual(desired.Select(p => p.Id)), "Add preserves chosen order, excludes duplicate and unsigned accounts");
        Invoke(window, "QueueMove_Click", new Button { DataContext = window.QueuePreview[2], CommandParameter = "up" }, new RoutedEventArgs(Button.ClickEvent));
        Check(window.QueuePreview.Select(row => row.Profile.Id).SequenceEqual(new[] { desired[0].Id, desired[2].Id, desired[1].Id }), "Up action reorders the actual launch sequence");
        Invoke(window, "MoveSequenceAccount", desired[0].Id, 2);
        Check(window.QueuePreview.Select(row => row.Number).SequenceEqual(new[] { 1, 2, 3 }) && window.QueuePreview[^1].Profile.Id == desired[0].Id,
            "Drag move renumbers entries and keeps the requested insertion position");
        var saved = window.QueuePreview.Select(row => row.Profile.Id).ToArray();
        foreach (var input in new[] { "2753915549", "https://www.roblox.com/games/2753915549/Example", "https://www.roblox.com/games/2753915549?privateServerLinkCode=fixture-private", "https://www.roblox.com/share?code=fixture-share&type=Server" })
        {
            destination.Text = input;
            var plan = SequencePlan(window, true);
            var target = (RobloxLaunchTarget)plan.GetType().GetProperty("Target")!.GetValue(plan)!;
            var profiles = (AccountProfile[])plan.GetType().GetProperty("Profiles")!.GetValue(plan)!;
            Check(profiles.Select(p => p.Id).SequenceEqual(saved) && target == RobloxLinkParser.Parse(input), "Destination sequence preserves queue order and the validated target type");
            destination.Text = "1818"; library.UnselectAll(); library.SelectedItem = window.Profiles[9];
            Check(target == RobloxLinkParser.Parse(input), "Click-time target remains frozen when input and library selection change");
        }
        var home = SequencePlan(window, false);
        Check(home.GetType().GetProperty("Target")!.GetValue(home) is null, "Home sequence explicitly uses no game override");
        destination.Text = "https://example.com/not-roblox";
        var invalidArgs = new object?[] { true, null, null };
        var valid = (bool)typeof(MainWindow).GetMethod("TryCreateSequencePlan", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, invalidArgs)!;
        Check(!valid && !string.IsNullOrWhiteSpace((string?)invalidArgs[2]), "Invalid destinations are rejected before guard or launch work");
        var snapshot = (AccountProfile[])home.GetType().GetProperty("Profiles")!.GetValue(home)!;
        var original = window.Profiles[0]; window.Profiles[0] = original with { Name = "Renamed fixture" };
        Layout(root, 1540, 1000);
        Check(window.QueuePreview.Any(row => row.Profile.Name == "Renamed fixture") && snapshot.Any(p => p.Name == original.Name), "Profile replacement refreshes queue rows without mutating an existing launch plan");
        window.Profiles[0] = original;
        using (var busy = new CancellationTokenSource())
        {
            var field = typeof(MainWindow).GetField("_launchBatchCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(window, busy); Invoke(window, "UpdateQueuePreview");
            Invoke(window, "OrbitClearSelection_Click", window, new RoutedEventArgs());
            Invoke(window, "MoveSequenceAccount", saved[0], 2);
            Check(window.QueuePreview.Count == 3 && !Get<ListBox>(window, "QueuePreviewList").IsEnabled && !Get<Button>(window, "LaunchDestinationSequenceButton").IsEnabled,
                "An active sequence blocks edits and duplicate destination submissions");
            field.SetValue(window, null); Invoke(window, "UpdateQueuePreview");
        }
        Invoke(window, "QueueRemove_Click", new Button { DataContext = window.QueuePreview[1] }, new RoutedEventArgs(Button.ClickEvent));
        Check(window.QueuePreview.Count == 2 && window.QueuePreview.Select(row => row.Number).SequenceEqual(new[] { 1, 2 }), "Individual removal changes and renumbers only the sequence");
        Invoke(window, "QueueAddAccounts_Click", window, new RoutedEventArgs());
        var chooser = (QueueAccountsPanel)Get<ContentControl>(window, "DrawerBody").Content;
        Check(chooser.Choices.Where(c => !c.CanChoose).Count() == 3, "Chooser distinguishes existing queue members and accounts needing login");
        var available = chooser.Choices.Where(c => c.CanChoose).Take(2).ToArray();
        available[0].IsChosen = true;
        ((TextBox)chooser.FindName("SearchInput")).Text = available[1].Profile.Name;
        InvokeResult(chooser, "ApplySearch");
        InvokeResult(chooser, "SelectVisible_Click", chooser, new RoutedEventArgs());
        Check(chooser.SelectedProfiles.Length == 2, "Chooser search preserves hidden selections and marks only visible matches");
        ((TextBox)chooser.FindName("SearchInput")).Clear(); InvokeResult(chooser, "ApplySearch");
        Render(root, 1080, 760, Path.Combine(output, "sequence-accounts.png"));
        CheckWholeAccountRows(chooser);
        InvokeResult(chooser, "Add_Click", chooser, new RoutedEventArgs());
        Check(window.QueuePreview.Count == 4, "Chooser confirmation appends exactly the chosen available accounts");
        HideDrawer(window);
        Invoke(window, "OrbitClearSelection_Click", window, new RoutedEventArgs());
        Invoke(window, "AddSequenceAccounts", (object)desired);
        Get<TextBox>(window, "TargetInput").Text = "5";
        destination.Text = "2753915549";
        library.UnselectAll(); library.SelectedItems.Add(window.Profiles[0]); library.SelectedItems.Add(window.Profiles[1]);
        Render(root, 1080, 760, Path.Combine(output, "sequence-workspace.png"));
    }
    private static object SequencePlan(MainWindow window, bool target)
    {
        var args = new object?[] { target, null, null };
        var success = (bool)typeof(MainWindow).GetMethod("TryCreateSequencePlan", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
        if (!success) throw new InvalidOperationException((string?)args[2]);
        return args[1]!;
    }
    private static void HideDrawer(MainWindow window)
    {
        Get<Border>(window, "DrawerHost").Visibility = Visibility.Collapsed;
        Get<ContentControl>(window, "DrawerBody").Content = null;
        var scrim = Get<Border>(window, "Scrim");
        scrim.BeginAnimation(UIElement.OpacityProperty, null); scrim.Opacity = 0; scrim.Visibility = Visibility.Collapsed;
        Invoke(window, "SetComfortPage", false);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void CheckTileBounds(InstanceMap map, string label)
    {
        var tiles = Descendants<ListBoxItem>(map).ToArray();
        var bounds = tiles.Select(tile => new Rect(tile.TranslatePoint(new Point(), map), tile.RenderSize)).ToArray();
        Check(tiles.Length > 0 && tiles.All(tile => tile.ActualWidth >= map.ActualWidth - 24 && tile.ActualHeight >= 80)
            && bounds.SelectMany((rect, i) => bounds.Skip(i + 1).Select(other => Math.Min(rect.Bottom, other.Bottom) - Math.Max(rect.Top, other.Top) <= 0.5)).All(clear => clear),
            $"{label}: full-width session rows remain legible and do not overlap");
        Check(tiles.All(tile => Descendants<TextBlock>(tile).Where(text => text.Text.StartsWith("PID ") || text.Text.StartsWith("Versão ")).All(text =>
            new Rect(tile.RenderSize).Contains(new Rect(text.TranslatePoint(new Point(), tile), text.RenderSize)))),
            $"{label}: PID and client version fit within each readable card");
    }
    private static object? InvokeResult(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void RunPanelChecks(MainWindow window, string output)
    {
        // Local executable fixture + injected verifier, no Roblox launch or real network.
        var fixtureRoot = Path.Combine(output, "version-fixture");
        var versionFolder = Path.Combine(fixtureRoot, "version-1111111111111111");
        Directory.CreateDirectory(versionFolder);
        File.Copy(Environment.ProcessPath!, Path.Combine(versionFolder, "RobloxPlayerBeta.exe"), true);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var uiThread = Environment.CurrentManagedThreadId;
        var scanThread = 0; var scans = 0;
        var locator = new RobloxInstallationLocator(fixtureRoot, () => null, () => null, () => "LIVE",
            (_, _) => { Interlocked.Increment(ref scans); scanThread = Environment.CurrentManagedThreadId; entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); return ("Fixture Roblox", true); });
        using var http = new System.Net.Http.HttpClient(new OfflineHandler());
        var panel = new VersionsPanel(locator, new RobloxVersionChecker(http), new RobloxDeploymentCatalog(http),
            new RobloxDeploymentDownloader(http, Path.Combine(output, "downloads-fixture")), null, "LIVE", true);
        InvokeResult(panel, "VersionsPanel_Loaded", panel, new RoutedEventArgs());
        PumpUntil(() => entered.IsSet);
        Check(scanThread != uiThread, "Version signature verification runs off the UI thread");
        var pending = (Task)panel.GetType().GetField("_localRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        Check(!pending.IsCompleted, "Version panel returns control while local discovery is still pending");
        release.Set(); PumpUntil(() => pending.IsCompleted);
        Check(scans == 1, "Effective version and installed rows share a single signature scan");
        var versions = (ListBox)panel.FindName("VersionsGrid");
        Check(versions.Items.Count == 1, "Installed version remains available when the network is unavailable");
        var deleting = panel.GetType().GetField("_isDeleting", BindingFlags.Instance | BindingFlags.NonPublic)!;
        deleting.SetValue(panel, true); InvokeResult(panel, "UpdateSelectionButtons");
        Check(!((Button)panel.FindName("UseSelectedButton")).IsEnabled && !((Button)panel.FindName("DownloadHashButton")).IsEnabled && !((Button)panel.FindName("AutomaticButton")).IsEnabled,
            "Version removal excludes conflicting apply and download actions");
        deleting.SetValue(panel, false); InvokeResult(panel, "UpdateSelectionButtons");
        var entries = Enumerable.Range(1, 2000).Select(i => new RobloxDeploymentEntry($"version-{i:x16}", $"0.735.{i}.0", DateTime.UtcNow.AddHours(-i))).ToArray();
        panel.GetType().GetField("_catalogEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, entries);
        var rows = (System.Collections.Specialized.INotifyCollectionChanged)panel.GetType().GetField("_rowCollection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var resets = 0;
        rows.CollectionChanged += (_, _) => resets++;
        InvokeResult(panel, "RebuildRows");
        Check(resets == 1, "Catalog rebuild sends one collection notification for 2000 versions");
        ((TextBlock)panel.FindName("CurrentVersionText")).Text = "0.735.3.0";
        ((TextBlock)panel.FindName("LatestVersionText")).Text = "0.735.2000.0";
        Render(panel, 640, 760, Path.Combine(output, "versions-panel.png"));
        Check(Descendants<ListBoxItem>(versions).Count() < 25, "Version catalog virtualizes 2000 deployments");
        ((TextBox)panel.FindName("FilterInput")).Text = "0.735.1999.0";
        InvokeResult(panel, "ApplyFilter");
        Check(versions.Items.Count == 1 && versions.SelectedItem is RobloxVersionRow { Version: "0.735.1999.0" }, "Version search selects a visible matching row");
        ((TextBox)panel.FindName("FilterInput")).Clear(); InvokeResult(panel, "ApplyFilter");
        ((Expander)panel.FindName("VersionAdvancedExpander")).IsExpanded = true;
        Render(panel, 640, 680, Path.Combine(output, "versions-advanced.png"));
        Check(((Button)panel.FindName("DownloadHashButton")).ActualWidth > 70, "Advanced version controls remain accessible");
        panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        var login = new AccountLoginWindow(window.Profiles[0], new AppPaths(Path.Combine(output, "login-fixture")));
        Render((FrameworkElement)login.Content, 1060, 770, Path.Combine(output, "login-window.png"));
        var loginRoot = (FrameworkElement)login.Content;
        var help = Descendants<Expander>(loginRoot).Single();
        help.IsExpanded = true;
        Render(loginRoot, 780, 580, Path.Combine(output, "login-compact-help.png"));
        var loginButtons = Descendants<Button>(loginRoot).Where(button => button.Visibility == Visibility.Visible && button.ActualWidth > 0).ToArray();
        Check(loginButtons.Length >= 5, "Compact login renders every primary and diagnostic action");
        foreach (var button in loginButtons)
            Check(new Rect(loginRoot.RenderSize).Contains(new Rect(button.TranslatePoint(new Point(), loginRoot), button.RenderSize)),
                $"Login action fits the minimum viewport: {button.Content}");
        Check(Descendants<TextBlock>(loginRoot).Any(text => text.Text == L.T("Problemas com login ou verificação?")), "Login help uses the selected language");
        Check(((Microsoft.Web.WebView2.Wpf.WebView2)login.FindName("IdentityBrowser")).Visibility == Visibility.Hidden,
            "Identity probe has no interactive surface");
        ((Microsoft.Web.WebView2.Wpf.WebView2)login.FindName("IdentityBrowser")).Dispose();
        ((Microsoft.Web.WebView2.Wpf.WebView2)login.FindName("Browser")).Dispose();
        var launch = new RobloxLaunchWindow(window.Profiles[0], new AppPaths(Path.Combine(output, "launch-fixture")),
            new RobloxInstallationInfo(null, null, "0.735.3.0", null, false, "Fixture"), Guid.NewGuid(), "LIVE");
        Render((FrameworkElement)launch.Content, 590, 470, Path.Combine(output, "launch-window.png"));
        InvokeResult(launch, "Fail", "fixture", L.T("A sessão desta conta expirou. Use Login / atualizar e tente novamente."));
        Render((FrameworkElement)launch.Content, 590, 470, Path.Combine(output, "launch-error.png"));
        Check(!((ProgressBar)launch.FindName("LaunchProgress")).IsIndeterminate, "Failed launch stops the busy indicator and exposes the failure");
        ((IDisposable)launch.FindName("Browser")).Dispose();
        var report = new ReportPanel(L.T("DIAGNÓSTICO / LOCAL"), L.T("Seu ambiente, em detalhes."), L.T("Estado medido nesta consulta. O relatório não inclui dados de autenticação."),
            L.T("MULTIJANELA PARA ROBLOX — DIAGNÓSTICO") + "\n\n" + L.F("Versão: {0}\n", "0.735.3.0") + L.F("Canal: {0}\n", "LIVE") + L.F("Processos Roblox: {0}", 5),
            [new(L.T("PROCESSOS"), "5"), new(L.T("JANELAS"), "5"), new(L.T("CONTAS"), "12")]);
        Render(report, 640, 760, Path.Combine(output, "diagnostics-panel.png"));
    }
    private sealed class OfflineHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame(); var started = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || started.Elapsed > TimeSpan.FromSeconds(8)) { timer.Stop(); frame.Continue = false; } };
        timer.Start(); Dispatcher.PushFrame(frame);
        if (!condition()) throw new TimeoutException("UI fixture did not finish.");
    }
    // Local, code-drawn fixture portraits exercise the real image bindings and
    // native decoder without requesting Roblox accounts or downloading assets.
    private static string FixtureAvatar(string output, int index)
    {
        var folder = Path.Combine(output, "avatars"); Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"fixture-{index}.png");
        var visual = new DrawingVisual();
        var colors = new[] { "#C8F593", "#8FD5ED", "#DBC9FC", "#F4C78A", "#9BE7D1", "#F8BFCB" };
        SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brush("#162B30"), null, new Rect(0, 0, 96, 96));
            dc.DrawEllipse(Brush(colors[index % colors.Length]), null, new Point(48, 56), 41, 41);
            dc.DrawRoundedRectangle(Brush(index % 3 == 0 ? "#C78D68" : "#F2C99D"), new Pen(Brush("#1C2832"), 1), new Rect(25, 25, 46, 53), 13, 13);
            dc.DrawRoundedRectangle(Brush(index % 3 == 1 ? "#58422E" : "#182633"), null, new Rect(23, 18, 50, 23), 8, 8);
            dc.DrawRectangle(Brush(index % 3 == 1 ? "#58422E" : "#182633"), null, new Rect(25, 34, 10, 13));
            dc.DrawEllipse(Brush("#182633"), null, new Point(39, 49), 2.3, 3.5);
            dc.DrawEllipse(Brush("#182633"), null, new Point(58, 49), 2.3, 3.5);
            var smile = Geometry.Parse("M 38 61 Q 48 71 59 60");
            dc.DrawGeometry(null, new Pen(Brush("#182633"), 2.5), smile);
        }
        var bitmap = new RenderTargetBitmap(96, 96, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
        return new Uri(file).AbsoluteUri;
    }
    private static T Get<T>(MainWindow window, string name) where T : class => (T)window.FindName(name)!;
    private static void Invoke(MainWindow window, string method, params object[] args)
    {
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        // Render the final drawer state, independently of an offscreen animation clock.
        if (method == "QueueAddAccounts_Click" && Get<Border>(window, "DrawerHost").Visibility == Visibility.Visible)
        {
            var shift = Get<TranslateTransform>(window, "DrawerShift");
            shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = 0;
            var scrim = Get<Border>(window, "Scrim"); scrim.BeginAnimation(UIElement.OpacityProperty, null); scrim.Opacity = 0.6;
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++; Console.WriteLine($"[PASS] {message}");
    }
    private static void Layout(FrameworkElement root, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        // Offscreen roots have no composition frame to apply deferred templates
        // after a recycled container receives its new DataContext.
        root.UpdateLayout();
    }
    private static void Render(FrameworkElement root, int width, int height, string path)
    {
        Layout(root, width, height);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
