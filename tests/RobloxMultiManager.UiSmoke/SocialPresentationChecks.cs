using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RobloxMultiManager;
using RobloxMultiManager.Core;
internal static partial class Program
{
    private sealed class SocialHttp(long userId = 42) : HttpMessageHandler
    {
        public int Writes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object payload;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v4/avatar") payload = new { avatarModel = new { assets = new[] { new { id = 10, name = "Shirt" }, new { id = 20, name = "Hair" } }, playerAvatarType = "R15", bodyColors = new { headColor = "FFFFFF", torsoColor = "FFFFFF", leftArmColor = "FFFFFF", rightArmColor = "FFFFFF", leftLegColor = "FFFFFF", rightLegColor = "FFFFFF" }, scales = new { height = 1d, width = 1d, head = 1d, depth = 1d, proportion = 0d, bodyType = 0d } } };
            else if (path == "/v1/presence/users") payload = new { userPresences = Array.Empty<object>() };
            else if (path == "/v1/avatar-rules") payload = new { scales = new Dictionary<string, object> { ["height"] = new { min = .9, max = 1.05, increment = .01 }, ["width"] = new { min = .7, max = 1d, increment = .01 } }, bodyColorsPalette = new[] { new { hexColor = "FFFFFF" }, new { hexColor = "#BB8844" } } };
            else if (path.EndsWith("/inventory") || path.Contains("/inventory/")) payload = new { data = new[] { new { assetId = 30, name = "Hat" }, new { assetId = 40, name = "Pants" } }, nextPageCursor = (string?)null };
            else payload = request.RequestUri.Host switch
            {
                "users.roblox.com" => new { id = userId },
                "thumbnails.roblox.com" => new { data = Array.Empty<object>() },
                "avatar.roblox.com" => new { data = new[] { new { id = 7, name = "Everyday", isEditable = true }, new { id = 8, name = "Adventure", isEditable = true } }, paginationToken = "" },
                _ => new { data = new object[] { new { id = 1, name = "Aurora", displayName = "Aurora", userPresence = new { placeId = 9, gameInstanceId = "12345678-1234-1234-1234-123456789012", lastLocation = "Adventure" } }, new { id = 2, name = "Orbit", displayName = "Orbit", userPresence = new { placeId = (long?)null, gameInstanceId = (string?)null, lastLocation = "" } } } }
            };
            if (request.Method != HttpMethod.Get) Writes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
    }
    private static void RunNativeSocialPresentationChecks(MainWindow main, Window host, FrameworkElement root, string output)
    {
        host.Width = 860; host.Height = 640; Layout(root, 860, 640);
        var sidebar = Get<Border>(main, "TopNavBar"); var workspace = Get<Grid>(main, "NexusWorkspace");
        var initial = workspace.TranslatePoint(new Point(), root);
        var mouse = new MouseEventArgs(Mouse.PrimaryDevice, 0);
        InvokeResult(main, "Navigation_MouseEnter", sidebar, mouse); Layout(root, 860, 640);
        Check(sidebar.ActualWidth >= 220 && workspace.TranslatePoint(new Point(), root) == initial, "Hover opens navigation labels without moving or narrowing the workspace");
        InvokeResult(main, "Navigation_MouseLeave", sidebar, mouse); Layout(root, 860, 640);
        Check(sidebar.ActualWidth == 64, "Leaving navigation immediately restores the compact rail");
        InvokeResult(main, "Navigation_MouseEnter", sidebar, mouse); InvokeResult(main, "CollapseNavigation"); Layout(root, 860, 640);
        Check(sidebar.ActualWidth == 64, "A window deactivation or outside pointer can also dismiss navigation");
        Invoke(main, "ShowWorkspacePage", Enum.Parse(typeof(MainWindow).GetNestedType("WorkspacePage", System.Reflection.BindingFlags.NonPublic)!, "Accounts"));
        Layout(root, 860, 640);
        var sessions = Get<ListBox>(main, "UnifiedAccountsList");
        Check(Descendants<Image>(sessions).Any(i => i.ActualWidth >= 30 && i.ActualHeight >= 30), "Open session rows display an account portrait");
        using var handler = new SocialHttp(); using var http = new HttpClient(handler);
        string? destination = null;
        using var friends = new SocialPanel(new AccountSocialService(http, 42), false, value => destination = value, "Aurora");
        friends.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !friends.IsBusy);
        var list = (ListBox)friends.FindName("Friends");
        Check(list.Items.Count == 2, "The native friends panel renders visible and private presence together");
        list.SelectedIndex = 1; Check(!((Button)friends.FindName("UseServer")).IsEnabled, "Private presence cannot enable a join action");
        list.SelectedIndex = 0; ((Button)friends.FindName("UseServer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(destination?.Contains("gameInstanceId=") == true, "Native friend selection feeds the existing launch destination");
        list.ItemsSource = list.Items.OfType<OnlineFriend>().Select(f => f with { ThumbnailUrl = main.Profiles[(int)f.UserId].AvatarUrl }).ToArray();
        Render(friends, 700, 480, Path.Combine(output, "native-friends.png"));
        using var outfits = new SocialPanel(new AccountSocialService(http, 42), true, _ => { }, "Aurora");
        outfits.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !outfits.IsBusy);
        var saved = (ListBox)outfits.FindName("Outfits");
        Check(saved.Items.Count == 2 && !((Button)outfits.FindName("ApplyOutfit")).IsEnabled, "Native saved outfits load without applying an unselected appearance");
        saved.ItemsSource = saved.Items.OfType<OwnedOutfit>().Select((o, i) => o with { ThumbnailUrl = main.Profiles[i].AvatarUrl }).ToArray();
        saved.SelectedIndex = 0; Layout(outfits, 700, 480);
        Check(((Image)outfits.FindName("OutfitPreview")).Source is not null && ((Button)outfits.FindName("ApplyOutfit")).IsEnabled && handler.Writes == 0, "Selecting a saved outfit shows its thumbnail preview without a silent account mutation");
        Render(outfits, 700, 480, Path.Combine(output, "native-outfits.png"));
        Render(outfits, 430, 360, Path.Combine(output, "native-outfits-compact.png"));
        Check(Descendants<ScrollViewer>((FrameworkElement)outfits.FindName("OutfitArea")).Any(v => v.VerticalScrollBarVisibility == ScrollBarVisibility.Auto), "Outfit preview content remains scrollable in compact panels");
        using var portal = new AccountPortal(new AppPaths(Path.Combine(output, "native-portal-fixture")), main.Profiles, (_, _) => { },
            (profile, _) => Task.FromResult(new HttpClient(new SocialHttp(profile.RobloxUserId!.Value))));
        var account = (ComboBox)portal.FindName("Account"); account.SelectedItem = main.Profiles[0];
        PumpUntil(() => !portal.IsBusy);
        var embedded = (SocialPanel)((ContentControl)portal.FindName("SocialHost")).Content;
        embedded.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !embedded.IsBusy);
        Render(portal, 980, 650, Path.Combine(output, "accounts-native.png"));
        Check(((Border)portal.FindName("BrowserHost")).Visibility == Visibility.Collapsed && ((Microsoft.Web.WebView2.Wpf.WebView2)typeof(AccountPortal).GetField("Browser", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(portal)!).CoreWebView2 is null, "Opening native accounts does not initialize or navigate the official web page");
        Check(((ListBox)embedded.FindName("Friends")).Items.Count == 2, "Native accounts use an identity-checked service for the selected profile");
        account.SelectedItem = main.Profiles[1]; PumpUntil(() => !portal.IsBusy);
        var switched = (SocialPanel)((ContentControl)portal.FindName("SocialHost")).Content;
        switched.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent)); PumpUntil(() => !switched.IsBusy);
        Check(!ReferenceEquals(embedded, switched) && ((TextBlock)switched.FindName("AccountCaption")).Text == main.Profiles[1].PublicName, "Changing account replaces its native view and scoped client");

    }
}
