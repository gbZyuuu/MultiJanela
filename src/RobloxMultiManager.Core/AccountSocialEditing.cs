using System.Net.Http.Json;
using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record AvatarEditorItem(long Id, string Name, string? ThumbnailUrl = null, int AssetType = 0);
public sealed record AvatarInventoryPage(AvatarEditorItem[] Items, string? Cursor);
public sealed record AvatarWearItem(long id, JsonElement? meta);

public sealed partial class AccountSocialService
{
    private readonly HashSet<long> _knownFriends = [];
    private readonly Dictionary<long, string> _friendAliases = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, AvatarWearItem> _knownAvatarItems = new();
    private DateTime _enrichmentRetryAt;
    private async Task<OnlineFriend[]> EnrichFriendsAsync(OnlineFriend[] rows, CancellationToken token)
    {
        if (DateTime.UtcNow < _enrichmentRetryAt) return rows;
        try
        {
            var missing = rows.Where(f => string.IsNullOrWhiteSpace(f.Username)).Take(200).Select(f => f.UserId).ToArray();
            foreach (var batch in missing.Chunk(100))
            {
                using var names = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/users") { Content = JsonContent.Create(new { userIds = batch }) }, token);
                var users = Data(names.RootElement).EnumerateArray().GroupBy(u => RobloxGameService.Number(u, "id")).ToDictionary(g => g.Key, g => g.Last());
                rows = rows.Select(f => users.TryGetValue(f.UserId, out var user) ? f with { Username = RobloxGameService.String(user, "name"), DisplayName = RobloxGameService.String(user, "displayName") } : f).ToArray();
            }
            var ids = rows.Where(f => f.InExperience && f.UniverseId is > 0).Select(f => f.UniverseId!.Value).Distinct().Take(100).ToArray();
            if (ids.Length > 0)
            {
                using var games = await ReadAsync("https://games.roblox.com/v1/games?universeIds=" + string.Join(",", ids), token);
                var names = Data(games.RootElement).EnumerateArray().GroupBy(g => RobloxGameService.Number(g, "id")).ToDictionary(g => g.Key, g => RobloxGameService.String(g.Last(), "name"));
                rows = rows.Select(f => f with { ExperienceName = f.UniverseId is { } universe ? names.GetValueOrDefault(universe) : null }).ToArray();
                using var icons = await ReadAsync("https://thumbnails.roblox.com/v1/games/icons?universeIds=" + string.Join(",", ids) + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", token);
                var images = Data(icons.RootElement).EnumerateArray().Where(i => RobloxGameService.String(i, "state") == "Completed").GroupBy(i => RobloxGameService.Number(i, "targetId")).ToDictionary(g => g.Key, g => SafeImage(RobloxGameService.NullableString(g.Last(), "imageUrl")));
                rows = rows.Select(f => f with { ExperienceIconUrl = f.UniverseId is { } universe ? images.GetValueOrDefault(universe) : null }).ToArray();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (RobloxApiException ex) { if (ex.StatusCode == 429) _enrichmentRetryAt = DateTime.UtcNow + (ex.RetryAfter ?? TimeSpan.FromMinutes(1)); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or OperationCanceledException) { }
        return rows;
    }
    private async Task RequireFriendAsync(long userId, RobloxAccountOperations operations, CancellationToken token)
    {
        if (!_knownFriends.Contains(userId) || userId == expectedUserId) throw new InvalidOperationException(L.T("Escolha um amigo desta conta."));
        await operations.VerifyAsync(token);
        using var status = await operations.SendAsync(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{expectedUserId}/friends/statuses?userIds={userId}", null, token);
        if (!Data(status.RootElement).EnumerateArray().Any(f => RobloxGameService.Number(f, "id") == userId && FriendshipReceipt.Read(f) == 1)
            && !await ConfirmFriendListAsync(userId, operations, token)) throw new InvalidOperationException(L.T("O Roblox não confirmou esta amizade. Atualize a lista."));
    }
    private async Task<bool> ConfirmFriendListAsync(long userId, RobloxAccountOperations operations, CancellationToken token)
    {
        using var list = await operations.SendAsync(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{expectedUserId}/friends", null, token);
        var rows = Data(list.RootElement);
        // This endpoint returns the full roster. Do not interpret truncated/paged data as absence.
        if (rows.GetArrayLength() > 2000 || !string.IsNullOrEmpty(RobloxGameService.NullableString(list.RootElement, "nextPageCursor"))) throw new InvalidDataException();
        return rows.EnumerateArray().Any(row => RobloxGameService.Number(row, "id") == userId);
    }
    public async Task RemoveFriendAsync(long userId, CancellationToken token = default)
    {
        using var operations = new RobloxAccountOperations(http, expectedUserId, leaveOpen: true);
        await RequireFriendAsync(userId, operations, token);
        InvalidateReads();
        using var response = await operations.SendAsync(HttpMethod.Post, $"https://friends.roblox.com/v1/users/{userId}/unfriend", new { }, token);
        using var status = await operations.SendAsync(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{expectedUserId}/friends/statuses?userIds={userId}", null, token);
        if (!Data(status.RootElement).EnumerateArray().Any(f => RobloxGameService.Number(f, "id") == userId && FriendshipReceipt.Read(f) == 0)
            && await ConfirmFriendListAsync(userId, operations, token)) throw new InvalidOperationException(L.T("O Roblox ainda não confirmou a remoção. Atualize antes de tentar novamente."));
        _knownFriends.Remove(userId); _friendAliases.Remove(userId); InvalidateReads();
    }
    public async Task<string> GetFriendAliasAsync(long userId, CancellationToken token = default)
    {
        using var operations = new RobloxAccountOperations(http, expectedUserId, leaveOpen: true);
        await RequireFriendAsync(userId, operations, token);
        return await ReadAliasAsync(userId, operations, token);
    }
    private static async Task<string> ReadAliasAsync(long userId, RobloxAccountOperations operations, CancellationToken token)
    {
        using var tags = await operations.SendAsync(HttpMethod.Post, "https://contacts.roblox.com/v1/user/get-tags", new { targetUserIds = new[] { userId } }, token);
        if (tags.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        return tags.RootElement.EnumerateArray().Where(t => RobloxGameService.Number(t, "targetUserId") == userId).Select(t => RobloxGameService.String(t, "targetUserTag")).FirstOrDefault() ?? "";
    }
    public async Task SetFriendAliasAsync(long userId, string alias, CancellationToken token = default)
    {
        alias = alias.Trim(); if (alias.Length > 50) throw new ArgumentException();
        using var operations = new RobloxAccountOperations(http, expectedUserId, leaveOpen: true);
        await RequireFriendAsync(userId, operations, token);
        InvalidateReads();
        using var response = await operations.SendAsync(HttpMethod.Post, "https://contacts.roblox.com/v1/user/tag", new { targetUserId = userId, userTag = alias }, token);
        if (RobloxGameService.String(response.RootElement, "status") != "Success") throw new InvalidOperationException(L.T("O Roblox não aceitou este apelido."));
        var actual = await ReadAliasAsync(userId, operations, token);
        if (actual != alias) throw new InvalidOperationException(L.T("O Roblox ainda não confirmou o apelido. Atualize antes de tentar novamente."));
        _friendAliases[userId] = actual; InvalidateReads();
    }
    public async Task<AvatarEditorItem[]> GetEquippedItemsAsync(CancellationToken token = default)
    {
        await VerifyAsync(token);
        using var document = await ReadAsync("https://avatar.roblox.com/v4/avatar?selectionTypes=0&selectionTypes=1&selectionTypes=2&selectionTypes=3&selectionTypes=4&selectionTypes=5&selectionTypes=6", token);
        if (!document.RootElement.TryGetProperty("avatarModel", out var model) || !model.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var items = new List<AvatarEditorItem>();
        foreach (var asset in assets.EnumerateArray().Take(50))
        {
            var id = RobloxGameService.Number(asset, "id"); if (id <= 0) continue;
            _knownAvatarItems[id] = new(id, asset.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object ? meta.Clone() : null);
            var name = RobloxGameService.String(asset, "name"); items.Add(new(id, string.IsNullOrWhiteSpace(name) ? L.F("Item {0}", id) : name));
        }
        return await AddItemImagesAsync(items.ToArray(), token);
    }
    public async Task<AvatarInventoryPage> GetAvatarInventoryAsync(string? cursor = null, CancellationToken token = default, int assetType = 0)
    {
        if (cursor?.Length > 2048) throw new ArgumentException();
        await VerifyAsync(token);
        if (assetType != 0 && !EditorTypes.Contains(assetType)) throw new ArgumentException();
        var path = assetType == 0 ? "?assetTypes=TShirt,Hat,Shirt,Pants,HairAccessory,FaceAccessory,NeckAccessory,ShoulderAccessory,FrontAccessory,BackAccessory,WaistAccessory&" : $"/{assetType}?";
        using var inventory = await ReadAsync($"https://inventory.roblox.com/v2/users/{expectedUserId}/inventory" + path + "limit=100&sortOrder=Desc" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), token);
        var items = Data(inventory.RootElement).EnumerateArray().Select(i => new AvatarEditorItem(RobloxGameService.Number(i, "assetId"), RobloxGameService.String(i, "name"), AssetType: assetType)).Where(i => i.Id > 0).Take(100).ToArray();
        foreach (var item in items) _knownAvatarItems.TryAdd(item.Id, new(item.Id, null));
        return new(await AddItemImagesAsync(items, token), RobloxGameService.NullableString(inventory.RootElement, "nextPageCursor"));
    }
    private async Task<AvatarEditorItem[]> AddItemImagesAsync(AvatarEditorItem[] rows, CancellationToken token)
    {
        if (rows.Length == 0) return rows;
        try
        {
            using var thumbs = await ReadAsync("https://thumbnails.roblox.com/v1/assets?assetIds=" + string.Join(",", rows.Select(i => i.Id).Distinct()) + "&size=150x150&format=Png&isCircular=false", token);
            var images = Data(thumbs.RootElement).EnumerateArray().Where(i => RobloxGameService.String(i, "state") == "Completed").GroupBy(i => RobloxGameService.Number(i, "targetId")).ToDictionary(g => g.Key, g => SafeImage(RobloxGameService.NullableString(g.Last(), "imageUrl")));
            return rows.Select(i => i with { ThumbnailUrl = images.GetValueOrDefault(i.Id) }).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException or RobloxApiException or OperationCanceledException) { return rows; }
    }
    public async Task ApplyEditorItemsAsync(IEnumerable<long> ids, CancellationToken token = default)
    {
        var chosen = ids.Distinct().ToArray();
        if (chosen.Length is < 1 or > 50 || chosen.Any(id => !_knownAvatarItems.ContainsKey(id))) throw new InvalidOperationException(L.T("Escolha apenas itens do inventário desta conta."));
        await WearItemsAsync(chosen.Select(id => _knownAvatarItems[id]).ToArray(), token);
    }
    private async Task WearItemsAsync(AvatarWearItem[] values, CancellationToken token)
    {
        using var operations = new RobloxAccountOperations(http, expectedUserId, leaveOpen: true);
        await operations.VerifyAsync(token);
        InvalidateReads();
        using var applied = await operations.SendAsync(HttpMethod.Post, "https://avatar.roblox.com/v2/avatar/set-wearing-assets", new { assets = values }, token);
        var result = applied.RootElement;
        if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || NonEmpty(result, "invalidAssets") || NonEmpty(result, "invalidAssetIds")) throw new InvalidOperationException(L.T("O Roblox não confirmou a aplicação de todos os itens."));
        using var actual = await ReadAsync($"https://avatar.roblox.com/v1/users/{expectedUserId}/currently-wearing", token);
        if (!actual.RootElement.TryGetProperty("assetIds", out var worn) || worn.ValueKind != JsonValueKind.Array || !worn.EnumerateArray().Select(a => a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var id) ? id : 0).Order().SequenceEqual(values.Select(a => a.id).Order()))
            throw new InvalidOperationException(L.T("O Roblox ainda não confirmou os itens no avatar. Atualize antes de tentar novamente."));
        try { using var redraw = await operations.SendAsync(HttpMethod.Post, "https://avatar.roblox.com/v1/avatar/redraw-thumbnail", new { }, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is RobloxApiException or AccountVerificationRequiredException or HttpRequestException or OperationCanceledException or JsonException) { }
    }
}
