using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RobloxMultiManager.Core;

namespace RobloxMultiManager.Tests;

internal static partial class Program
{
    private static string? _watchdogEntry;
    private static async Task TestCpuPolicyAsync()
    {
        Assert(CpuLimitPolicy.Effective(30, null) == 30, "null inherits global");
        Assert(CpuLimitPolicy.Effective(60, null) == 60, "inherited accounts follow changed global");
        Assert(CpuLimitPolicy.Effective(60, 25) == 25, "override remains independent");
        Assert(CpuLimitPolicy.Effective(30, 100) == 100, "explicit unlimited differs from inheritance");
        Assert(CpuLimitPolicy.Effective(0, 0) == 100, "invalid legacy data fails open");
        Assert(!CpuLimitPolicy.IsValid(0) && !CpuLimitPolicy.IsValid(101), "invalid cap rejected");
        var profile = JsonSerializer.Deserialize<AccountProfile>("{\"Id\":\"11111111-1111-1111-1111-111111111111\",\"Name\":\"Fixture\",\"GameUrl\":\"https://www.roblox.com/home\"}")!;
        Assert(profile.CpuLimitPercent is null, "legacy accounts inherit");
        Assert(JsonSerializer.Deserialize<AppSettings>("{}")!.GlobalCpuLimitPercent == 100, "legacy settings stay unlimited");
        var root = Path.Combine(Path.GetTempPath(), "MultiJanela.CpuTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalStore(new AppPaths(root));
            await store.SaveProfilesAsync([profile with { CpuLimitPercent = 25 }, profile with { Id = Guid.NewGuid(), CpuLimitPercent = null }, profile with { Id = Guid.NewGuid(), CpuLimitPercent = 100 }]);
            await store.SaveSettingsAsync(new AppSettings(GlobalCpuLimitPercent: 40));
            var loaded = await store.LoadProfilesAsync();
            Assert(loaded.Select(p => p.CpuLimitPercent).SequenceEqual(new int?[] {25, null, 100}), "all modes survive restart");
            Assert((await store.LoadSettingsAsync()).GlobalCpuLimitPercent == 40, "global persisted");
            Assert(loaded[0].WithIdentity(new AccountIdentity(123, "test", "Test")).CpuLimitPercent == 25, "login refresh preserves cap");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class FakeCpuJob : ICpuProcessJob
    {
        public bool IsAlive { get; set; } = true;
        public int Percent = 100, Sets;
        public bool Reject, Ignore;
        public List<string> Events = [];
        public int ReadLimit() => Percent;
        public void SetLimit(int percent)
        {
            Sets++; Events.Add($"set:{percent}");
            if (Reject) throw new Win32Exception(5);
            if (!Ignore) Percent = percent;
        }
        public void Dispose() => Events.Add("dispose");
    }
    private sealed class FakeCpuPlatform(FakeCpuJob job) : ICpuJobPlatform
    {
        public int Opens;
        public ICpuProcessJob? Open(CpuLimitRequest request, bool create) { Opens++; return create ? job : null; }
    }
    private static Task TestCpuServiceAsync()
    {
        var job = new FakeCpuJob(); var platform = new FakeCpuPlatform(job);
        var request = new CpuLimitRequest(1234, DateTime.UtcNow, 25);
        using (var service = new CpuLimitService(platform))
        {
            Assert(service.Reconcile([request]).Single() is { Success: true, AppliedPercent: 25 }, "native confirmation required");
            service.Reconcile([request]); Assert(job.Sets == 1 && platform.Opens == 1, "unchanged policy performs no new writes");
            job.Reject = true;
            var result = service.Reconcile([request with { Percent = 40 }]).Single();
            Assert(!result.Success && result.WindowsError == 5 && result.AppliedPercent == 25, "denial preserves actual cap and visible error");
            service.Reconcile([request with { Percent = 40 }]); Assert(job.Sets == 2, "failed policy is not retried each tick");
            job.Reject = false;
            Assert(service.Reconcile([request with { Percent = 40 }], true).Single().Success, "manual retry recovers");
            job.Ignore = true;
            Assert(service.Reconcile([request with { Percent = 50 }]).Single().Code == "limit-not-confirmed", "API success alone is insufficient");
            job.Ignore = false;
            service.Reconcile([]);
            Assert(job.Events.TakeLast(2).SequenceEqual(new[] { "set:100", "dispose" }), "removal disables cap before closing handles");
        }
        var exitJob = new FakeCpuJob();
        using (var service = new CpuLimitService(new FakeCpuPlatform(exitJob))) service.Reconcile([request]);
        Assert(exitJob.Percent == 100 && exitJob.IsAlive, "manager exit restores unlimited without killing client");
        return Task.CompletedTask;
    }
    private static Process StartCpuFixture(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RobloxMultiManager.Tests.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    private static async Task TestNativeCpuAsync()
    {
        using var child = StartCpuFixture("--cpu-fixture-worker");
        try
        {
            Assert(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "ready", "own fixture ready");
            var request = new CpuLimitRequest(child.Id, child.StartTime.ToUniversalTime(), 20);
            using var platform = new WindowsCpuJobPlatform();
            ICpuProcessJob? observer = null;
            var ownedPlatform = new WindowsCpuJobPlatform(entryExecutable: _watchdogEntry);
            using (var service = new CpuLimitService(ownedPlatform))
            {
                var result = service.Reconcile([request]).Single();
                Assert(result.Success, $"OS rejected fixture quota: {result.Code}, Windows {result.WindowsError}");
                observer = platform.Open(request, false);
                Assert(observer?.ReadLimit() == 20, "kernel quota is 20%");
                Assert(service.Reconcile([request with { Percent = 45 }]).Single().AppliedPercent == 45, "live process cap changes");
                var stale = service.Reconcile([request with { StartTimeUtc = request.StartTimeUtc.AddTicks(1) }]).Single();
                Assert(stale.Code == "process-identity-mismatch", "recycled PID protected by creation time");
                // Previous identity was removed by reconciliation and must be unlimited.
                using (var observed = platform.Open(request, false)) Assert(observed is null || observed.ReadLimit() == 100, $"removed process quota restored (job absent={observed is null}, rate={observed?.ReadLimit()})");
                Assert(service.Reconcile([request with { Percent = 35 }]).Single().Success, "existing owned job reused");
                using var ownedGuard = Process.GetProcessById(ownedPlatform.GuardProcessId!.Value);
                Console.WriteLine($"       Idle quota helper: {ownedGuard.WorkingSet64 / 1024 / 1024} MiB resident.");
                ownedGuard.Kill(); // Only the helper just spawned by this fixture, never a user process.
                await ownedGuard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(service.Reconcile([request with { Percent = 35 }]).Single() is { Success: false, Code: "cpu-watchdog-unavailable" }, "helper loss is visible");
                Assert(observer!.ReadLimit() == 100 && observer.IsAlive, "helper loss resets quota without killing client");
            }
            using var retainedObserver = observer!;
            Assert(retainedObserver.ReadLimit() == 100 && retainedObserver.IsAlive, "dispose clears quota, process survives");
            using (var abandoned = StartCpuFixture("--cpu-fixture-orphan", child.Id.ToString(CultureInfo.InvariantCulture), request.StartTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), "30"))
            {
                Assert(await abandoned.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "applied", "abandoned manager fixture attached quota");
                Assert(retainedObserver.ReadLimit() == 30, "quota confirmed before manager loss");
                await abandoned.StandardInput.WriteLineAsync("exit-without-dispose");
                await abandoned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert(abandoned.ExitCode == 0, "fixture manager exits without cleanup");
            }
            var deadline = Stopwatch.StartNew();
            while (retainedObserver.ReadLimit() != 100 && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Assert(retainedObserver.ReadLimit() == 100 && retainedObserver.IsAlive, "independent watchdog removes quota after abrupt manager loss");
            using (var recovered = new CpuLimitService())
                Assert(recovered.Reconcile([request with { Percent = 100 }]).Single() is { Success: true, AppliedPercent: 100 }, "unlimited remains confirmed after recovery");
        }
        finally
        {
            if (!child.HasExited) { await child.StandardInput.WriteLineAsync("exit"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
    private static Task TestResourcesAsync()
    {
        Assert(GpuMemoryCounters.TryProcessId("pid_234_luid_0x0_phys_0", out var pid) && pid == 234, "GPU instance PID parsed");
        Assert(!GpuMemoryCounters.TryProcessId("_Total", out _) && !GpuMemoryCounters.TryProcessId("pid_-1_luid_0", out _), "invalid counters rejected");
        using var self = Process.GetCurrentProcess();
        var instance = new RobloxInstanceInfo(self.Id, self.StartTime.ToUniversalTime(), 0, "Fixture", RobloxInstanceState.Running, true);
        using var sampler = new ProcessResourceSampler();
        var sample = sampler.Sample([instance])!;
        Assert(sample.TryGetValue(instance.Key, out var usage) && usage.ResidentBytes > 0 && usage.PrivateBytes > 0, "real RAM readings for own process");
        Assert(sampler.Sample([instance]) is null, "two-second throttle");
        using var staleSampler = new ProcessResourceSampler();
        Assert(staleSampler.Sample([instance with { StartTimeUtc = instance.StartTimeUtc.AddSeconds(-1) }])!.Count == 0, "PID reuse cannot leak memory reading");
        using var gpu = new GpuMemoryCounters();
        var gpuData = gpu.Read();
        Assert(gpuData.Values.All(v => v.Dedicated is null or >= 0 && v.Shared is null or >= 0), "unavailable GPU remains optional");
        Console.WriteLine($"       GPU counters: {gpuData.Count} processes (no Roblox process modified).");
        return Task.CompletedTask;
    }
}
