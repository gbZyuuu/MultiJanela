using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RobloxMultiManager.Core;

internal sealed class WindowsCpuJobPlatform(bool withWatchdog = true, string? entryExecutable = null) : ICpuJobPlatform, IDisposable
{
    private readonly CpuQuotaWatchdogClient? _watchdog = withWatchdog ? new(entryExecutable) : null;
    public int? GuardProcessId => _watchdog?.ProcessId;
    public bool GuardFailed => _watchdog?.Failed == true;
    public void Dispose() => _watchdog?.Dispose();
    private const uint JobAllRequired = 0x0001 | 0x0002 | 0x0004; // assign, set attributes, query
    private const uint ProcessRequired = 0x0001 | 0x0100 | 0x1000; // terminate (required by assign), set quota, query limited
    private const int CpuRateInformation = 15;

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRate { public uint Flags; public uint Rate; }

    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class ProcessJob(JobHandle job, SafeProcessHandle process, CpuLimitRequest request, CpuQuotaWatchdogClient? watchdog) : ICpuProcessJob
    {
        private bool _guarded;
        public bool IsAlive => GetExitCodeProcess(process, out var code) && code == 259;
        public int ReadLimit()
        {
            if (!QueryInformationJobObject(job, CpuRateInformation, out var rate, Marshal.SizeOf<CpuRate>(), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return (rate.Flags & 1) == 0 ? CpuLimitPolicy.Unlimited : (int)rate.Rate / 100;
        }
        public void SetLimit(int percent)
        {
            if (percent < 100 && !_guarded && watchdog is not null) { watchdog.Hold(request); _guarded = true; }
            var rate = new CpuRate { Flags = percent == CpuLimitPolicy.Unlimited ? 0u : 1u | 4u, Rate = (uint)percent * 100 };
            if (!SetInformationJobObject(job, CpuRateInformation, ref rate, Marshal.SizeOf<CpuRate>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose()
        {
            try { if (_guarded && watchdog?.Failed == false) watchdog.Forget(request); }
            finally { job.Dispose(); process.Dispose(); }
        }
    }

    public ICpuProcessJob? Open(CpuLimitRequest request, bool create)
    {
        var fileTime = request.StartTimeUtc.ToUniversalTime().ToFileTimeUtc();
        // The helper holds a second handle before throttling, keeping the name and
        // quota recoverable until normal release or unexpected manager exit.
        var name = $"Local\\MultiJanela.Cpu.{request.ProcessId}.{fileTime:x}";
        var job = OpenJobObjectW(JobAllRequired, false, name);
        var existed = !job.IsInvalid;
        if (!existed)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            if (error != 2) throw new Win32Exception(error);
            if (!create) return null;
            job = CreateJobObjectW(nint.Zero, name);
            var createError = Marshal.GetLastWin32Error();
            if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(createError); }
            existed = createError == 183;
        }
        var process = OpenProcess(ProcessRequired, false, request.ProcessId);
        try
        {
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!GetProcessTimes(process, out var actualCreation, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (actualCreation != fileTime) throw new CpuLimitException("process-identity-mismatch");
            if (existed)
            {
                if (!IsProcessInJob(process, job, out var associated)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!associated) throw new CpuLimitException("job-identity-mismatch");
            }
            else if (!AssignProcessToJobObject(job, process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new ProcessJob(job, process, request, _watchdog);
        }
        catch { job.Dispose(); process.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern JobHandle CreateJobObjectW(nint attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern JobHandle OpenJobObjectW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, JobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int kind, ref CpuRate information, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(JobHandle job, int kind, out CpuRate information, int size, out int returned);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
