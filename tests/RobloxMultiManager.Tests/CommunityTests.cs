using System.Net;
using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task TestCommunitiesAsync()
    {
        var owner=true;var auth=42;var writes=0;var shout="Welcome";var description="Community";var role=2L;var present=true;var pending=true;var accepted=false;var ignoreWrite=false;
        using var http=new HttpClient(new ApiFixtureHandler(request=>
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("authenticated"))return JsonResponse(new{id=auth});
            if(request.RequestUri.Host=="thumbnails.roblox.com")return JsonResponse(new{data=new[]{new{targetId=5,state="Completed",imageUrl="https://t0.rbxcdn.com/icon"}}});
            if(path=="/v1/groups/5")return JsonResponse(new{id=5,name="Fixture community",description,shout=new{body=shout},memberCount=12,owner=new{username="Owner"},isLocked=false});
            if(path.EndsWith("/membership"))return JsonResponse(new{isOwner=owner,isPendingJoin=false,userRole=new{role=new{rank=owner?255:1}},canViewMemberList=true,permissions=new{groupMembershipPermissions=new{changeRank=owner,removeMembers=owner,inviteMembers=owner},groupPostsPermissions=new{postToStatus=owner}}});
            if(path=="/v1/groups/5/roles")return JsonResponse(new{roles=new[]{new{id=2,name="Member",rank=1,memberCount=12},new{id=3,name="Moderator",rank=100,memberCount=1}}});
            if(path=="/v2/users/8/groups/roles")return JsonResponse(new{data=accepted?new[]{new{group=new{id=5},role=new{id=2,rank=1}}}:[]});
            if(path=="/v2/users/42/groups/roles"||path=="/v2/users/7/groups/roles")return JsonResponse(new{data=present?new[]{new{group=new{id=5,name="Fixture community",memberCount=12},role=new{id=role,name="Member",rank=1}}}:[]});
            if(path=="/v1/groups/search")return JsonResponse(new{data=new[]{new{id=5,name="Fixture community",memberCount=12}},nextPageCursor="next"});
            if(path=="/v1/groups/5/users")return JsonResponse(new{data=new[]{new{user=new{userId=7,username="Member",displayName="Friend"},role=new{id=role,name="Member",rank=1}}},nextPageCursor=(string?)null});
            if(path=="/v1/groups/5/join-requests")return JsonResponse(new{data=pending?new[]{new{requester=new{userId=8,username="Applicant",displayName="Applicant"}}}:[],nextPageCursor=(string?)null});
            if(path=="/v1/groups/5/join-requests/users/8")
            {if(request.Method==HttpMethod.Get)return pending?JsonResponse(new{requester=new{userId=8}}):new HttpResponseMessage(HttpStatusCode.NotFound);writes++;if(!ignoreWrite){pending=false;accepted=request.Method==HttpMethod.Post;}return JsonResponse(new{});}
            if(path=="/v1/groups/5/users/7")
            {writes++;if(!ignoreWrite){if(request.Method==HttpMethod.Delete)present=false;else{using var body=JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);role=body.RootElement.GetProperty("roleId").GetInt64();}}return JsonResponse(new{});}
            if(path.EndsWith("/status")||path.EndsWith("/description"))
            {writes++;using var body=JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);if(!ignoreWrite){if(path.EndsWith("/status"))shout=body.RootElement.GetProperty("message").GetString()!;else description=body.RootElement.GetProperty("description").GetString()!;}return JsonResponse(new{});}
            throw new Exception("Unexpected community route: "+path);
        }));
        var api=new CommunityService(http,42);
        Assert((await api.MineAsync()).Single().Icon is not null,"Community list has verified CDN icons");
        Assert((await api.SearchAsync("Fixture")).Cursor=="next","Community search retains pagination");
        var details=await api.DetailsAsync(5);Assert(details.CanChangeRank&&details.CanShout&&details.CanManageRequests,"Community permissions are read from authenticated membership");
        Assert((await api.MembersAsync(5)).Items.Single().Id==7&&(await api.RolesAsync(5)).Length==2,"Members and ranks use native data");
        Assert((await api.RequestsAsync(5)).Items.Single().Id==8,"Join requests load individually");
        await api.SetTextAsync(5,"Updated",false);Assert(shout=="Updated","Community shout requires readback");
        await api.SetTextAsync(5,"New description",true);Assert(description=="New description","Community description requires owner permissions");
        ignoreWrite=true;await MustFailAsync<InvalidOperationException>(()=>api.SetTextAsync(5,"Not stored",false));ignoreWrite=false;
        owner=false;var before=writes;await MustFailAsync<InvalidOperationException>(()=>api.SetTextAsync(5,"Forbidden",false));Assert(writes==before,"Missing permissions prevent mutations");
        owner=true;auth=9;await MustFailAsync<InvalidOperationException>(()=>api.ChangeMemberAsync(5,7,3));Assert(writes==before,"Wrong account prevents membership writes");auth=42;
        await api.ChangeMemberAsync(5,7,3);Assert(role==3,"Member rank updates verified by fresh membership");
        await api.ChangeMemberAsync(5,7,null);Assert(!present,"Member removal verified by fresh membership");
        ignoreWrite=true;await MustFailAsync<InvalidOperationException>(()=>api.RespondRequestAsync(5,8,true));ignoreWrite=false;
        await api.RespondRequestAsync(5,8,true);Assert(!pending,"Join request disappearance is checked after handling");
        before=writes;await MustFailAsync<InvalidOperationException>(()=>api.RespondRequestAsync(5,42,true));Assert(writes==before,"A user cannot approve their own join request");
    }
}
