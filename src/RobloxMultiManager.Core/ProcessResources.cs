using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RobloxMultiManager.Core;

public sealed record ProcessResourceUsage(string Key, long? ResidentBytes, long? PrivateBytes,
    long? DedicatedGpuBytes, long? SharedGpuBytes, DateTime SampledUtc, double? CpuPercent = null);

/// <summary>Called on the existing background monitor, at most once every two seconds.</summary>
internal sealed class ProcessResourceSampler : IDisposable
{
    private readonly GpuMemoryCounters _gpu = new();
    private long _lastSample;
    private readonly Dictionary<string, (TimeSpan Cpu, long Timestamp)> _cpuTimes = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, ProcessResourceUsage>? Sample(IReadOnlyList<RobloxInstanceInfo> instances)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastSample != 0 && Stopwatch.GetElapsedTime(_lastSample, now) < TimeSpan.FromSeconds(2)) return null;
        _lastSample = now;
        if (instances.Count == 0) { _cpuTimes.Clear(); return new Dictionary<string, ProcessResourceUsage>(); }
        var live = instances.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _cpuTimes.Keys.Where(k => !live.Contains(k)).ToArray()) _cpuTimes.Remove(key);
        var gpu = _gpu.Read();
        var result = new Dictionary<string, ProcessResourceUsage>(StringComparer.Ordinal);
        var sampled = DateTime.UtcNow;
        foreach (var instance in instances)
        {
            long? resident = null, committed = null; double? cpuPercent = null;
            try
            {
                using var process = Process.GetProcessById(instance.ProcessId);
                // A recycled PID must never inherit another process's memory figures.
                if (process.StartTime.ToUniversalTime() != instance.StartTimeUtc) continue;
                resident = process.WorkingSet64;
                committed = process.PrivateMemorySize64;
                var cpu = process.TotalProcessorTime;
                if (_cpuTimes.TryGetValue(instance.Key, out var previous))
                {
                    var elapsed = Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds;
                    if (elapsed > 0 && cpu >= previous.Cpu) cpuPercent = Math.Clamp((cpu - previous.Cpu).TotalSeconds / elapsed / Math.Max(1, Environment.ProcessorCount) * 100, 0, 100);
                }
                _cpuTimes[instance.Key] = (cpu, now);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            { /* Permissions/exit are unavailable readings, never fabricated zeroes. */ }
            gpu.TryGetValue(instance.ProcessId, out var memory);
            result[instance.Key] = new(instance.Key, resident, committed, memory.Dedicated, memory.Shared, sampled, cpuPercent);
        }
        return result;
    }
    public void Dispose() => _gpu.Dispose();
}

/// <summary>One language-neutral PDH query for all GPU process instances and adapters.</summary>
internal sealed class GpuMemoryCounters : IDisposable
{
    private nint _query, _dedicated, _shared;
    private bool _initialized;
    private const uint MoreData = 0x800007D2;
    private const uint FormatLarge = 0x400;
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CounterValue
    {
        [FieldOffset(0)] public uint Status;
        [FieldOffset(8)] public long Value;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem { public nint Name; public CounterValue Value; }

    public Dictionary<int, (long? Dedicated, long? Shared)> Read()
    {
        var result = new Dictionary<int, (long?, long?)>();
        if (!_initialized)
        {
            _initialized = true;
            if (PdhOpenQueryW(null, 0, out _query) != 0) { _query = 0; return result; }
            if (PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out _dedicated) != 0) _dedicated = 0;
            if (PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Shared Usage", 0, out _shared) != 0) _shared = 0;
        }
        if (_query == 0 || PdhCollectQueryData(_query) != 0) return result;
        foreach (var (pid, value) in ReadCounter(_dedicated)) result[pid] = (value, null);
        foreach (var (pid, value) in ReadCounter(_shared))
        {
            result.TryGetValue(pid, out var current);
            result[pid] = (current.Item1, value);
        }
        return result;
    }

    private static Dictionary<int, long> ReadCounter(nint counter)
    {
        var result = new Dictionary<int, long>();
        if (counter == 0) return result;
        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, FormatLarge, ref size, out _, 0);
        if (status != MoreData || size == 0 || size > 16 * 1024 * 1024) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            // Instances may change between calls. A changed-size sample is simply skipped.
            if (PdhGetFormattedCounterArrayW(counter, FormatLarge, ref size, out var count, buffer) != 0) return result;
            var stride = Marshal.SizeOf<CounterItem>();
            if ((long)count * stride > size) return result;
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + (int)i * stride);
                if (item.Value.Status > 1 || item.Value.Value < 0) continue;
                var name = Marshal.PtrToStringUni(item.Name);
                if (!TryProcessId(name, out var pid)) continue;
                result.TryGetValue(pid, out var current);
                // Each instance represents a different GPU adapter for that PID.
                result[pid] = current > long.MaxValue - item.Value.Value ? current : current + item.Value.Value;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }
    internal static bool TryProcessId(string? instance, out int pid)
    {
        pid = 0;
        if (instance is null || !instance.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) return false;
        var end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;
    }
    public void Dispose() { if (_query != 0) PdhCloseQuery(_query); _query = 0; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? source, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, out uint count, nint buffer);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
