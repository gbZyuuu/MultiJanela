using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task TestThemeMigrationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.ThemeUpgrade." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "appearance.json"); var store = new AppearancePreferencesStore(new AppPaths(root));
            const string legacy = "{\"Theme\":\"forest\",\"Accent\":\"#65DAC8\",\"TextScale\":125,\"Font\":\"Tahoma\",\"StreamerMode\":true}";
            await File.WriteAllTextAsync(file, legacy); var value = await store.LoadAsync();
            Assert(value.Theme == "midnight" && value.TextScale == 125 && value.Font == "Tahoma" && value.StreamerMode && await File.ReadAllTextAsync(file) == legacy, "legacy dark theme refresh preserves accessibility settings and original file");
            await store.SaveAsync(value with { Theme = "forest" });
            Assert((await store.LoadAsync()).Theme == "forest", "an explicitly saved forest choice remains after upgrade");
            await File.WriteAllTextAsync(file, legacy.Replace("forest", "contrast"));
            Assert((await store.LoadAsync()).Theme == "contrast", "high contrast is never replaced by the visual migration");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task TestDiscoveryStudioAsync()
    {
        var sessions = new List<string>(); var feeds = 0;
        using var handler = new ApiFixtureHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("get-sorts"))
            {
                feeds++; sessions.Add(uri.Query.Split("sessionId=")[1].Split('&')[0]);
                object Game(int n, bool sponsored = false) => new { universeId = n + 1, rootPlaceId = n + 100, name = "Fixture " + n, playerCount = n, isSponsored = sponsored };
                return JsonResponse(new { sorts = new[] {
                    new { contentType = "Games", sortId = "top-playing-now", games = Enumerable.Range(0, 60).Select(n => Game(n)).Append(Game(60, true)).ToArray() },
                    new { contentType = "Games", sortId = "top-trending", games = Enumerable.Range(30, 60).Select(n => Game(n)).ToArray() } } });
            }
            if (uri.AbsolutePath.EndsWith("icons")) return JsonResponse(new { data = Array.Empty<object>() });
            if (uri.AbsolutePath.EndsWith("omni-search"))
            {
                sessions.Add(uri.Query.Split("sessionId=")[1].Split('&')[0]);
                return JsonResponse(new { searchResults = Array.Empty<object>(), nextPageToken = " " });
            }
            throw new Exception("Unexpected discovery fixture endpoint");
        });
        using var http = new HttpClient(handler); var service = new RobloxGameService(http);
        var first = await service.DiscoverAsync(); var second = await service.DiscoverAsync(offset: first.NextOffset);
        Assert(first.Games.Length == 24 && first.HasMore && second.Games.Length == 24 && !first.Games.Select(g => g.UniverseId).Intersect(second.Games.Select(g => g.UniverseId)).Any(), "discovery pages without repeating cards");
        var tail = await service.DiscoverAsync(offset: 72);
        Assert(tail.Games.Length == 18 && !tail.HasMore && !tail.Games.Any(g => g.UniverseId == 61), "discovery deduplicates and skips sponsored games");
        var popular = await service.DiscoverAsync(GameDiscoveryCategory.MostPlayed);
        Assert(popular.Games[0].Playing == 59 && popular.Games.All(g => g.UniverseId <= 60) && feeds == 1, "most played uses the official playing sort and cached discovery");
        var result = await service.SearchAsync("a"); await service.SearchAsync("a", "second-page");
        Assert(result.NextPageToken is null && sessions.Distinct().Count() == 1, "search accepts single characters and retains the discovery session across pages");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.DiscoverAsync(token: cancelled.Token); throw new Exception("Cached cancellation ignored"); } catch (OperationCanceledException) { }
    }
    private static async Task TestLaunchGraphicsStudioAsync()
    {
        var one = Guid.NewGuid(); var two = Guid.NewGuid();
        var policy = GraphicsLaunchPolicy.Validate(new(true, 60, 4)).WithAccount(one, new(120, 2));
        policy = policy with { GlobalFps = 30, GlobalQuality = 1 };
        Assert(policy.ForAccount(one) == new RobloxGraphicsPreferences(120, 2) && policy.ForAccount(two) == new RobloxGraphicsPreferences(30, 1), "account override persists while untouched accounts inherit new globals");
        policy = policy.WithAccount(one, null);
        Assert(policy.ForAccount(one) == new RobloxGraphicsPreferences(30, 1), "use global removes the account override");
        var roundTrip = GraphicsLaunchPolicy.Validate(JsonSerializer.Deserialize<GraphicsLaunchPolicy>(JsonSerializer.Serialize(policy))!);
        Assert(roundTrip.Enabled && roundTrip.ForAccount(two) == policy.ForAccount(two), "graphics policy round trips without running processes");
        try { GraphicsLaunchPolicy.Validate(policy with { GlobalQuality = 11 }); throw new Exception("Invalid quality accepted"); } catch (InvalidDataException) { }
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.StudioGraphics." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.xml");
            const string original = "<roblox><Item class=\"UserGameSettings\"><Properties><int name=\"FramerateCap\">-1</int><token name=\"SavedQualityLevel\">0</token><string name=\"Untouched\">fixture</string></Properties></Item></roblox>";
            await File.WriteAllTextAsync(path, original); var service = new RobloxGraphicsSettingsService(path, () => true);
            var lease = await service.BeginLaunchAsync(new(60, 3));
            Assert(service.ReadCurrent() == new RobloxGraphicsPreferences(60, 3), "stages launch preferences without requiring running sessions to close");
            using var cancel = new CancellationTokenSource(); var pending = service.BeginLaunchAsync(new(120, 5), cancel.Token);
            Assert(!pending.IsCompleted, "parallel launch waits for prior staging lease"); cancel.Cancel();
            try { await pending; throw new Exception("Waiting launch did not cancel"); } catch (OperationCanceledException) { }
            await lease.DisposeAsync(); await lease.DisposeAsync();
            Assert(await File.ReadAllTextAsync(path) == original, "dispose restores exact previous bytes once");
            await using (await service.BeginLaunchAsync(new(120, 6))) await File.WriteAllTextAsync(path, original.Replace("fixture", "external-change"));
            Assert((await File.ReadAllTextAsync(path)).Contains("external-change"), "restore respects concurrent Roblox or user changes");
            var before = await File.ReadAllBytesAsync(path);
            try { await service.BeginLaunchAsync(new(12, 99)); throw new Exception("Invalid graphics applied"); } catch (ArgumentException) { }
            Assert(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "invalid graphics preserve native preferences");
        }
        finally { Directory.Delete(root, true); }
    }
}
