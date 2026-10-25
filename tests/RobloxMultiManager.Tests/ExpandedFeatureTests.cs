using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task TestRestartWaitAsync()
    {
        using var worker = StartCpuFixture("--cpu-fixture-worker");
        try
        {
            Assert(await worker.StandardOutput.ReadLineAsync() == "ready", "restart fixture starts");
            var path = Path.Combine(AppContext.BaseDirectory, "RobloxMultiManager.Tests.exe"); var retained = 0;
            string[] Args(long start) => ["--restart-after", worker.Id.ToString(), start.ToString()];
            var started = worker.StartTime.ToUniversalTime().Ticks;
            await ManagerRestartWait.WaitAsync(Args(started + 1), path, () => retained++, CancellationToken.None);
            Assert(retained == 0, "restart ignores a reused PID without retaining a guard");
            var wait = ManagerRestartWait.WaitAsync(Args(started), path, () => retained++, CancellationToken.None);
            Assert(retained == 1 && !wait.IsCompleted, "replacement waits for exact previous executable and retains continuity");
            await worker.StandardInput.WriteLineAsync("exit"); await wait.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(worker.HasExited, "restart proceeds only after prior owner has exited");
        }
        finally { if (!worker.HasExited) { await worker.StandardInput.WriteLineAsync("exit"); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
    }
    private static async Task TestAutomationHistoryAsync()
    {
        var preset = Guid.NewGuid(); var id = Guid.NewGuid(); var now = DateTime.UtcNow;
        var schedule = new ScheduledManagerAction(id, "Fixture", "rejoin", preset, now, 5, true, true);
        var data = AutomationPreferences.Validate(new([schedule], []));
        var claimed = AutomationPreferences.Claim(data, id, now);
        Assert(AutomationPreferences.NextDue(claimed, now) is null && claimed.Schedules[0].NextRunUtc == now.AddMinutes(5), "schedule claims before dispatch without replay burst");
        try { AutomationPreferences.Validate(new([schedule with { AllowRejoin = false }], [])); throw new Exception("Unapproved rejoin accepted."); } catch (InvalidDataException) { }
        var recipe = new AutomationRecipe(1, Guid.NewGuid(), "Fixture", false, [new("arrange"), new("delay", Seconds: 1), new("trim")]);
        using var stop = new CancellationTokenSource(); var calls = 0;
        try { await RecipeRunner.RunAsync(recipe, (_, _) => { calls++; stop.Cancel(); return Task.CompletedTask; }, stop.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Assert(calls == 1, "recipe cancellation prevents later actions");
        try { RecipeRunner.Validate(recipe with { Steps = [new("shell")] }); throw new Exception("Arbitrary code accepted."); } catch (InvalidDataException) { }
        var profile = AccountProfile.Create("Fixture Pilot") with { RobloxUsername = "fixture_user", RobloxUserId = 42 };
        var metadata = new AccountMetadata(profile.Id, "Main account", ["daily"], new() { ["role"] = "navigator" });
        var groups = new[] { new AccountGroup(Guid.NewGuid(), "Blue team", [profile.Id]) };
        Assert(AccountSearch.Matches(profile, metadata, groups, "tag:daily group:\"Blue team\" field:navigator user:fixture")
            && !AccountSearch.Matches(profile, metadata, groups, "tag:missing") && AccountSearch.Matches(profile, metadata, groups, "\"Fixture Pilot\" id:42"), "advanced search combines quoted fields without false positives");
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.HistoryFixture." + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SessionHistoryStore(new AppPaths(root));
            var entries = Enumerable.Range(0, 510).Select(n => new SessionHistoryEntry(n.ToString(), now.AddSeconds(n), "opened", "fixture", n, preset, "=PRIVATE_FIXTURE()", "1.0", "observed")).ToArray();
            await store.AppendAsync(entries); await store.AppendAsync(entries);
            var loaded = await store.LoadAsync(); Assert(loaded.Length == 500 && loaded.Select(e => e.Id).Distinct().Count() == 500, "history is bounded and deduplicated");
            Assert(!SessionHistoryStore.ExportCsv(loaded).Contains("PRIVATE_FIXTURE"), "CSV hides identities by default");
            Assert(SessionHistoryStore.ExportCsv(loaded, true).Contains("'=PRIVATE_FIXTURE"), "CSV escapes spreadsheet formulas");
            var graph = new ResourceHistory();
            for (var n = 0; n < 200; n++) graph.Add(new Dictionary<string, ProcessResourceUsage> { ["fixture"] = new("fixture", n, n, null, null, now.AddSeconds(n), n) });
            Assert(graph.Get("fixture").Length == 180 && graph.Get("fixture")[0].ResidentBytes == 20, "resource chart retains a bounded sample window");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task TestGraphicsAndTuningAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.GraphicsFixture." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.xml");
            const string xml = "<roblox><Item class=\"UserGameSettings\"><Properties><int name=\"FramerateCap\">-1</int><token name=\"SavedQualityLevel\">0</token><string name=\"Untouched\">fixture</string></Properties></Item></roblox>";
            await File.WriteAllTextAsync(path, xml); var running = false;
            var service = new RobloxGraphicsSettingsService(path, () => running);
            await service.ApplyAsync(new(60, 3));
            Assert(service.ReadCurrent() == new RobloxGraphicsPreferences(60, 3) && XDocument.Load(path).Descendants("string").Single().Value == "fixture", "native graphics update preserves unrelated preferences");
            Assert(Directory.GetFiles(Path.Combine(root, "MultiJanelaGraphicsBackups")).Length == 1, "native graphics keeps a reversible backup");
            var before = await File.ReadAllBytesAsync(path); running = true;
            try { await service.ApplyAsync(new(120, 1)); throw new Exception("Running-client guard ignored."); } catch (InvalidOperationException) { }
            Assert(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "graphics never writes while a game is open");
            var reads = 0; running = false; var raced = new RobloxGraphicsSettingsService(path, () => ++reads >= 2);
            try { await raced.ApplyAsync(new(120, 1)); throw new Exception("Launch race ignored."); } catch (InvalidOperationException) { }
            Assert(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "graphics catches a client starting during apply");
            await File.WriteAllTextAsync(path, "<!DOCTYPE roblox [<!ENTITY xxe SYSTEM 'file:///fixture'>]><roblox>&xxe;</roblox>");
            try { service.ReadCurrent(); throw new Exception("DTD accepted."); } catch (System.Xml.XmlException) { }
        }
        finally { Directory.Delete(root, true); }
        using var child = StartCpuFixture("--cpu-fixture-worker");
        try
        {
            Assert(await child.StandardOutput.ReadLineAsync() == "ready", "isolated tuning worker starts");
            var priority = child.PriorityClass;
            var session = new RobloxInstanceInfo(child.Id, child.StartTime.ToUniversalTime(), 0, "Fixture", RobloxInstanceState.Running, true);
            using var tuning = new ProcessTuningService();
            Assert(tuning.Reconcile([session], new(true, true, false, false)).Single().Success, "priority applies to owned test worker");
            child.Refresh(); Assert(child.PriorityClass == ProcessPriorityClass.BelowNormal, "native priority changed");
            tuning.Reconcile([session], new()); child.Refresh(); Assert(child.PriorityClass == priority, "disabling restores prior worker priority");
            Assert(!tuning.TrimMemory(session with { StartTimeUtc = session.StartTimeUtc.AddSeconds(-1) }).Success, "working-set trim rejects reused PID");
        }
        finally { if (!child.HasExited) { await child.StandardInput.WriteLineAsync("exit"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
    }
    private static async Task TestSocialIdentityAsync()
    {
        var sid = Guid.NewGuid().ToString("D");
        using var handler = new ApiFixtureHandler(request => request.RequestUri!.Host == "users.roblox.com" ? JsonResponse(new { id = 42 }) : JsonResponse(new { data = new object[] {
            new { id = 1, name = "Fixture", displayName = "Fixture", userPresence = new { placeId = 34, gameInstanceId = sid, lastLocation = "Fixture game" } },
            new { id = 2, name = "Private", displayName = "Private", userPresence = new { placeId = (long?)null, gameInstanceId = (string?)null, lastLocation = "" } } } }));
        using var http = new HttpClient(handler); var service = new AccountSocialService(http, 42);
        var friends = await service.GetOnlineFriendsAsync();
        Assert(friends[0].CanJoin && !friends[1].CanJoin && friends[0].Destination!.Contains(sid), "friends honor unavailable private server data");
        using var mismatch = new ApiFixtureHandler(_ => JsonResponse(new { id = 9 })); using var mismatchHttp = new HttpClient(mismatch);
        try { await new AccountSocialService(mismatchHttp, 42).GetOnlineFriendsAsync(); throw new Exception("Wrong account identity accepted."); } catch (InvalidOperationException) { }
        Assert(mismatch.Requests == 1, "identity mismatch prevents friends request");
        var secret = "fixture-key-" + Guid.NewGuid(); var encrypted = LocalSecretProtection.Protect(secret);
        Assert(!encrypted.Contains(secret) && LocalSecretProtection.Unprotect(encrypted) == secret, "DPAPI protects local integration keys");
    }
    private static async Task TestLocalApiAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var actions = 0;
        await using var api = new LocalControlApi();
        await api.StartAsync(new(true, port, true, false), key, () => Task.FromResult(new ApiSnapshot("fixture", false, [], [], [])), _ => { actions++; return Task.FromResult(true); });
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(5) };
        Assert((await http.GetAsync("/v1/status")).StatusCode == HttpStatusCode.Unauthorized, "local API rejects missing key");
        http.DefaultRequestHeaders.Authorization = new("Bearer", key);
        Assert((await http.GetAsync("/v1/status")).IsSuccessStatusCode, "local API accepts authenticated read");
        using (var browser = new HttpRequestMessage(HttpMethod.Get, "/v1/status")) { browser.Headers.Add("Origin", "https://fixture.invalid"); Assert((await http.SendAsync(browser)).StatusCode == HttpStatusCode.Forbidden, "browser origin is rejected"); }
        Assert((await http.PostAsJsonAsync("/v1/actions", new ApiAction("rejoin", Guid.NewGuid()))).StatusCode == HttpStatusCode.Forbidden && actions == 0, "API cannot close sessions without rejoin permission");
        Assert((await http.PostAsJsonAsync("/v1/actions", new ApiAction("shell", null))).StatusCode == HttpStatusCode.BadRequest, "API rejects unknown commands");
        Assert((await http.PostAsJsonAsync("/v1/actions", new ApiAction("launch", null))).StatusCode == HttpStatusCode.BadRequest, "launch requires a preset");
        Assert((await http.PostAsJsonAsync("/v1/actions", new ApiAction("arrange", null))).StatusCode == HttpStatusCode.Accepted && actions == 1, "authorized action dispatches exactly once");
    }
    private static async Task TestVerifiedInstallAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.InstallFixture." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.Copy(typeof(Program).Assembly.Location, Path.Combine(source, "RobloxMultiManager.exe"));
            foreach (var name in ManagerUpdateService.PayloadFiles.Where(n => n is not "RobloxMultiManager.exe" and not "SHA256SUMS.txt")) await File.WriteAllTextAsync(Path.Combine(source, name), Convert.ToBase64String(RandomNumberGenerator.GetBytes(2000)));
            async Task Manifest(string directory)
            {
                var lines = new List<string>(); foreach (var name in ManagerUpdateService.PayloadFiles.Where(n => n != "SHA256SUMS.txt"))
                { await using var input = File.OpenRead(Path.Combine(directory, name)); lines.Add(Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant() + "  " + name); }
                await File.WriteAllLinesAsync(Path.Combine(directory, "SHA256SUMS.txt"), lines);
            }
            await Manifest(source); var version = Version.Parse(FileVersionInfo.GetVersionInfo(Path.Combine(source, "RobloxMultiManager.exe")).FileVersion!).ToString(3);
            var zip = Path.Combine(root, "release.zip"); ZipFile.CreateFromDirectory(source, zip); var bytes = await File.ReadAllBytesAsync(zip);
            using var handler = new ApiFixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); using var http = new HttpClient(handler);
            var updater = new ManagerUpdateService(http); var release = new ManagerRelease(version, $"https://github.com/gbZyuuu/MultiJanela/releases/download/v{version}/MultiJanela-{version}-win-x64.zip", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
            var cache = Path.Combine(root, "cache"); var exe = await updater.DownloadAsync(release, cache); await updater.DownloadAsync(release, cache);
            Assert(handler.Requests == 1, "verified update cache avoids duplicate downloads");
            var payload = Path.GetDirectoryName(exe)!; await File.AppendAllTextAsync(Path.Combine(payload, "README.md"), "tampered"); await Manifest(payload);
            try { await updater.DownloadAsync(release, cache); throw new Exception("Forged cached manifest accepted."); } catch (InvalidDataException) { }
            try { await updater.DownloadAsync(release with { Url = "https://fixture.invalid/release.zip" }, cache); throw new Exception("External update source accepted."); } catch (ArgumentException) { }
            var integrations = 0; var installation = new UserInstallationService(Path.Combine(root, "installed"), (_, _, _) => integrations++, () => integrations--);
            var launcher = await installation.InstallAsync(source, false);
            Assert(File.Exists(launcher) && File.Exists(installation.ResolveCurrent()) && integrations == 1, "per-user install selects verified payload using injectable integrations");
            await File.WriteAllTextAsync(Path.Combine(installation.Root, "keep-user-file.txt"), "fixture");
            Assert(!await installation.UninstallAsync(Environment.ProcessId) && File.Exists(Path.Combine(installation.Root, "keep-user-file.txt")) && integrations == 0, "uninstall preserves additional files");
            var bad = Path.Combine(root, "unmanaged"); Directory.CreateDirectory(bad); await File.WriteAllTextAsync(Path.Combine(bad, "installation.json"), "{}");
            try { await new UserInstallationService(bad, (_, _, _) => { }, () => { }).UninstallAsync(Environment.ProcessId); throw new Exception("Invalid marker accepted."); } catch (InvalidDataException) { }
        }
        finally { Directory.Delete(root, true); }
    }
}
