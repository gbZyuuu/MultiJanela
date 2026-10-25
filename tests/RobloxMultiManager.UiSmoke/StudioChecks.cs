using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;

internal static partial class Program
{
    private sealed class StudioHttp : System.Net.Http.HttpMessageHandler
    {
        public int ServerRequests;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); object response;
            if (request.RequestUri!.AbsolutePath.EndsWith("get-sorts")) response = new { sorts = new[] { new { contentType = "Games", sortId = "top-playing-now", games = Enumerable.Range(1, 80).Select(n => new { universeId = n, rootPlaceId = n + 100, name = "Adventure " + n, playerCount = 120000 - n * 1500 }).ToArray() } } };
            else if (request.RequestUri.AbsolutePath.EndsWith("omni-search")) response = new { searchResults = new[] { new { contentGroupType = "Game", contents = new[] { new { universeId = 90, rootPlaceId = 190, name = "Search result", playerCount = 25 } } } } };
            else if(request.RequestUri.AbsolutePath.EndsWith("servers/Public"))
            {ServerRequests++;var more=request.RequestUri.Query.Contains("cursor=");response=new{data=Enumerable.Range(more?101:1,100).Select(n=>new{id=new Guid(n,0,0,new byte[8]).ToString(),playing=n%30,maxPlayers=30,ping=30}).ToArray(),nextPageCursor=more?null:"page2"};}
            else if (request.RequestUri.AbsolutePath.EndsWith("icons")) response = new { data = Array.Empty<object>() };
            else return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
    private static void RunStudioChecks(MainWindow main, string output)
    {
        void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        void Load(FrameworkElement panel) => panel.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        T Find<T>(FrameworkElement owner, string name) where T : class => (T)owner.FindName(name);
        var root = (FrameworkElement)main.Content;
        typeof(MainWindow).GetField("_paths", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, new AppPaths(Path.Combine(output, "isolated-shell-" + Guid.NewGuid().ToString("N"))));
        var names = new[] { "Aurora", "Altair", "Atlas", "Nova", "Orbit", "Eclipse", "Luna", "Cosmo" };
        for (var i = 0; i < names.Length; i++) main.Profiles.Add(new AccountProfile(Guid.NewGuid(), names[i], "https://www.roblox.com/home", 720100L + i, names[i].ToLowerInvariant() + "_play", names[i], FixtureAvatar(output, i)));
        for (var i = 0; i < 4; i++) main.Instances.Add(new RobloxInstanceInfo(18240 + i * 24, DateTime.UtcNow.AddMinutes(-i * 7), (nint)(i + 1), "Roblox", RobloxInstanceState.Running, true, ProfileName: names[i], VersionId: "version-1111111111111111", FileVersion: "0.744.0.7441080", AccountUserId: 720100L + i, AccountUsername: names[i].ToLowerInvariant() + "_play", AccountIsExact: true));
        Invoke(main, "RefreshWorkspaceAccounts"); Layout(root, 1180, 800);
        Check(main.WorkspaceAccounts.Count == 4 && main.SavedAccounts.Count == 8, "Live sessions and the account library stay separate on the dashboard");
        Check(main.WorkspaceAccounts[0].Pid.Contains("18240") && main.WorkspaceAccounts[0].Version == "0.744.0.7441080", "PID and client version are available without opening details");
        main.WorkspaceAccounts[0].IsSelected = true;
        Check(Get<Button>(main, "CanvasCloseSelectedButton").IsEnabled, "Live session selection enables close selected");
        Invoke(main, "CanvasClearSelection_Click", main, new RoutedEventArgs()); Layout(root, 1180, 800);
        Check(main.WorkspaceAccounts.All(r => !r.IsSelected), "Clear selection clears session selection");
        var paths = new AppPaths(Path.Combine(output, "studio-fixtures-" + Guid.NewGuid().ToString("N"))); paths.EnsureCreated();
        var library = new WorkspaceLibraryStore(paths);
        // Form creation must work even when the launch sequence starts empty.
        var collections = new WorkspacesPanel(library, main.Profiles,
            (name, id, ids) => new(id, name, ids, "https://www.roblox.com/home", 5, 100, -1, false), _ => { }, () => [], _ => { }, _ => { });
        Load(collections); PumpUntil(() => !collections.IsBusy);
        Check(Find<TextBlock>(collections, "NoPresets").Visibility == Visibility.Visible && !Find<Button>(collections, "LoadPreset").IsEnabled, "Empty presets explain creation and disable unavailable actions");
        InvokeResult(collections, "RestoreDraft", new Dictionary<string, string> { ["preset-accounts"] = main.Profiles[0].Id.ToString(), ["group-accounts"] = main.Profiles[1].Id.ToString() });
        // IsLoaded is false in a non-presented fixture; restore the chosen IDs using the same pending draft once presented below.
        typeof(WorkspacesPanel).GetField("_presetAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(collections, new[] { main.Profiles[0].Id });
        typeof(WorkspacesPanel).GetField("_groupAccounts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(collections, new[] { main.Profiles[1].Id });
        Find<TextBox>(collections, "PresetName").Text = "Daily adventures";
        Find<TextBox>(collections, "PresetDestination").Text = "123456";
        InvokeResult(collections, "SavePreset_Click", collections, new RoutedEventArgs()); PumpUntil(() => !collections.IsBusy);
        var saved = library.LoadAsync(); Await(saved);
        Check(saved.Result.Presets.Length == 1 && saved.Result.Presets[0].Accounts.SequenceEqual(new[] { main.Profiles[0].Id }) && saved.Result.Presets[0].Destination == "123456", "Create preset saves its own chosen accounts and destination with an empty queue");
        Find<TextBox>(collections, "GroupName").Text = "Adventure team";
        InvokeResult(collections, "SaveGroup_Click", collections, new RoutedEventArgs()); PumpUntil(() => !collections.IsBusy);
        saved = library.LoadAsync(); Await(saved);
        Check(saved.Result.Groups.Single().Accounts.Single() == main.Profiles[1].Id && Find<Button>(collections, "UseGroup").IsEnabled, "Create group saves chosen accounts and enables use");
        var combo = Find<ComboBox>(collections, "Account"); combo.SelectedItem = main.Profiles[0];
        Layout(combo, 600, 48);
        Check(main.Profiles[0].ToString() == "Aurora" && Descendants<TextBlock>(combo).Any(t => t.Text == "Aurora") && Descendants<TextBlock>(combo).All(t => !t.Text.Contains("AccountProfile")), "Selected account renders only its public name");
        combo.IsDropDownOpen = true; Layout(combo, 600, 48);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        Layout((FrameworkElement)popup.Child, 600, 280);
        Console.WriteLine("[POPUP] " + string.Join(" / ", Descendants<TextBlock>(popup.Child).Select(t => t.Text)));
        Check(Descendants<TextBlock>(popup.Child).Any(t => t.Text == "Aurora") && Descendants<TextBlock>(popup.Child).All(t => !t.Text.Contains("GameUrl")), "Account popup rows render names without record details"); combo.IsDropDownOpen = false;
        var policy = GraphicsLaunchPolicy.Validate(new(true, 60, 4));
        var graphics = new GraphicsPanel(new RobloxGraphicsSettingsService(Path.Combine(paths.Root, "unused.xml")), main.Profiles, policy, value => { policy = value; return Task.CompletedTask; });
        Find<ComboBox>(graphics, "Account").SelectedIndex = 1;
        Check(Find<CheckBox>(graphics, "UseGlobal").IsChecked == true && !Find<ComboBox>(graphics, "Fps").IsEnabled, "An unopened account inherits global graphics by default");
        Find<CheckBox>(graphics, "UseGlobal").IsChecked = false; Find<ComboBox>(graphics, "Fps").SelectedValue = "120"; Find<Slider>(graphics, "Quality").Value = 2;
        InvokeResult(graphics, "SaveChanges"); PumpUntil(() => !graphics.IsBusy);
        Check(policy.ForAccount(main.Profiles[0].Id) == new RobloxGraphicsPreferences(120, 2) && policy.ForAccount(main.Profiles[1].Id) == new RobloxGraphicsPreferences(60, 4), "Individual graphics are editable while offline without changing other accounts");
        var draft = InvokeResult(graphics, "CaptureDraft"); Find<Slider>(graphics, "Quality").Value = 8; InvokeResult(graphics, "RestoreDraft", draft!);
        Check(Find<Slider>(graphics, "Quality").Value == 2, "Graphics handoff draft preserves slider values");
        using var gameHandler=new StudioHttp();using var http = new System.Net.Http.HttpClient(gameHandler);
        var destination = ""; var games = new GamesHub(new RobloxGameService(http), new GameLibraryStore(paths), (link, _) => destination = link, () => 2);
        InvokeResult(games, "RestoreDraft", new Dictionary<string, string> { ["query"] = "", ["games"] = "[]" });
        Load(games); PumpUntil(() => !games.IsBusy && Find<ListBox>(games, "Games").Items.Count == 24);
        Check(Find<ListBox>(games, "Games").Items.Count == 24, "All automatically loads discovery even after restoring an empty handoff draft");
        InvokeResult(games, "MoreGames_Click", games, new RoutedEventArgs()); PumpUntil(() => !games.IsBusy);
        Check(Find<ListBox>(games, "Games").Items.Count == 48, "Load more appends discovery cards");
        Find<ListBox>(games, "Games").SelectedIndex = 0; PumpUntil(()=>!games.IsBusy);
        Check(Find<ListBox>(games,"Servers").Items.Count==100&&gameHandler.ServerRequests==1,"Selecting a game loads servers without a separate click");
        InvokeResult(games,"Servers_Click",games,new RoutedEventArgs());PumpUntil(()=>!games.IsBusy);
        Check(gameHandler.ServerRequests==2,"Refresh invalidates the short-lived server cache"); InvokeResult(games, "UseGame_Click", games, new RoutedEventArgs());
        Check(destination == "https://www.roblox.com/games/101" && Find<Border>(games, "Inspector").Visibility == Visibility.Visible, "Selecting a game exposes details and a functional destination action");
        InvokeResult(games, "CloseDetails_Click", games, new RoutedEventArgs());
        Check(Find<Border>(games, "Inspector").Visibility == Visibility.Collapsed, "Game details close to recover browsing space");
        Find<TextBox>(games, "Query").Text = "a"; InvokeResult(games, "Search_Click", games, new RoutedEventArgs()); PumpUntil(() => !games.IsBusy);
        Check(Find<ListBox>(games, "Games").Items.OfType<GameSummary>().Single().Name == "Search result", "Search queries Roblox rather than filtering the current discovery list");
        InvokeResult(games, "All_Click", games, new RoutedEventArgs()); PumpUntil(() => !games.IsBusy);
        var automation = new AutomationPanel(new PreferenceFile<AutomationPreferences>(paths, "automation.json", () => AutomationPreferences.Empty, AutomationPreferences.Validate), () => saved.Result.Presets, _ => Task.CompletedTask, () => { }); Load(automation); PumpUntil(() => !automation.IsBusy);
        RunFocusResourceChecks(main, paths, output);
        var appearanceType = typeof(MainWindow).Assembly.GetType("RobloxMultiManager.AppearanceService")!;
        void Theme(int scale) => appearanceType.GetMethod("Apply")!.Invoke(null, new object[] { new AppearancePreferences(TextScale: scale) });
        RunCpuGlobalSliderChecks(main); RunOfflineCpuChecks(main, output); RunMotionChecks(main);
        var presented = (FrameworkElement)main.Content; main.Content = null; presented.DataContext = main;
        var host = new Window { Content = presented, Width = 1180, Height = 800, WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
        var rendered = false; host.ContentRendered += (_, _) => rendered = true;
        try
        {
            host.Show(); PumpUntil(() => rendered);
            foreach (var scale in new[] { 100, 125, 150 })
            {
                Theme(scale); host.Width = 860; host.Height = 640; Render(presented, 860, 640, Path.Combine(output, $"dashboard-compact-{scale}.png"));
                var left = Get<Grid>(main, "OpenSessionsSection"); var right = Get<Grid>(main, "SavedAccountsSection");
                Check(!new Rect(left.TranslatePoint(new Point(), presented), left.RenderSize).IntersectsWith(new Rect(right.TranslatePoint(new Point(), presented), right.RenderSize)), $"Separate account and session regions do not overlap at {scale}% text");
                var sidebar = Get<System.Windows.Controls.Border>(main, "TopNavBar");
                var workspace = Get<Grid>(main, "NexusWorkspace");
                Check(sidebar.ActualWidth == 64 && !new Rect(sidebar.TranslatePoint(new Point(), presented), sidebar.RenderSize).IntersectsWith(new Rect(workspace.TranslatePoint(new Point(), presented), workspace.RenderSize)), $"Fixed sidebar reserves its own space at {scale}% text");
                var addAccount = Get<Button>(main, "AddAccountButton");
                Check(new Rect(right.RenderSize).Contains(new Rect(addAccount.TranslatePoint(new Point(), right), addAccount.RenderSize)), $"Add account stays within the account section at {scale}% text");
                foreach (var id in new[] { "CanvasArrangeAllButton", "CanvasCloseAllButton", "CanvasCloseSelectedButton", "SettingsButton", "GamesNavButton" })
                { var button = Get<Button>(main, id); Check(button.ActualHeight >= 30 && new Rect(presented.RenderSize).Contains(new Rect(button.TranslatePoint(new Point(), presented), button.RenderSize)), $"{id} remains visible at minimum width and {scale}% text"); }
            }
            Theme(100); host.Width = 1180; host.Height = 800; Render(presented, 1180, 800, Path.Combine(output, "dashboard.png"));
            if (Environment.GetCommandLineArgs().Contains("--theme-gallery"))
            {
                foreach (var themeId in new[] { "midnight", "graphite", "ocean", "forest", "amethyst", "rose", "sand", "light", "contrast" })
                {
                    appearanceType.GetMethod("Apply")!.Invoke(null, new object[] { new AppearancePreferences(Theme: themeId) });
                    Render(presented, 1180, 800, Path.Combine(output, "dashboard-theme-" + themeId + ".png"));
                }
                Theme(100);
            }
            typeof(MainWindow).GetField("_gamesHub", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, games);
            typeof(MainWindow).GetField("_workspacesPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, collections);
            Get<ContentControl>(main, "GamesPageHost").Content = games;
            Get<ContentControl>(main, "WorkspacesPageHost").Content = collections;
            foreach (var page in new[] { "Games", "Workspaces", "Launch", "Settings" })
            {
                Invoke(main, "ShowWorkspacePage", Enum.Parse(typeof(MainWindow).GetNestedType("WorkspacePage", BindingFlags.NonPublic)!, page));
                Layout(presented, 1180, 800); if (page == "Games")
                {
                    PumpUntil(() => !games.IsBusy);
                    var cards = Find<ListBox>(games, "Games"); var previousItems = cards.ItemsSource;
                    cards.ItemsSource = Enumerable.Range(1, 2000).Select(n => new GameSummary(n, n + 100, "Virtualized game " + n, "Fixture")).ToArray(); Layout(presented, 1180, 800);
                    var realized = Descendants<ListBoxItem>(cards).Count();
                    Check(realized > 0 && realized < 100, "2000 game cards materialize only the viewport and overscan");
                    cards.ScrollIntoView(cards.Items[1999]); Layout(presented, 1180, 800);
                    Check(cards.ItemContainerGenerator.ContainerFromIndex(1999) is ListBoxItem, "Virtualized game grid scrolls to the last result");
                    cards.ItemsSource = previousItems; Layout(presented, 1180, 800);
                }
                Render(presented, 1180, 800, Path.Combine(output, page.ToLowerInvariant() + ".png"));
            }
            RunFocusPresentationChecks(main, games, automation, host, presented, output);
            RunExpansionPresentationChecks(main,host,presented,output);
            RunNativeSocialPresentationChecks(main, host, presented, output);
            RunPolishPresentationChecks(main, host, presented, output);
            RunComfortChecks(output,host);
            var tabs = Get<TabControl>(main, "SettingsCategories");
            for (var n = 0; n < 5; n++) { tabs.SelectedIndex = n; Layout(presented, 860, 640); Check(tabs.SelectedContent is not null, "Every settings category exposes its own content"); }
            var previous = L.Language; L.Language = previous == "en" ? "pt-BR" : "en"; Layout(presented, 1180, 800);
            Check(Descendants<TextBlock>(presented).Any(t => t.Text == L.T("Configurações")), "Language changes immediately on the existing settings tree"); L.Language = previous;
        }
        finally { host.Content = null; host.Close(); main.Content = presented; Theme(100); }
        Render(collections, 1000, 620, Path.Combine(output, "collections-form.png"));
        Find<TabControl>(collections, "CollectionsTabs").SelectedIndex = 1; Render(collections, 1000, 620, Path.Combine(output, "groups-form.png"));
        Render(graphics, 680, 640, Path.Combine(output, "graphics.png"));
        Render(automation, 680, 800, Path.Combine(output, "automation.png"));
        Render(new FeatureGuidePanel(), 760, 800, Path.Combine(output, "guide.png"));
        games.Dispose();
    }
}
