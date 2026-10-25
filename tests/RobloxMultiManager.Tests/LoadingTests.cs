using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private sealed class LoadingHttp : HttpMessageHandler
    {
        public readonly ConcurrentDictionary<string,int> Calls = new();
        public bool Pending;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.AddOrUpdate(path,1,(_,n)=>n+1);
            await Task.Delay(35,token);
            object data = path switch
            {
                "/v1/users/authenticated" => new { id=42 },
                "/v4/avatar" => new { avatarModel = new { assets = new[]{new{id=10,name="Cap"}}, bodyColors = new { headColor="FFFFFF",torsoColor="FFFFFF",leftArmColor="FFFFFF",rightArmColor="FFFFFF",leftLegColor="FFFFFF",rightLegColor="FFFFFF"}, scales = new {height=1}, playerAvatarType="R15" } },
                "/v1/users/avatar-3d" => new {state = Pending ? "Pending" : "Completed", imageUrl="https://t0.rbxcdn.com/model"},
                _ => new { data=Array.Empty<object>() }
            };
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data),Encoding.UTF8,"application/json")};
        }
    }
    private static async Task TestAccountLoadingAsync()
    {
        using var handler = new LoadingHttp(); using var http = new HttpClient(handler);
        var service = new AccountSocialService(http,42);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(service.GetEquippedItemsAsync(), service.GetAvatarBodyAsync());
        var cold = clock.Elapsed.TotalMilliseconds;
        Assert(handler.Calls["/v1/users/authenticated"]==1 && handler.Calls["/v4/avatar"]==1,"Concurrent body and items share one verified avatar snapshot");
        clock.Restart(); await Task.WhenAll(service.GetEquippedItemsAsync(),service.GetAvatarBodyAsync());
        var warm = clock.Elapsed.TotalMilliseconds;
        Assert(handler.Calls["/v4/avatar"]==1,"Warm avatar reads perform no repeated HTTP request");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await MustFailAsync<OperationCanceledException>(()=>service.GetAvatarBodyAsync(canceled.Token));
        service.InvalidateReads(); await service.GetAvatarBodyAsync();
        Assert(handler.Calls["/v4/avatar"]==2,"Explicit refresh bypasses the previous avatar snapshot");
        var other = new AccountSocialService(http,99);
        await MustFailAsync<InvalidOperationException>(()=>other.GetAvatarBodyAsync());
        Assert(handler.Calls["/v4/avatar"]==2,"Cached account data cannot bypass a different account identity");
        handler.Pending=true; await service.GetCurrentAvatarRenderAsync(); await service.GetCurrentAvatarRenderAsync();
        Assert(handler.Calls["/v1/users/avatar-3d"]==2,"Pending 3D renders are never cached as completed previews");
        handler.Pending=false; await service.GetCurrentAvatarRenderAsync(); await service.GetCurrentAvatarRenderAsync();
        Assert(handler.Calls["/v1/users/avatar-3d"]==3,"Completed 3D metadata is reused until refresh");
        Console.WriteLine($"Loading fixture (35 ms/request): cold {cold:0} ms, warm {warm:0.0} ms; avatar snapshots 1 instead of 2.");
    }
}
