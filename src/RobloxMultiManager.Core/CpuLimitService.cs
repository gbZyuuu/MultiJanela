using System.ComponentModel;

namespace RobloxMultiManager.Core;

public static class CpuLimitPolicy
{
    public const int Unlimited = 100;
    public static bool IsValid(int value) => value is >= 1 and <= 100;
    public static int Effective(int global, int? accountOverride) =>
        accountOverride is { } value && IsValid(value) ? value : IsValid(global) ? global : Unlimited;
}

public sealed record CpuLimitRequest(int ProcessId, DateTime StartTimeUtc, int Percent)
{
    public string Key => $"{ProcessId}:{StartTimeUtc.ToUniversalTime().Ticks}";
}

public sealed record CpuLimitResult(string Key, int RequestedPercent, int? AppliedPercent,
    bool Success, string Code, int? WindowsError = null);

internal interface ICpuProcessJob : IDisposable
{
    bool IsAlive { get; }
    int ReadLimit();
    void SetLimit(int percent);
}

internal interface ICpuJobPlatform
{
    // null means that an unlimited process has no existing manager-owned job.
    ICpuProcessJob? Open(CpuLimitRequest request, bool create);
}

internal sealed class CpuLimitException(string code, int? windowsError = null) : Exception(code)
{
    public string Code { get; } = code;
    public int? WindowsError { get; } = windowsError;
}

/// <summary>One named, non-terminating Windows job per PID + creation time.</summary>
public sealed class CpuLimitService : IDisposable
{
    private sealed record Entry(ICpuProcessJob Job, int Percent);
    private readonly object _gate = new();
    private readonly ICpuJobPlatform _platform;
    private readonly Dictionary<string, Entry> _jobs = [];
    private readonly Dictionary<string, CpuLimitResult> _results = [];
    private bool _disposed;
    private bool _guardFailureHandled;

    public CpuLimitService() : this(new WindowsCpuJobPlatform()) { }
    internal CpuLimitService(ICpuJobPlatform platform) => _platform = platform;

    public IReadOnlyList<CpuLimitResult> Reconcile(IReadOnlyList<CpuLimitRequest> requests, bool retryFailures = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_platform is WindowsCpuJobPlatform { GuardFailed: true } && !_guardFailureHandled)
            {
                // A dead helper must not leave the UI claiming an actively protected quota.
                foreach (var key in _jobs.Keys.ToArray())
                {
                    var entry = _jobs[key];
                    try { entry.Job.SetLimit(100); _jobs[key] = entry with { Percent = entry.Job.ReadLimit() }; }
                    catch (Exception ex) when (ex is Win32Exception or CpuLimitException) { }
                }
                _results.Clear();
                _guardFailureHandled = true;
            }
            var keys = requests.Select(request => request.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _jobs.Keys.Where(key => !keys.Contains(key)).ToArray()) Release(key);
            foreach (var key in _results.Keys.Where(key => !keys.Contains(key)).ToArray()) _results.Remove(key);
            return requests.DistinctBy(request => request.Key).Select(request => Apply(request, retryFailures)).ToArray();
        }
    }

    private CpuLimitResult Apply(CpuLimitRequest request, bool retryFailures)
    {
        var key = request.Key;
        if (request.ProcessId <= 0 || request.StartTimeUtc == default || !CpuLimitPolicy.IsValid(request.Percent))
            return new(key, request.Percent, null, false, "invalid-request");
        if (_results.TryGetValue(key, out var previous) && previous.RequestedPercent == request.Percent
            && (!retryFailures || previous.Success)) return previous;
        if (request.Percent < 100 && _platform is WindowsCpuJobPlatform { GuardFailed: true })
            return Failure(request, "cpu-watchdog-unavailable", null);
        try
        {
            if (!_jobs.TryGetValue(key, out var entry))
            {
                var job = _platform.Open(request, create: request.Percent < CpuLimitPolicy.Unlimited);
                if (job is null)
                    return Remember(new(key, request.Percent, CpuLimitPolicy.Unlimited, true, "unlimited"));
                try { entry = new(job, job.ReadLimit()); }
                catch { job.Dispose(); throw; }
                _jobs[key] = entry;
            }
            if (!entry.Job.IsAlive) throw new CpuLimitException("process-exited");
            if (entry.Percent != request.Percent) entry.Job.SetLimit(request.Percent);
            var measured = entry.Job.ReadLimit();
            _jobs[key] = entry with { Percent = measured };
            if (measured != request.Percent)
                return Remember(new(key, request.Percent, measured, false, "limit-not-confirmed"));
            return Remember(new(key, request.Percent, measured, true,
                measured == CpuLimitPolicy.Unlimited ? "unlimited" : "applied"));
        }
        catch (CpuLimitException ex) { return Failure(request, ex.Code, ex.WindowsError); }
        catch (Win32Exception ex) { return Failure(request, "windows-rejected", ex.NativeErrorCode); }
        catch (UnauthorizedAccessException) { return Failure(request, "access-denied", 5); }
    }

    private CpuLimitResult Failure(CpuLimitRequest request, string code, int? error)
    {
        int? actual = _jobs.TryGetValue(request.Key, out var entry) ? entry.Percent : null;
        return Remember(new(request.Key, request.Percent, actual, false, code, error));
    }

    private CpuLimitResult Remember(CpuLimitResult result) => _results[result.Key] = result;

    private void Release(string key)
    {
        if (!_jobs.TryGetValue(key, out var entry)) return;
        // Closing the handle alone does NOT remove a live job's CPU quota.
        // Disable it explicitly before releasing the handles. Never use kill-on-close.
        if (entry.Job.IsAlive)
        {
            entry.Job.SetLimit(CpuLimitPolicy.Unlimited);
            if (entry.Job.ReadLimit() != CpuLimitPolicy.Unlimited)
                throw new CpuLimitException("release-not-confirmed");
        }
        entry.Job.Dispose();
        _jobs.Remove(key);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            List<Exception> failures = [];
            foreach (var key in _jobs.Keys.ToArray())
            {
                try { Release(key); }
                catch (Exception ex)
                {
                    failures.Add(ex);
                    _jobs[key].Job.Dispose();
                    _jobs.Remove(key);
                }
            }
            (_platform as IDisposable)?.Dispose();
            _disposed = true;
            if (failures.Count > 0) throw new AggregateException("cpu-limit-release-failed", failures);
        }
    }
}
