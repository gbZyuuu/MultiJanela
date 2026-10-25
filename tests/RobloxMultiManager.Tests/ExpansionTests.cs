using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RobloxMultiManager.Core;
namespace RobloxMultiManager.Tests;
internal static partial class Program
{
    private static async Task MustFailAsync<T>(Func<Task> action) where T:Exception
    {try{await action();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
    private static RobloxAccountOperations Operations(Func<HttpRequestMessage,HttpResponseMessage> respond,long user=42)=>new(new HttpClient(new ApiFixtureHandler(request=>request.Method==HttpMethod.Get&&request.RequestUri!.AbsolutePath.EndsWith("/friends/statuses")?JsonResponse(new{data=new[]{new{id=99,status=2}}}):respond(request))),user);
    private static async Task TestAccountBatchesAsync()
    {
        var profile=Guid.NewGuid();var writes=0;var requests=0;
        using(var api=Operations(request=>{if(request.RequestUri!.Host=="users.roblox.com")return JsonResponse(new{id=8});writes++;return JsonResponse(new{});}))
            await MustFailAsync<InvalidOperationException>(()=>api.ExecuteAsync(new(AccountBatchAction.RequestFriendship,[profile],99),default));
        Assert(writes==0,"Identity mismatch must stop before any mutation");
        using(var api=Operations(request=>
        {
            if(request.RequestUri!.Host=="users.roblox.com")return JsonResponse(new{id=42});
            requests++;
            if(!request.Headers.Contains("x-csrf-token")){var response=new HttpResponseMessage(HttpStatusCode.Forbidden);response.Headers.Add("x-csrf-token","fixture");return response;}
            Assert(request.Headers.GetValues("x-csrf-token").Single()=="fixture","CSRF retry retains exact token");return JsonResponse(new{});
        }))Assert(await api.ExecuteAsync(new(AccountBatchAction.RequestFriendship,[profile],99),default)==AccountOperationState.Completed&&requests==2,"Exactly one CSRF retry");
        using(var api=Operations(request=>{if(request.RequestUri!.Host=="users.roblox.com")return JsonResponse(new{id=42});var response=new HttpResponseMessage(HttpStatusCode.Forbidden);response.Headers.Add("rblx-challenge-id","fixture");response.Headers.Add("x-csrf-token","do-not-retry");return response;}))
            await MustFailAsync<AccountVerificationRequiredException>(()=>api.ExecuteAsync(new(AccountBatchAction.JoinCommunity,[profile],99),default));
        using(var api=Operations(request=>request.RequestUri!.Host=="users.roblox.com"?JsonResponse(new{id=42}):request.Method==HttpMethod.Get?JsonResponse(new{data=Array.Empty<object>()}):JsonResponse(new{})))
            Assert(await api.ExecuteAsync(new(AccountBatchAction.JoinCommunity,[profile],99),default)==AccountOperationState.PendingApproval,"Joining requires a verified membership receipt");
        var ids=new[]{Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid()};var results=new Dictionary<Guid,AccountOperationResult>();var opened=0;
        await new AccountBatchRunner().RunAsync(new(AccountBatchAction.RequestFriendship,ids,99),(id,_)=>{opened++;return Task.FromResult(Operations(request=>request.RequestUri!.Host=="users.roblox.com"?JsonResponse(new{id=42}):new(HttpStatusCode.TooManyRequests)));},row=>results[row.ProfileId]=row,default,TimeSpan.Zero);
        Assert(opened==1&&results[ids[0]].State==AccountOperationState.Failed&&results[ids[1]].State==AccountOperationState.Cancelled&&results[ids[2]].State==AccountOperationState.Cancelled,"429 stops the batch and resolves every pending row");
        using var cancel=new CancellationTokenSource();opened=0;results.Clear();
        await MustFailAsync<OperationCanceledException>(()=>new AccountBatchRunner().RunAsync(new(AccountBatchAction.RequestFriendship,ids,99),(id,_)=>{opened++;return Task.FromResult(Operations(request=>JsonResponse(new{id=42})));},row=>{results[row.ProfileId]=row;if(row.State==AccountOperationState.Completed)cancel.Cancel();},cancel.Token,TimeSpan.FromSeconds(1)));
        Assert(opened==1&&results[ids[0]].State==AccountOperationState.Completed&&results[ids[1]].State==AccountOperationState.Cancelled,"Cancellation does not erase a completed receipt or start the next account");
        using(var api=Operations(_=>throw new Exception("Untrusted host must not be contacted")))await MustFailAsync<ArgumentException>(()=>api.SendAsync(HttpMethod.Get,"https://roblox.com.evil.invalid/",null,default));
    }
    private static async Task TestAvatarDistributionAsync()
    {
        var profile=Guid.NewGuid();var avatar=new AvatarDefinition(Guid.NewGuid(),"Fixture",[10,20]);var mutations=0;
        using(var api=Operations(request=>
        {
            var uri=request.RequestUri!;if(uri.Host=="users.roblox.com")return JsonResponse(new{id=42});
            if(uri.Host=="inventory.roblox.com")return JsonResponse(true);
            if(request.Method==HttpMethod.Post){mutations++;var body=request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();Assert(body.Contains("10")&&body.Contains("20"),"Avatar apply carries chosen assets");return JsonResponse(new{});}
            return JsonResponse(new{assets=new[]{new{id=20},new{id=10}}});
        }))Assert(await api.ExecuteAsync(new(AccountBatchAction.ApplyAvatar,[profile],Avatar:avatar),default)==AccountOperationState.Completed&&mutations==1,"Apply verifies returned asset set, independent of order");
        mutations=0;
        using(var api=Operations(request=>
        {
            if(request.RequestUri!.Host=="users.roblox.com")return JsonResponse(new{id=42});
            if(request.Method==HttpMethod.Post)mutations++;
            if(request.RequestUri.Host=="inventory.roblox.com")return JsonResponse(false);
            return JsonResponse(new{PriceInRobux=1,Creator=new{Id=1},IsForSale=true,ProductId=999});
        }))await MustFailAsync<InvalidOperationException>(()=>api.ExecuteAsync(new(AccountBatchAction.ApplyAvatar,[profile],Avatar:avatar),default));
        Assert(mutations==0,"Paid items never trigger a purchase or an avatar mutation");
        using(var api=Operations(request=>request.RequestUri!.Host=="users.roblox.com"?JsonResponse(new{id=42}):request.RequestUri.Host=="inventory.roblox.com"?JsonResponse(true):JsonResponse(new{assets=Array.Empty<object>()})))
            await MustFailAsync<InvalidOperationException>(()=>api.ExecuteAsync(new(AccountBatchAction.ApplyAvatar,[profile],Avatar:avatar),default));
        using var catalog=JsonDocument.Parse("{\"data\":[{\"id\":1,\"name\":\"Free\",\"assetType\":8,\"creatorTargetId\":1,\"price\":0},{\"id\":2,\"name\":\"Paid\",\"assetType\":8,\"creatorTargetId\":1,\"price\":2},{\"id\":3,\"name\":\"Unknown\",\"assetType\":8,\"creatorTargetId\":1}]}");
        Assert(RobloxDiscoveryTools.ReadFreeCatalog(catalog.RootElement).Single().Id==1,"Catalog filtering rejects unknown prices and paid items");
        AssertThrows(()=>RobloxAccountOperations.ValidateAvatar(avatar with{AssetIds=[10,10]}),"Duplicate assets rejected");
    }
    private sealed class MacroFixture:IMacroTarget
    {public bool Current=true;public int Sent,Released;public Action? AfterSend;public bool Focus()=>true;public bool IsCurrent()=>Current;public void Send(MacroStep step){Sent++;AfterSend?.Invoke();}public void Release()=>Released++;}
    private static async Task TestMacroExpansionAsync()
    {
        byte[] Rec(int size)
        {var bytes=new byte[size*2];BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x100);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4),87);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12),uint.MaxValue-20);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(size),0x101);BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(size+4),87);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(size+12),29);return bytes;}
        foreach(var size in new[]{20,24}){var item=TinyTaskImporter.Import(Rec(size),new(0,0,800,600),"Fixture");Assert(item.Steps.Length==3&&item.Steps[1].Milliseconds==50,"TinyTask pointer sizes and timestamp wrap are handled");}
        AssertThrows(()=>TinyTaskImporter.Import([1,2,3],new(0,0,800,600),"Bad"),"Malformed recording rejected");
        var macro=new MacroRecording(Guid.NewGuid(),"Fixture",[new("keyDown",87),new("wait",Milliseconds:100),new("keyUp",87)]);
        using var stop=new CancellationTokenSource();var target=new MacroFixture{AfterSend=()=>stop.Cancel()};await MustFailAsync<OperationCanceledException>(()=>MacroPlayback.PlayAsync(macro,target,stop.Token));Assert(target.Sent==1&&target.Released==1,"Cancel releases held input before another key is sent");
        target=new MacroFixture();target.AfterSend=()=>target.Current=false;await MustFailAsync<InvalidOperationException>(()=>MacroPlayback.PlayAsync(macro,target,default));Assert(target.Released==1&&target.Sent==1,"Focus loss releases input and prevents subsequent steps");
        AssertThrows(()=>MacroRecording.Validate(macro with{Steps=[new("keyDown",18),new("keyDown",115)]}),"Alt F4 is not accepted");
        AssertThrows(()=>MacroRecording.Validate(macro with{Steps=[new("keyDown",164),new("keyDown",115)]}),"Left Alt F4 is not accepted");
        AssertThrows(()=>MacroRecording.Validate(macro with{Steps=[new("mouseDown",X:double.NaN)]}),"Non-finite mouse coordinates rejected");
    }
    private sealed class AudioFixture:IProcessAudioSession,IProcessAudioBackend
    {public string Id=>"fixture";public int Pid=>123;public float Volume{get;set;}=.8f;public bool Muted{get;set;}public IEnumerable<IProcessAudioSession> Enumerate(IReadOnlySet<int> pids)=>pids.Contains(Pid)?[this]:[];public void Dispose(){} }
    private static Task TestAudioExpansionAsync()
    {
        var profile=Guid.NewGuid();var now=DateTime.UtcNow;var audio=new AudioFixture();using var service=new ProcessAudioService(audio);var target=new AudioTarget(123,now,profile);
        service.Reconcile([target],new(true,60,true,[new(profile,25)]),0,_=>true);Assert(Math.Abs(audio.Volume-.25f)<.001&&audio.Muted,"Per-account override and background mute applied");
        service.Reconcile([target],new(true,60,true),123,_=>true);Assert(Math.Abs(audio.Volume-.6f)<.001&&!audio.Muted,"Foreground restores its mute state and global inheritance");
        service.Reconcile([],new(),0,_=>true);Assert(Math.Abs(audio.Volume-.8f)<.001&&!audio.Muted,"Disabling restores the captured baseline");
        audio.Muted=true;service.Reconcile([target],new(true,30),123,_=>true);Assert(audio.Muted,"Existing user mute is preserved");audio.Volume=.9f;service.Reconcile([],new(),0,_=>true);Assert(Math.Abs(audio.Volume-.9f)<.001,"External volume changes are not overwritten during restore");
        audio.Muted=false;audio.Volume=.8f;service.Reconcile([target],new(true,20),123,_=>false);Assert(Math.Abs(audio.Volume-.8f)<.001,"Stale process identity cannot receive audio changes");
        AssertThrows(()=>AudioPreferences.Validate(new(true,101)),"Out of range volume rejected");
        using var process=System.Diagnostics.Process.GetCurrentProcess();using var native=new ProcessAudioService();native.Reconcile([new(process.Id,process.StartTime.ToUniversalTime(),null)],new(false),0);return Task.CompletedTask;
    }
    private static async Task TestVaultExpansionAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"MultiJanela-vault-fixture-"+Guid.NewGuid().ToString("N"));var paths=new AppPaths(root);paths.EnsureCreated();var profile=AccountProfile.Create("Fixture","https://www.roblox.com/home") with{RobloxVersionId="version-1111111111111111"};var store=new LocalStore(paths);await store.SaveProfilesAsync([profile]);await File.WriteAllTextAsync(Path.Combine(root,"ui-preferences.json"),"{}");
        var vault=new AccountVault(root);const string password="fixture-vault-password";
        try
        {
            await vault.CreateAsync(password,5);Assert(SecureJsonFiles.Encrypted(await File.ReadAllBytesAsync(paths.ProfilesFile)),"Existing data is encrypted on activation");Assert(!SecureJsonFiles.Encrypted(await File.ReadAllBytesAsync(Path.Combine(root,"ui-preferences.json"))),"Startup UI preferences remain readable before unlock");
            Assert((await store.LoadProfilesAsync()).Single().RobloxVersionId==profile.RobloxVersionId,"Per-account version survives encrypted persistence");
            var workspace=new ManagerWorkspaceStore(root);await workspace.SaveAsync(new([],"",null,null,"","Tools",1000,700,false,[],Drafts:new(){{"tools",new(){{"input:8:1",new string('x',1100000)}}}}));
            Assert((await workspace.LoadAsync())?.Drafts?["tools"]["input:8:1"].Length==1100000,"Large unsaved recording/script drafts survive encrypted handoff checkpoints");
            var backup=Path.Combine(root,"Backups","fixture.mjb");var backupService=new EncryptedBackupService(paths);await backupService.ExportAsync(backup,password,false);
            await store.SaveProfilesAsync([profile with{Name="Changed"}]);await backupService.RestoreAsync(backup,password);Assert((await store.LoadProfilesAsync()).Single().Name=="Fixture"&&SecureJsonFiles.Encrypted(await File.ReadAllBytesAsync(paths.ProfilesFile)),"Backup restore re-encrypts data under the active vault");
            SecureJsonFiles.Lock();await MustFailAsync<InvalidOperationException>(()=>store.LoadProfilesAsync());await MustFailAsync<InvalidOperationException>(()=>store.SaveProfilesAsync([profile]));await MustFailAsync<CryptographicException>(()=>vault.UnlockAsync("incorrect-password"));Assert(!SecureJsonFiles.IsUnlocked,"Wrong password does not unlock the vault");
            await vault.UnlockAsync(password);await vault.ChangePasswordAsync("different-fixture-password",3);SecureJsonFiles.Lock();await MustFailAsync<CryptographicException>(()=>vault.UnlockAsync(password));await vault.UnlockAsync("different-fixture-password");Assert((await store.LoadProfilesAsync()).Single().Name=="Fixture","Password rotation retains encrypted profiles");
            var bytes=await File.ReadAllBytesAsync(paths.ProfilesFile);bytes[^1]^=1;await File.WriteAllBytesAsync(paths.ProfilesFile,bytes);await MustFailAsync<CryptographicException>(()=>store.LoadProfilesAsync());
            var imported=AccountImportPreview.Parse("{\"Accounts\":[{\"Username\":\"Fixture\",\"UserID\":42,\"SecurityToken\":\"fixture-cookie\",\"Password\":\"never-import\"}]}");Assert(imported.Single().Alias=="Fixture"&&imported.Single().ToString()=="Fixture","Import preview contains public names only");AssertThrows(()=>AccountImportPreview.Parse("[{\"Name\":\"x\",\"SessionCookie\":\"a;b\"}]"),"Malformed cookies rejected");
        }
        finally{SecureJsonFiles.Lock();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static async Task TestNexusExpansionAsync()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var id=Guid.NewGuid();var key=new string('a',64);await using var bridge=new NexusBridge();await bridge.StartAsync(port,key,()=>[new(id,42,"Fixture")]);
        using(var denied=new ClientWebSocket())await MustFailAsync<WebSocketException>(()=>denied.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/Nexus?id=42&name=Fixture&key=wrong"),default));
        using var socket=new ClientWebSocket();socket.Options.SetRequestHeader("Authorization","Bearer "+key);await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/Nexus?id=42&name=Fixture"),default);using var timeout=new CancellationTokenSource(5000);
        while(bridge.Peers.Length==0)await Task.Delay(10,timeout.Token);
        await MustFailAsync<InvalidOperationException>(()=>bridge.SendAsync([id],"execute print(1)",false,default));Assert(await bridge.SendAsync([Guid.NewGuid()],"rejoin",false,default)==0,"Nexus commands cannot reach unselected accounts");Assert(await bridge.SendAsync([id],"rejoin",false,default)==1,"Authenticated scoped Nexus send");var bytes=new byte[1024];var read=await socket.ReceiveAsync(bytes,timeout.Token);Assert(Encoding.UTF8.GetString(bytes,0,read.Count)=="rejoin","Client receives exact command");
        bridge.ReadText=(_,name)=>name=="answer"?"42":null;await socket.SendAsync(Encoding.UTF8.GetBytes("{\"Name\":\"GetText\",\"Payload\":{\"Name\":\"answer\"}}"),WebSocketMessageType.Text,true,timeout.Token);read=await socket.ReceiveAsync(bytes,timeout.Token);Assert(Encoding.UTF8.GetString(bytes,0,read.Count)=="ElementText:42","Nexus text request returns only its scoped UI value");socket.Abort();
    }
}
