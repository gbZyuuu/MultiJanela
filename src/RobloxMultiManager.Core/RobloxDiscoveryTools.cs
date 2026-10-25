using System.Net;
using System.Text.Json;
namespace RobloxMultiManager.Core;
public sealed record ServerRegion(string Ip,string City,string Region,string Country)
{public override string ToString()=>string.Join(", ",new[]{City,Region,Country}.Where(x=>!string.IsNullOrWhiteSpace(x)));}
public static class RobloxDiscoveryTools
{
    public static CatalogAsset[] ReadFreeCatalog(JsonElement root)
    {
        var output=new List<CatalogAsset>();if(!root.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Array)throw new InvalidDataException();
        foreach(var item in data.EnumerateArray())
        {
            var id=RobloxGameService.Number(item,"id");var creator=RobloxGameService.Number(item,"creatorTargetId");var type=(int)RobloxGameService.Number(item,"assetType");
            if(!item.TryGetProperty("price",out var price)||!price.TryGetInt64(out var amount)||amount!=0||creator!=1||id<=0||type is not (2 or 8 or 11 or 12 or 41 or 42 or 43 or 44 or 45 or 46 or 47))continue;
            var name=RobloxGameService.String(item,"name");if(name.Length is <1 or >300)continue;
            Guid? collectible=Guid.TryParse(RobloxGameService.String(item,"collectibleItemId"),out var collectibleId)?collectibleId:null;
            output.Add(new(id,name,type,creator,amount,collectible));
        }
        return output.DistinctBy(x=>x.Id).Take(120).ToArray();
    }
    public static string? PublicServerAddress(JsonElement root)
    {
        if(!root.TryGetProperty("joinScript",out var join))return null;
        if(join.ValueKind==JsonValueKind.String){var text=join.GetString();if(text?.Length>2*1024*1024)throw new InvalidDataException();using var inner=JsonDocument.Parse(text??"{}");return Address(inner.RootElement);}
        return Address(join);
    }
    private static string? Address(JsonElement script)
    {
        if(script.ValueKind!=JsonValueKind.Object)return null;
        if(script.TryGetProperty("UdmuxEndpoints",out var endpoints)&&endpoints.ValueKind==JsonValueKind.Array)
            foreach(var value in endpoints.EnumerateArray())if(IsPublicIPv4(RobloxGameService.String(value,"Address"),out var address))return address;
        return IsPublicIPv4(RobloxGameService.String(script,"MachineAddress"),out var machine)?machine:null;
    }
    public static bool IsPublicIPv4(string text,out string? address)
    {
        address=null;if(!IPAddress.TryParse(text,out var ip)||ip.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork)return false;var b=ip.GetAddressBytes();
        if(b[0] is 0 or 10 or 127 or >=224||b[0]==169&&b[1]==254||b[0]==172&&b[1] is >=16 and <=31||b[0]==192&&b[1]==168||b[0]==100&&b[1] is >=64 and <=127||b[0]==198&&b[1] is 18 or 19)return false;address=ip.ToString();return true;
    }
    public static async Task<ServerRegion> RegionAsync(RobloxAccountOperations api,HttpClient anonymous,long place,string server,CancellationToken token)
    {
        if(place<=0||!Guid.TryParse(server,out _))throw new ArgumentException();await api.VerifyAsync(token);
        using var join=await api.SendAsync(HttpMethod.Post,"https://gamejoin.roblox.com/v1/join-game-instance",new{placeId=place,gameId=server},token);
        var ip=PublicServerAddress(join.RootElement)??throw new InvalidOperationException(L.T("O Roblox não informou um endereço público para este servidor."));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response=await anonymous.GetAsync("https://ipwho.is/"+Uri.EscapeDataString(ip),HttpCompletionOption.ResponseHeadersRead,timeout.Token);response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>65536)throw new InvalidDataException();
        await using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var bytes=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await stream.ReadAsync(buffer,timeout.Token))>0){if(bytes.Length+count>65536)throw new InvalidDataException();bytes.Write(buffer,0,count);}using var json=JsonDocument.Parse(bytes.ToArray());var root=json.RootElement;
        if(!root.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True)return new(ip,"","","");
        return new(ip,RobloxGameService.String(root,"city"),RobloxGameService.String(root,"region"),RobloxGameService.String(root,"country"));
    }
}
