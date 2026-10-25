using System.Net;
using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task TestNativeSocialAsync()
    {
        var server = Guid.NewGuid().ToString();
        using (var http = new HttpClient(new ApiFixtureHandler(request => request.RequestUri!.Host switch
        {
            "users.roblox.com" => JsonResponse(new { id = 42 }),
            "thumbnails.roblox.com" => JsonResponse(new { data = new[] { new { targetId = 1, state = "Completed", imageUrl = "https://tr.rbxcdn.com/fixture.png" } } }),
            _ => JsonResponse(new { data = new object[] {
                new { id = 1, name = "Visible", displayName = "Visible", userPresence = new { placeId = 9, gameInstanceId = server, lastLocation = "Game" } },
                new { id = 2, userPresence = new { placeId = (long?)null, gameInstanceId = (string?)null } },
                new { id = 3, userPresence = new { placeId = "private", gameInstanceId = false } },
                new { id = 4, userPresence = (object?)null } } })
        })))
        {
            var friends = await new AccountSocialService(http, 42).GetOnlineFriendsAsync();
            Assert(friends.Length == 4 && friends[0].CanJoin && friends[0].ThumbnailUrl is not null && friends.Skip(1).All(f => !f.CanJoin), "Null/private presence does not discard the friend list or invent join access");
        }
        using (var http = new HttpClient(new ApiFixtureHandler(request => request.RequestUri!.Host switch
        {
            "users.roblox.com" => JsonResponse(new { id = 42 }),
            "thumbnails.roblox.com" => JsonResponse(new { data = Array.Empty<object>() }),
            "presence.roblox.com" => JsonResponse(new { userPresences = new[] { new { userId = 1, placeId = (long?)null, gameId = (string?)null } } }),
            _ => JsonResponse(new { data = new[] { new { id = 1, name = "Private", displayName = "Private" } } })
        })))
        {
            var friends = await new AccountSocialService(http, 42).GetFriendsAsync();
            Assert(friends.Length == 1 && !friends[0].CanJoin, "Native all-friends view respects withheld presence");
        }
        var writes = 0; var auth = 42; var failResult = false; var wrongReceipt = false; var challenge = false; var secondPage = false;
        using var handler = new ApiFixtureHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "users.roblox.com") return JsonResponse(new { id = auth });
            if (uri.Host == "thumbnails.roblox.com") return JsonResponse(new { data = new[] { new { targetId = 7, state = "Completed", imageUrl = "https://tr.rbxcdn.com/outfit.png" }, new { targetId = 8, state = "Pending", imageUrl = "https://invalid.example/ignored.png" } } });
            if (uri.AbsolutePath.EndsWith("/outfits"))
            {
                secondPage = uri.Query.Contains("paginationToken=next%2F%2B%3F", StringComparison.OrdinalIgnoreCase);
                return JsonResponse(new { data = new[] { new { id = 7, name = "Fixture outfit", isEditable = true }, new { id = 8, name = "Pending thumbnail", isEditable = false } }, paginationToken = secondPage ? "" : "next/+?" });
            }
            if (uri.AbsolutePath.EndsWith("/details")) return JsonResponse(new { outfitModel = new { assets = new[] { new { id = 10, meta = new { order = 1 } }, new { id = 20, meta = new { order = 2 } } } } });
            if (uri.AbsolutePath.EndsWith("/redraw-thumbnail")) return JsonResponse(new { });
            if (request.Method == HttpMethod.Post)
            {
                Assert(uri.AbsolutePath == "/v2/avatar/set-wearing-assets", "Outfit application never changes body settings or purchases items");
                writes++;
                if (challenge) { var r = new HttpResponseMessage(HttpStatusCode.Forbidden); r.Headers.Add("rblx-challenge-id", "fixture"); return r; }
                if (!request.Headers.Contains("x-csrf-token")) { var r = new HttpResponseMessage(HttpStatusCode.Forbidden); r.Headers.Add("x-csrf-token", "fixture"); return r; }
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                Assert(body.RootElement.GetProperty("assets")[0].GetProperty("meta").GetProperty("order").GetInt32() == 1, "Layered accessory metadata survives the request");
                return JsonResponse(new { success = !failResult, invalidAssets = Array.Empty<object>(), invalidAssetIds = Array.Empty<long>() });
            }
            if (uri.AbsolutePath.EndsWith("/currently-wearing")) return JsonResponse(new { assetIds = wrongReceipt ? new[] { 10 } : new[] { 10, 20 } });
            throw new Exception("Unexpected social route");
        });
        using var client = new HttpClient(handler); var service = new AccountSocialService(client, 42);
        await MustFailAsync<InvalidOperationException>(() => service.ApplyOutfitItemsAsync(7));
        Assert(handler.Requests == 0, "Unlisted outfits cannot trigger a request");
        var page = await service.GetOutfitPageAsync();
        Assert(page.Cursor == "next/+?" && page.Items[0].ThumbnailUrl is not null && page.Items[1].ThumbnailUrl is null, "Outfit thumbnails handle completed and pending render states");
        var last = await service.GetOutfitPageAsync(page.Cursor);
        Assert(secondPage && last.Cursor is null, "Outfits use the documented cursor, not a repeated first page");
        await service.ApplyOutfitItemsAsync(7);
        Assert(writes == 2 && (await service.GetOutfitPageAsync()).Items.Length == 2, "One CSRF retry, a verified appearance receipt and a reusable caller client");
        auth = 9; var previous = writes;
        await MustFailAsync<InvalidOperationException>(() => service.ApplyOutfitItemsAsync(7));
        Assert(writes == previous, "Identity is rechecked immediately before changing a previously listed outfit");
        auth = 42; failResult = true;
        await MustFailAsync<InvalidOperationException>(() => service.ApplyOutfitItemsAsync(7));
        failResult = false; wrongReceipt = true;
        await MustFailAsync<InvalidOperationException>(() => service.ApplyOutfitItemsAsync(7));
        wrongReceipt = false; challenge = true; previous = writes;
        await MustFailAsync<AccountVerificationRequiredException>(() => service.ApplyOutfitItemsAsync(7));
        Assert(writes == previous + 1, "A verification challenge is never retried automatically");
    }
}
