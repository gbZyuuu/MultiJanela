using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record ManagerOpenRequest(string Version, string? ProductVersion = null)
{
    public string EffectiveVersion => ProductVersion ?? Version;
}
public sealed record ManagerOpenReply(string Action);

/// <summary>One writer per Windows session; activation and acknowledged version handoff stay local.</summary>
public sealed class ManagerInstanceCoordinator : IDisposable
{
    private readonly Mutex _lease;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private bool _ownsLease;
    private Task? _listener;

    public ManagerInstanceCoordinator(string? instanceName = null)
    {
        _lease = new Mutex(false, instanceName ?? @"Local\RobloxMultiManager.App");
        var identity = instanceName ?? (WindowsIdentity.GetCurrent().User?.Value + ":" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        _pipeName = "MultiJanela.Activation." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    // Call and release on the UI thread: Windows mutex ownership is thread-bound.
    public bool TryAcquire()
    {
        if (_ownsLease) return true;
        try { return _ownsLease = _lease.WaitOne(0); }
        catch (AbandonedMutexException) { return _ownsLease = true; }
    }

    public static ManagerOpenRequest CreateRequest(string version) =>
        System.Version.TryParse(version, out var value) && value.Major == 1
            ? new("3.0.1.0", version) : new(version);

    public static bool IsDifferentVersion(string requested, string current) =>
        Version.TryParse(requested, out var next) && Version.TryParse(current, out var active) && next != active;

    public void Listen(Func<ManagerOpenRequest, CancellationToken, Task<ManagerOpenReply>> prepare,
        Func<Task> commit, Func<Task> abort)
    {
        if (!_ownsLease || _listener is not null) throw new InvalidOperationException();
        _listener = ListenAsync(prepare, commit, abort);
    }

    private async Task ListenAsync(Func<ManagerOpenRequest, CancellationToken, Task<ManagerOpenReply>> prepare,
        Func<Task> commit, Func<Task> abort)
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 4,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            catch (IOException) { pipe.Dispose(); continue; }
            _ = HandleAsync(pipe, prepare, commit, abort);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe,
        Func<ManagerOpenRequest, CancellationToken, Task<ManagerOpenReply>> prepare,
        Func<Task> commit, Func<Task> abort)
    {
        var prepared = false;
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true })
        {
            try
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var request = JsonSerializer.Deserialize<ManagerOpenRequest>(await ReadBoundedAsync(reader, timeout.Token));
                if (request?.Version is null || request.Version.Length > 32 || !Version.TryParse(request.Version, out _)) return;
                if (request.ProductVersion is { } product && (product.Length > 32 || !System.Version.TryParse(product, out _))) return;
                var reply = await prepare(request, timeout.Token).ConfigureAwait(false);
                prepared = reply.Action == "ready";
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), timeout.Token);
                if (!prepared) return;
                if (await ReadBoundedAsync(reader, timeout.Token) != "commit") return;
                await commit().ConfigureAwait(false);
                prepared = false;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
            finally { if (prepared) await abort().ConfigureAwait(false); }
        }
    }

    public async Task<string> RequestAsync(string version, Func<Task> beforeCommit, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(800, token).ConfigureAwait(false); }
        catch (TimeoutException) { return "legacy"; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(CreateRequest(version)).AsMemory(), timeout.Token);
        var reply = JsonSerializer.Deserialize<ManagerOpenReply>(await ReadBoundedAsync(reader, timeout.Token));
        if (reply?.Action == "ready")
        {
            await beforeCommit().ConfigureAwait(false);
            await writer.WriteLineAsync("commit".AsMemory(), timeout.Token);
        }
        return reply?.Action ?? "unavailable";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var character = new char[1];
        while (text.Length < 256)
        {
            if (await reader.ReadAsync(character.AsMemory(), token) == 0) throw new IOException();
            if (character[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append(character[0]);
        }
        throw new IOException("Invalid activation message.");
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_ownsLease) { _lease.ReleaseMutex(); _ownsLease = false; }
        _lease.Dispose();
        // In-flight pipe operations observe cancellation before their CTS can be collected.
    }
}
