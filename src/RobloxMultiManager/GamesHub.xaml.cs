using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RobloxMultiManager.Core;
using UserControl = System.Windows.Controls.UserControl;

namespace RobloxMultiManager;

public partial class GamesHub : UserControl
{
    private readonly RobloxGameService _service;
    private readonly GameLibraryStore _store;
    private readonly Action<string, bool> _use;
    private readonly Func<int> _groupCount;
    private readonly List<GameBookmark> _bookmarks = [];
    private readonly ObservableCollection<PublicGameServer> _servers = [];
    private CancellationTokenSource? _operation;
    private GameSummary? _selected;
    private bool _ready, _disposed;
    private string? _cursor;
    private int _serverEpoch;private bool _serversAtEnd;
    private readonly HashSet<string> _serverCursors=[];
    private DateTime _retryAt, _serverRetryAt, _serverRefreshAt, _serverPageAt;
    private bool? _loadedServerSort;
    private readonly DispatcherTimer _serverCooldown = new() { Interval = TimeSpan.FromSeconds(1) };
    private sealed record ServerSnapshot(PublicGameServer[] Rows, string? Cursor, bool? Sort, DateTime Saved);
    private readonly Dictionary<long, ServerSnapshot> _serverSnapshots = [];
    private readonly SemaphoreSlim _networkGate = new(1);
    private readonly DispatcherTimer _queryTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private enum BrowseView { All, MostPlayed, Search, Favorites, Recent, Saved }
    private BrowseView _view = BrowseView.All;
    private int _discoveryOffset, _operations;
    private string? _searchCursor;
    private string _searchTerm = "";
    private bool _hasMore, _editingQuery;

    private Task? _loadTask;
    private Dictionary<string, string>? _pendingDraft;
    internal Dictionary<string, string> CaptureDraft() => new()
    {
        ["query"] = Query.Text, ["view"] = _view.ToString(), ["games"] = JsonSerializer.Serialize(Games.Items.OfType<GameSummary>().Take(100).ToArray()),
        ["selected"] = (_selected?.PlaceId ?? 0).ToString(), ["preference"] = Preference.SelectedIndex.ToString(), ["server"] = ServerId.Text
    };
    internal void RestoreDraft(Dictionary<string, string>? draft) { _pendingDraft = draft; }
    private void ApplyDraft()
    {
        if (_pendingDraft is not { } draft) return; _pendingDraft = null;
        try
        {
            _editingQuery = true; Query.Text = draft.GetValueOrDefault("query", ""); _editingQuery = false;
            if (Enum.TryParse<BrowseView>(draft.GetValueOrDefault("view"), out var view) && Enum.IsDefined(view)) _view = view;
            if (int.TryParse(draft.GetValueOrDefault("preference"), out var preference) && preference is >= 0 and <= 2) Preference.SelectedIndex = preference;
            var json = draft.GetValueOrDefault("games", "[]");
            if (json.Length <= 128000)
            {
                var games = JsonSerializer.Deserialize<GameSummary[]>(json) ?? [];
                if (games.Length <= 100 && games.All(g => g is { IsValid: true }))
                {
                    Games.ItemsSource = games;
                    if (long.TryParse(draft.GetValueOrDefault("selected"), out var place)) Games.SelectedItem = games.FirstOrDefault(g => g.PlaceId == place);
                }
            }
            ServerId.Text = draft.GetValueOrDefault("server", "");
        }
        catch (JsonException) { }
    }
    private readonly SemaphoreSlim _saveGate = new(1);
    private int _writes;
    public bool IsBusy => _operations != 0 || _writes != 0;

    public GamesHub(RobloxGameService service, GameLibraryStore store, Action<string, bool> use, Func<int> groupCount)
    {
        _service = service; _store = store; _use = use; _groupCount = groupCount;
        InitializeComponent(); Servers.ItemsSource = _servers;
        if(System.Windows.Data.CollectionViewSource.GetDefaultView(_servers) is System.Windows.Data.ListCollectionView view)
            view.CustomSort=Comparer<PublicGameServer>.Create((left,right)=>
            {var count=Math.Max(1,_groupCount());var fit=(right.Available>=count).CompareTo(left.Available>=count);return fit!=0?fit:Preference.SelectedIndex==1?left.Playing.CompareTo(right.Playing):Preference.SelectedIndex==2?0:right.Playing.CompareTo(left.Playing);});
        SizeChanged += (_, _) => Inspector.Width = Math.Min(780, Math.Max(360, ActualWidth - 44));
        Loaded += async (_, _) =>
        {
            if (!_ready) await RunAsync(async _ => { await EnsureLoadedAsync(); });
            if (_pendingDraft is not null) ApplyDraft();
            // An empty handoff draft is not a loaded discovery feed.
            if (Games.Items.Count == 0 && _view is BrowseView.All or BrowseView.MostPlayed or BrowseView.Search) await BrowseAsync(reset: true);
            RefreshLabels();
            RefreshPrivacy(); UpdateServerActions();
        };
        Unloaded += (_, _) => { _queryTimer.Stop(); _serverCooldown.Stop(); _operation?.Cancel(); };
        _queryTimer.Tick += async (_, _) => { _queryTimer.Stop(); await BrowseAsync(reset: true); };
        L.LanguageChanged += LanguageChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) UpdateServerActions(); else _serverCooldown.Stop(); };
        _serverCooldown.Tick += (_, _) => UpdateServerActions();
        Preference.SelectionChanged += async (_, _) =>
        {
            System.Windows.Data.CollectionViewSource.GetDefaultView(_servers).Refresh();
            _serverEpoch++; _operation?.Cancel();
            if (_selected is not null) await LoadServersAsync(replace: true, refresh: true);
        };
    }
    public void Dispose() { _disposed = true; _serverCooldown.Stop(); _queryTimer.Stop(); _operation?.Cancel(); L.LanguageChanged -= LanguageChanged; }
    public void RefreshPrivacy() => PrivateLinks.Visibility = DisplayPrivacy.Enabled ? Visibility.Hidden : Visibility.Visible;
    private void LanguageChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { RefreshLabels(); Status.Text = _operation is null ? "" : L.T("Carregando..."); if (_selected is null) GameName.Text = L.T("Escolha um jogo"); });
    private void RefreshLabels()
    {
        GroupSize.Text = L.F("Espaço necessário: {0} conta(s) da sequência.", Math.Max(1, _groupCount()));
        NoResults.Visibility = Games.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultsCaption.Text = L.F("{0} jogos", Games.Items.Count);
        MoreGamesButton.Visibility = _hasMore ? Visibility.Visible : Visibility.Collapsed;
        NavigationState.SetActive(AllTab, _view == BrowseView.All); NavigationState.SetActive(PopularTab, _view == BrowseView.MostPlayed);
        NavigationState.SetActive(FavoritesTab, _view == BrowseView.Favorites); NavigationState.SetActive(RecentTab, _view == BrowseView.Recent); NavigationState.SetActive(SavedTab, _view == BrowseView.Saved);
        FavoriteButton.Content = _bookmarks.Any(b => b.Game.PlaceId == _selected?.PlaceId && b.Favorite) ? L.T("Remover dos favoritos") : L.T("Favoritar");
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, bool replace = false, bool serverRequest = false)
    {
        if (!replace && _operation is not null) return;
        if (DateTime.UtcNow < (serverRequest ? _serverRetryAt : _retryAt)) { Status.Text = L.T("Aguarde antes de atualizar novamente."); return; }
        if (replace) _operation?.Cancel();
        using var operation = new CancellationTokenSource(); _operation = operation; _operations++; var entered = false;
        CancelSearchButton.Visibility = Visibility.Visible; Status.Text = L.T("Carregando...");
        try
        {
            await _networkGate.WaitAsync(operation.Token); entered = true;
            await action(operation.Token);
            if (ReferenceEquals(_operation, operation) && Status.Text == L.T("Carregando...")) Status.Text = "";
        }
        catch (OperationCanceledException) { if (ReferenceEquals(_operation, operation)) Status.Text = L.T("Operação cancelada."); }
        catch (RobloxApiException ex)
        {
            if (ex.StatusCode == 429) { var until = DateTime.UtcNow + (ex.RetryAfter ?? TimeSpan.FromSeconds(30)); if (serverRequest) _serverRetryAt = until; else _retryAt = until; }
            if (ReferenceEquals(_operation, operation)) Status.Text = serverRequest && ex.StatusCode == 429 ? "" : ex.Message;
        }
        catch (Exception) { if (ReferenceEquals(_operation, operation)) Status.Text = L.T("Não foi possível carregar os dados. Verifique a conexão e tente novamente."); }
        finally
        {
            if (entered) _networkGate.Release(); _operations--;
            if (ReferenceEquals(_operation, operation)) { _operation = null; CancelSearchButton.Visibility = Visibility.Collapsed; }
            RefreshLabels(); UpdateServerActions();
        }
    }
    private async Task BrowseAsync(bool reset)
    {
        var query = Query.Text.Trim();
        if (reset) { _searchCursor = null; _discoveryOffset = 0; _searchTerm = query; _hasMore = false; }
        var view = query.Length == 0 ? (_view == BrowseView.MostPlayed ? BrowseView.MostPlayed : BrowseView.All) : BrowseView.Search;
        _view = view;
        await RunAsync(async token =>
        {
            var previous = reset ? Array.Empty<GameSummary>() : Games.Items.OfType<GameSummary>().ToArray();
            void ShowRows(GameSummary[] rows) { if (!token.IsCancellationRequested) Games.ItemsSource = previous.Concat(rows).DistinctBy(g => g.UniverseId).Take(3000).ToArray(); }
            GameSummary[] games;
            try
            {
                if (RobloxLinkParser.TryParse(query, out var target, out _) && target.PlaceId is long place)
                { games = [await _service.GetPlaceAsync(place, token)]; _hasMore = false; }
                else if (view == BrowseView.Search)
                {
                    var result = await _service.SearchAsync(_searchTerm, _searchCursor, token, ShowRows);
                    games = result.Games; _searchCursor = result.NextPageToken; _hasMore = _searchCursor is not null;
                }
                else
                {
                    var page = await _service.DiscoverAsync(view == BrowseView.MostPlayed ? GameDiscoveryCategory.MostPlayed : GameDiscoveryCategory.All, _discoveryOffset, token, ShowRows);
                    games = page.Games; _discoveryOffset = page.NextOffset; _hasMore = page.HasMore;
                }
            }
            catch (RobloxApiException) when (_bookmarks.Count > 0 && reset && query.Length == 0)
            { games = _bookmarks.Select(b => b.Game).ToArray(); _hasMore = false; Status.Text = L.T("Sem conexão com a descoberta. Exibindo jogos salvos."); }
            token.ThrowIfCancellationRequested();
            ShowRows(games);
        }, replace: true);
    }
    private void Query_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    { if (_editingQuery || SearchButton is null) return; _queryTimer.Stop(); _queryTimer.Start(); }
    private async void Search_Click(object sender, RoutedEventArgs e) { _queryTimer.Stop(); await BrowseAsync(true); }
    private void Query_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) { Search_Click(sender, e); e.Handled = true; } }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _queryTimer.Stop(); _operation?.Cancel(); }
    private async void All_Click(object sender, RoutedEventArgs e) { SetBrowseView(BrowseView.All); await BrowseAsync(true); }
    private async void Popular_Click(object sender, RoutedEventArgs e) { SetBrowseView(BrowseView.MostPlayed); await BrowseAsync(true); }
    private async void MoreGames_Click(object sender, RoutedEventArgs e) { if (_hasMore && _operation is null) await BrowseAsync(false); }
    private void SetBrowseView(BrowseView view)
    { _queryTimer.Stop(); _operation?.Cancel(); _operation = null; CancelSearchButton.Visibility = Visibility.Collapsed; Status.Text = ""; _editingQuery = true; Query.Clear(); _editingQuery = false; _view = view; }
    private void Saved_Click(object sender, RoutedEventArgs e) => ShowSaved(0);
    private void Favorites_Click(object sender, RoutedEventArgs e) => ShowSaved(1);
    private void Recent_Click(object sender, RoutedEventArgs e) => ShowSaved(2);
    private void ShowSaved(int kind)
    {
        SetBrowseView(kind == 1 ? BrowseView.Favorites : kind == 2 ? BrowseView.Recent : BrowseView.Saved); _hasMore = false;
        Games.ItemsSource = _bookmarks.Where(b => kind != 1 || b.Favorite).Where(b => kind != 2 || b.LastPlayedUtc != default).OrderByDescending(b => b.LastPlayedUtc).Select(b => b.Game).ToArray();
        RefreshLabels();
    }
    private void CloseDetails_Click(object sender, RoutedEventArgs e) => Games.SelectedItem = null;
    private async void Game_Selected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        SaveServerSnapshot();
        _selected = Games.SelectedItem as GameSummary; Details.IsEnabled = _selected is not null;
        Inspector.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        Inspector.DataContext = _selected; GameBackdrop.Visibility=Inspector.Visibility;
        Inspector.Width = Math.Min(780, Math.Max(360, ActualWidth - 44));
        BrowseSurface.Effect = _selected is null ? null : new System.Windows.Media.Effects.BlurEffect { Radius = 5, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance };
        BrowseSurface.IsHitTestVisible = _selected is null;
        if (_selected is not null) CloseGameButton.Focus(); else Games.Focus();
        PlaceIdValue.Text = _selected?.PlaceId.ToString() ?? ""; UniverseIdValue.Text = _selected?.UniverseId.ToString() ?? "";

        GameName.Text = _selected?.Name ?? L.T("Escolha um jogo"); Description.Text = _selected?.Description ?? "";
        _serverEpoch++; _operation?.Cancel(); _cursor = null; _loadedServerSort = null; _serverCursors.Clear(); _servers.Clear();
        if (_selected is { } chosen && _serverSnapshots.TryGetValue(chosen.PlaceId, out var snapshot) && DateTime.UtcNow - snapshot.Saved < TimeSpan.FromMinutes(2))
        { foreach (var row in snapshot.Rows) _servers.Add(row); _cursor = snapshot.Cursor; _loadedServerSort = snapshot.Sort; }
        MoreButton.IsEnabled = false; MoreButton.Visibility = Visibility.Collapsed; UpdateServerActions();
        PrivateLinks.ItemsSource = _bookmarks.FirstOrDefault(b => b.Game.PlaceId == _selected?.PlaceId)?.PrivateLinks ?? [];
        RefreshLabels();
        if (_selected is not null && _servers.Count == 0) await LoadServersAsync(replace: true);
    }
    private GameBookmark CurrentBookmark() => _bookmarks.FirstOrDefault(b => b.Game.PlaceId == _selected!.PlaceId)
        ?? new(_selected!, false, default, []);
    private Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();
    private async Task LoadAsync()
    {
        _bookmarks.AddRange(await _store.LoadAsync()); _ready = true;
    }
    private async Task SaveBookmarkAsync(GameSummary game, Func<GameBookmark, GameBookmark> change)
    {
        _writes++;
        try
        {
            await EnsureLoadedAsync(); await _saveGate.WaitAsync();
            try
            {
                var current = _bookmarks.FirstOrDefault(b => b.Game.PlaceId == game.PlaceId) ?? new(game, false, default, []);
                var updated = change(current);
                var next = _bookmarks.Where(b => b.Game.PlaceId != game.PlaceId).Append(updated).ToArray();
                await _store.SaveAsync(next);
                _bookmarks.Clear(); _bookmarks.AddRange(next);
                if (_selected?.PlaceId == game.PlaceId) PrivateLinks.ItemsSource = updated.PrivateLinks;
                RefreshLabels();
            }
            finally { _saveGate.Release(); }
        }
        finally { _writes--; }
    }
    private async void Favorite_Click(object sender, RoutedEventArgs e)
    { if (_selected is { } game) await RunAsync(_ => SaveBookmarkAsync(game, current => current with { Favorite = !current.Favorite })); }
    private void UseGame_Click(object sender, RoutedEventArgs e) { if (_selected is not null) _use(_selected.Url, false); }
    private void LaunchGame_Click(object sender, RoutedEventArgs e) { if (_selected is not null) _use(_selected.Url, true); }
    private void UseLink_Click(object sender, RoutedEventArgs e) { if (PrivateLinks.SelectedItem is SavedLaunchDestination link) _use(link.Link, false); }
    private void AddLink_Click(object sender, RoutedEventArgs e) => EditPrivateLink(null);
    private void EditLink_Click(object sender, RoutedEventArgs e) { if (PrivateLinks.SelectedItem is SavedLaunchDestination link) EditPrivateLink(link); }
    private void EditPrivateLink(SavedLaunchDestination? original)
    {
        if (DisplayPrivacy.Enabled) { Status.Text = L.T("Desative o modo de gravação para editar links privados."); return; }
        if (_selected is null || IsBusy || !_ready) return;
        var bookmark = CurrentBookmark();
        var dialog = new Window { Owner = Window.GetWindow(this), Title = L.T("Servidor privado"), Width = 570, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        dialog.SetResourceReference(StyleProperty, "AppWindowStyle");
        var editor = new DestinationEditorPanel(original, "", async link =>
        {
            var target = RobloxLinkParser.Parse(link.Link);
            if (target.PlaceId is long place && place != bookmark.Game.PlaceId) throw new ArgumentException();
            await SaveBookmarkAsync(bookmark.Game, current => current with { PrivateLinks = current.PrivateLinks.Where(l => l.Id != link.Id).Append(link).ToArray() });
        });
        dialog.Content = editor; editor.Completed += (_, _) => Dispatcher.BeginInvoke(new Action(dialog.Close)); editor.CancelRequested += (_, _) => dialog.Close();
        dialog.Closing += (_, e) => e.Cancel = editor.IsSaving;
        dialog.ShowDialog();
    }
    private async void DeleteLink_Click(object sender, RoutedEventArgs e)
    {
        if (PrivateLinks.SelectedItem is not SavedLaunchDestination link) return;
        if (AppDialog.Show(Window.GetWindow(this), L.F("Excluir o destino salvo ‘{0}’?", link.Name), L.T("Excluir destino"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_selected is { } game) await RunAsync(_ => SaveBookmarkAsync(game, current => current with { PrivateLinks = current.PrivateLinks.Where(l => l.Id != link.Id).ToArray() }));
    }
    private void SaveServerSnapshot()
    {
        if (_selected is null || _servers.Count == 0) return;
        if (_serverSnapshots.Count >= 16 && !_serverSnapshots.ContainsKey(_selected.PlaceId)) _serverSnapshots.Remove(_serverSnapshots.MinBy(x => x.Value.Saved).Key);
        _serverSnapshots[_selected.PlaceId] = new(_servers.ToArray(), _cursor, _loadedServerSort, DateTime.UtcNow);
    }
    private void UpdateServerActions()
    {
        if (_disposed) { _serverCooldown.Stop(); return; }
        var now = DateTime.UtcNow;
        var wait = Math.Max(0, (int)Math.Ceiling((_serverRetryAt - now).TotalSeconds));
        if (wait > 0)
        {
            ServerNotice.Text = _servers.Count > 0 ? L.F("O Roblox limitou as consultas. Aguarde {0}s; os servidores carregados foram mantidos.", wait) : L.F("O Roblox limitou as consultas. Tente novamente em {0}s.", wait);
            _serverCooldown.Start();
        }
        else { ServerNotice.Text = ""; if (now < _serverRefreshAt || now < _serverPageAt) _serverCooldown.Start(); else _serverCooldown.Stop(); }
        RefreshServersButton.IsEnabled = !IsBusy && wait == 0 && now >= _serverRefreshAt;
        MoreButton.IsEnabled = !IsBusy && wait == 0 && now >= _serverPageAt && _cursor is not null;
        MoreButton.Visibility = _cursor is not null && _serversAtEnd ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible) _serverCooldown.Stop();
    }
    private async void Servers_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _selected is null) return;
        if (DateTime.UtcNow < _serverRetryAt || DateTime.UtcNow < _serverRefreshAt) { UpdateServerActions(); return; }
        _serverRefreshAt = DateTime.UtcNow.AddSeconds(8);
        _service.InvalidateServers(_selected.PlaceId);
        await LoadServersAsync(refresh: true);
    }
    private async void MoreServers_Click(object sender, RoutedEventArgs e) => await LoadServersAsync();
    private async Task LoadServersAsync(bool replace = false, bool refresh = false)
    {
        if (_selected is null) return;
        if (DateTime.UtcNow < _serverRetryAt) { UpdateServerActions(); return; }
        if (!refresh && _cursor is not null && DateTime.UtcNow < _serverPageAt) return;
        var selected = _selected; var epoch = _serverEpoch; var preference = (ServerPreference)Preference.SelectedIndex;
        var sort = preference == ServerPreference.BestFit;
        var firstPage = refresh || _loadedServerSort != sort || _servers.Count == 0;
        await RunAsync(async token =>
        {
            _serverPageAt = DateTime.UtcNow.AddSeconds(1);
            var page = await _service.GetServersAsync(selected.PlaceId, firstPage ? null : _cursor, token, sort);
            if (_selected?.PlaceId != selected.PlaceId || epoch != _serverEpoch) return;
            // Replace only after a valid response: failures and cancellation keep the previous list.
            if (firstPage) { _servers.Clear(); _serverCursors.Clear(); }
            var known = _servers.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var row in page.Servers) if (_servers.Count < 3000 && known.Add(row.Id)) _servers.Add(row);
            _loadedServerSort = sort;
            _cursor = page.Cursor is { } cursor && _serverCursors.Add(cursor) ? cursor : null;
            if (_servers.Count >= 3000) _cursor = null;
            if (Servers.SelectedItem is null) Servers.SelectedItem = RobloxGameService.ChooseServer(_servers, Math.Max(1, _groupCount()), preference);
            SaveServerSnapshot(); RefreshLabels();
        }, replace: replace, serverRequest: true);
        UpdateServerActions();
    }
    private async void Servers_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        _serversAtEnd = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 64;
        UpdateServerActions();
        if (e.VerticalChange > 0 && _serversAtEnd && _cursor is not null && _operation is null && DateTime.UtcNow >= _serverPageAt && DateTime.UtcNow >= _serverRetryAt) await LoadServersAsync();
    }
    private void CopyServerId_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: PublicGameServer server }) System.Windows.Clipboard.SetText(server.Id); }
    private void UseServerRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PublicGameServer server }) { Servers.SelectedItem = server; UseServer_Click(sender, e); }
    }
    private void UseServer_Click(object sender, RoutedEventArgs e)
    {
        if (Servers.SelectedItem is not PublicGameServer server || _selected is null) return;
        if (server.Available < Math.Max(1, _groupCount())) { Status.Text = L.T("Este servidor não tem espaço suficiente para a sequência."); return; }
        UseServer(server.Id);
    }
    private void UseServerId_Click(object sender, RoutedEventArgs e)
    { if (Guid.TryParse(ServerId.Text.Trim(), out var id)) UseServer(id.ToString("D")); else Status.Text = L.T("Informe um ID de servidor válido."); }
    private void UseServer(string id) { if (_selected is not null) _use($"https://www.roblox.com/games/start?placeId={_selected.PlaceId}&gameInstanceId={id}", false); }
    public async Task RecordLaunchAsync(long placeId)
    {
        _writes++;
        try
        {
            await EnsureLoadedAsync();
            var game = _bookmarks.FirstOrDefault(b => b.Game.PlaceId == placeId)?.Game ?? await _service.GetPlaceAsync(placeId);
            await SaveBookmarkAsync(game, current => current with { LastPlayedUtc = DateTime.UtcNow });
        }
        finally { _writes--; }
    }
}
