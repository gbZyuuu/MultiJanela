using System.Text.Json;

namespace RobloxMultiManager.Core;

/// <summary>Legacy endpoints serialize this enum as either a name or a number.</summary>
internal static class FriendshipReceipt
{
    public static int? Read(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("status", out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number is >= 0 and <= 3) return number;
        return value.ValueKind == JsonValueKind.String ? value.GetString() switch
        {
            "NotFriends" => 0, "Friends" => 1, "RequestSent" => 2, "RequestReceived" => 3, _ => null
        } : null;
    }
}
