using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace RobloxMultiManager.Core;

public enum AccountBatchAction { RequestFriendship, AcceptFriendship, JoinCommunity, LeaveCommunity, ApplyAvatar }
public enum AccountOperationState { Pending, Running, Completed, PendingApproval, VerificationRequired, Failed, Cancelled }
public sealed record AccountOperationResult(Guid ProfileId, AccountOperationState State, string Code);
public sealed record AvatarDefinition(Guid Id, string Name, long[] AssetIds);
public sealed record CatalogAsset(long Id, string Name, int AssetType, long CreatorId, long Price, Guid? CollectibleId = null)
{ public override string ToString() => Name; }
public sealed record AccountBatchPlan(AccountBatchAction Action, Guid[] Profiles, long TargetId = 0, AvatarDefinition? Avatar = null);
public sealed class AccountVerificationRequiredException : Exception { }

/// <summary>Own-account operations; a CSRF challenge is retried once, account verification is never automated.</summary>
public sealed class RobloxAccountOperations(HttpClient http, long expectedUserId, bool leaveOpen = false) : IDisposable
{
    public void Dispose() { if (!leaveOpen) http.Dispose(); }
    private readonly Dictionary<string,string> _csrf = new(StringComparer.OrdinalIgnoreCase);
    private bool _verified;
    private static readonly HashSet<string> Hosts = ["users.roblox.com","friends.roblox.com","groups.roblox.com","avatar.roblox.com","inventory.roblox.com","catalog.roblox.com","economy.roblox.com","presence.roblox.com","develop.roblox.com","apis.roblox.com","gamejoin.roblox.com","contacts.roblox.com","thumbnails.roblox.com"];
    public async Task VerifyAsync(CancellationToken token)
    {
        if (_verified) return;
        using var json = await SendAsync(HttpMethod.Get,"https://users.roblox.com/v1/users/authenticated",null,token);
        if (RobloxGameService.Number(json.RootElement,"id") != expectedUserId) throw new InvalidOperationException(L.T("A sessão não corresponde à conta selecionada."));
        _verified = true;
    }
    public async Task<JsonDocument> SendAsync(HttpMethod method,string url,object? body,CancellationToken token)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || !Hosts.Contains(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(method,uri);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (_csrf.TryGetValue(uri.Host,out var csrf)) request.Headers.TryAddWithoutValidation("x-csrf-token",csrf);
            using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if (response.Headers.Contains("rblx-challenge-id")) throw new AccountVerificationRequiredException();
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0 && method != HttpMethod.Get
                && response.Headers.TryGetValues("x-csrf-token",out var values) && values.FirstOrDefault() is { Length: >0 and <4096 } value)
            { _csrf[uri.Host] = value; continue; }
            if (!response.IsSuccessStatusCode) throw new RobloxApiException((int)response.StatusCode,response.Headers.RetryAfter?.Delta);
            if (response.Content.Headers.ContentLength > 4*1024*1024) throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token); using var data = new MemoryStream(); var buffer = new byte[16384]; int read;
            while ((read = await input.ReadAsync(buffer,timeout.Token)) > 0) { if (data.Length + read > 4*1024*1024) throw new InvalidDataException(); data.Write(buffer,0,read); }
            return JsonDocument.Parse(data.Length == 0 ? "{}" : System.Text.Encoding.UTF8.GetString(data.ToArray()));
        }
        throw new InvalidOperationException();
    }
    public async Task<AccountOperationState> ExecuteAsync(AccountBatchPlan plan,CancellationToken token)
    {
        await VerifyAsync(token);
        if (plan.Action is AccountBatchAction.RequestFriendship or AccountBatchAction.AcceptFriendship)
        {
            if (plan.TargetId <= 0 || plan.TargetId == expectedUserId) throw new ArgumentException();
            var action = plan.Action == AccountBatchAction.RequestFriendship ? "request-friendship" : "accept-friend-request";
            using var result = await SendAsync(HttpMethod.Post,$"https://friends.roblox.com/v1/users/{plan.TargetId}/{action}",new { },token);
            if(result.RootElement.TryGetProperty("isCaptchaRequired",out var captcha)&&captcha.ValueKind==JsonValueKind.True)throw new AccountVerificationRequiredException();
            if(result.RootElement.TryGetProperty("success",out var accepted)&&accepted.ValueKind==JsonValueKind.False)throw new InvalidOperationException();
            using var status=await SendAsync(HttpMethod.Get,$"https://friends.roblox.com/v1/users/{expectedUserId}/friends/statuses?userIds={plan.TargetId}",null,token);
            var receipt=status.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault(x=>RobloxGameService.Number(x,"id")==plan.TargetId);
            var value=FriendshipReceipt.Read(receipt);
            return value==1||plan.Action==AccountBatchAction.RequestFriendship&&value==2?AccountOperationState.Completed:AccountOperationState.PendingApproval;
        }
        if (plan.Action is AccountBatchAction.JoinCommunity or AccountBatchAction.LeaveCommunity)
        {
            if (plan.TargetId <= 0) throw new ArgumentException();
            var leaving = plan.Action == AccountBatchAction.LeaveCommunity;
            using var result = await SendAsync(leaving ? HttpMethod.Delete : HttpMethod.Post,
                $"https://groups.roblox.com/v1/groups/{plan.TargetId}/users" + (leaving ? "/" + expectedUserId : ""),leaving ? null : new { },token);
            using var memberships = await SendAsync(HttpMethod.Get,$"https://groups.roblox.com/v2/users/{expectedUserId}/groups/roles",null,token);
            var joined = memberships.RootElement.GetProperty("data").EnumerateArray().Any(x => x.TryGetProperty("group",out var group) && RobloxGameService.Number(group,"id") == plan.TargetId);
            return joined != leaving ? AccountOperationState.Completed : AccountOperationState.PendingApproval;
        }
        if(plan.Action!=AccountBatchAction.ApplyAvatar)throw new ArgumentException();
        var avatar = ValidateAvatar(plan.Avatar ?? throw new ArgumentException());
        foreach (var id in avatar.AssetIds)
        {
            using var owns = await SendAsync(HttpMethod.Get,$"https://inventory.roblox.com/v1/users/{expectedUserId}/items/Asset/{id}/is-owned",null,token);
            if (owns.RootElement.ValueKind != JsonValueKind.True)
            {
                await ClaimFreeAssetAsync(id,token);
                using var acquired = await SendAsync(HttpMethod.Get,$"https://inventory.roblox.com/v1/users/{expectedUserId}/items/Asset/{id}/is-owned",null,token);
                if(acquired.RootElement.ValueKind!=JsonValueKind.True)throw new InvalidOperationException(L.T("O Roblox não confirmou a obtenção gratuita."));
            }
        }
        using var applied = await SendAsync(HttpMethod.Post,"https://avatar.roblox.com/v2/avatar/set-wearing-assets",new { assets = avatar.AssetIds.Select(id => new { id }).ToArray() },token);
        using var actual = await SendAsync(HttpMethod.Get,$"https://avatar.roblox.com/v1/users/{expectedUserId}/avatar",null,token);
        var ids = actual.RootElement.GetProperty("assets").EnumerateArray().Select(a => RobloxGameService.Number(a,"id")).Order().ToArray();
        if (!ids.SequenceEqual(avatar.AssetIds.Order())) throw new InvalidOperationException(L.T("O avatar retornado não corresponde ao visual escolhido."));
        return AccountOperationState.Completed;
    }
    private async Task ClaimFreeAssetAsync(long id,CancellationToken token)
    {
        using var details = await SendAsync(HttpMethod.Get,$"https://economy.roblox.com/v2/assets/{id}/details",null,token); var item = details.RootElement;
        if (!item.TryGetProperty("PriceInRobux",out var price) || price.ValueKind != JsonValueKind.Number || price.GetInt64()!=0
            || !item.TryGetProperty("Creator",out var creator) || RobloxGameService.Number(creator,"Id") != 1
            || !item.TryGetProperty("IsForSale",out var sale) || sale.ValueKind != JsonValueKind.True) throw new InvalidOperationException(L.T("Somente itens oficiais gratuitos podem ser obtidos."));
        var product = RobloxGameService.Number(item,"ProductId"); if (product <= 0) throw new InvalidOperationException(L.T("Este item exige obtenção manual no catálogo oficial."));
        using var result = await SendAsync(HttpMethod.Post,$"https://economy.roblox.com/v1/purchases/products/{product}",new { expectedCurrency=1,expectedPrice=0,expectedSellerId=1 },token);
        if (!result.RootElement.TryGetProperty("purchased",out var purchased) || purchased.ValueKind != JsonValueKind.True) throw new InvalidOperationException(L.T("O Roblox não confirmou a obtenção gratuita."));
        await Task.Delay(TimeSpan.FromSeconds(7),token);
    }
    public static AvatarDefinition ValidateAvatar(AvatarDefinition value)
    {
        if (value.Id==Guid.Empty || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length>80 || value.AssetIds is not {Length:>0 and <=50} || value.AssetIds.Any(id=>id<=0) || value.AssetIds.Distinct().Count()!=value.AssetIds.Length) throw new InvalidDataException(); return value;
    }
    public async Task<JsonDocument> InventoryAsync(string? cursor,CancellationToken token)
    {
        if(cursor?.Length>2048)throw new ArgumentException();await VerifyAsync(token);
        return await SendAsync(HttpMethod.Get,$"https://inventory.roblox.com/v2/users/{expectedUserId}/inventory?assetTypes=TShirt,Hat,Shirt,Pants,HairAccessory,FaceAccessory,NeckAccessory,ShoulderAccessory,FrontAccessory,BackAccessory,WaistAccessory&limit=100&sortOrder=Desc"+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor)),null,token);
    }
    public async Task<AvatarDefinition> CurrentAvatarAsync(string name,CancellationToken token)
    {
        await VerifyAsync(token);using var json=await SendAsync(HttpMethod.Get,$"https://avatar.roblox.com/v1/users/{expectedUserId}/avatar",null,token);
        return ValidateAvatar(new(Guid.NewGuid(),name,json.RootElement.GetProperty("assets").EnumerateArray().Select(a=>RobloxGameService.Number(a,"id")).ToArray()));
    }
    public async Task<JsonDocument> PresenceAsync(long userId,CancellationToken token)
    { if(userId<=0)throw new ArgumentException();await VerifyAsync(token);return await SendAsync(HttpMethod.Post,"https://presence.roblox.com/v1/presence/users",new {userIds=new[]{userId}},token); }
    public async Task<JsonDocument> SubplacesAsync(long universe,string? cursor,CancellationToken token)
    { if(universe<=0||cursor?.Length>2048)throw new ArgumentException();await VerifyAsync(token);return await SendAsync(HttpMethod.Get,$"https://develop.roblox.com/v1/universes/{universe}/places?limit=100"+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor)),null,token); }
}

public sealed class AccountBatchRunner
{
    private int _running;
    public bool IsRunning => Volatile.Read(ref _running)!=0;
    public async Task RunAsync(AccountBatchPlan plan,Func<Guid,CancellationToken,Task<RobloxAccountOperations>> client,Action<AccountOperationResult> report,CancellationToken token,TimeSpan? interval=null)
    {
        if(plan.Profiles.Length is <1 or >2000 || plan.Profiles.Distinct().Count()!=plan.Profiles.Length || plan.TargetId<0)throw new ArgumentException();
        if(Interlocked.CompareExchange(ref _running,1,0)!=0)throw new InvalidOperationException(L.T("Já existe um lote em execução."));
        var next=0;
        foreach(var id in plan.Profiles)report(new(id,AccountOperationState.Pending,"pending"));
        try
        {
            for(;next<plan.Profiles.Length;)
            {
                token.ThrowIfCancellationRequested();var id=plan.Profiles[next];report(new(id,AccountOperationState.Running,"running"));
                try{using var api=await client(id,token);var result=await api.ExecuteAsync(plan,token);report(new(id,result,"accepted"));}
                catch(AccountVerificationRequiredException){report(new(id,AccountOperationState.VerificationRequired,"challenge"));}
                catch(RobloxApiException ex) when(ex.StatusCode==429){report(new(id,AccountOperationState.Failed,"rate-limit"));next++;break;}
                catch(OperationCanceledException) when(token.IsCancellationRequested){report(new(id,AccountOperationState.Cancelled,"cancelled"));next++;throw;}
                catch(Exception){report(new(id,AccountOperationState.Failed,"not-confirmed"));}
                next++;if(next<plan.Profiles.Length)await Task.Delay(interval??TimeSpan.FromMilliseconds(1800),token);
            }
        }
        finally{for(;next<plan.Profiles.Length;next++)report(new(plan.Profiles[next],AccountOperationState.Cancelled,"not-run"));Interlocked.Exchange(ref _running,0);}
    }
}
