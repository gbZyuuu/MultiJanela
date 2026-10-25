using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record CommunityRow(long Id, string Name, string Role, long Members, string? Icon = null);
public sealed record CommunityRole(long Id, string Name, int Rank, long Members);
public sealed record CommunityMember(long Id, string Name, string DisplayName, long RoleId, string Role, int Rank)
{ public string Caption => string.IsNullOrWhiteSpace(DisplayName) ? Name : $"{DisplayName} (@{Name})"; }
public sealed record CommunityPage<T>(T[] Items, string? Cursor);
public sealed record CommunityDetails(long Id, string Name, string Description, string Shout, long Members, string Owner, int Rank,
    bool Joined, bool Pending, bool Locked, bool CanViewMembers, bool CanShout, bool CanManageRequests, bool CanChangeRank, bool CanRemove, bool IsOwner);

/// <summary>Account-scoped community operations with fresh permission and receipt checks.</summary>
public sealed class CommunityService(HttpClient http, long user)
{
    private const string Base = "https://groups.roblox.com";
    private static long N(JsonElement e, string key) => RobloxGameService.Number(e, key);
    private static string S(JsonElement e, string key) => RobloxGameService.String(e, key);
    private static bool B(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static JsonElement O(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v : default;
    private static JsonElement A(JsonElement e, string key = "data") => O(e,key) is { ValueKind: JsonValueKind.Array } a ? a : throw new InvalidDataException();
    private static string Page(string? cursor)
    { if(cursor?.Length > 2048)throw new ArgumentException();return cursor is null ? "" : "&cursor="+Uri.EscapeDataString(cursor); }
    private static string? Next(JsonElement e, string? previous)
    { var next=RobloxGameService.NullableString(e,"nextPageCursor");if(next?.Length>2048)throw new InvalidDataException();return string.IsNullOrEmpty(next)||next==previous?null:next; }
    private static void Valid(long id) { if(id<=0)throw new ArgumentException(); }
    private async Task<JsonDocument> ReadAsync(string path, CancellationToken token)
    { using var api=new RobloxAccountOperations(http,user,leaveOpen:true);await api.VerifyAsync(token);return await api.SendAsync(HttpMethod.Get,Base+path,null,token); }
    public async Task<CommunityRow[]> MineAsync(CancellationToken token=default)
    {
        using var json=await ReadAsync($"/v2/users/{user}/groups/roles",token);
        return await IconsAsync(A(json.RootElement).EnumerateArray().Take(2000).Select(row=>{var g=O(row,"group");return new CommunityRow(N(g,"id"),S(g,"name"),S(O(row,"role"),"name"),N(g,"memberCount"));}).Where(g=>g.Id>0).ToArray(),token);
    }
    public async Task<CommunityPage<CommunityRow>> SearchAsync(string keyword,string? cursor=null,CancellationToken token=default)
    {
        keyword=keyword.Trim();if(keyword.Length is <1 or >100)throw new ArgumentException();
        using var json=await ReadAsync("/v1/groups/search?keyword="+Uri.EscapeDataString(keyword)+"&prioritizeExactMatch=true&limit=25"+Page(cursor),token);
        var rows=A(json.RootElement).EnumerateArray().Take(25).Select(g=>new CommunityRow(N(g,"id"),S(g,"name"),"",N(g,"memberCount"))).Where(g=>g.Id>0).ToArray();
        return new(await IconsAsync(rows,token),Next(json.RootElement,cursor));
    }
    private async Task<CommunityRow[]> IconsAsync(CommunityRow[] rows,CancellationToken token)
    {
        if(rows.Length==0)return rows;
        try
        {
            using var api=new RobloxAccountOperations(http,user,leaveOpen:true);
            using var icons=await api.SendAsync(HttpMethod.Get,"https://thumbnails.roblox.com/v1/groups/icons?groupIds="+string.Join(',',rows.Take(100).Select(r=>r.Id))+"&size=150x150&format=Png&isCircular=false",null,token);
            var images=A(icons.RootElement).EnumerateArray().Where(i=>S(i,"state")=="Completed").GroupBy(i=>N(i,"targetId")).ToDictionary(g=>g.Key,g=>S(g.First(),"imageUrl"));
            return rows.Select(r=>images.TryGetValue(r.Id,out var image)&&Uri.TryCreate(image,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.Host.EndsWith(".rbxcdn.com",StringComparison.OrdinalIgnoreCase)&&uri.Port==443&&string.IsNullOrEmpty(uri.UserInfo)?r with {Icon=image}:r).ToArray();
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){throw;}
        catch(Exception ex)when(ex is RobloxApiException or HttpRequestException or JsonException or InvalidDataException or OperationCanceledException){return rows;}
    }
    public async Task<CommunityDetails> DetailsAsync(long id,CancellationToken token=default)
    {
        Valid(id);using var json=await ReadAsync($"/v1/groups/{id}",token);using var membership=await ReadAsync($"/v1/groups/{id}/membership?includeNotificationPreferences=false",token);
        var g=json.RootElement;var m=membership.RootElement;var role=O(O(m,"userRole"),"role");var permissions=O(m,"permissions");var posts=O(permissions,"groupPostsPermissions");var members=O(permissions,"groupMembershipPermissions");
        var rank=(int)N(role,"rank");var owner=B(m,"isOwner");var locked=B(g,"isLocked")||B(g,"isDeactivated");
        return new(id,S(g,"name"),S(g,"description"),S(O(g,"shout"),"body"),N(g,"memberCount"),S(O(g,"owner"),"username"),rank,rank>0,B(m,"isPendingJoin"),locked,B(m,"canViewMemberList"),!locked&&(owner||B(posts,"postToStatus")),!locked&&(owner||B(members,"inviteMembers")),!locked&&(owner||B(members,"changeRank")),!locked&&(owner||B(members,"removeMembers")),owner&&!locked);
    }
    public async Task<CommunityRole[]> RolesAsync(long id,CancellationToken token=default)
    {
        Valid(id);using var json=await ReadAsync($"/v1/groups/{id}/roles?includePrivate=false",token);
        return A(json.RootElement,"roles").EnumerateArray().Select(r=>new CommunityRole(N(r,"id"),S(r,"name"),(int)N(r,"rank"),N(r,"memberCount"))).ToArray();
    }
    public async Task<CommunityPage<CommunityMember>> MembersAsync(long id,string? cursor=null,CancellationToken token=default)
    {
        Valid(id);using var json=await ReadAsync($"/v1/groups/{id}/users?limit=50&sortOrder=Asc"+Page(cursor),token);
        return new(A(json.RootElement).EnumerateArray().Take(50).Select(row=>{var u=O(row,"user");var role=O(row,"role");return new CommunityMember(N(u,"userId"),S(u,"username"),S(u,"displayName"),N(role,"id"),S(role,"name"),(int)N(role,"rank"));}).ToArray(),Next(json.RootElement,cursor));
    }
    public async Task<CommunityPage<CommunityMember>> RequestsAsync(long id,string? cursor=null,CancellationToken token=default)
    {
        Valid(id);using var json=await ReadAsync($"/v1/groups/{id}/join-requests?limit=50&sortOrder=Desc"+Page(cursor),token);
        return new(A(json.RootElement).EnumerateArray().Take(50).Select(row=>{var u=O(row,"requester");return new CommunityMember(N(u,"userId"),S(u,"username"),S(u,"displayName"),0,"",0);}).ToArray(),Next(json.RootElement,cursor));
    }
    public async Task ChangeMembershipAsync(long id,bool leave,CancellationToken token=default)
    { Valid(id);using var api=new RobloxAccountOperations(http,user,leaveOpen:true);await api.ExecuteAsync(new AccountBatchPlan(leave?AccountBatchAction.LeaveCommunity:AccountBatchAction.JoinCommunity,[],id),token); }
    public async Task SetTextAsync(long id,string text,bool description,CancellationToken token=default)
    {
        Valid(id);text=text.Trim();if(text.Length>(description?1000:255))throw new ArgumentException();
        var details=await DetailsAsync(id,token);if(!(description?details.IsOwner:details.CanShout))throw new InvalidOperationException(L.T("Sua conta não tem permissão para esta ação."));
        using var api=new RobloxAccountOperations(http,user,leaveOpen:true);await api.VerifyAsync(token);
        using var response=await api.SendAsync(HttpMethod.Patch,Base+$"/v1/groups/{id}/"+(description?"description":"status"),description?new{description=text}:(object)new{message=text},token);
        var receipt=await DetailsAsync(id,token);
        if((description?receipt.Description:receipt.Shout)!=text)throw new InvalidOperationException(L.T("O Roblox ainda não confirmou a alteração. Atualize antes de tentar novamente."));
    }
    public async Task RespondRequestAsync(long id,long target,bool accept,CancellationToken token=default)
    {
        Valid(target);var details=await DetailsAsync(id,token);
        if(target!=user&&!details.CanManageRequests||target==user&&accept)throw new InvalidOperationException(L.T("Sua conta não tem permissão para esta ação."));
        using var api=new RobloxAccountOperations(http,user,leaveOpen:true);await api.VerifyAsync(token);
        using var response=await api.SendAsync(accept?HttpMethod.Post:HttpMethod.Delete,Base+$"/v1/groups/{id}/join-requests/users/{target}",null,token);
        // Read the exact request after a mutation. No automatic retry of writes.
        try { using var remaining=await api.SendAsync(HttpMethod.Get,Base+$"/v1/groups/{id}/join-requests/users/{target}",null,token);if(remaining.RootElement.ValueKind!=JsonValueKind.Null)throw new InvalidOperationException(L.T("O Roblox ainda não confirmou a alteração. Atualize antes de tentar novamente.")); }
        catch(RobloxApiException e)when(e.StatusCode==404) { }
        if(accept)
        {
            using var memberships=await ReadAsync($"/v2/users/{target}/groups/roles",token);
            if(!A(memberships.RootElement).EnumerateArray().Any(row=>N(O(row,"group"),"id")==id&&N(O(row,"role"),"rank")>0))
                throw new InvalidOperationException(L.T("O Roblox ainda não confirmou a alteração. Atualize antes de tentar novamente."));
        }
    }
    public async Task ChangeMemberAsync(long id,long target,long? roleId,CancellationToken token=default)
    {
        Valid(target);var details=await DetailsAsync(id,token);
        if(target==user||!(roleId.HasValue?details.CanChangeRank:details.CanRemove))throw new InvalidOperationException(L.T("Sua conta não tem permissão para esta ação."));
        using var targetGroups=await ReadAsync($"/v2/users/{target}/groups/roles",token);
        var before=A(targetGroups.RootElement).EnumerateArray().FirstOrDefault(r=>N(O(r,"group"),"id")==id);
        if(before.ValueKind!=JsonValueKind.Object||N(O(before,"role"),"rank")>=details.Rank)throw new InvalidOperationException(L.T("Sua conta não tem permissão para esta ação."));
        if(roleId is {} selected && !(await RolesAsync(id,token)).Any(r=>r.Id==selected&&r.Rank>0&&r.Rank<details.Rank))throw new ArgumentException();
        using var api=new RobloxAccountOperations(http,user,leaveOpen:true);await api.VerifyAsync(token);
        using var response=await api.SendAsync(roleId.HasValue?HttpMethod.Patch:HttpMethod.Delete,Base+$"/v1/groups/{id}/users/{target}",roleId.HasValue?new{roleId=roleId.Value}:null,token);
        using var groups=await ReadAsync($"/v2/users/{target}/groups/roles",token);var row=A(groups.RootElement).EnumerateArray().FirstOrDefault(r=>N(O(r,"group"),"id")==id);
        if(roleId.HasValue?N(O(row,"role"),"id")!=roleId.Value:row.ValueKind==JsonValueKind.Object)throw new InvalidOperationException(L.T("O Roblox ainda não confirmou a alteração. Atualize antes de tentar novamente."));
    }
}
