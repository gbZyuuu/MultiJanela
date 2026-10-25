using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using RobloxMultiManager.Core;
namespace RobloxMultiManager;
public partial class MainWindow
{
    private ExpansionPanel? _expansionPanel;
    private readonly ProcessAudioService _processAudio=new();
    private AudioPreferences _audioPreferences=new();
    private readonly DispatcherTimer _audioTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private readonly DispatcherTimer _vaultTimer=new(){Interval=TimeSpan.FromSeconds(15)};
    private Task _audioWork=Task.CompletedTask;private bool _vaultLocked;
    private DateTime _lastVaultActivity=DateTime.UtcNow;private int _vaultIdleMinutes=15;
    private PreferenceFile<AudioPreferences> AudioStore=>new(_paths,"audio.json",()=>new(),AudioPreferences.Validate);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    private async Task InitializeExpansionAsync()
    {
        try{_audioPreferences=await AudioStore.LoadAsync();}catch(Exception){_audioPreferences=new();}
        _audioTimer.Tick+=async(_,_)=>await QueueAudioAsync();_audioTimer.Start();
        var config=await new AccountVault(_paths.Root).ReadAsync();_vaultIdleMinutes=config?.IdleMinutes??15;
        PreviewMouseDown+=(_,_)=>_lastVaultActivity=DateTime.UtcNow;PreviewKeyDown+=(_,_)=>_lastVaultActivity=DateTime.UtcNow;
        _vaultTimer.Tick+=async(_,_)=>{if(new AccountVault(_paths.Root).Exists&&!_vaultLocked&&DateTime.UtcNow-_lastVaultActivity>TimeSpan.FromMinutes(_vaultIdleMinutes))try{await LockVaultAsync();}catch(Exception){_lastVaultActivity=DateTime.UtcNow;}};_vaultTimer.Start();
    }
    private async Task QueueAudioAsync()
    {
        if(!_audioWork.IsCompleted||_shutdownStarted||!_audioPreferences.Enabled)return;
        var targets=Instances.Where(i=>_launchOwnership.ContainsKey(i.Key)).Select(i=>new AudioTarget(i.ProcessId,i.StartTimeUtc,_launchOwnership[i.Key].ProfileId)).ToArray();
        GetWindowThreadProcessId(GetForegroundWindow(),out var focus);var preferences=_audioPreferences;
        _audioWork=Task.Run(()=>_processAudio.Reconcile(targets,preferences,(int)focus));
        try{await _audioWork;}catch(Exception ex)when(ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception){ }
    }
    private async Task SaveAudioAsync(AudioPreferences preferences)
    {
        preferences=AudioPreferences.Validate(preferences);await AudioStore.SaveAsync(preferences);_audioPreferences=preferences;
        await _audioWork;var targets=Instances.Where(i=>_launchOwnership.ContainsKey(i.Key)).Select(i=>new AudioTarget(i.ProcessId,i.StartTimeUtc,_launchOwnership[i.Key].ProfileId)).ToArray();GetWindowThreadProcessId(GetForegroundWindow(),out var focus);
        _audioWork=Task.Run(()=>_processAudio.Reconcile(targets,preferences,(int)focus));
        try{await _audioWork;}catch(Exception ex)when(ex is COMException or InvalidOperationException or System.ComponentModel.Win32Exception){ }
    }
    private void Tools_Click(object sender,RoutedEventArgs e)=>ShowWorkspacePage(WorkspacePage.Tools);
    private ExpansionPanel EnsureExpansionPanel()
    {if(_expansionPanel is null){_expansionPanel=new ExpansionPanel(this);_expansionPanel.RestoreDraft(_formDrafts.GetValueOrDefault("tools"));ToolsPageHost.Content=_expansionPanel;}return _expansionPanel;}
    private async Task<RobloxAccountOperations> OpenAccountOperationsAsync(Guid id,CancellationToken token)
    {
        var profile = Profiles.Single(p => p.Id == id);
        return new(await OpenAccountHttpAsync(profile, token), profile.RobloxUserId!.Value);
    }
    private async Task<HttpClient> OpenAccountHttpAsync(AccountProfile profile, CancellationToken token)
    {
        if (_vaultLocked || profile.RobloxUserId is not > 0) throw new InvalidOperationException();
        var cookie = await ReadSessionCookieAsync(profile, token); token.ThrowIfCancellationRequested();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        handler.CookieContainer.Add(new Cookie(".ROBLOSECURITY", cookie, "/", ".roblox.com") { Secure = true, HttpOnly = true });
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
    private async Task<string> ReadSessionCookieAsync(AccountProfile profile,CancellationToken token)
    {
        if(!Directory.Exists(_paths.BrowserProfilePath(profile)))throw new InvalidOperationException();
        var host=new Window{ShowInTaskbar=false,ShowActivated=false,Width=1,Height=1};CoreWebView2Controller? controller=null;
        try{var environment=await CoreWebView2Environment.CreateAsync(userDataFolder:_paths.BrowserProfilePath(profile));controller=await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).EnsureHandle());controller.IsVisible=false;token.ThrowIfCancellationRequested();var cookies=await controller.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");return cookies.FirstOrDefault(c=>c.Name==".ROBLOSECURITY")?.Value??throw new InvalidOperationException();}
        finally{controller?.Close();host.Close();}
    }
    private async Task ImportAccountsAsync(AccountImportEntry[] entries,CancellationToken token)
    {
        foreach(var entry in entries)
        {
            token.ThrowIfCancellationRequested();var profile=AccountProfile.Create(entry.Alias);
            if(entry.SessionCookie is {} cookie)
            {
                using var handler=new HttpClientHandler{AllowAutoRedirect=false,CookieContainer=new CookieContainer()};handler.CookieContainer.Add(new Cookie(".ROBLOSECURITY",cookie,"/",".roblox.com"){Secure=true,HttpOnly=true});using var http=new HttpClient(handler);using var response=await http.GetAsync("https://users.roblox.com/v1/users/authenticated",token);response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));var identity=json.RootElement;var userId=identity.GetProperty("id").GetInt64();
                if(entry.UserId is {} expected&&expected!=userId)throw new InvalidDataException();if(Profiles.Any(p=>p.RobloxUserId==userId))continue;
                profile=profile with{RobloxUserId=userId,RobloxUsername=identity.GetProperty("name").GetString(),RobloxDisplayName=identity.GetProperty("displayName").GetString()};
                var host=new Window{ShowInTaskbar=false,ShowActivated=false};CoreWebView2Controller? controller=null;
                try{var env=await CoreWebView2Environment.CreateAsync(userDataFolder:_paths.BrowserProfilePath(profile));controller=await env.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(host).EnsureHandle());controller.IsVisible=false;var manager=controller.CoreWebView2.CookieManager;var native=manager.CreateCookie(".ROBLOSECURITY",cookie,".roblox.com","/");native.IsSecure=true;native.IsHttpOnly=true;native.Expires=DateTime.UtcNow.AddDays(30);manager.AddOrUpdateCookie(native);}
                finally{controller?.Close();host.Close();}
            }
            Profiles.Add(profile);await SaveProfilesAsync(throwOnError:true);await Task.Delay(1500,token);
        }
    }
    private async Task SetProfileVersionAsync(AccountProfile profile,string? version)
    {
        if(version is not null&&!System.Text.RegularExpressions.Regex.IsMatch(version,"^version-[0-9a-fA-F]{16}$"))throw new ArgumentException();
        if(_launchingProfiles.Contains(profile.Id))throw new InvalidOperationException();var index=Profiles.IndexOf(profile);if(index<0)throw new InvalidOperationException();Profiles[index]=profile with{RobloxVersionId=version};await SaveProfilesAsync(throwOnError:true);RefreshWorkspaceAccounts();
    }
    private async Task LockVaultAsync()
    {
        if(_vaultLocked||_storageMaintenanceRequested||!new AccountVault(_paths.Root).Exists)return;
        if(_activeLaunches>0||_launchBatchCancellation is not null||_accountPortal is not null||_expansionPanel?.IsBusy==true||_automationCancellation is not null||_apiApplying||_reconnectInspections>0||!_reconnectPump.IsCompleted)return;
        _storageMaintenanceRequested=true;IsEnabled=false;
        try
        {
            await HomeCpuSlider.FlushAsync(finishInteraction:true);await HomeGraphics.FlushAsync();await SaveProfilesAsync(true);await SaveSettingsAsync(true);await SaveOwnershipAsync(true);await SaveWorkspaceAsync();
            _storageMaintenance=true;await _historyWrite;
            await _profilesSaveGate.WaitAsync();_profilesSaveGate.Release();await _settingsSaveGate.WaitAsync();_settingsSaveGate.Release();await _ownershipSaveGate.WaitAsync();_ownershipSaveGate.Release();
            if(_expansionPanel is not null)await _expansionPanel.StopBridgeAsync();
            _vaultLocked=true;SecureJsonFiles.Lock();NexusWorkspace.Visibility=Visibility.Hidden;TopNavBar.IsEnabled=false;
            while(!await VaultUnlockWindow.UnlockIfNeededAsync(_paths,this))
                if(AppDialog.Show(this,L.T("O cofre continuará bloqueado. Tentar desbloquear?"),L.T("Cofre"),MessageBoxButton.YesNo)==MessageBoxResult.No){await ExitApplicationAsync(handoff:true);return;}
        }
        finally
        {
            if(!_vaultLocked||SecureJsonFiles.IsUnlocked)
            {_vaultLocked=false;_storageMaintenanceRequested=_storageMaintenance=false;ResumeHistoryWrites();_ = SaveOwnershipAsync();NexusWorkspace.Visibility=Visibility.Visible;TopNavBar.IsEnabled=true;IsEnabled=true;_lastVaultActivity=DateTime.UtcNow;}
        }
    }
    private async Task DisposeExpansionAsync()
    { _audioTimer.Stop();_vaultTimer.Stop();if(_expansionPanel is not null)await _expansionPanel.DisposeAsync();await _audioWork;await Task.Run(()=>_processAudio.Dispose()); }
}
