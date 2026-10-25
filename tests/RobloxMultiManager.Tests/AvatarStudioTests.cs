using System.Net.Http;
using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task TestAvatarStudioAsync()
    {
        var colors=new Dictionary<string,string>{{"headColor","#FFFFFF"},{"torsoColor","#FFFFFF"},{"leftArmColor","#FFFFFF"},{"rightArmColor","#FFFFFF"},{"leftLegColor","#FFFFFF"},{"rightLegColor","#FFFFFF"}};
        var scales=new Dictionary<string,double>{{"height",1},{"width",1},{"head",1},{"depth",1},{"proportion",0},{"bodyType",0}};
        var avatarType=3;var renders=0;var mutations=0;var rejectReceipt=false;var badRender=false;var lastCategory="";
        using var handler=new ApiFixtureHandler(request=>
        {
            var path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("authenticated"))return JsonResponse(new{id=42});
            if(path=="/v4/avatar")return JsonResponse(new{avatarModel=new{assets=new[]{new{id=10,name="Hair"}},bodyColors=colors.ToDictionary(p=>p.Key,p=>p.Value.TrimStart('#')),scales,playerAvatarType=avatarType==1?"R6":"R15"}});
            if(path=="/v1/avatar-rules")return JsonResponse(new{scales=scales.ToDictionary(p=>p.Key,p=>new{min=0,max=1,increment=.01}),bodyColorsPalette=new[]{new{hexColor="#FFFFFF"},new{hexColor="#BB8844"}}});
            if(path.Contains("/inventory/")){lastCategory=path;return JsonResponse(new{data=new[]{new{assetId=20,name="Owned hair"}},nextPageCursor=(string?)null});}
            if(path=="/v1/users/avatar-3d")return JsonResponse(new{state="Completed",imageUrl="https://t0.rbxcdn.com/current-model"});
            if(path=="/v1/users/avatar")return JsonResponse(new{data=new[]{new{targetId=42,state="Completed",imageUrl="https://t0.rbxcdn.com/full-body"}}});
            if(request.RequestUri.Host=="thumbnails.roblox.com")return JsonResponse(new{data=Array.Empty<object>()});
            using var body=JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            if(path.EndsWith("set-body-colors")){mutations++;if(!rejectReceipt)colors=body.RootElement.EnumerateObject().ToDictionary(p=>p.Name[..^1],p=>"#"+p.Value.GetString());return JsonResponse(new{success=true});}
            if(path.EndsWith("set-scales")){mutations++;scales=body.RootElement.EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetDouble());return JsonResponse(new{success=true});}
            if(path.EndsWith("set-player-avatar-type")){mutations++;avatarType=body.RootElement.GetProperty("playerAvatarType").GetInt32();return JsonResponse(new{success=true});}
            if(path.EndsWith("/render")){renders++;Assert(body.RootElement.GetProperty("avatarDefinition").GetProperty("bodyColors").GetProperty("headColor").GetString()=="BB8844","Draft rendering sends the unprefixed colors accepted by the live render API");Assert(body.RootElement.GetProperty("thumbnailConfig").GetProperty("thumbnailType").GetString()=="3d","Preview requests real 3D thumbnails");return JsonResponse(new{state="Completed",imageUrl=badRender?"https://example.com/model":"https://t0.rbxcdn.com/fixture-model"});}
            throw new Exception("Unexpected avatar studio fixture route: "+path);
        });
        using var http=new HttpClient(handler);var service=new AccountSocialService(http,42);
        Assert(await service.GetFullBodyThumbnailAsync()=="https://t0.rbxcdn.com/full-body","Avatar fallback requests the full body, never a headshot");
        Assert((await service.GetCurrentAvatarRenderAsync()).Url=="https://t0.rbxcdn.com/current-model"&&mutations==0,"Current avatar uses the read-only official 3D thumbnail");
        await service.GetEquippedItemsAsync();var inventory=await service.GetAvatarInventoryAsync(assetType:41);
        Assert(inventory.Items.Single().Id==20&&lastCategory.EndsWith("/41"),"Categories load their own paginated inventory, not a filter of the first page");
        await MustFailAsync<ArgumentException>(()=>service.GetAvatarInventoryAsync(assetType:999));
        var body=await service.GetAvatarBodyAsync();var rules=await service.GetAvatarRulesAsync();
        Assert(body.Type==3&&body.Colors["headColor"]=="#FFFFFF","Live avatar response normalizes unprefixed hex and named rig enums");
        body.Colors["headColor"]="#BB8844";body.Scales["height"]=.9;
        await service.RenderAvatarAsync([10,20],body);
        Assert(renders==1&&mutations==0&&colors["headColor"]=="#FFFFFF","Live preview does not equip items or change the actual avatar");
        await service.ApplyBodyAsync(body);
        Assert(mutations==2&&colors["headColor"]=="#BB8844"&&scales["height"]==.9,"Native body edits require colors and scales readback");
        body.Scales["height"]=2;await MustFailAsync<ArgumentException>(()=>service.ApplyBodyAsync(body));Assert(mutations==2,"Out-of-range proportions never reach Roblox");
        body.Scales["height"]=.9;body.Colors["torsoColor"]="#BB8844";rejectReceipt=true;await MustFailAsync<InvalidOperationException>(()=>service.ApplyBodyAsync(body));
        rejectReceipt=false;body.Colors["torsoColor"]="#FFFFFF";await service.ApplyBodyAsync(body with { Type=1 });Assert(avatarType==1,"Avatar rig selection uses a verified native operation");
        badRender=true;await MustFailAsync<InvalidDataException>(()=>service.RenderAvatarAsync([10],body));
        await MustFailAsync<ArgumentException>(()=>service.RenderAvatarAsync([999],body));
        Assert(renders==2,"Unknown items are refused before requesting a preview");
    }
}
