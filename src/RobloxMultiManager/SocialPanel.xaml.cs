using System.Collections.ObjectModel;
using System.Net.Http;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class SocialPanel : System.Windows.Controls.UserControl, IDisposable
{
    private readonly AccountSocialService _service;
    private readonly Action<string> _use;
    private readonly Action<string> _play;
    private readonly bool _outfits, _allFriends;
    private readonly ObservableCollection<OwnedOutfit> _outfitRows = [];
    private CancellationTokenSource? _operation;
    private bool _disposed;
    private string? _cursor;
    private bool _loaded;
    private long? _aliasTarget;
    public Visibility ManagementVisibility => _allFriends ? Visibility.Visible : Visibility.Collapsed;
    private void RowAlias_Click(object sender, RoutedEventArgs e) { if (IsBusy || sender is not FrameworkElement { DataContext: OnlineFriend row }) return; Friends.SelectedItem = row; EditAlias_Click(sender, e); }
    private void RowRemove_Click(object sender, RoutedEventArgs e) { if (IsBusy || sender is not FrameworkElement { DataContext: OnlineFriend row }) return; Friends.SelectedItem = row; RemoveFriend_Click(sender, e); }
    public event EventHandler? AvatarChanged;
    public event Action<long>? ProfileRequested;
    public bool HasDraft => _aliasTarget is not null;
    public bool IsBusy => _operation is not null;
    public event EventHandler? BusyChanged;
    public SocialPanel(AccountSocialService service, bool outfits, Action<string> use, string accountName = "", bool allFriends = false, Action<string>? play = null)
    {
        _service = service; _outfits = outfits; _allFriends = allFriends; _use = use; _play = play ?? use; InitializeComponent();
        AccountCaption.Text = accountName;
        L.LanguageChanged += LanguageChanged;
        Title.Text = outfits ? L.T("Roupas salvas") : allFriends ? L.T("Amigos") : L.T("Amigos online");
        if (outfits) { ViewProfile.Visibility = Visibility.Collapsed; Friends.Visibility = UseServer.Visibility = Visibility.Collapsed; OutfitArea.Visibility = ApplyOutfit.Visibility = Visibility.Visible; Outfits.ItemsSource = _outfitRows; }
        RemoveFriend.Visibility = EditAlias.Visibility = ViewProfile.Visibility = UseServer.Visibility = Visibility.Collapsed;
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await LoadAsync(false); } };
        Unloaded += (_, _) => _operation?.Cancel();
    }
    public Task RefreshAsync() { _service.InvalidateReads(); return LoadAsync(false); }
    private async Task LoadAsync(bool more)
    {
        if (IsBusy || _disposed) return;
        using var source = new CancellationTokenSource(); _operation = source; UpdateActions(); Status.Text = L.T("Carregando...");
        try
        {
            if (_outfits)
            {
                var page = await _service.GetOutfitPageAsync(more ? _cursor : null, source.Token, rows =>
                { if (!more && !_disposed && !source.IsCancellationRequested) { _outfitRows.Clear(); foreach (var row in rows) _outfitRows.Add(row); } });
                if (_disposed) return;
                if (!more) _outfitRows.Clear();
                foreach (var item in page.Items) if (_outfitRows.All(o => o.Id != item.Id)) _outfitRows.Add(item);
                _cursor = page.Cursor; More.Visibility = _cursor is null ? Visibility.Collapsed : Visibility.Visible;
                Status.Text = _outfitRows.Count == 0 ? L.T("Nenhum visual salvo nesta conta.") : L.T("Escolha um visual para ver a prévia.");
            }
            else
            {
                void ShowRows(OnlineFriend[] rows) { if (!_disposed && !source.IsCancellationRequested) { Friends.ItemsSource = rows; UpdateCount(); } }
                var friends = _allFriends ? await _service.GetFriendsAsync(source.Token, ShowRows) : await _service.GetOnlineFriendsAsync(source.Token, ShowRows);
                if (_disposed) return;
                Friends.ItemsSource = friends; UpdateCount();
                Status.Text = friends.Length == 0 ? (_allFriends ? L.T("Nenhum amigo encontrado.") : L.T("Nenhum amigo online encontrado.")) : L.T("A entrada depende da privacidade do amigo e das permissões do servidor.");
            }
        }
        catch (Exception ex) { ShowFailure(ex, source); }
        finally { _operation = null; if (!_disposed) UpdateActions(); }
    }
    private void ShowFailure(Exception ex, CancellationTokenSource source)
    {
        if (_disposed) return;
        Status.Text = ex switch
        {
            OperationCanceledException when source.IsCancellationRequested => L.T("Operação cancelada."),
            OperationCanceledException => L.T("O Roblox demorou a responder. Tente atualizar mais tarde."),
            AccountVerificationRequiredException => L.T("O Roblox pediu uma verificação. Conclua-a na página oficial desta conta."),
            RobloxApiException api => api.Message,
            HttpRequestException => L.T("Não foi possível conectar ao Roblox. Verifique a conexão e tente novamente."),
            InvalidDataException or System.Text.Json.JsonException => L.T("O Roblox retornou dados inesperados. Tente atualizar mais tarde."),
            InvalidOperationException => ex.Message,
            _ => L.T("Não foi possível carregar os dados desta conta. Tente atualizar novamente.")
        };
    }
    private void LanguageChanged(object? sender, EventArgs e) { Title.Text = _outfits ? L.T("Roupas salvas") : _allFriends ? L.T("Amigos") : L.T("Amigos online"); if (!_outfits && Friends.ItemsSource is not null) UpdateCount(); }
    private void UpdateCount() => FriendCount.Text = L.F("{0} amigos", Friends.Items.Count);
    private void UpdateActions()
    {
        BusyChanged?.Invoke(this, EventArgs.Empty);
        Stop.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        Refresh.IsEnabled = More.IsEnabled = !IsBusy;
        UseServer.IsEnabled = !IsBusy && Friends.SelectedItem is OnlineFriend { CanJoin: true };
        ApplyOutfit.IsEnabled = !IsBusy && Outfits.SelectedItem is OwnedOutfit;
        RemoveFriend.IsEnabled = EditAlias.IsEnabled = ViewProfile.IsEnabled = !IsBusy && Friends.SelectedItem is OnlineFriend;
        SaveAlias.IsEnabled = !IsBusy && _aliasTarget is not null;
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void More_Click(object sender, RoutedEventArgs e) { if (_cursor is not null) await LoadAsync(true); }
    private void Friend_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_aliasTarget != (Friends.SelectedItem as OnlineFriend)?.UserId) { _aliasTarget = null; AliasPanel.Visibility = Visibility.Collapsed; }
        UpdateActions();
    }
    private void FriendPlay_Click(object sender, RoutedEventArgs e)
    { if (!_disposed && !IsBusy && sender is FrameworkElement { DataContext: OnlineFriend { CanJoin: true, Destination: { } destination } }) _play(destination); }
    private void ViewProfile_Click(object sender, RoutedEventArgs e)
    { if (!IsBusy && Friends.SelectedItem is OnlineFriend friend) ProfileRequested?.Invoke(friend.UserId); }
    private async Task FriendActionAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        using var source = new CancellationTokenSource(); _operation = source; UpdateActions(); Status.Text = L.T("Carregando...");
        try { await action(source.Token); }
        catch (Exception ex) { ShowFailure(ex, source); }
        finally { _operation = null; if (!_disposed) UpdateActions(); }
    }
    private async void RemoveFriend_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || Friends.SelectedItem is not OnlineFriend friend) return;
        if (AppDialog.Show(Window.GetWindow(this), L.F("Remover {0} dos amigos de {1}?", friend.Caption, AccountCaption.Text), L.T("Remover amizade"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await FriendActionAsync(async token => { await _service.RemoveFriendAsync(friend.UserId, token); if (_disposed) return; Friends.ItemsSource = Friends.Items.OfType<OnlineFriend>().Where(f => f.UserId != friend.UserId).ToArray(); UpdateCount(); Status.Text = L.T("Remoção confirmada pelo Roblox."); });
    }
    private async void EditAlias_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || Friends.SelectedItem is not OnlineFriend friend) return;
        await FriendActionAsync(async token =>
        {
            var alias = await _service.GetFriendAliasAsync(friend.UserId, token);
            if (_disposed || (Friends.SelectedItem as OnlineFriend)?.UserId != friend.UserId) return;
            _aliasTarget = friend.UserId; AliasInput.Text = alias; AliasCaption.Text = friend.Caption; AliasPanel.Visibility = Visibility.Visible;
            Status.Text = L.T("Este apelido é salvo no Roblox e só aparece para esta conta.");
        });
    }
    private async void SaveAlias_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _aliasTarget is not { } target) return;
        var alias = AliasInput.Text.Trim();
        await FriendActionAsync(async token => { await _service.SetFriendAliasAsync(target, alias, token); if (_disposed) return; Friends.ItemsSource = Friends.Items.OfType<OnlineFriend>().Select(f => f.UserId == target ? f with { Alias = alias } : f).ToArray(); AliasPanel.Visibility = Visibility.Collapsed; Status.Text = L.T("Apelido confirmado pelo Roblox."); });
    }
    private void Outfit_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Outfits.SelectedItem is OwnedOutfit outfit)
        {
            OutfitCaption.Text = outfit.Name;
            OutfitPreview.Source = outfit.ThumbnailUrl is { } url ? new BitmapImage(new Uri(url)) : null;
        }
        else { OutfitCaption.Text = ""; OutfitPreview.Source = null; }
        UpdateActions();
    }
    private async void ApplyOutfit_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _disposed || Outfits.SelectedItem is not OwnedOutfit outfit) return;
        if (AppDialog.Show(Window.GetWindow(this), L.F("Aplicar os itens de {0} em {1}? Cores, escala e tipo do corpo serão mantidos. Nenhum item será comprado.", outfit.Name, AccountCaption.Text), L.T("Aplicar itens"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        using var source = new CancellationTokenSource(); _operation = source; UpdateActions(); Status.Text = L.T("Aplicando itens...");
        try { await _service.ApplyOutfitItemsAsync(outfit.Id, source.Token); if (!_disposed) { Status.Text = L.T("Itens aplicados e confirmados pelo Roblox. Um jogo aberto pode exigir renascer ou entrar novamente."); AvatarChanged?.Invoke(this, EventArgs.Empty); } }
        catch (Exception ex) { ShowFailure(ex, source); }
        finally { _operation = null; if (!_disposed) UpdateActions(); }
    }
    private void UseServer_Click(object sender, RoutedEventArgs e) { if (!IsBusy && Friends.SelectedItem is OnlineFriend { Destination: { } destination }) _use(destination); }
    public void Dispose() { L.LanguageChanged -= LanguageChanged; _disposed = true; _operation?.Cancel(); }
}
