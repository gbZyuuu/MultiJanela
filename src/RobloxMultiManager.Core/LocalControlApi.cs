using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting;

namespace RobloxMultiManager.Core;

public sealed record LocalApiPreferences(bool Enabled = false, int Port = 18889, bool AllowActions = false, bool AllowRejoin = false,
    bool IncludeNames = false, string ProtectedToken = "")
{
    public static LocalApiPreferences Validate(LocalApiPreferences data)
    {
        if (data.Port is < 1024 or > 65535 || data.AllowRejoin && !data.AllowActions || data.ProtectedToken.Length > 32768) throw new ArgumentException();
        return data;
    }
}
public sealed record ApiAccount(Guid Id, string Name, bool Ready);
public sealed record ApiSession(string Key, int Pid, Guid? AccountId, string Version, long? RamBytes, long? VramBytes, double? CpuPercent);
public sealed record ApiPreset(Guid Id, string Name, int Accounts);
public sealed record ApiSnapshot(string Version, bool Busy, ApiAccount[] Accounts, ApiSession[] Sessions, ApiPreset[] Presets);
public sealed record ApiAction(string Action, Guid? PresetId);

/// <summary>Opt-in loopback-only control, with token authentication, no browser origins and no raw sessions or destinations.</summary>
public sealed class LocalControlApi : IAsyncDisposable
{
    private WebApplication? _app;
    private byte[] _token = [];
    public async Task StartAsync(LocalApiPreferences preferences, string token, Func<Task<ApiSnapshot>> snapshot, Func<ApiAction, Task<bool>> dispatch)
    {
        LocalApiPreferences.Validate(preferences);
        if (_app is not null || token.Length < 32) throw new InvalidOperationException();
        _token = Encoding.UTF8.GetBytes(token);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(LocalControlApi).Assembly.GetName().Name });
        builder.Configuration.Sources.Clear(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false; options.Listen(IPAddress.Loopback, preferences.Port, listen => listen.Protocols = HttpProtocols.Http1);
            options.Limits.MaxRequestBodySize = 16384; options.Limits.MaxRequestHeadersTotalSize = 16384;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            options.Limits.MaxConcurrentConnections = 8;
        });
        var app = builder.Build();
        var gate = new object(); var window = DateTime.UtcNow; var requests = 0;
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) || context.Request.Headers.ContainsKey("Origin"))
            { context.Response.StatusCode = 403; return; }
            var header = context.Request.Headers.Authorization.ToString();
            var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8.GetBytes(header[7..]) : [];
            var valid = supplied.Length == _token.Length && CryptographicOperations.FixedTimeEquals(supplied, _token);
            CryptographicOperations.ZeroMemory(supplied);
            if (!valid) { context.Response.StatusCode = 401; return; }
            lock (gate)
            {
                if (DateTime.UtcNow - window > TimeSpan.FromMinutes(1)) { requests = 0; window = DateTime.UtcNow; }
                if (++requests > 120) { context.Response.StatusCode = 429; return; }
            }
            await next(context);
        });
        app.MapGet("/v1/status", async () => Results.Json(await snapshot()));
        app.MapGet("/v1/accounts", async () => Results.Json((await snapshot()).Accounts));
        app.MapGet("/v1/sessions", async () => Results.Json((await snapshot()).Sessions));
        app.MapGet("/v1/presets", async () => Results.Json((await snapshot()).Presets));
        app.MapPost("/v1/actions", async (HttpContext context) =>
        {
            if (!preferences.AllowActions) return Results.StatusCode(403);
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            ApiAction? action;
            try { action = await context.Request.ReadFromJsonAsync<ApiAction>(context.RequestAborted); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException) { return Results.BadRequest(); }
            if (action?.Action is not ("launch" or "rejoin" or "arrange" or "pulse" or "trim")) return Results.BadRequest();
            if (action.Action == "rejoin" && !preferences.AllowRejoin) return Results.StatusCode(403);
            if (action.Action is "launch" or "rejoin" or "pulse" && action.PresetId is null) return Results.BadRequest();
            return await dispatch(action) ? Results.Accepted() : Results.StatusCode(409);
        });
        try { await app.StartAsync(); _app = app; }
        catch { await app.DisposeAsync(); CryptographicOperations.ZeroMemory(_token); _token = []; throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token); } catch (OperationCanceledException) { }
            await _app.DisposeAsync(); _app = null;
        }
        CryptographicOperations.ZeroMemory(_token); _token = [];
    }
}
