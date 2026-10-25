using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Reflection;
using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;

internal static partial class Program
{
    private sealed class ApiFixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Requests++; return Task.FromResult(respond(request)); }
    }
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static async Task TestGameServicesAsync()
    {
        var serverId = Guid.NewGuid().ToString("D");
        using var handler = new ApiFixtureHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("omni-search")) return JsonResponse(new { searchResults = new[] { new { contentGroupType = "Game", contents = new[] {
                new { universeId = 12, rootPlaceId = 34, name = "Fixture game", description = "Fixture", playerCount = 8 },
                new { universeId = 12, rootPlaceId = 34, name = "Duplicate", description = "Fixture", playerCount = 8 } } } } });
            if (uri.AbsolutePath.EndsWith("icons")) return JsonResponse(new { data = new[] { new { targetId = 12, imageUrl = "https://tr.rbxcdn.com/fixture.png" } } });
            if (uri.AbsolutePath.EndsWith("universe")) return JsonResponse(new { universeId = 12 });
            if (uri.AbsolutePath.EndsWith("servers/Public")) return JsonResponse(new { data = new[] { new { id = serverId, playing = 7, maxPlayers = 10, ping = 25 } }, nextPageCursor = "next/cursor+" });
            if (uri.AbsolutePath == "/v1/games") return JsonResponse(new { data = new[] { new { id = 12, name = "Fixture game", description = "Fixture", playing = 8, maxPlayers = 10 } } });
            throw new Exception("Unexpected fixture request.");
        });
        using var http = new HttpClient(handler);
        var service = new RobloxGameService(http);
        var first = await service.SearchAsync("fixture");
        if (first.Games.Length != 1 || first.Games[0].PlaceId != 34 || first.Games[0].IconUrl != "https://tr.rbxcdn.com/fixture.png")
            throw new Exception("Search parsing, deduplication or image validation failed.");
        await service.SearchAsync("fixture");
        if (handler.Requests != 2) throw new Exception("Repeated searches bypassed the bounded cache.");
        var game = await service.GetPlaceAsync(34);
        if (game.UniverseId != 12 || game.MaxPlayers != 10) throw new Exception("Place resolution failed.");
        var servers = await service.GetServersAsync(34);
        if (servers.Cursor != "next/cursor+" || servers.Servers.Single().Available != 3) throw new Exception("Server pagination or capacity failed.");
        var choices = new[] { new PublicGameServer("full", 9, 10, null), new PublicGameServer("fit", 7, 10, null), new PublicGameServer("empty", 1, 10, null) };
        if (RobloxGameService.ChooseServer(choices, 3, ServerPreference.BestFit)?.Id != "fit"
            || RobloxGameService.ChooseServer(choices, 3, ServerPreference.Emptiest)?.Id != "empty"
            || RobloxGameService.ChooseServer(choices, 30, ServerPreference.Random) is not null)
            throw new Exception("Server selection ignored group capacity.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await service.SearchAsync("cancel fixture", token: cancelled.Token); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        using var limited = new ApiFixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(27)) } });
        using var limitedHttp = new HttpClient(limited);
        try { await new RobloxGameService(limitedHttp).SearchAsync("fixture"); throw new Exception("Rate limit was ignored."); }
        catch (RobloxApiException ex) when (ex.StatusCode == 429 && ex.RetryAfter == TimeSpan.FromSeconds(27)) { }
        if (limited.Requests != 1) throw new Exception("A limited request was retried automatically.");
    }

    private static async Task TestFeatureLibrariesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.FeatureFixture." + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root); paths.EnsureCreated();
            var games = new GameLibraryStore(paths);
            var game = new GameSummary(12, 34, "Fixture game", "Fixture");
            if (!SavedLaunchDestination.TryCreate("Fixture server", "https://www.roblox.com/games/34?privateServerLinkCode=fixture", null, out var link, out _)) throw new Exception();
            await games.SaveAsync([new(game, true, DateTime.UtcNow, [link])]);
            var restored = (await games.LoadAsync()).Single();
            if (!restored.Favorite || restored.PrivateLinks.Single().Name != "Fixture server") throw new Exception("Game favorites or named links lost.");
            var id = Guid.NewGuid();
            var store = new WorkspaceLibraryStore(paths);
            await store.UpdateAsync(d => d with { Groups = [new(Guid.NewGuid(), "Fixture group", [id])] });
            await store.UpdateAsync(d => d with { Metadata = [new(id, "Fixture note", ["fixture-tag"])] });
            await store.UpdateAsync(d => d with { Presets = [new(Guid.NewGuid(), "Fixture preset", [id], "https://www.roblox.com/home", 7, 45, -1, true)] });
            var library = await store.LoadAsync();
            if (library.Groups.Length != 1 || library.Metadata.Single().Tags.Single() != "fixture-tag" || library.Presets.Single().GlobalCpuPercent != 45)
                throw new Exception("Independent workspace updates overwrote each other.");
            var before = await File.ReadAllBytesAsync(Path.Combine(root, "workspaces.json"));
            try { await store.UpdateAsync(d => d with { Presets = [d.Presets[0] with { GlobalCpuPercent = 0 }] }); throw new Exception("Invalid preset accepted."); }
            catch (InvalidDataException) { }
            if (!before.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(root, "workspaces.json")))) throw new Exception("A failed save changed valid data.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task TestEncryptedBackupsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.BackupFixture." + Guid.NewGuid().ToString("N"));
        const string password = "Fixture-only-password-123";
        Directory.CreateDirectory(root);
        try
        {
            var source = new AppPaths(Path.Combine(root, "source")); source.EnsureCreated();
            await File.WriteAllTextAsync(source.ProfilesFile, "[{\"Name\":\"Fixture-only-account\"}]");
            await File.WriteAllTextAsync(source.SettingsFile, "{\"TargetInstances\":4}");
            var file = Path.Combine(root, "backup.mjb");
            var service = new EncryptedBackupService(source);
            var exported = await service.ExportAsync(file, password, false);
            var cipher = await File.ReadAllBytesAsync(file);
            if (exported.Files != 2 || Encoding.UTF8.GetString(cipher).Contains("Fixture-only-account")) throw new Exception("Backup inventory or encryption failed.");
            var inventory = await service.InspectAsync(file, password);
            if (inventory.Files != 2 || inventory.IncludesBrowserProfiles) throw new Exception("Backup preview failed.");
            try { await service.InspectAsync(file, "Wrong-fixture-password"); throw new Exception("Wrong password accepted."); }
            catch (CryptographicException) { }
            var target = new AppPaths(Path.Combine(root, "target")); target.EnsureCreated();
            await File.WriteAllTextAsync(target.ProfilesFile, "[{\"Name\":\"Previous fixture\"}]");
            await File.WriteAllTextAsync(target.SettingsFile, "{\"TargetInstances\":2}");
            await new EncryptedBackupService(target).RestoreAsync(file, password);
            if (await File.ReadAllTextAsync(target.ProfilesFile) != await File.ReadAllTextAsync(source.ProfilesFile)
                || Directory.GetFiles(Path.Combine(target.Root, "Backups"), "*.mjb").Length != 1)
                throw new Exception("Restore or pre-restore safety copy failed.");
            cipher[^1] ^= 1; var corrupt = Path.Combine(root, "tampered.mjb"); await File.WriteAllBytesAsync(corrupt, cipher);
            try { await service.InspectAsync(corrupt, password); throw new Exception("Modified backup accepted."); }
            catch (CryptographicException) { }
            using var zipped = new MemoryStream();
            using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, true))
            { using var writer = new StreamWriter(zip.CreateEntry("../profiles.json").Open()); writer.Write("[]"); }
            var encrypted = (byte[])typeof(EncryptedBackupService).GetMethod("Encrypt", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [zipped.ToArray(), password])!;
            var unsafeFile = Path.Combine(root, "unsafe.mjb"); await File.WriteAllBytesAsync(unsafeFile, encrypted);
            var unchanged = await File.ReadAllBytesAsync(source.ProfilesFile);
            try { await service.RestoreAsync(unsafeFile, password); throw new Exception("Unsafe backup path accepted."); }
            catch (InvalidDataException) { }
            if (!unchanged.SequenceEqual(await File.ReadAllBytesAsync(source.ProfilesFile))) throw new Exception("Unsafe restore altered current data.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task TestAppearancePreferencesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.AppearanceFixture." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AppearancePreferencesStore(new AppPaths(root));
            var preferences = new AppearancePreferences("contrast", "#FFCC00", 150, "Tahoma", true);
            await store.SaveAsync(preferences);
            if (await store.LoadAsync() != preferences) throw new Exception("Appearance settings did not persist.");
            if (AppearancePreferencesStore.Import(AppearancePreferencesStore.Export(preferences)).StreamerMode) throw new Exception("Theme export included recording privacy state.");
            try { AppearancePreferences.Validate(preferences with { Accent = "not-a-color" }); throw new Exception("Invalid theme accepted."); }
            catch (InvalidDataException) { }
            DisplayPrivacy.Enabled = true;
            var profile = AccountProfile.Create("Fixture account");
            if (profile.PublicName.Contains("Fixture account") || profile.PublicAvatarUrl is not null || profile.AccountDisplay.Contains("Fixture account"))
                throw new Exception("Recording mode exposed an account identity.");
        }
        finally { DisplayPrivacy.Enabled = false; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
