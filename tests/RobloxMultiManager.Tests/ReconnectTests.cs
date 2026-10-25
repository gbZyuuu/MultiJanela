using System.Reflection;
using RobloxMultiManager;
using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;

internal static partial class Program
{
    private static async Task TestReconnectQueueAsync()
    {
        var now = DateTime.UtcNow;
        var busy = true; var replaced = false; var calls = 0;
        var request = new ReconnectRequest(Guid.NewGuid(), "123:1", RobloxLinkParser.Parse("123"));
        var queue = new ReconnectCoordinator(_ => replaced, _ => !busy, (_, _) =>
        { calls++; return calls == 1 ? Task.FromException<bool>(new IOException("fixture")) : Task.FromResult(calls == 2); }, utcNow: () => now);
        Assert(queue.Enqueue(request, 3, false), "initial crash queued");
        await queue.TickAsync(); Assert(calls == 0 && queue.HasWork, "busy launch does not discard reconnect");
        busy = false; await queue.TickAsync(); Assert(calls == 1 && queue.HasWork, "failure stays queued");
        now = now.AddSeconds(5); await queue.TickAsync(); Assert(calls == 1, "cooldown respected");
        now = now.AddSeconds(7); await queue.TickAsync(); Assert(calls == 2 && !queue.HasWork, "deferred reconnect eventually runs");
        Assert(queue.Enqueue(request with { SourceProcessKey = "124:2" }, 3, false), "rapid second crash remains pending");
        now = now.AddSeconds(1); await queue.TickAsync(); Assert(calls == 2 && queue.HasWork, "rapid second crash is not lost during cooldown");
        now = now.AddSeconds(12); await queue.TickAsync(); Assert(calls == 3 && !queue.HasWork, "failed launches stop at retry budget");
        Assert(!queue.Enqueue(request, 3, false), "failed confirmation does not reset budget");
        Assert(queue.Enqueue(request, 3, true), "stable session renews budget");
        replaced = true; await queue.TickAsync(); Assert(calls == 3 && !queue.HasWork, "manual replacement cancels queued duplicate");
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new ReconnectCoordinator(_ => false, _ => true, (_, _) => gate.Task, utcNow: () => now);
        pending.Enqueue(request, 3, false); var running = pending.TickAsync();
        pending.CancelAll(); gate.SetResult(false); await running;
        Assert(!pending.HasWork, "disabling reconnect cancels retries of in-flight attempts");
    }
    private static Task TestAuthenticationFailureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.AuthTests", Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "logs"); Directory.CreateDirectory(folder);
        var start = DateTime.UtcNow.AddSeconds(-5);
        var log = Path.Combine(folder, "fixture_last.log");
        try
        {
            var reader = new RobloxAuthenticationFailureReader(root);
            File.WriteAllText(log, "status:403 url:https://auth.roblox.com/v1/authentication-ticket/\n"); File.SetCreationTimeUtc(log, start.AddMilliseconds(50));
            Assert(!reader.WasRejected(start), "CSRF ticket challenge is not a native redemption failure");
            File.AppendAllText(log, "status:200 url:https://auth.roblox.com/v1/authentication-ticket/redeem\n");
            Assert(!reader.WasRejected(start), "successful redemption is not rejected");
            File.AppendAllText(log, new string('x', 300000) + "\nstatus:403 Forbidden url:https://auth.roblox.com/v1/authentication-ticket/redeem\n");
            Assert(reader.WasRejected(start), "native rejection is read from the real file tail without requiring a crash");
            File.AppendAllText(log, "status:200 OK url:https://auth.roblox.com/v1/authentication-ticket/redeem\nstatus:403 url:https://example.invalid/\n");
            Assert(!reader.WasRejected(start), "a later successful redemption supersedes the earlier challenge");
            File.AppendAllText(log, "status:403 Forbidden url:https://auth.roblox.com/v1/authentication-ticket/redeem\n");
            var crash = Path.Combine(folder, "fixture_CrashHandler_last.log");
            File.WriteAllText(crash, "[FLog::CrashpadHandlerLog] fixture helper\n"); File.SetCreationTimeUtc(crash, start.AddMilliseconds(80));
            Assert(reader.WasRejected(start), "a separate CrashHandler log does not hide a uniquely attributable client rejection");
            File.Copy(log, Path.Combine(folder, "other_last.log")); File.SetCreationTimeUtc(Path.Combine(folder, "other_last.log"), start.AddMilliseconds(100));
            Assert(!reader.WasRejected(start), "concurrent ambiguous client logs are not attributed to the wrong launch");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static Task TestReconnectSessionAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.ReconnectTests", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs"); Directory.CreateDirectory(logs);
        var start = DateTime.UtcNow.AddSeconds(-5);
        const string job = "11111111-2222-4333-8444-555555555555";
        var log = Path.Combine(logs, "fixture_last.log");
        try
        {
            File.WriteAllText(log, "\"UserId\":42,\"UserName\":\"fixture\"\n" + new string('x', 700000)
                + $"\nJoining game '{job}' place 999 at localhost\nRBXCRASH: fixture exception\n");
            File.SetCreationTimeUtc(log, start.AddMilliseconds(50));
            var reader = new RobloxSessionLogReader(root);
            var evidence = reader.Read(start, 42);
            Assert(evidence is { FailureKind: "crash", PlaceId: 999, GameInstanceId: job }, "latest destination and crash come from actual file tail");
            Assert(reader.Read(start, 43) is null, "another account's log is never accepted");
            var target = ReconnectTargetPolicy.Resolve("https://www.roblox.com/games/123", evidence)!;
            Assert(target.PlaceId == 999 && target.GameInstanceId == job, "last observed place/server overrides original public place");
            Assert(RobloxLinkParser.Parse(target.LauncherUrl).GameInstanceId == job, "instance ID survives validation and canonicalization");
            var build = typeof(RobloxLaunchWindow).GetMethod("BuildProtocolLauncherUrl", BindingFlags.NonPublic | BindingFlags.Static)!;
            var protocol = (string)build.Invoke(null, [target, 123L])!;
            Assert(protocol.Contains("request=RequestGameJob") && protocol.Contains("gameId=" + job), "specific server reaches native launcher request");
            Assert(ReconnectTargetPolicy.WithoutServer(target) is { PlaceId: 999, GameInstanceId: null }, "later attempts can fall back to the same place");
            var privateUrl = "https://www.roblox.com/games/123?privateServerLinkCode=fixture_private_code";
            var privateTarget = ReconnectTargetPolicy.Resolve(privateUrl, evidence)!;
            Assert(privateTarget.Kind == RobloxLaunchTargetKind.PrivateServer && privateTarget.PrivateServerCode == "fixture_private_code" && privateTarget.PlaceId == 123,
                "private authorization and original destination retained");
            var share = "https://www.roblox.com/share?code=fixture_share&type=Server";
            Assert(ReconnectTargetPolicy.Resolve(share, evidence)!.LauncherUrl == share, "share invitation survives reconnect");
            Assert(!RobloxLinkParser.TryParse("https://www.roblox.com/games/start?placeId=999&gameInstanceId=invalid", out _, out _), "invalid server ID rejected");
            Assert(MainWindow.ShouldReconnect(true, true, false, 0, "crash", 0, 3), "explicit account-matched crash evidence can override zero exit code");
            var originalLog = File.ReadAllText(log);
            File.WriteAllText(log, "\"UserId\":43\n" + originalLog); File.SetCreationTimeUtc(log, start.AddMilliseconds(50));
            Assert(reader.Read(start, 42) is null && reader.Read(start, 43) is null, "mixed account identities never select a server");
            File.WriteAllText(log, originalLog); File.SetCreationTimeUtc(log, start.AddMilliseconds(50));
            var duplicate = Path.Combine(logs, "duplicate_last.log"); File.Copy(log, duplicate); File.SetCreationTimeUtc(duplicate, start.AddSeconds(1));
            Assert(reader.Read(start, 42) is null, "ambiguous logs never invent a server attribution");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
    private static async Task TestRetainedExitAsync()
    {
        using var child = StartCpuFixture("--cpu-fixture-worker", "23");
        Assert(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready", "fixture ready");
        var start = child.StartTime.ToUniversalTime();
        using var observer = ProcessExitObserver.Open(child.Id, start);
        Assert(observer.ReadExitCode() is null, "running process has no exit verdict");
        AssertThrows(() => ProcessExitObserver.Open(child.Id, start.AddTicks(1)), "PID creation time validated");
        await child.StandardInput.WriteLineAsync("exit"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert(observer.ReadExitCode() == 23, "native handle preserves exit status after process exit");
    }
}
