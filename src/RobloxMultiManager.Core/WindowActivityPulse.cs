using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RobloxMultiManager.Core;

public sealed class WindowActivityPulse
{
    private readonly Dictionary<string, DateTime> _last = new(StringComparer.Ordinal);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint map);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nint key, nint flags);
    public bool Send(RobloxInstanceInfo session)
    {
        if (!session.HasWindow || _last.TryGetValue(session.Key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(30)) return false;
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != session.StartTimeUtc
                || GetWindowThreadProcessId(session.WindowHandle, out var owner) == 0 || owner != (uint)session.ProcessId) return false;
            const uint key = 0x7E; // F15: an explicit, opt-in pulse without focus changes or cursor movement.
            var down = 1u | (MapVirtualKeyW(key, 0) & 0xFF) << 16;
            var posted = PostMessageW(session.WindowHandle, 0x100, (nint)key, (nint)down)
                && PostMessageW(session.WindowHandle, 0x101, (nint)key, (nint)(down | 0xC0000000u));
            if (posted) _last[session.Key] = DateTime.UtcNow;
            if (_last.Count > 256) foreach (var old in _last.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromMinutes(30)).Select(p => p.Key).ToArray()) _last.Remove(old);
            return posted;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }
}
