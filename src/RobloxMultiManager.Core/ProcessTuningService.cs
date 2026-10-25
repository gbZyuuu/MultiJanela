using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxMultiManager.Core;

public sealed record ProcessTuningPreferences(bool Enabled = false, bool LowerPriority = false, bool Eco = false, bool OnlyBackground = true);
public sealed record ProcessTuningResult(string Key, bool Success, string Code);

public sealed class ProcessTuningService : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct PowerState { public uint Version, ControlMask, StateMask; }
    private sealed record Baseline(int Pid, DateTime Start, ProcessPriorityClass Priority, PowerState? Power,
        ProcessPriorityClass LastPriority, PowerState? LastPower);
    private readonly Dictionary<string, Baseline> _original = new(StringComparer.Ordinal);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessInformation(nint process, int information, ref PowerState state, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessInformation(nint process, int information, ref PowerState state, uint size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool EmptyWorkingSet(nint process);

    public IReadOnlyList<ProcessTuningResult> Reconcile(IReadOnlyList<RobloxInstanceInfo> sessions, ProcessTuningPreferences preferences)
    {
        var live = sessions.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _original.Keys.Where(k => !live.Contains(k) || !preferences.Enabled).ToArray()) Restore(key);
        if (!preferences.Enabled) return [];
        GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
        var result = new List<ProcessTuningResult>();
        foreach (var session in sessions)
        {
            try
            {
                using var process = Process.GetProcessById(session.ProcessId);
                if (process.StartTime.ToUniversalTime() != session.StartTimeUtc || process.HasExited)
                { result.Add(new(session.Key, false, "identity-changed")); continue; }
                var previous = new PowerState { Version = 1 };
                var canPower = GetProcessInformation(process.Handle, 4, ref previous, (uint)Marshal.SizeOf<PowerState>());
                if (!_original.TryGetValue(session.Key, out var baseline))
                    _original[session.Key] = baseline = new(process.Id, session.StartTimeUtc, process.PriorityClass, canPower ? previous : null, process.PriorityClass, null);
                var background = !preferences.OnlyBackground || foreground != (uint)process.Id;
                var priority = preferences.LowerPriority && background ? ProcessPriorityClass.BelowNormal : baseline.Priority;
                if (process.PriorityClass != priority) process.PriorityClass = priority;
                _original[session.Key] = baseline = baseline with { LastPriority = priority };
                PowerState? appliedPower = null;
                if (preferences.Eco && canPower)
                {
                    var state = background ? new PowerState { Version = 1, ControlMask = 1, StateMask = 1 } : baseline.Power!.Value;
                    if (!SetProcessInformation(process.Handle, 4, ref state, (uint)Marshal.SizeOf<PowerState>())) throw new Win32Exception();
                    appliedPower = state;
                }
                else if (baseline.LastPower is not null && baseline.Power is PowerState original)
                {
                    if (!SetProcessInformation(process.Handle, 4, ref original, (uint)Marshal.SizeOf<PowerState>())) throw new Win32Exception();
                    appliedPower = original;
                }
                _original[session.Key] = baseline with { LastPriority = priority, LastPower = appliedPower };
                result.Add(new(session.Key, !preferences.Eco || canPower, preferences.Eco && !canPower ? "eco-unavailable" : "applied"));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
            { result.Add(new(session.Key, false, "unavailable")); }
        }
        return result;
    }
    public ProcessTuningResult TrimMemory(RobloxInstanceInfo session)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.StartTime.ToUniversalTime() != session.StartTimeUtc || process.HasExited) return new(session.Key, false, "identity-changed");
            return new(session.Key, EmptyWorkingSet(process.Handle), "trim");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return new(session.Key, false, "unavailable"); }
    }
    private void Restore(string key)
    {
        if (!_original.Remove(key, out var baseline)) return;
        try
        {
            using var process = Process.GetProcessById(baseline.Pid);
            if (process.StartTime.ToUniversalTime() != baseline.Start || process.HasExited) return;
            if (process.PriorityClass == baseline.LastPriority) process.PriorityClass = baseline.Priority;
            if (baseline.Power is PowerState original && baseline.LastPower is not null)
            {
                var current = new PowerState { Version = 1 };
                if (GetProcessInformation(process.Handle, 4, ref current, (uint)Marshal.SizeOf<PowerState>())
                    && current.ControlMask == baseline.LastPower.Value.ControlMask && current.StateMask == baseline.LastPower.Value.StateMask)
                    SetProcessInformation(process.Handle, 4, ref original, (uint)Marshal.SizeOf<PowerState>());
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { }
    }
    public void Dispose() { foreach (var key in _original.Keys.ToArray()) Restore(key); }
}
