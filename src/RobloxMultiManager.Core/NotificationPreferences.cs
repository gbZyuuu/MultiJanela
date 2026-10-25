using System.Net.Http.Json;
using System.Threading.Channels;

namespace RobloxMultiManager.Core;

public sealed record NotificationPreferences(bool Desktop = false, bool Webhook = false, string WebhookUrl = "")
{
    public static NotificationPreferences Validate(NotificationPreferences value)
    {
        if (value.Webhook && (!Uri.TryCreate(value.WebhookUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !string.IsNullOrEmpty(uri.UserInfo) || value.WebhookUrl.Length > 2048))
            throw new ArgumentException(L.T("Informe um endereço HTTPS válido para o webhook."));
        return value;
    }
}

public sealed class NotificationDispatcher(HttpClient http) : IAsyncDisposable
{
    private sealed record Notice(string Url, string Kind, int Sessions);
    private readonly Channel<Notice> _queue = Channel.CreateBounded<Notice>(new BoundedChannelOptions(16)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    public void Enqueue(NotificationPreferences preferences, string kind, int sessions)
    {
        if (!preferences.Webhook) return;
        NotificationPreferences.Validate(preferences);
        _worker ??= RunAsync();
        _queue.Writer.TryWrite(new(preferences.WebhookUrl, kind, Math.Max(0, sessions)));
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(_stop.Token))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
                    // A generic payload supported by webhook receivers; never include
                    // names, private destinations, session cookies or authorization data.
                    using var response = await http.PostAsJsonAsync(message.Url, new { content = $"MultiJanela: {message.Kind}; sessions={message.Sessions}",
                        @event = message.Kind, sessions = message.Sessions, timeUtc = DateTime.UtcNow }, timeout.Token);
                    if ((int)response.StatusCode == 429)
                        await Task.Delay(response.Headers.RetryAfter?.Delta is { } wait && wait >= TimeSpan.Zero && wait <= TimeSpan.FromMinutes(1) ? wait : TimeSpan.FromSeconds(30), _stop.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException) { }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete(); _stop.Cancel();
        if (_worker is not null) await _worker;
        _stop.Dispose();
    }
}
