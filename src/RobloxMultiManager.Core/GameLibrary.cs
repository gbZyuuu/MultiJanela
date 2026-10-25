using System.Net;
using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record GameSummary(long UniverseId, long PlaceId, string Name, string Description, int Playing = 0,
    int MaxPlayers = 0, string? IconUrl = null)
{
    public string Url => $"https://www.roblox.com/games/{PlaceId}";
    public bool IsValid => PlaceId > 0 && UniverseId > 0 && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 300
        && Description is not null && Description.Length <= 16000 && (IconUrl is null || IconUrl.Length <= 2048
        && Uri.TryCreate(IconUrl, UriKind.Absolute, out var icon) && icon.Scheme == "https" && icon.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase));
}
public sealed record GameSearchPage(GameSummary[] Games, string? NextPageToken);
public enum GameDiscoveryCategory { All, MostPlayed, Trending }
public sealed record GameDiscoveryPage(GameSummary[] Games, int NextOffset, bool HasMore);
public sealed record PublicGameServer(string Id, int Playing, int Capacity, double? ReportedPing)
{
    public int Available => Math.Max(0, Capacity - Playing);
}
public sealed record GameServerPage(PublicGameServer[] Servers, string? Cursor);
public enum ServerPreference { BestFit, Emptiest, Random }

public sealed class RobloxApiException(int statusCode, TimeSpan? retryAfter = null) : Exception(
    statusCode == 429 ? L.T("Muitas solicitações. Aguarde antes de atualizar novamente.")
        : statusCode is 401 or 403 ? L.T("O Roblox recusou esta solicitação. Verifique o login e as permissões.")
        : L.F("O serviço do Roblox respondeu com erro {0}.", statusCode))
{
    public int StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class RobloxGameService(HttpClient http)
{
    private readonly SemaphoreSlim _requests = new(2);
    private readonly string _sessionId = Guid.NewGuid().ToString("D");
    private readonly Dictionary<string, (DateTime Expires, string Json)> _cache = new();
    private static readonly HashSet<string> Hosts = ["games.roblox.com", "apis.roblox.com", "thumbnails.roblox.com"];

    public async Task<GameSearchPage> SearchAsync(string query, string? pageToken = null, CancellationToken token = default, Action<GameSummary[]>? loaded = null)
    {
        query = query.Trim();
        if (query.Length is < 1 or > 100) throw new ArgumentException(L.T("Digite entre 1 e 100 caracteres para buscar jogos."));
        var url = "https://apis.roblox.com/search-api/omni-search?searchQuery=" + Uri.EscapeDataString(query)
            + "&sessionId=" + _sessionId
            + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
        using var json = await GetAsync(url, "search:" + query + ":" + pageToken, TimeSpan.FromMinutes(1), token);
        var games = new List<GameSummary>();
        if (!json.RootElement.TryGetProperty("searchResults", out var groups) || groups.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        if (groups.ValueKind == JsonValueKind.Array)
            foreach (var group in groups.EnumerateArray())
                if (String(group, "contentGroupType") == "Game" && group.TryGetProperty("contents", out var items))
                    foreach (var game in items.EnumerateArray())
                    {
                        var place = Number(game, "rootPlaceId"); var universe = Number(game, "universeId");
                        if (place > 0 && universe > 0)
                            games.Add(new(universe, place, String(game, "name"), String(game, "description"), (int)Number(game, "playerCount")));
                    }
        var result = games.DistinctBy(g => g.PlaceId).Take(50).ToArray();
        loaded?.Invoke(result);
        return new(await AddIconsAsync(result, token), NullableString(json.RootElement, "nextPageToken"));
    }

    public async Task<GameDiscoveryPage> DiscoverAsync(GameDiscoveryCategory category = GameDiscoveryCategory.All, int offset = 0, CancellationToken token = default, Action<GameSummary[]>? loaded = null)
    {
        if (offset is < 0 or > 3000) throw new ArgumentOutOfRangeException(nameof(offset));
        using var json = await GetAsync("https://apis.roblox.com/explore-api/v1/get-sorts?sessionId=" + _sessionId,
            "discovery", TimeSpan.FromMinutes(2), token);
        if (!json.RootElement.TryGetProperty("sorts", out var sorts) || sorts.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var games = new List<GameSummary>();
        foreach (var sort in sorts.EnumerateArray().Where(s => String(s, "contentType") == "Games")
            .OrderBy(s => String(s, "sortId") == "top-playing-now" ? 0 : 1))
        {
            var kind = String(sort, "sortId");
            if (category == GameDiscoveryCategory.MostPlayed && kind != "top-playing-now"
                || category == GameDiscoveryCategory.Trending && kind != "top-trending") continue;
            if (!sort.TryGetProperty("games", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("isSponsored", out var sponsored) && sponsored.ValueKind == JsonValueKind.True) continue;
                var game = new GameSummary(Number(item, "universeId"), Number(item, "rootPlaceId"), String(item, "name"), String(item, "description"), (int)Number(item, "playerCount"));
                if (game.IsValid) games.Add(game);
            }
        }
        var unique = games.DistinctBy(g => g.UniverseId).Take(3000).ToArray();
        if (category == GameDiscoveryCategory.MostPlayed) unique = unique.OrderByDescending(g => g.Playing).ToArray();
        var initial = unique.Skip(offset).Take(24).ToArray(); loaded?.Invoke(initial);
        var page = await AddIconsAsync(initial, token);
        return new(page, offset + page.Length, offset + page.Length < unique.Length);
    }

    public async Task<GameSummary> GetPlaceAsync(long placeId, CancellationToken token = default)
    {
        if (placeId <= 0) throw new ArgumentOutOfRangeException(nameof(placeId));
        using var universe = await GetAsync($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", "place:" + placeId, TimeSpan.FromHours(1), token);
        var id = Number(universe.RootElement, "universeId");
        if (id <= 0) throw new RobloxApiException(404);
        using var details = await GetAsync($"https://games.roblox.com/v1/games?universeIds={id}", "game:" + id, TimeSpan.FromMinutes(1), token);
        var game = details.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (game.ValueKind != JsonValueKind.Object) throw new RobloxApiException(404);
        return (await AddIconsAsync([new(id, placeId, String(game, "name"), String(game, "description"),
            (int)Number(game, "playing"), (int)Number(game, "maxPlayers"))], token)).Single();
    }

    private async Task<GameSummary[]> AddIconsAsync(GameSummary[] games, CancellationToken token)
    {
        if (games.Length == 0) return games;
        var ids = string.Join(",", games.Select(g => g.UniverseId).Distinct().Order());
        try
        {
            using var json = await GetAsync("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + ids
                + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", "icons:" + ids, TimeSpan.FromMinutes(10), token);
            var icons = json.RootElement.GetProperty("data").EnumerateArray().Where(i => Number(i, "targetId") > 0)
                .GroupBy(i => Number(i, "targetId")).ToDictionary(g => g.Key, g => NullableString(g.First(), "imageUrl"));
            return games.Select(g => g with { IconUrl = icons.GetValueOrDefault(g.UniverseId) is { } image
                && Uri.TryCreate(image, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase) ? image : null }).ToArray();
        }
        catch (RobloxApiException) { return games; }
    }

    public void InvalidateServers(long placeId)
    { lock(_cache) foreach(var key in _cache.Keys.Where(k=>k.StartsWith("servers:"+placeId+":",StringComparison.Ordinal)).ToArray()) _cache.Remove(key); }

    public async Task<GameServerPage> GetServersAsync(long placeId, string? cursor = null, CancellationToken token = default, bool fullestFirst = false)
    {
        if (placeId <= 0 || cursor?.Length > 2048) throw new ArgumentException();
        var url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?sortOrder={(fullestFirst ? "Desc" : "Asc")}&limit=100"
            + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
        using var json = await GetAsync(url, "servers:" + placeId + ":" + cursor + ":" + fullestFirst, TimeSpan.FromSeconds(20), token);
        var servers = new List<PublicGameServer>();
        foreach (var server in json.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = String(server, "id"); var capacity = (int)Number(server, "maxPlayers"); var playing = (int)Number(server, "playing");
            if (Guid.TryParse(id, out _) && capacity > 0 && playing >= 0 && playing <= capacity)
                servers.Add(new(id, playing, capacity, server.TryGetProperty("ping", out var ping) && ping.ValueKind == JsonValueKind.Number && ping.TryGetDouble(out var ms) ? ms : null));
        }
        return new(servers.ToArray(), NullableString(json.RootElement, "nextPageCursor"));
    }

    public static PublicGameServer? ChooseServer(IEnumerable<PublicGameServer> servers, int accounts, ServerPreference preference)
    {
        if (accounts < 1) throw new ArgumentOutOfRangeException(nameof(accounts));
        var suitable = servers.Where(s => s.Available >= accounts).DistinctBy(s => s.Id).ToArray();
        return preference switch
        {
            ServerPreference.Emptiest => suitable.OrderBy(s => s.Playing).FirstOrDefault(),
            ServerPreference.Random => suitable.Length == 0 ? null : suitable[Random.Shared.Next(suitable.Length)],
            _ => suitable.OrderByDescending(s => s.Playing).FirstOrDefault()
        };
    }

    private async Task<JsonDocument> GetAsync(string url, string cacheKey, TimeSpan lifetime, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !Hosts.Contains(uri.Host)) throw new ArgumentException();
        lock (_cache) if (_cache.TryGetValue(cacheKey, out var item) && item.Expires > DateTime.UtcNow) return JsonDocument.Parse(item.Json);
        await _requests.WaitAsync(token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new RobloxApiException((int)response.StatusCode, response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : null));
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + count > 4 * 1024 * 1024) throw new InvalidDataException();
                output.Write(buffer, 0, count);
            }
            var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
            var parsed = JsonDocument.Parse(text);
            lock (_cache)
            {
                if (_cache.Count >= 128) _cache.Remove(_cache.MinBy(p => p.Value.Expires).Key);
                _cache[cacheKey] = (DateTime.UtcNow + lifetime, text);
            }
            return parsed;
        }
        finally { _requests.Release(); }
    }

    internal static long Number(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : 0;
    internal static string String(JsonElement element, string name) => NullableString(element, name) ?? "";
    internal static string? NullableString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString()) ? property.GetString() : null;
}

public sealed record GameBookmark(GameSummary Game, bool Favorite, DateTime LastPlayedUtc, SavedLaunchDestination[] PrivateLinks);
public sealed record GameLibraryData(int Version, GameBookmark[] Games);

public sealed class GameLibraryStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Root, "game-library.json");
    public async Task<GameBookmark[]> LoadAsync()
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException();
        var data = JsonSerializer.Deserialize<GameLibraryData>(await SecureJsonFiles.ReadAllTextAsync(FilePath));
        if (data?.Version != 1 || data.Games is null || data.Games.Length > 500) throw new InvalidDataException();
        return Validate(data.Games);
    }
    public async Task SaveAsync(IEnumerable<GameBookmark> entries)
    {
        var data = new GameLibraryData(1, Validate(entries));
        var json = JsonSerializer.Serialize(data);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new InvalidDataException();
        Directory.CreateDirectory(paths.Root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await SecureJsonFiles.WriteAllTextAsync(temporary, json); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static GameBookmark[] Validate(IEnumerable<GameBookmark> entries)
    {
        var result = entries.ToArray();
        if (result.Length > 500 || result.Any(e => e?.Game is null)) throw new InvalidDataException();
        if (result.Select(e => e.Game.PlaceId).Distinct().Count() != result.Length
            || result.Any(e => !e.Game.IsValid
                || e.PrivateLinks is null || e.PrivateLinks.Length > 30
                || e.PrivateLinks.Any(l => l is null || !SavedLaunchDestination.TryCreate(l.Name, l.Link, l.Id, out _, out _))))
            throw new InvalidDataException();
        return result;
    }
}
