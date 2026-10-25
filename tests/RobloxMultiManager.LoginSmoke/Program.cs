using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RobloxMultiManager;
using RobloxMultiManager.Core;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var output = Path.Combine(root, "dist", "_login-regression");
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var source = XDocument.Load(Path.Combine(root, "src/RobloxMultiManager/App.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = source.Root!.Element(ns + "Application.Resources")!.Elements().Single();
        resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
        resources.SetAttributeValue(XNamespace.Xmlns + "local", "clr-namespace:RobloxMultiManager;assembly=RobloxMultiManager");
        application.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString(),
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/RobloxMultiManager;component/App.xaml") });
        L.Language = "en";
        // Fresh fixture profile only. Every main/identity HTTP request is replaced
        // before navigation; no real Roblox sign-in or account credentials.
        var window = new AccountLoginWindow(AccountProfile.Create("Login fixture"),
            new AppPaths(Path.Combine(output, Guid.NewGuid().ToString("N"))));
        var browser = Get<WebView2>(window, "Browser");
        var identity = Get<WebView2>(window, "IdentityBrowser");
        var finish = Get<Button>(window, "DetectButton");
        var ready = new TaskCompletionSource<bool>();
        var requests = new List<string>();
        var authenticate = false;
        Exception? failure = null;
        void Intercept(WebView2 view)
        {
            view.CoreWebView2InitializationCompleted += (_, initialized) =>
            {
                if (!initialized.IsSuccess) { ready.TrySetException(initialized.InitializationException); return; }
                var core = view.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    var uri = new Uri(e.Request.Uri);
                    requests.Add(uri.Host);
                    var isIdentity = uri.Host == "users.roblox.com";
                    var body = isIdentity
                        ? authenticate ? "{\"id\":123456,\"name\":\"FixtureUser\",\"displayName\":\"Fixture\"}" : "{\"errors\":[{\"code\":0}]}"
                        : "<!doctype html><title>Offline login fixture</title><body><h1 id='marker'>Verification is still open</h1><script>window.fixtureState=42;</script></body>";
                    e.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryStream(Encoding.UTF8.GetBytes(body)), isIdentity && !authenticate ? 401 : 200,
                        isIdentity && !authenticate ? "Unauthorized" : "OK",
                        "Content-Type: " + (isIdentity ? "application/json" : "text/html") + "; charset=utf-8\r\nCache-Control: no-store");
                };
                if (view == browser)
                    core.NavigationCompleted += (_, _) => ready.TrySetResult(true);
            };
        }
        Intercept(browser);
        Intercept(identity);
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        watchdog.Tick += (_, _) => { failure ??= new TimeoutException("Login fixture timed out."); window.Close(); };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await UntilAsync(() => finish.IsEnabled);
                var original = browser.Source!.ToString();
                Check(original == "https://www.roblox.com/login", "Official login origin loaded through the offline fixture");
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await UntilAsync(() => finish.IsEnabled);
                Check(window.Result is null, "An unauthenticated response is rejected");
                Check(browser.Source!.ToString() == original, "Failed confirmation preserves the verification page");
                Check(await browser.CoreWebView2.ExecuteScriptAsync("window.fixtureState") == "42", "In-page verification state survives confirmation");
                // The old handler replaced the main page with about:blank. The
                // real popup must retain the opener and share its browser profile.
                await browser.CoreWebView2.ExecuteScriptAsync("window.fixturePopup=window.open('about:blank');");
                await UntilAsync(() => window.OwnedWindows.Count == 1);
                var popup = window.OwnedWindows.Cast<Window>().Single();
                var popupBrowser = (WebView2)popup.GetType().GetField("_browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(popup)!;
                await UntilAsync(() => popupBrowser.CoreWebView2 is not null);
                Check(await popupBrowser.CoreWebView2.ExecuteScriptAsync("window.opener !== null") == "true", "Popup retains window.opener");
                Check(browser.Source!.ToString() == original, "Opening a popup does not navigate the credential page");
                await popupBrowser.CoreWebView2.ExecuteScriptAsync("window.close()");
                await UntilAsync(() => window.OwnedWindows.Count == 0);
                Check(true, "Popup closes normally through the WebView close event");
                authenticate = true;
                finish.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; window.Close(); }
        };
        try
        {
            watchdog.Start();
            var result = window.ShowDialog();
            watchdog.Stop();
            if (failure is not null) throw failure;
            Check(result == true && window.Result?.UserId == 123456, "Valid official-shaped identity completes the login");
            Check(requests.Contains("users.roblox.com"), "Identity was checked in the separate browser");
            Check(requests.All(host => host is "www.roblox.com" or "users.roblox.com"), "All fixture traffic stayed on intercepted endpoints");
            RunScriptFixtures(root);
            Console.WriteLine($"[PASS] {_checks} offline WebView2 checks; no real credentials or sign-in.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { watchdog.Stop(); application.Shutdown(); }
    }

    private static void RunScriptFixtures(string root)
    {
        var runtime=typeof(MainWindow).Assembly.GetType("RobloxMultiManager.SandboxedScriptRuntime")!;
        var method=runtime.GetMethod("RunAsync",BindingFlags.Public|BindingFlags.Static)!;
        var paths=new AppPaths(Path.Combine(root,"dist","_script-regression",Guid.NewGuid().ToString("N")));
        var logs=new List<string>();
        Func<string,System.Text.Json.JsonElement,CancellationToken,Task<object?>> broker=(name,args,token)=>
            name=="accounts"?Task.FromResult<object?>(new[]{new{name="Fixture"}}):Task.FromException<object?>(new InvalidOperationException("Denied fixture permission"));
        Task Run(string script,CancellationToken token)=>(Task)method.Invoke(null,new object[]{paths,script,broker,(Action<string>)(text=>logs.Add(text)),token})!;
        void Await(Task task)
        {
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(task.IsCompleted)frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);timer.Stop();task.GetAwaiter().GetResult();
        }
        Await(Run("const a=await ram.accounts();ram.log(a[0].name);try{await fetch('https://example.invalid/')}catch{ram.log('network-blocked')}try{await ram.arrange()}catch{ram.log('permission-denied')}",default));
        Check(logs.SequenceEqual(new[]{"Fixture","network-blocked","permission-denied"}),"Scripts use the scoped broker, deny network and retain permission checks");
        Check(!Directory.EnumerateDirectories(Path.Combine(paths.Root,"ScriptRuntime")).Any(),"Script browser profile is cleaned after a normal run");
        using(var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(6)))
        {try{Await(Run("while(true){}",cancellation.Token));throw new InvalidOperationException("Infinite script was not cancelled");}catch(OperationCanceledException){Check(true,"Infinite renderer is stopped by cancellation without freezing WPF");}}
        Await(Run("ram.log('fresh-runtime')",default));Check(logs.Last()=="fresh-runtime","A fresh script works after terminating a hung renderer");
    }

    private static T Get<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;

    private static async Task UntilAsync(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Fixture condition did not settle.");
            await Task.Delay(30);
        }
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        _checks++; Console.WriteLine("[PASS] " + label);
    }
}
