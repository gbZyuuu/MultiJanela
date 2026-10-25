using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RobloxMultiManager.Core;

public sealed class ProcessExitObserver : IDisposable
{
    private readonly SafeProcessHandle _handle;
    private ProcessExitObserver(SafeProcessHandle handle) => _handle = handle;
    public static ProcessExitObserver Open(int processId, DateTime startTimeUtc)
    {
        var handle = OpenProcess(0x1000 | 0x100000, false, processId);
        try
        {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (creation != startTimeUtc.ToUniversalTime().ToFileTimeUtc()) throw new InvalidOperationException("process-identity-mismatch");
            return new(handle);
        }
        catch { handle.Dispose(); throw; }
    }
    public int? ReadExitCode()
    {
        // A real native handle must remain open; retaining a lazy Process object is insufficient.
        if (WaitForSingleObject(_handle, 0) != 0) return null;
        return GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : null;
    }
    public void Dispose() => _handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
}
