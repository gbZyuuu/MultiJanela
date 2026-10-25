using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace RobloxMultiManager.Core;

/// <summary>A single idle helper restores quotas if its owning manager exits unexpectedly.</summary>
public static class CpuQuotaWatchdog
{
    internal const string Switch = "--cpu-quota-watchdog";
    internal sealed record Message(string Kind, int ProcessId, long StartTicks);
    public static int? RunIfRequested(string[] args)
    {
        if (args.FirstOrDefault() != Switch) return null;
        try { return RunAsync(args).GetAwaiter().GetResult(); }
        catch { return 1; }
    }
    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 4 || !args[1].StartsWith("MultiJanela.Cpu.", StringComparison.Ordinal)) return 1;
        using var manager = Process.GetProcessById(int.Parse(args[2]));
        if (manager.StartTime.ToUniversalTime().Ticks != long.Parse(args[3])) return 1;
        using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var platform = new WindowsCpuJobPlatform(withWatchdog: false);
        var held = new Dictionary<string, ICpuProcessJob>();
        var managerExit = manager.WaitForExitAsync();
        try
        {
            while (!managerExit.IsCompleted)
            {
                var read = reader.ReadLineAsync();
                if (await Task.WhenAny(read, managerExit) != read) break;
                var line = await read;
                if (line is null) break;
                try
                {
                    var message = JsonSerializer.Deserialize<Message>(line)!;
                    var request = new CpuLimitRequest(message.ProcessId, new DateTime(message.StartTicks, DateTimeKind.Utc), 100);
                    if (message.Kind == "hold")
                    {
                        if (!held.ContainsKey(request.Key))
                            held[request.Key] = platform.Open(request, false) ?? throw new IOException("quota-job-not-found");
                    }
                    else if (message.Kind == "release")
                    {
                        if (held.Remove(request.Key, out var job)) Release(job);
                    }
                    else throw new IOException("unknown-quota-command");
                    await writer.WriteLineAsync("ok");
                }
                catch { await writer.WriteLineAsync("error"); }
            }
        }
        finally
        {
            foreach (var job in held.Values) { try { Release(job); } catch { /* Continue restoring the other processes. */ } }
        }
        return 0;
    }
    private static void Release(ICpuProcessJob job)
    {
        try { if (job.IsAlive) job.SetLimit(100); }
        finally { job.Dispose(); }
    }
}

internal sealed class CpuQuotaWatchdogClient(string? entryExecutable = null) : IDisposable
{
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _worker;
    private bool _failed;
    public int? ProcessId => _worker?.Id;
    public bool Failed => _failed || _worker is { HasExited: true };
    public void Hold(CpuLimitRequest request)
    {
        if (_failed) throw new CpuLimitException("cpu-watchdog-unavailable");
        if (_pipe is null) Start();
        Send(new("hold", request.ProcessId, request.StartTimeUtc.ToUniversalTime().Ticks));
    }
    public void Forget(CpuLimitRequest request)
    {
        if (_pipe is not null) Send(new("release", request.ProcessId, request.StartTimeUtc.ToUniversalTime().Ticks));
    }
    private void Start()
    {
        try
        {
            var pipeName = "MultiJanela.Cpu." + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var self = Process.GetCurrentProcess();
            var executable = entryExecutable ?? Environment.ProcessPath ?? throw new IOException("entry-path-unavailable");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            foreach (var arg in new[] { CpuQuotaWatchdog.Switch, pipeName, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString() })
                info.ArgumentList.Add(arg);
            _worker = Process.Start(info) ?? throw new IOException("watchdog-start-failed");
            _pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(6)).GetAwaiter().GetResult();
            _reader = new StreamReader(_pipe, leaveOpen: true);
            _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        }
        catch
        {
            _failed = true; Dispose();
            throw new CpuLimitException("cpu-watchdog-unavailable");
        }
    }
    private void Send(CpuQuotaWatchdog.Message message)
    {
        try
        {
            _writer!.WriteLine(JsonSerializer.Serialize(message));
            if (_reader!.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult() != "ok")
                throw new IOException("watchdog-rejected");
        }
        catch
        {
            _failed = true; Dispose();
            throw new CpuLimitException("cpu-watchdog-unavailable");
        }
    }
    public void Dispose()
    {
        try { _writer?.Dispose(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        _reader?.Dispose(); _reader = null; _writer = null;
        _pipe?.Dispose(); _pipe = null;
        if (_worker is { } worker)
        {
            // Closing the pipe asks the helper to restore quotas, including after a manager crash.
            try { worker.WaitForExit(2000); } catch (InvalidOperationException) { }
            worker.Dispose(); _worker = null;
        }
    }
}
