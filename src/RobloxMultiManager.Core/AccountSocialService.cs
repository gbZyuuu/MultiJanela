using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record OnlineFriend(long UserId, string Username, string DisplayName, string Location, long? PlaceId, string? ServerId, string? ThumbnailUrl = null, long? UniverseId = null, string? ExperienceName = null, string? ExperienceIconUrl = null, bool IsInExperience = false, string? Alias = null)
{
    public string Caption => !DisplayPrivacy.Enabled && !string.IsNullOrWhiteSpace(Alias) ? Alias + " · " + Username : DisplayPrivacy.Enabled ? L.T("Amigo") : string.IsNullOrWhiteSpace(Username) ? L.F("Usuário {0}", UserId) : $"{(string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName)} (@{Username})";
    public bool InExperience => IsInExperience || PlaceId is > 0;
    public string ExperienceCaption => ExperienceName ?? (string.IsNullOrWhiteSpace(Location) ? (InExperience ? L.T("Em experiência · acesso privado") : L.T("Online")) : Location);
    public bool CanJoin => PlaceId is > 0 && Guid.TryParse(ServerId, out _);
    public string? Destination => CanJoin ? $"https://www.roblox.com/games/start?placeId={PlaceId}&gameInstanceId={ServerId}" : null;
}
public sealed record OwnedOutfit(long Id, string Name, bool Editable, string? ThumbnailUrl = null);
public sealed record OwnedOutfitPage(OwnedOutfit[] Items, string? Cursor);

/// <summary>Account-scoped requests. Credentials stay in the caller's ephemeral client.</summary>
public sealed partial class AccountSocialService(HttpClient http, long expectedUserId)
{
    private bool _verified;
    private readonly HashSet<long> _listedOutfits = [];
    private async Task VerifyAsync(CancellationToken token)
    {
        await _identityGate.WaitAsync(token);
        try
        {
            if (_verified) return;
            using var json = await ReadAsync("https://users.roblox.com/v1/users/authenticated", token);
            if (RobloxGameService.Number(json.RootElement, "id") != expectedUserId)
                throw new InvalidOperationException(L.T("A sessão do navegador não corresponde à conta selecionada. Atualize o login."));
            _verified = true;
        }
        finally { _identityGate.Release(); }
    }
    public async Task<OnlineFriend[]> GetOnlineFriendsAsync(CancellationToken token = default, Action<OnlineFriend[]>? loaded = null)
    {
        await VerifyAsync(token);
        using var json = await ReadAsync($"https://friends.roblox.com/v1/users/{expectedUserId}/friends/online", token);
        var result = new List<OnlineFriend>();
        foreach (var friend in Data(json.RootElement).EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object).Take(2000))
        {
            var id = RobloxGameService.Number(friend, "id"); if (id <= 0) continue;
            var presence = friend.TryGetProperty("userPresence", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
            result.Add(Friend(friend, id, presence));
        }
        var rows = result.DistinctBy(f => f.UserId).OrderByDescending(f => f.InExperience).ToArray(); loaded?.Invoke(rows);
        return await WithHeadshotsAsync(rows, token);
    }
    public async Task<OnlineFriend[]> GetFriendsAsync(CancellationToken token = default, Action<OnlineFriend[]>? loaded = null)
    {
        await VerifyAsync(token);
        using var json = await ReadAsync($"https://friends.roblox.com/v1/users/{expectedUserId}/friends", token);
        var friends = Data(json.RootElement).EnumerateArray().Where(f => RobloxGameService.Number(f, "id") > 0).Take(2000).Select(f => f.Clone()).ToArray();
        loaded?.Invoke(friends.Select(f => Friend(f, RobloxGameService.Number(f, "id"), default)).ToArray());
        var presence = new Dictionary<long, JsonElement>();
        foreach (var batch in friends.Select(f => RobloxGameService.Number(f, "id")).Distinct().Chunk(100))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users") { Content = JsonContent.Create(new { userIds = batch }) };
            using var observed = await SendAsync(request, token);
            if (observed.RootElement.TryGetProperty("userPresences", out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray()) { var id = RobloxGameService.Number(row, "userId"); if (id > 0) presence[id] = row.Clone(); }
        }
        return await WithHeadshotsAsync(friends.Select(f => { var id = RobloxGameService.Number(f, "id"); return Friend(f, id, presence.GetValueOrDefault(id)); }).DistinctBy(f => f.UserId).ToArray(), token);
    }
    private static OnlineFriend Friend(JsonElement friend, long id, JsonElement presence)
    {
        var place = RobloxGameService.Number(presence, "placeId");
        var server = RobloxGameService.NullableString(presence, "gameInstanceId") ?? RobloxGameService.NullableString(presence, "gameId");
        if (!Guid.TryParse(server, out _)) server = null;
        var universe = RobloxGameService.Number(presence, "universeId");
        var kind = RobloxGameService.String(presence, "UserPresenceType");
        var inGame = kind.Equals("InGame", StringComparison.OrdinalIgnoreCase) || RobloxGameService.Number(presence, "userPresenceType") == 2;
        return new(id, RobloxGameService.String(friend, "name"), RobloxGameService.String(friend, "displayName"), RobloxGameService.String(presence, "lastLocation"), place > 0 ? place : null, server, UniverseId: universe > 0 ? universe : null, IsInExperience: inGame);
    }
    private async Task<OnlineFriend[]> WithHeadshotsAsync(OnlineFriend[] rows, CancellationToken token)
    {
        foreach (var row in rows) _knownFriends.Add(row.UserId);
        var enriched = EnrichFriendsAsync(rows, token);
        var headshots = SocialHeadshotsAsync(rows.OrderByDescending(f => f.InExperience).Take(200).Select(f => f.UserId), token);
        await Task.WhenAll(enriched, headshots);
        rows = await enriched; var images = await headshots;
        token.ThrowIfCancellationRequested();
        return rows.Select(f => f with { ThumbnailUrl = SafeImage(images.GetValueOrDefault(f.UserId)), Alias = _friendAliases.GetValueOrDefault(f.UserId) }).OrderByDescending(f => f.InExperience).ThenByDescending(f => f.CanJoin).ToArray();
    }
    public async Task<OwnedOutfitPage> GetOutfitPageAsync(string? cursor = null, CancellationToken token = default, Action<OwnedOutfit[]>? loaded = null)
    {
        if (cursor?.Length > 2048) throw new ArgumentException();
        await VerifyAsync(token);
        using var json = await ReadAsync($"https://avatar.roblox.com/v2/avatar/users/{expectedUserId}/outfits?itemsPerPage=50" + (cursor is null ? "" : "&paginationToken=" + Uri.EscapeDataString(cursor)), token);
        var rows = Data(json.RootElement).EnumerateArray().Where(o => RobloxGameService.Number(o, "id") > 0).Take(50)
            .Select(o => new OwnedOutfit(RobloxGameService.Number(o, "id"), RobloxGameService.String(o, "name"), o.TryGetProperty("isEditable", out var editable) && editable.ValueKind == JsonValueKind.True)).DistinctBy(o => o.Id).ToArray();
        foreach (var row in rows) _listedOutfits.Add(row.Id);
        loaded?.Invoke(rows);
        var next = RobloxGameService.NullableString(json.RootElement, "paginationToken");
        if (next?.Length > 2048 || next == cursor) next = null;
        try
        {
            if (rows.Length > 0)
            {
                using var thumbnails = await ReadAsync("https://thumbnails.roblox.com/v1/users/outfits?userOutfitIds=" + string.Join(",", rows.Select(o => o.Id)) + "&size=150x150&format=Png&isCircular=false", token);
                var images = Data(thumbnails.RootElement).EnumerateArray().Where(t => RobloxGameService.String(t, "state") == "Completed")
                    .GroupBy(t => RobloxGameService.Number(t, "targetId")).ToDictionary(g => g.Key, g => SafeImage(RobloxGameService.NullableString(g.Last(), "imageUrl")));
                rows = rows.Select(o => o with { ThumbnailUrl = images.GetValueOrDefault(o.Id) }).ToArray();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or RobloxApiException or JsonException or InvalidDataException or OperationCanceledException) { }
        return new(rows, next);
    }
    public async Task ApplyOutfitItemsAsync(long outfitId, CancellationToken token = default)
    {
        if (!_listedOutfits.Contains(outfitId)) throw new InvalidOperationException(L.T("Escolha um visual desta conta antes de aplicar."));
        // Recheck identity immediately before a write, even after a cached list.
        _verified = false; await VerifyAsync(token);
        using var definition = await ReadAsync($"https://avatar.roblox.com/v4/outfits/{outfitId}/details", token);
        if (!definition.RootElement.TryGetProperty("outfitModel", out var model) || model.ValueKind != JsonValueKind.Object
            || !model.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() is < 1 or > 50) throw new InvalidDataException(L.T("O Roblox retornou dados de visual inválidos."));
        if (assets.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.Object)) throw new InvalidDataException();
        var values = assets.EnumerateArray().Select(a => new { id = RobloxGameService.Number(a, "id"), meta = a.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object ? (JsonElement?)meta.Clone() : null }).ToArray();
        if (values.Any(a => a.id <= 0) || values.Select(a => a.id).Distinct().Count() != values.Length) throw new InvalidDataException();
        await WearItemsAsync(values.Select(a => new AvatarWearItem(a.id, a.meta)).ToArray(), token);
    }
    private static bool NonEmpty(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
    private static string? SafeImage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase) ? value : null;
    private static JsonElement Data(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data : throw new InvalidDataException(L.T("O Roblox retornou dados inesperados. Tente atualizar mais tarde."));
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using (request)
        {
            var uri = request.RequestUri!;
            if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || uri.Host is not ("users.roblox.com" or "friends.roblox.com" or "avatar.roblox.com" or "thumbnails.roblox.com" or "presence.roblox.com" or "games.roblox.com" or "inventory.roblox.com")) throw new ArgumentException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Headers.Contains("rblx-challenge-id")) throw new AccountVerificationRequiredException();
            if (!response.IsSuccessStatusCode) throw new RobloxApiException((int)response.StatusCode, response.Headers.RetryAfter?.Delta);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[16384]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) != 0)
            { if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException(); output.Write(buffer, 0, count); }
            return JsonDocument.Parse(output.ToArray());
        }
    }
}
