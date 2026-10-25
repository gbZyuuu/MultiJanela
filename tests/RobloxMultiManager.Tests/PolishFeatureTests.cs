using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task TestPolishedSocialAsync()
    {
        var isFriend = true; var alias = ""; var moderated = false; var auth = 42; var writes = 0;
        var unknownStatus = false; var numericStatus = false;
        var worn = new long[] { 10, 20 };
        using var handler = new ApiFixtureHandler(request =>
        {
            var uri = request.RequestUri!; var path = uri.AbsolutePath;
            if (path == "/v1/users/authenticated") return JsonResponse(new { id = auth });
            if (path.EndsWith("/friends")) return JsonResponse(new { data = isFriend ? new[] { new { id = 7, name = "Buddy", displayName = "Buddy" } } : [] });
            if (uri.Host == "presence.roblox.com") return JsonResponse(new { userPresences = new[] { new { userId = 7, placeId = 99, gameId = "12345678-1234-1234-1234-123456789012", universeId = 9, userPresenceType = 2, lastLocation = "Old name" } } });
            if (uri.Host == "games.roblox.com") return JsonResponse(new { data = new[] { new { id = 9, name = "Fixture experience" } } });
            if (uri.Host == "thumbnails.roblox.com") return JsonResponse(new { data = new[] { new { targetId = 9, state = "Completed", imageUrl = "https://tr.rbxcdn.com/game.png" } } });
            if (path.EndsWith("/friends/statuses")) return JsonResponse(new { data = new[] { new { id = 7, status = unknownStatus ? "Unknown" : numericStatus ? (object)(isFriend ? 1 : 0) : isFriend ? "Friends" : "NotFriends" } } });
            if (path == "/v1/user/get-tags") return JsonResponse(new[] { new { targetUserId = 7, targetUserTag = alias } });
            if (path == "/v1/user/tag")
            {
                writes++; using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                if (!moderated) alias = body.RootElement.GetProperty("userTag").GetString()!;
                return JsonResponse(new { status = moderated ? "Moderated" : "Success" });
            }
            if (path.EndsWith("/unfriend")) { writes++; isFriend = false; return JsonResponse(new { }); }
            if (path == "/v4/avatar") return JsonResponse(new { avatarModel = new { assets = new[] { new { id = 10, name = "Shirt", meta = new { order = 2 } }, new { id = 20, name = "Hair", meta = new { order = 3 } } } } });
            if (path.Contains("/inventory")) return JsonResponse(new { data = new[] { new { assetId = 30, name = "Hat" } }, nextPageCursor = (string?)null });
            if (path == "/v2/avatar/set-wearing-assets")
            {
                writes++; using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                worn = body.RootElement.GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("id").GetInt64()).ToArray();
                Assert(body.RootElement.GetProperty("assets")[0].GetProperty("meta").GetProperty("order").GetInt32() == 2, "Editor preserves existing accessory metadata");
                return JsonResponse(new { success = true, invalidAssets = Array.Empty<object>() });
            }
            if (path.EndsWith("/currently-wearing")) return JsonResponse(new { assetIds = worn });
            if (path.EndsWith("/redraw-thumbnail")) return JsonResponse(new { });
            throw new Exception("Unexpected polish API route: " + path);
        });
        using var http = new HttpClient(handler); var service = new AccountSocialService(http, 42);
        var friends = await service.GetFriendsAsync();
        Assert(friends[0].InExperience && friends[0].CanJoin && friends[0].ExperienceName == "Fixture experience" && friends[0].ExperienceIconUrl is not null, "Native friend cards enrich permitted experience metadata");
        Assert(await service.GetFriendAliasAsync(7) == "", "An unset Roblox nickname reads as empty");
        await service.SetFriendAliasAsync(7, "Best friend");
        Assert(alias == "Best friend", "Native nicknames persist through Roblox and are verified");
        numericStatus = true; Assert(await service.GetFriendAliasAsync(7) == "Best friend", "Numeric friendship receipts remain supported"); numericStatus = false;
        unknownStatus = true; Assert(await service.GetFriendAliasAsync(7) == "Best friend", "Unknown enum rechecks the fresh complete friendship roster");
        moderated = true; await MustFailAsync<InvalidOperationException>(() => service.SetFriendAliasAsync(7, "Rejected"));
        Assert(alias == "Best friend", "Moderated nickname does not report success");
        moderated = false; auth = 9; var before = writes;
        await MustFailAsync<InvalidOperationException>(() => service.RemoveFriendAsync(7));
        Assert(writes == before, "Wrong identity prevents friendship mutation");
        auth = 42; await service.RemoveFriendAsync(7); Assert(!isFriend, "Removal requires a nonfriend status receipt");
        await MustFailAsync<InvalidOperationException>(() => service.SetFriendAliasAsync(7, "No longer a friend"));
        var editor = new AccountSocialService(http, 42);
        var equipped = await editor.GetEquippedItemsAsync(); var inventory = await editor.GetAvatarInventoryAsync();
        Assert(equipped.Length == 2 && inventory.Items.Single().Id == 30, "Native editor loads current and owned items separately");
        before = writes; await MustFailAsync<InvalidOperationException>(() => editor.ApplyEditorItemsAsync([999])); Assert(writes == before, "Unknown avatar items cannot reach a mutation request");
        await editor.ApplyEditorItemsAsync([10, 30]); Assert(worn.SequenceEqual(new long[] { 10, 30 }), "Native editor applies and verifies only selected known items");
        using var limitedHttp = new HttpClient(new ApiFixtureHandler(_ => { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(45)); return response; }));
        try { await new RobloxGameService(limitedHttp).GetServersAsync(1); throw new Exception("Expected 429"); }
        catch (RobloxApiException ex) { Assert(ex.RetryAfter?.TotalSeconds is > 30 and < 60, "Server cooldown honors HTTP-date Retry-After headers"); }
        using var nullablePing = new HttpClient(new ApiFixtureHandler(_ => JsonResponse(new { data = new[] { new { id = Guid.NewGuid(), maxPlayers = 12, playing = 9, ping = (double?)null } }, nextPageCursor = (string?)null })));
        Assert((await new RobloxGameService(nullablePing).GetServersAsync(1)).Servers.Length == 1, "An optional null ping does not discard a valid server page");
    }
}
