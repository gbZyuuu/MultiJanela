using System.Net.Http;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

/// <summary>Native social views; official pages are initialized only when explicitly opened.</summary>
public partial class AccountPortal : System.Windows.Controls.UserControl, IDisposable
{
    private readonly AppPaths _paths;
    private readonly Action<AccountProfile, string> _use;
    private readonly Action<AccountProfile, string> _play;
    private readonly Func<AccountProfile, CancellationToken, Task<HttpClient>> _clientFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<LoginPopupWindow> _popups = [];
    private SocialPanel? _social;
    private AvatarEditorPanel? _avatarEditor;
    private CommunitiesPanel? _communities;
    public event Action<Guid>? AvatarChanged;
    private bool NativeBusy => _social?.IsBusy == true || _avatarEditor?.IsBusy == true || _communities?.IsBusy == true;
    private HttpClient? _socialClient;
    private AccountSocialService? _accountService;
    private async Task<AccountSocialService> ServiceAsync(AccountProfile profile)
    {
        if (_accountService is not null) return _accountService;
        var client = await _clientFactory(profile, _lifetime.Token);
        if (_disposed) { client.Dispose(); throw new OperationCanceledException(); }
        _socialClient?.Dispose(); _socialClient = client;
        return _accountService = new AccountSocialService(client, profile.RobloxUserId!.Value);
    }
    private AccountProfile? _profile;
    private bool _starting, _disposed, _covered, _nativeVisible = true;
    public bool BlocksHandoff => IsBusy || !_nativeVisible || _communities?.HasDraft == true || _avatarEditor?.HasDraft == true || _social?.HasDraft == true;
    private string _currentView = "OnlineTab";
    private Dictionary<string, string>? _restoreView;
    public Dictionary<string, string> CaptureDraft() => new() { ["account"] = _profile?.Id.ToString() ?? "", ["view"] = _currentView };
    public void RestoreDraft(Dictionary<string, string>? draft) => _restoreView = draft;
    public bool IsBusy => _starting || NativeBusy || _popups.Count > 0;

    public AccountPortal(AppPaths paths, IEnumerable<AccountProfile> profiles, Action<AccountProfile, string> use,
        Func<AccountProfile, CancellationToken, Task<HttpClient>> clientFactory, Action<AccountProfile, string>? play = null)
    {
        _paths = paths; _use = use; _play = play ?? use; _clientFactory = clientFactory;
        InitializeComponent(); Account.ItemsSource = profiles;
        Loaded += (_, _) => { if (Account.SelectedItem is null) Account.SelectedItem = profiles.FirstOrDefault(p => p.Id.ToString() == _restoreView?.GetValueOrDefault("account")) ?? profiles.FirstOrDefault(p => p.IsSignedIn); };
    }
    private async void Account_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_starting || _disposed || Account.SelectedItem is not AccountProfile profile) return;
        _starting = true; Account.IsEnabled = false;
        try
        {
            DisposeSocial(true); _profile = profile;
            var holder = (System.Windows.Controls.Border)Browser.Parent;
            Browser.Dispose(); holder.Child = null;
            Browser = new Microsoft.Web.WebView2.Wpf.WebView2 { Visibility = Visibility.Hidden }; holder.Child = Browser;
            BrowserHost.Visibility = Visibility.Collapsed;
            await ShowSocialAsync(false, false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) Status.Text = L.T("Não foi possível acessar os dados. Atualize o login desta conta e tente novamente."); }
        finally { _starting = false; if (!_disposed) Account.IsEnabled = !NativeBusy; }
        var restore = _restoreView?.GetValueOrDefault("view"); _restoreView = null;
        if (restore == "AvatarTab") await OpenAvatarAsync();
        else if (restore == "FriendsTab") await OpenSocialAsync(false, true);
        else if (restore == "OutfitsTab") await OpenSocialAsync(true);
        else if (restore == "MoreTab") Groups_Click(this, new RoutedEventArgs());
    }
    private async Task OpenOfficialAsync(string path)
    {
        if (_disposed || IsBusy || _profile is not { } profile) return;
        _starting = true; Account.IsEnabled = false;
        try
        {
            DisposeSocial(true); SelectPortalTab(MoreTab); _nativeVisible = false;
            NativeHost.Visibility = Visibility.Collapsed; BrowserHost.Visibility = Visibility.Visible;
            UseDestinationButton.Visibility = Visibility.Collapsed;
            Browser.Visibility = _covered ? Visibility.Hidden : Visibility.Visible;
            if (Browser.CoreWebView2 is null)
            {
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: _paths.BrowserProfilePath(profile));
                if (_disposed) return;
                await Browser.EnsureCoreWebView2Async(environment);
                if (_disposed || Browser.CoreWebView2 is null) return;
                AccountLoginWindow.ConfigureLoginBrowser(Browser.CoreWebView2);
                Browser.CoreWebView2.NavigationCompleted += (_, _) => RefreshBrowserDestination();
                Browser.CoreWebView2.SourceChanged += (_, _) => RefreshBrowserDestination();
                Browser.CoreWebView2.NavigationStarting += (_, navigation) =>
                {
                    UseDestinationButton.Visibility = Visibility.Collapsed;
                    if (!Uri.TryCreate(navigation.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                        || !(uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase)))
                    { navigation.Cancel = true; Status.Text = L.T("Uma navegação externa foi bloqueada."); }
                };
                Browser.CoreWebView2.LaunchingExternalUriScheme += (_, launch) =>
                { launch.Cancel = true; Status.Text = L.T("Use Abrir destino no gerenciador para iniciar o jogo com a conta selecionada."); };
                Browser.CoreWebView2.NewWindowRequested += async (_, popup) =>
                {
                    popup.Handled = true;
                    if (_disposed || !LoginBrowserPolicy.IsLoginPopup(popup.Uri) || _popups.Count >= 3) return;
                    using var deferral = popup.GetDeferral();
                    var window = new LoginPopupWindow(Window.GetWindow(this)); _popups.Add(window);
                    window.Closed += (_, _) => _popups.Remove(window);
                    try
                    {
                        window.Show();
                        var core = await window.InitializeAsync(environment, (_, nested) => nested.Handled = true, _ => { });
                        if (!_disposed && core is not null) popup.NewWindow = core;
                    }
                    catch (Exception) { window.Close(); Status.Text = L.T("Não foi possível abrir a janela de verificação."); }
                };
            }
            Browser.CoreWebView2.Navigate("https://www.roblox.com" + path);
            Status.Text = L.T("Página oficial do Roblox · use somente sua própria conta.");
        }
        catch (Exception) { if (!_disposed) Status.Text = L.T("Não foi possível abrir a página desta conta. Atualize o login e tente novamente."); }
        finally { _starting = false; if (!_disposed) Account.IsEnabled = true; }
    }
    private void SelectPortalTab(System.Windows.Controls.Button selected)
    { _currentView = selected.Name; foreach (var tab in PortalTabs.Children.OfType<System.Windows.Controls.Button>()) NavigationState.SetActive(tab, ReferenceEquals(tab, selected)); }
    private async void Friends_Click(object sender, RoutedEventArgs e) => await OpenSocialAsync(false, true);
    private async void Avatar_Click(object sender, RoutedEventArgs e) => await OpenAvatarAsync();
    private async void Groups_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || IsBusy || _profile is not { RobloxUserId: > 0 } profile) return;
        _starting = true; Account.IsEnabled = false;
        try {
            var client = await _clientFactory(profile, _lifetime.Token);
            if (_disposed) { client.Dispose(); return; }
            DisposeSocial(true); _socialClient = client;
            _communities = new CommunitiesPanel(client, profile.RobloxUserId.Value);
            _communities.BusyChanged += (_, _) => Account.IsEnabled = !_starting && !NativeBusy;
            SelectPortalTab(MoreTab); _nativeVisible = true; BrowserHost.Visibility = Visibility.Collapsed; Browser.Visibility = Visibility.Hidden; NativeHost.Visibility = Visibility.Visible; UseDestinationButton.Visibility = Visibility.Collapsed;
            SocialHost.Content = _communities; UiMotion.Reveal(SocialHost); Status.Text = "";
        } catch (Exception) { Status.Text = L.T("Não foi possível carregar os dados desta conta. Tente atualizar novamente."); }
        finally { _starting = false; Account.IsEnabled = !NativeBusy; }
    }
    private async void Privacy_Click(object sender, RoutedEventArgs e) => await OpenOfficialAsync("/my/account#!/privacy");
    private async void Security_Click(object sender, RoutedEventArgs e) => await OpenOfficialAsync("/my/account#!/security");
    private async void QuickLogin_Click(object sender, RoutedEventArgs e)
    {
        await OpenOfficialAsync("/crossdevicelogin/ConfirmCode");
        if (!_disposed && !_nativeVisible) Status.Text = L.T("Use o código exibido no seu outro dispositivo para autorizar a entrada nesta conta.");
    }
    public void SetCovered(bool covered) { _covered = covered; Browser.Visibility = covered || _nativeVisible ? Visibility.Hidden : Visibility.Visible; }
    private void More_Click(object sender, RoutedEventArgs e)
    { if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; } }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    { if (IsBusy || _disposed) return; if (_nativeVisible && _communities is not null) await _communities.RefreshAsync(); else if (_nativeVisible && _avatarEditor is not null) await _avatarEditor.RefreshAsync(); else if (_nativeVisible && _social is not null) await _social.RefreshAsync(); else if (Browser.CoreWebView2 is not null) Browser.Reload(); }
    private async void OnlineFriends_Click(object sender, RoutedEventArgs e) => await OpenSocialAsync(false);
    private async void SavedOutfits_Click(object sender, RoutedEventArgs e) => await OpenSocialAsync(true);
    private async Task OpenSocialAsync(bool outfits, bool allFriends = false)
    {
        if (IsBusy || _disposed || _profile?.RobloxUserId is not > 0) return;
        _starting = true; Account.IsEnabled = false;
        try { await ShowSocialAsync(outfits, allFriends); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) Status.Text = L.T("Não foi possível acessar os dados. Atualize o login desta conta e tente novamente."); }
        finally { _starting = false; if (!_disposed) Account.IsEnabled = !NativeBusy; }
    }
    private async Task ShowSocialAsync(bool outfits, bool allFriends)
    {
        if (_disposed || _profile is not { RobloxUserId: > 0 } profile) return;
        var service = await ServiceAsync(profile);
        DisposeSocial(); SelectPortalTab(outfits ? OutfitsTab : allFriends ? FriendsTab : OnlineTab);
        _social = new SocialPanel(service, outfits, destination => _use(profile, destination), profile.PublicName, allFriends, destination => _play(profile, destination));
        _social.BusyChanged += (_, _) => Account.IsEnabled = !_starting && !NativeBusy;
        _social.AvatarChanged += (_, _) => AvatarChanged?.Invoke(profile.Id);
        _social.ProfileRequested += async id => await OpenOfficialAsync($"/users/{id}/profile");
        _nativeVisible = true; BrowserHost.Visibility = Visibility.Collapsed; NativeHost.Visibility = Visibility.Visible;
        UseDestinationButton.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Hidden; Status.Text = ""; SocialHost.Content = _social; UiMotion.Reveal(SocialHost);
    }
    private async Task OpenAvatarAsync()
    {
        if (IsBusy || _disposed || _profile is not { RobloxUserId: > 0 } profile) return;
        _starting = true; Account.IsEnabled = false; Status.Text = L.T("Carregando...");
        try
        {
            var service = await ServiceAsync(profile);
            DisposeSocial(); SelectPortalTab(AvatarTab);
            _avatarEditor = new AvatarEditorPanel(service, profile.PublicName, profile.PublicAvatarUrl);
            _avatarEditor.ExpandRequested += (_, _) => AccountToolbar.Visibility = AccountToolbar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            _avatarEditor.BusyChanged += (_, _) => Account.IsEnabled = !_starting && !NativeBusy;
            _avatarEditor.AvatarChanged += (_, _) => AvatarChanged?.Invoke(profile.Id);
            _nativeVisible = true; BrowserHost.Visibility = Visibility.Collapsed; NativeHost.Visibility = Visibility.Visible;
            UseDestinationButton.Visibility = Visibility.Collapsed; Browser.Visibility = Visibility.Hidden;
            Status.Text = ""; SocialHost.Content = _avatarEditor; UiMotion.Reveal(SocialHost);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!_disposed) Status.Text = L.T("Não foi possível acessar os dados. Atualize o login desta conta e tente novamente."); }
        finally { _starting = false; if (!_disposed) Account.IsEnabled = !NativeBusy; }
    }
    public void UpdateProfilePortrait(AccountProfile updated, Action? updateCollection = null)
    {
        if (_disposed) { updateCollection?.Invoke(); return; }
        var starting = _starting; _starting = true;
        try { updateCollection?.Invoke(); if (_profile?.Id == updated.Id) { _profile = updated; Account.SelectedItem = updated; _avatarEditor?.UpdatePortrait(updated.PublicAvatarUrl); } }
        finally { _starting = starting; }
    }
    internal static bool IsGameDestinationUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!uri.AbsolutePath.StartsWith("/games/", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.Equals("/share", StringComparison.OrdinalIgnoreCase)) return false;
        return RobloxLinkParser.TryParse(value, out _, out _);
    }
    private void RefreshBrowserDestination() => UseDestinationButton.Visibility = !_nativeVisible && IsGameDestinationUri(Browser.Source?.AbsoluteUri) ? Visibility.Visible : Visibility.Collapsed;
    private void DisposeSocial(bool releaseClient = false)
    {
        AccountToolbar.Visibility = Visibility.Visible;
        _social?.Dispose(); _avatarEditor?.Dispose(); _communities?.Dispose(); _communities = null; SocialHost.Content = null; _social = null; _avatarEditor = null;
        if (releaseClient) { _socialClient?.Dispose(); _socialClient = null; _accountService = null; }
    }
    private void UseDestination_Click(object sender, RoutedEventArgs e)
    {
        if (!_nativeVisible && _profile is not null && IsGameDestinationUri(Browser.Source?.AbsoluteUri) && RobloxLinkParser.TryParse(Browser.Source?.AbsoluteUri, out var target, out _)) _use(_profile, target.LauncherUrl);
        else Status.Text = L.T("Abra a página de um jogo para selecionar esse destino.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        foreach (var popup in _popups.ToArray()) popup.Close();
        DisposeSocial(true); Browser.Dispose(); _lifetime.Dispose();
    }
}
