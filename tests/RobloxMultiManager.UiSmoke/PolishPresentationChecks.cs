using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;
internal static partial class Program
{
    private sealed class PolishGamesHttp : HttpMessageHandler
    {
        public bool Limited; public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Limited) { var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20)); return Task.FromResult(limited); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data = new[] { new { id = "12345678-1234-1234-1234-123456789012", maxPlayers = 12, playing = 9, ping = (double?)null } }, nextPageCursor = (string?)null }), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class PolishUpdatesHttp : HttpMessageHandler
    {
        public bool Failed; public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(Failed ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v9.0.0", assets = new[] { new { name = "MultiJanela-9.0.0-win-x64.zip", browser_download_url = "https://github.com/gbZyuuu/MultiJanela/releases/download/v9.0.0/MultiJanela-9.0.0-win-x64.zip", digest = "sha256:" + new string('a', 64), size = 4096 } } }), Encoding.UTF8, "application/json") });
        }
    }
    private static void RunPolishPresentationChecks(MainWindow main, Window host, FrameworkElement root, string output)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Await(Task task) { PumpUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        using var handler = new PolishGamesHttp(); using var http = new HttpClient(handler);
        var games = new GamesHub(new RobloxGameService(http), new GameLibraryStore(new AppPaths(Path.Combine(output, "polish-game-store"))), (_, _) => { }, () => 1);
        var list = (ListBox)games.FindName("Games"); list.ItemsSource = new[] { new GameSummary(1, 9, "Fixture game", "") }; list.SelectedIndex = 0;
        PumpUntil(() => !games.IsBusy);
        var servers = (ListBox)games.FindName("Servers");
        Check(servers.Items.Count == 1 && ((FrameworkElement)games.FindName("BrowseSurface")).IsEnabled && !((FrameworkElement)games.FindName("BrowseSurface")).IsHitTestVisible, "Game overlay blocks interaction without activating the white Windows disabled palette");
        handler.Limited = true; InvokeResult(games, "Servers_Click", games, new RoutedEventArgs()); PumpUntil(() => !games.IsBusy);
        Check(servers.Items.Count == 1 && ((TextBlock)games.FindName("ServerNotice")).Text.Length > 0, "429 preserves loaded server cards and explains the cooldown");
        var calls = handler.Requests; InvokeResult(games, "Servers_Click", games, new RoutedEventArgs()); PumpUntil(() => !games.IsBusy);
        Check(handler.Requests == calls, "A refresh during Retry-After performs no additional HTTP request");
        list.SelectedItem = null; list.SelectedIndex = 0; PumpUntil(() => !games.IsBusy);
        Check(servers.Items.Count == 1 && handler.Requests == calls, "Reopening a game during cooldown restores its loaded server snapshot");
        games.Dispose();
        string? chosen = null; string? played = null;
        using var friendHttp = new HttpClient(new SocialHttp());
        using var social = new SocialPanel(new AccountSocialService(friendHttp, 42), false, target => chosen = target, "Aurora", play: target => played = target);
        social.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !social.IsBusy);
        var friend = ((ListBox)social.FindName("Friends")).Items.OfType<OnlineFriend>().First(f => f.CanJoin);
        InvokeResult(social, "FriendPlay_Click", new Button { DataContext = friend }, new RoutedEventArgs());
        Check(played == friend.Destination && chosen is null, "Per-friend Play invokes the launch action rather than only filling a destination field");
        using var managed = new SocialPanel(new AccountSocialService(friendHttp, 42), false, _ => { }, "Aurora", allFriends: true);
        managed.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !managed.IsBusy);
        Render(managed, 850, 480, Path.Combine(output, "manage-friends-native.png"));
        Check(((TextBlock)managed.FindName("FriendCount")).Text == L.F("{0} amigos", ((ListBox)managed.FindName("Friends")).Items.Count), "Friend count reflects the displayed roster");
        Check(Descendants<Button>((ListBox)managed.FindName("Friends")).Count(b => Equals(b.Content, L.T("Remover amizade"))) >= 1 && ((Button)managed.FindName("RemoveFriend")).Visibility == Visibility.Collapsed, "Friend removal lives on the person row rather than the bottom toolbar");
        using var editorHandler = new SocialHttp(); using var editorHttp = new HttpClient(editorHandler);
        using var editor = new AvatarEditorPanel(new AccountSocialService(editorHttp, 42), "Aurora", main.Profiles[0].AvatarUrl);
        editor.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !editor.IsBusy);
        var inventory = (ListBox)editor.FindName("Inventory"); var equipped = (ListBox)editor.FindName("Equipped");
        Check(inventory.Items.Count == 2 && equipped.Items.Count == 2, "Native avatar editor loads inventory and equipped items through its scoped service");
        inventory.SelectedIndex = 0; ((Button)editor.FindName("Equip")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(equipped.Items.Count == 3 && ((Button)editor.FindName("Apply")).IsEnabled && editorHandler.Writes == 0, "Avatar edits remain a local draft until explicit application");
        var inventoryRows = (ObservableCollection<AvatarEditorItem>)inventory.ItemsSource; var wornRows = (ObservableCollection<AvatarEditorItem>)equipped.ItemsSource;
        for (var i = 0; i < inventoryRows.Count; i++) inventoryRows[i] = inventoryRows[i] with { ThumbnailUrl = main.Profiles[i].AvatarUrl };
        for (var i = 0; i < wornRows.Count; i++) wornRows[i] = wornRows[i] with { ThumbnailUrl = main.Profiles[i].AvatarUrl };
        Render(editor, 850, 580, Path.Combine(output, "avatar-editor-native.png"));
        Render(editor, 720, 480, Path.Combine(output, "avatar-editor-compact.png"));
        Check(inventory.ActualHeight >= 250, "Compact avatar inventory reserves enough room for multiple rows");
        InvokeResult(editor, "Worn_Click", editor, new RoutedEventArgs());
        Render(editor, 850, 580, Path.Combine(output, "avatar-equipped.png"));
        Check(equipped.ActualHeight >= 320 && Descendants<Image>(equipped).Any(), "Equipped items have their own full-height image grid");
        var removeRow = Descendants<Button>(equipped).First(b => b.DataContext is AvatarEditorItem);
        var beforeRemoval = equipped.Items.Count; removeRow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(equipped.Items.Count == beforeRemoval - 1 && editorHandler.Writes == 0, "A tile removes its own item from the draft without changing the account");
        InvokeResult(editor, "Body_Click", editor, new RoutedEventArgs());
        Check(((FrameworkElement)editor.FindName("BodyArea")).Visibility == Visibility.Visible && ((System.Windows.Controls.Primitives.UniformGrid)editor.FindName("ColorsForm")).Children.Count == 6, "Body colors open natively with a control for each body part");
        Render(editor, 850, 580, Path.Combine(output, "avatar-body-native.png"));
        var swatches = Descendants<Button>((FrameworkElement)editor.FindName("ColorsForm")).Select(b => b.ToolTip as string).ToArray();
        Check(swatches.Contains("#000000") && swatches.Contains("#FFFFFF"), "Body palette always includes black and white");
        var model = typeof(AvatarViewport).GetMethod("Build", flags | BindingFlags.Static)!.Invoke(null, new object[] { "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3", "", new Dictionary<string, byte[]>(), CancellationToken.None }) as System.Windows.Media.Media3D.Model3D;
        Check(model?.IsFrozen == true && !model.Bounds.IsEmpty, "OBJ preview geometry is bounded and frozen before UI presentation");
        var connections = 0;
        using var portal = new AccountPortal(new AppPaths(Path.Combine(output, "polish-portal")), main.Profiles, (_, _) => { }, (p, _) => { connections++; return Task.FromResult(new HttpClient(new SocialHttp(p.RobloxUserId!.Value))); });
        var account = (ComboBox)portal.FindName("Account"); account.SelectedItem = main.Profiles[0]; PumpUntil(() => !portal.IsBusy);
        InvokeResult(portal, "Friends_Click", portal, new RoutedEventArgs()); PumpUntil(() => !portal.IsBusy);
        Check(NavigationState.GetActive((Button)portal.FindName("FriendsTab")) && !NavigationState.GetActive((Button)portal.FindName("OnlineTab")), "Native friend management highlights its own tab");
        InvokeResult(portal, "Avatar_Click", portal, new RoutedEventArgs()); PumpUntil(() => !portal.IsBusy);
        Check(((ContentControl)portal.FindName("SocialHost")).Content is AvatarEditorPanel && ((Border)portal.FindName("BrowserHost")).Visibility == Visibility.Collapsed, "Avatar editor opens inside MultiJanela rather than the Roblox website");
        var updated = main.Profiles[0] with { AvatarUrl = main.Profiles[1].AvatarUrl };
        var nativeEditor = (AvatarEditorPanel)((ContentControl)portal.FindName("SocialHost")).Content;
        ((Button)nativeEditor.FindName("ExpandEditor")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((Border)portal.FindName("AccountToolbar")).Visibility == Visibility.Collapsed, "Expanded avatar editor reclaims the account toolbar space");
        ((Button)nativeEditor.FindName("ExpandEditor")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((Border)portal.FindName("AccountToolbar")).Visibility == Visibility.Visible, "Restoring the editor keeps account navigation available");
        Check(connections == 1, "Changing native account tabs reuses the verified account connection");
        var previous = ((ContentControl)portal.FindName("SocialHost")).Content;
        portal.UpdateProfilePortrait(updated, () => main.Profiles[0] = updated);
        Check(ReferenceEquals(account.SelectedItem, updated) && ReferenceEquals(previous, ((ContentControl)portal.FindName("SocialHost")).Content), "Updating a portrait preserves the selected account and the active native editor");
        var target = typeof(AccountPortal).GetMethod("IsGameDestinationUri", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Valid(string url) => (bool)target.Invoke(null, new object[] { url })!;
        Check(Valid("https://www.roblox.com/games/9/Fixture") && !Valid("https://www.roblox.com/communities") && !Valid("https://www.roblox.com/my/avatar") && !Valid("https://www.roblox.com/users/1/profile"), "Use this destination is restricted to actual game or server pages");
        using var updateHandler = new PolishUpdatesHttp(); using var updateHttp = new HttpClient(updateHandler);
        var updates = new UpdatesPanel(new ManagerUpdateService(updateHttp), Path.Combine(output, "polish-update-cache"));
        var field = typeof(MainWindow).GetField("_updatesPanel", flags)!; var original = field.GetValue(main); field.SetValue(main, updates);
        try
        {
            InvokeResult(main, "RefreshUpdateHeader"); Check(Get<Button>(main, "HeaderUpdateButton").Visibility == Visibility.Collapsed, "Download icon stays hidden without a verified newer release");
            Await(updates.CheckForUpdatesAsync()); InvokeResult(main, "RefreshUpdateHeader");
            Check(updates.HasUpdate && Get<System.Windows.Shapes.Ellipse>(main, "HeaderUpdateDot").Visibility == Visibility.Visible, "A verified newer release activates the persistent header update indicator");
            host.Width = 860; host.Height = 640; Layout(root, 860, 640);
            foreach (var page in new[] { "Accounts", "Launch", "Games", "Settings" })
            {
                Invoke(main, "ShowWorkspacePage", Enum.Parse(typeof(MainWindow).GetNestedType("WorkspacePage", flags)!, page)); Layout(root, 860, 640);
                Check(new Rect(root.RenderSize).Contains(new Rect(Get<Button>(main, "HeaderUpdateButton").TranslatePoint(new Point(), root), Get<Button>(main, "HeaderUpdateButton").RenderSize)) && Get<Button>(main, "HeaderUpdateCheckButton").IsVisible, "Update and check controls stay in the header on " + page);
            }
            var loaded = typeof(MainWindow).GetField("_loaded", flags)!;
            var active = typeof(MainWindow).GetField("_activeLaunches", flags)!;
            var wasLoaded = loaded.GetValue(main); var launches = active.GetValue(main);
            try {
                loaded.SetValue(main, true);
                var baseline = (bool)InvokeResult(main, "HandoffWorkPending", true)!;
                typeof(UpdatesPanel).GetProperty("IsBusy")!.SetValue(updates, true);
                Check((bool)InvokeResult(main, "HandoffWorkPending", false)! && (bool)InvokeResult(main, "HandoffWorkPending", true)! == baseline, "Update handoff excludes only its own download from the shared busy guard");
                active.SetValue(main, 1);
                Check((bool)InvokeResult(main, "HandoffWorkPending", true)!, "Applying an update still waits for active Roblox launches");
            } finally { loaded.SetValue(main, wasLoaded); active.SetValue(main, launches); typeof(UpdatesPanel).GetProperty("IsBusy")!.SetValue(updates, false); }
            updateHandler.Failed = true; typeof(UpdatesPanel).GetField("_lastCheck", flags)!.SetValue(updates, DateTime.UtcNow.AddMinutes(-1));
            Await(updates.CheckForUpdatesAsync()); Check(updates.HasUpdate, "A temporary update-check failure does not discard a previously verified available release");
        }
        finally { field.SetValue(main, original); InvokeResult(main, "RefreshUpdateHeader"); }
    }
}
