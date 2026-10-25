using System.Text.Json;
namespace RobloxMultiManager.Core;
public sealed partial class AccountSocialService
{
    private readonly SemaphoreSlim _identityGate = new(1);
    private readonly SemaphoreSlim[] _readGates = Enumerable.Range(0, 16).Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly Dictionary<string, (DateTime Expires, string Json)> _reads = [];
    private int _cacheGeneration;
    public void InvalidateReads() { lock (_reads) { _reads.Clear(); _cacheGeneration++; } }
    private async Task<IReadOnlyDictionary<long, string>> SocialHeadshotsAsync(IEnumerable<long> userIds, CancellationToken token)
    {
        var images = new Dictionary<long, string>();
        foreach (var batch in userIds.Distinct().Order().Chunk(100))
        {
            try
            {
                using var data = await ReadAsync("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(",", batch) + "&size=150x150&format=Png&isCircular=false", token);
                foreach (var row in Data(data.RootElement).EnumerateArray())
                    if (RobloxGameService.String(row, "state") == "Completed" && SafeImage(RobloxGameService.NullableString(row, "imageUrl")) is { } url)
                        images[RobloxGameService.Number(row, "targetId")] = url;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is RobloxApiException or HttpRequestException or JsonException or InvalidDataException or OperationCanceledException) { break; }
        }
        return images;
    }
    private async Task<JsonDocument> ReadAsync(string url, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (url.Contains("/authenticated") || url.Contains("/currently-wearing"))
            return await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), token);
        var gate = _readGates[(uint)StringComparer.Ordinal.GetHashCode(url) % (uint)_readGates.Length];
        await gate.WaitAsync(token);
        try
        {
            int generation;
            lock (_reads)
            {
                if (_reads.TryGetValue(url, out var cached) && cached.Expires > DateTime.UtcNow) return JsonDocument.Parse(cached.Json);
                generation = _cacheGeneration;
            }
            using var result = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), token);
            var text = result.RootElement.GetRawText();
            // Short account snapshots; immutable thumbnail URLs can be reused longer.
            var age = url.Contains("thumbnails.roblox.com") ? 120 : url.Contains("inventory.roblox.com") ? 60 : 15;
            lock (_reads)
            {
                if (generation == _cacheGeneration && !text.Contains("\"Pending\"", StringComparison.Ordinal) && !text.Contains("\"InReview\"", StringComparison.Ordinal))
                {
                    while (_reads.Count >= 64 || (_reads.Count > 0 && _reads.Values.Sum(v => v.Json.Length) + text.Length > 4 * 1024 * 1024))
                        _reads.Remove(_reads.MinBy(p => p.Value.Expires).Key);
                    _reads[url] = (DateTime.UtcNow.AddSeconds(age), text);
                }
            }
            return JsonDocument.Parse(text);
        }
        finally { gate.Release(); }
    }
}
