using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed record AvatarBody(Dictionary<string, string> Colors, Dictionary<string, double> Scales, int Type);
public sealed record AvatarScaleRule(double Min, double Max, double Increment);
public sealed record AvatarBodyRules(Dictionary<string, AvatarScaleRule> Scales, string[] Colors);
public sealed record AvatarRender(string State, string? Url);

public sealed partial class AccountSocialService
{
    private static readonly HashSet<int> EditorTypes = [2,8,11,12,17,18,27,28,29,30,31,41,42,43,44,45,46,47,64,65,66,67,68,69,70,71,72,79];
    private AvatarBodyRules? _bodyRules;
    private static readonly string[] BodyParts = ["head", "torso", "rightArm", "leftArm", "rightLeg", "leftLeg"];
    private async Task<JsonDocument> InventoryCategoryAsync(RobloxAccountOperations operations, int type, string? cursor, CancellationToken token)
    {
        if (!EditorTypes.Contains(type)) throw new ArgumentException();
        await operations.VerifyAsync(token);
        return await operations.SendAsync(HttpMethod.Get, $"https://inventory.roblox.com/v2/users/{expectedUserId}/inventory/{type}?limit=100&sortOrder=Desc" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), null, token);
    }
    public async Task<AvatarBody> GetAvatarBodyAsync(CancellationToken token = default)
    {
        await VerifyAsync(token);
        using var json = await ReadAsync("https://avatar.roblox.com/v4/avatar?selectionTypes=0&selectionTypes=1&selectionTypes=2&selectionTypes=3&selectionTypes=4&selectionTypes=5&selectionTypes=6", token);
        var model = json.RootElement.GetProperty("avatarModel");
        var colors = model.GetProperty("bodyColors"); var scales = model.GetProperty("scales");
        return new(BodyParts.ToDictionary(p => p + "Color", p => NormalizeAvatarColor(colors.GetProperty(p + "Color").GetString())), scales.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble()), ReadAvatarType(model.GetProperty("playerAvatarType")));
    }
    public static string NormalizeAvatarColor(string? color)
    {
        if (!Regex.IsMatch(color ?? "", "^#?[a-fA-F0-9]{6}$")) throw new InvalidDataException();
        return "#" + color!.TrimStart('#').ToUpperInvariant();
    }
    private static int ReadAvatarType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.GetString() == "R6" => 1,
        JsonValueKind.String when value.GetString() == "R15" => 3,
        JsonValueKind.Number when value.TryGetInt32(out var type) && type is 1 or 3 => type,
        _ => throw new InvalidDataException()
    };
    public async Task<string?> GetFullBodyThumbnailAsync(CancellationToken token = default)
    {
        using var json = await ReadAsync($"https://thumbnails.roblox.com/v1/users/avatar?userIds={expectedUserId}&size=420x420&format=Png&isCircular=false", token);
        return Data(json.RootElement).EnumerateArray().Where(row => RobloxGameService.Number(row, "targetId") == expectedUserId && RobloxGameService.String(row, "state") == "Completed").Select(row => SafeImage(RobloxGameService.NullableString(row, "imageUrl"))).FirstOrDefault();
    }
    public async Task<AvatarRender> GetCurrentAvatarRenderAsync(CancellationToken token = default)
    {
        await VerifyAsync(token);
        using var json = await ReadAsync($"https://thumbnails.roblox.com/v1/users/avatar-3d?userId={expectedUserId}", token);
        return new(RobloxGameService.String(json.RootElement, "state"), SafeImage(RobloxGameService.NullableString(json.RootElement, "imageUrl")));
    }
    public async Task<AvatarBodyRules> GetAvatarRulesAsync(CancellationToken token = default)
    {
        if (_bodyRules is not null) return _bodyRules;
        using var json = await ReadAsync("https://avatar.roblox.com/v1/avatar-rules", token);
        var rules = json.RootElement;
        return _bodyRules = new(rules.GetProperty("scales").EnumerateObject().ToDictionary(p => p.Name, p => new AvatarScaleRule(p.Value.GetProperty("min").GetDouble(), p.Value.GetProperty("max").GetDouble(), p.Value.GetProperty("increment").GetDouble())), rules.GetProperty("bodyColorsPalette").EnumerateArray().Select(c => RobloxGameService.String(c, "hexColor")).Where(c => Regex.IsMatch(c, "^#?[a-fA-F0-9]{6}$")).Select(NormalizeAvatarColor).Distinct().ToArray());
    }
    public async Task ApplyBodyAsync(AvatarBody body, CancellationToken token = default)
    {
        InvalidateReads();
        var rules = await GetAvatarRulesAsync(token);
        var current = await GetAvatarBodyAsync(token);
        if (body.Type is not (1 or 3) || body.Colors.Count != BodyParts.Length || body.Scales.Count != current.Scales.Count || body.Scales.Any(p => !rules.Scales.ContainsKey(p.Key) && (!current.Scales.TryGetValue(p.Key, out var old) || old != p.Value)) || BodyParts.Any(p => !body.Colors.TryGetValue(p + "Color", out var c) || !Regex.IsMatch(c, "^#[a-fA-F0-9]{6}$")) || rules.Scales.Any(p => !body.Scales.TryGetValue(p.Key, out var s) || !double.IsFinite(s) || s < p.Value.Min || s > p.Value.Max)) throw new ArgumentException();
        using var operations = new RobloxAccountOperations(http, expectedUserId, leaveOpen: true);
        await operations.VerifyAsync(token);
        if (current.Colors.Any(p => !string.Equals(body.Colors.GetValueOrDefault(p.Key), p.Value, StringComparison.OrdinalIgnoreCase)))
        {
            using var colors = await operations.SendAsync(HttpMethod.Post, "https://avatar.roblox.com/v2/avatar/set-body-colors", body.Colors.ToDictionary(p => p.Key + "3", p => p.Value.TrimStart('#')), token);
            RequireSuccess(colors.RootElement);
        }
        if (current.Scales.Any(p => !body.Scales.TryGetValue(p.Key, out var value) || Math.Abs(value-p.Value) > .0001))
        {
            using var scales = await operations.SendAsync(HttpMethod.Post, "https://avatar.roblox.com/v1/avatar/set-scales", body.Scales, token);
            RequireSuccess(scales.RootElement);
        }
        if (current.Type != body.Type)
        {
            using var type = await operations.SendAsync(HttpMethod.Post, "https://avatar.roblox.com/v1/avatar/set-player-avatar-type", new { playerAvatarType = body.Type }, token);
            RequireSuccess(type.RootElement);
        }
        InvalidateReads();
        var actual = await GetAvatarBodyAsync(token);
        if (actual.Type != body.Type || body.Colors.Any(p => !string.Equals(actual.Colors.GetValueOrDefault(p.Key), p.Value, StringComparison.OrdinalIgnoreCase)) || body.Scales.Any(p => !actual.Scales.TryGetValue(p.Key, out var v) || Math.Abs(v-p.Value) > .001)) throw new InvalidOperationException(L.T("O Roblox ainda não confirmou todas as alterações. Atualize o avatar antes de tentar novamente."));
    }
    private static void RequireSuccess(JsonElement result)
    { if (!result.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True) throw new InvalidOperationException(L.T("O Roblox não confirmou a alteração.")); }
    public async Task<AvatarRender> RenderAvatarAsync(IEnumerable<long> ids, AvatarBody body, CancellationToken token = default)
    {
        var chosen = ids.Distinct().ToArray();
        if (chosen.Length > 50 || chosen.Any(id => !_knownAvatarItems.ContainsKey(id))) throw new ArgumentException();
        using var operations = new RobloxAccountOperations(http, expectedUserId, leaveOpen: true);
        await operations.VerifyAsync(token);
        using var response = await operations.SendAsync(HttpMethod.Post, "https://avatar.roblox.com/v1/avatar/render", new { thumbnailConfig = new { thumbnailId = 1, size = "420x420", thumbnailType = "3d" }, avatarDefinition = new { scales = body.Scales, bodyColors = body.Colors.ToDictionary(p => p.Key, p => p.Value.TrimStart('#')), playerAvatarType = new { playerAvatarType = body.Type == 1 ? "R6" : "R15" }, assets = chosen.Select(id => _knownAvatarItems[id]).ToArray() } }, token);
        var state = RobloxGameService.String(response.RootElement, "state");
        var url = RobloxGameService.NullableString(response.RootElement, "imageUrl");
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase) || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo))) throw new InvalidDataException();
        return new(state, url);
    }
}
