using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
namespace RobloxMultiManager.Core;
public sealed record NexusIdentity(Guid ProfileId,long UserId,string Username);
public sealed record NexusPeer(Guid ProfileId,string ConnectionId,DateTime ConnectedUtc);
public sealed record NexusElement(Guid ProfileId,string Kind,string Name,string Content);
public sealed class NexusBridge:IAsyncDisposable
{
    private sealed record Peer(NexusPeer Info,WebSocket Socket,SemaphoreSlim Send);
    private WebApplication? _app;private byte[] _key=[];
    private readonly ConcurrentDictionary<Guid,Peer> _peers=new();
    public event Action<Guid,string>? Log;
    public event Action<NexusElement>? Element;
    public Func<Guid,string,string?>? ReadText {get;set;}
    public NexusPeer[] Peers=>_peers.Values.Select(p=>p.Info).ToArray();
    public async Task StartAsync(int port,string token,Func<NexusIdentity[]> identities,CancellationToken cancellation=default)
    {
        if(_app is not null||port is <1024 or >65535||token.Length is <32 or >256)throw new ArgumentException();_key=Encoding.UTF8.GetBytes(token);
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.ConfigureKestrel(o=>{o.Listen(IPAddress.Loopback,port);o.Limits.MaxRequestBodySize=65536;o.Limits.MaxRequestHeadersTotalSize=16384;o.AddServerHeader=false;});
        var app=builder.Build();app.UseWebSockets(new(){KeepAliveInterval=TimeSpan.FromSeconds(20)});
        app.MapGet("/Nexus",async context=>
        {
            if(!context.WebSockets.IsWebSocketRequest||context.Connection.RemoteIpAddress is not {} remote||!IPAddress.IsLoopback(remote)){context.Response.StatusCode=403;return;}
            if(context.Request.Headers.ContainsKey("Origin")){context.Response.StatusCode=403;return;}
            var header=context.Request.Headers.Authorization.ToString();var value=header.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?header[7..]:context.Request.Query["key"].ToString();
            var supplied=Encoding.UTF8.GetBytes(value);var authorized=supplied.Length==_key.Length&&CryptographicOperations.FixedTimeEquals(supplied,_key);CryptographicOperations.ZeroMemory(supplied);
            if(!authorized){context.Response.StatusCode=401;return;}
            if(!long.TryParse(context.Request.Query["id"],out var user)){context.Response.StatusCode=400;return;}
            var identity=identities().FirstOrDefault(a=>a.UserId==user&&string.Equals(a.Username,context.Request.Query["name"],StringComparison.Ordinal));
            if(identity is null||_peers.Count>=128){context.Response.StatusCode=403;return;}
            using var socket=await context.WebSockets.AcceptWebSocketAsync();var peer=new Peer(new(identity.ProfileId,Guid.NewGuid().ToString("N"),DateTime.UtcNow),socket,new(1));
            if(_peers.TryGetValue(identity.ProfileId,out var previous))previous.Socket.Abort();_peers[identity.ProfileId]=peer;
            var buffer=new byte[16384];var elements=0;var messages=0;var started=DateTime.UtcNow;
            try
            {
                while(socket.State==WebSocketState.Open)
                {
                    using var message=new MemoryStream();WebSocketReceiveResult result;
                    do{result=await socket.ReceiveAsync(buffer,context.RequestAborted);if(result.MessageType==WebSocketMessageType.Close)return;if(result.MessageType!=WebSocketMessageType.Text)throw new InvalidDataException();if(message.Length+result.Count>16384)throw new InvalidDataException();message.Write(buffer,0,result.Count);}while(!result.EndOfMessage);
                    if(DateTime.UtcNow-started>TimeSpan.FromSeconds(10)){started=DateTime.UtcNow;messages=0;}if(++messages>100)throw new InvalidDataException();
                    using var json=JsonDocument.Parse(message.ToArray());var root=json.RootElement;var name=RobloxGameService.String(root,"Name");
                    if(name=="ping")continue;
                    if(!root.TryGetProperty("Payload",out var payload)||payload.ValueKind!=JsonValueKind.Object)continue;
                    var content=RobloxGameService.String(payload,"Content");if(content.Length>4096)throw new InvalidDataException();
                    if(name=="Log")Log?.Invoke(identity.ProfileId,content);
                    else if(name=="GetText")
                    {var element=RobloxGameService.String(payload,"Name");if(element.Length is >0 and <=80&&ReadText?.Invoke(identity.ProfileId,element) is {} text){await peer.Send.WaitAsync(context.RequestAborted);try{await socket.SendAsync(Encoding.UTF8.GetBytes("ElementText:"+text[..Math.Min(text.Length,2048)]),WebSocketMessageType.Text,true,context.RequestAborted);}finally{peer.Send.Release();}}}
                    else if(name is "CreateButton" or "CreateTextBox" or "CreateLabel" or "CreateNumeric")
                    {var element=RobloxGameService.String(payload,"Name");if(element.Length is >0 and <=80&&++elements<=100)Element?.Invoke(new(identity.ProfileId,name,element,content));}
                }
            }
            catch(Exception ex)when(ex is WebSocketException or OperationCanceledException or JsonException or InvalidDataException or InvalidOperationException or ArgumentException){ }
            finally{if(_peers.TryGetValue(identity.ProfileId,out var current)&&ReferenceEquals(current,peer))_peers.TryRemove(identity.ProfileId,out _);}
        });
        try{await app.StartAsync(cancellation);_app=app;}catch{await app.DisposeAsync();CryptographicOperations.ZeroMemory(_key);_key=[];throw;}
    }
    public async Task<int> SendAsync(Guid[] profiles,string command,bool allowExecute,CancellationToken token)
    {
        if(command.Length is <1 or >65536||command.Contains('\0'))throw new ArgumentException();
        var verb=command.Split(' ',2)[0];if(verb is not ("execute" or "teleport" or "rejoin" or "mute" or "unmute" or "ButtonClicked:" or "ElementText:")&&!verb.StartsWith("ButtonClicked:",StringComparison.Ordinal)&&!verb.StartsWith("ElementText:",StringComparison.Ordinal))throw new ArgumentException();
        if(verb=="execute"&&!allowExecute)throw new InvalidOperationException(L.T("Autorize scripts antes de enviar código."));
        var bytes=Encoding.UTF8.GetBytes(command);var sent=0;
        foreach(var id in profiles.Distinct())if(_peers.TryGetValue(id,out var peer))
        {await peer.Send.WaitAsync(token);try{if(peer.Socket.State==WebSocketState.Open){await peer.Socket.SendAsync(bytes,WebSocketMessageType.Text,true,token);sent++;}}finally{peer.Send.Release();}}
        return sent;
    }
    public async ValueTask DisposeAsync()
    {foreach(var peer in _peers.Values)peer.Socket.Abort();_peers.Clear();if(_app is not null){await _app.StopAsync();await _app.DisposeAsync();_app=null;}CryptographicOperations.ZeroMemory(_key);_key=[];}
}
