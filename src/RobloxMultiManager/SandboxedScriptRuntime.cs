using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using RobloxMultiManager.Core;
namespace RobloxMultiManager;
/// <summary>Separate cookie-free WebView renderer with a bounded asynchronous broker, no host objects and no network.</summary>
internal static class SandboxedScriptRuntime
{
    public static Task RunAsync(AppPaths paths,string script,Func<string,JsonElement,CancellationToken,Task<object?>> dispatch,Action<string> log,CancellationToken token)
        =>System.Windows.Application.Current.Dispatcher.InvokeAsync(()=>RunOnDispatcherAsync(paths,script,dispatch,log,token)).Task.Unwrap();
    private static async Task RunOnDispatcherAsync(AppPaths paths,string script,Func<string,JsonElement,CancellationToken,Task<object?>> dispatch,Action<string> log,CancellationToken token)
    {
        if(script.Length is <1 or >65536)throw new ArgumentException();
        var root=Path.Combine(paths.Root,"ScriptRuntime",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var host=new Window{ShowInTaskbar=false,ShowActivated=false,Width=1,Height=1,WindowStyle=WindowStyle.None};
        CoreWebView2Controller? controller=null;Process? process=null;DateTime started=default;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var finished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;var key=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        try
        {
            var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:root);
            controller=await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).EnsureHandle());controller.IsVisible=false;var core=controller.CoreWebView2;
            process=Process.GetProcessById((int)core.BrowserProcessId);started=process.StartTime.ToUniversalTime();
            core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            // The bootstrap is the host's single NavigateToString call. Some runtime
            // builds expose a synthetic navigation URI, while the document source
            // remains about:blank. Deny every subsequent top-level navigation.
            var bootstrap=true;
            core.NavigationStarting+=(_,e)=>{if(bootstrap){bootstrap=false;}else e.Cancel=true;};
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(_,e)=>e.Response=environment.CreateWebResourceResponse(Stream.Null,403,"Blocked","");
            core.WebMessageReceived+=async(_,message)=>
            {
                try
                {
                    if(timeout.IsCancellationRequested||message.Source!="about:blank"||message.WebMessageAsJson.Length>16384||++count>120)return;
                    using var json=JsonDocument.Parse(message.WebMessageAsJson);var data=json.RootElement;
                    if(data.GetProperty("key").GetString()!=key)return;var type=data.GetProperty("type").GetString();
                    if(type=="done"){finished.TrySetResult();return;}if(type=="error"){finished.TrySetException(new InvalidOperationException(L.T("O script terminou com erro.")));return;}
                    if(type=="log"){var text=data.GetProperty("value").GetString()??"";log(text[..Math.Min(text.Length,2048)]);return;}
                    if(type!="call")return;var id=data.GetProperty("id").GetInt32();var action=data.GetProperty("name").GetString()??"";
                    try{var result=await dispatch(action,data.GetProperty("args").Clone(),timeout.Token);core.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,result,error=(string?)null}));}
                    catch(Exception){core.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,error=L.T("A ação foi recusada ou falhou.")}));}
                }
                catch(Exception){finished.TrySetException(new InvalidOperationException(L.T("Mensagem inválida do script.")));}
            };
            var code=JsonSerializer.Serialize(script);var secret=JsonSerializer.Serialize(key);
            var html="<html><head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; connect-src 'none'; frame-src 'none'\"></head><body><script>"+
                "const key="+secret+", pending=new Map();let next=0;const send=(type,p={})=>chrome.webview.postMessage({key,type,...p});chrome.webview.addEventListener('message',e=>{const p=pending.get(e.data.id);if(p){pending.delete(e.data.id);e.data.error?p.reject(Error(e.data.error)):p.resolve(e.data.result)}});"+
                "const call=(name,...args)=>new Promise((resolve,reject)=>{const id=++next;pending.set(id,{resolve,reject});send('call',{id,name,args})});const ram=Object.freeze({accounts:()=>call('accounts'),wait:ms=>call('wait',ms),arrange:()=>call('arrange'),nexus:command=>call('nexus',command),log:value=>send('log',{value:String(value)})});"+
                "(async()=>{try{await new (Object.getPrototypeOf(async function(){}).constructor)('ram',"+code+")(ram);send('done')}catch(e){send('error')}})();</script></body></html>";
            core.NavigateToString(html);await finished.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            timeout.Cancel();controller?.Close();host.Close();
            if(process is not null){try{if(!process.HasExited&&process.StartTime.ToUniversalTime()==started&&process.ProcessName.Equals("msedgewebview2",StringComparison.OrdinalIgnoreCase))process.Kill(true);}catch(InvalidOperationException){}finally{process.Dispose();}}
            try{var resolved=Path.GetFullPath(root);var allowed=Path.GetFullPath(Path.Combine(paths.Root,"ScriptRuntime"))+Path.DirectorySeparatorChar;if(resolved.StartsWith(allowed,StringComparison.OrdinalIgnoreCase))Directory.Delete(resolved,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
}
