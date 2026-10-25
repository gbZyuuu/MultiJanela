using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media.Imaging;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class AvatarEditorPanel : System.Windows.Controls.UserControl, IDisposable
{
    private readonly AccountSocialService _service;
    private readonly ObservableCollection<AvatarEditorItem> _inventory = [], _equipped = [];
    private CancellationTokenSource? _operation;
    private string? _cursor;
    private bool _loaded, _disposed, _dirty;
    public bool HasDraft => _dirty || _bodyDirty;
    public bool IsBusy => _operation is not null;
    public event EventHandler? BusyChanged;
    public event EventHandler? AvatarChanged;
    public event EventHandler? ExpandRequested;
    private bool _expanded;
    private void Expand_Click(object sender, RoutedEventArgs e)
    { _expanded = !_expanded; ExpandEditor.Content = (_expanded ? L.T("Restaurar editor") : L.T("Ampliar editor")); ExpandRequested?.Invoke(this, EventArgs.Empty); }
    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || sender is not FrameworkElement { DataContext: AvatarEditorItem item }) return;
        _equipped.Remove(item); _dirty = true;
        Status.Text = L.T("Prévia de itens alterada. Clique em Aplicar itens para confirmar.");
        UpdateActions(); QueuePreview();
    }

    public AvatarEditorPanel(AccountSocialService service, string name, string? portrait)
    {
        _service = service; InitializeComponent(); AccountCaption.Text = name;
        Inventory.ItemsSource = _inventory; Equipped.ItemsSource = _equipped;
        InitializeStudio();
        Loaded += async (_, _) => { if (!_loaded) { _loaded = true; await RefreshAsync(false); } };
        Unloaded += (_, _) => { _operation?.Cancel(); _previewCancellation?.Cancel(); };
    }
    public void UpdatePortrait(string? url) { if (_loaded && !_disposed) _ = LoadFullBodyAsync(CancellationToken.None); }
    private async Task LoadFullBodyAsync(CancellationToken token)
    {
        try { var url = await _service.GetFullBodyThumbnailAsync(token); if (!_disposed && !token.IsCancellationRequested && url is not null) Portrait.Source = new BitmapImage(new Uri(url)); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* The 3D request remains independent of the static thumbnail. */ }
    }
    public async Task RefreshAsync(bool invalidate = true)
    {
        if (IsBusy || _disposed) return;
        if ((_dirty || _bodyDirty) && AppDialog.Show(Window.GetWindow(this), L.T("Descartar as alterações deste visual e recarregar o avatar?"), L.T("Editor de avatar"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WorkAsync(async token =>
        {
            if (invalidate) { _service.InvalidateReads(); _previewCache.Clear(); }
            var wornTask = _service.GetEquippedItemsAsync(token);
            var pageTask = _service.GetAvatarInventoryAsync(token: token, assetType: (Category.SelectedItem as AvatarCategory)?.Type ?? 0);
            var bodyTask = LoadBodyAsync(token);
            var portraitTask = LoadFullBodyAsync(token);
            await Task.WhenAll(wornTask, pageTask, bodyTask, portraitTask);
            var worn = await wornTask;
            await bodyTask;
            if (_disposed) return;
            _equipped.Clear(); foreach (var item in worn) _equipped.Add(item);
            _dirty = false; QueuePreview();
            var page = await pageTask;
            await portraitTask;
            if (_disposed) return;
            _equipped.Clear(); foreach (var item in worn) _equipped.Add(item);
            _inventory.Clear(); foreach (var item in page.Items) _inventory.Add(item);
            _cursor = page.Cursor; _dirty = false;

            Status.Text = L.T("Itens equipados confirmados pelo Roblox. A foto pode demorar para atualizar.");
        });
    }
    private async Task WorkAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _disposed) return;
        using var source = new CancellationTokenSource(); _operation = source; UpdateActions(); Status.Text = L.T("Carregando...");
        try { await action(source.Token); }
        catch (OperationCanceledException) { if (!_disposed) Status.Text = source.IsCancellationRequested ? L.T("Operação cancelada.") : L.T("O Roblox demorou a responder. Tente atualizar mais tarde."); }
        catch (AccountVerificationRequiredException) { if (!_disposed) Status.Text = L.T("O Roblox pediu uma verificação. Conclua-a na página oficial desta conta."); }
        catch (RobloxApiException ex) { if (!_disposed) Status.Text = ex.Message; }
        catch (InvalidOperationException ex) { if (!_disposed) Status.Text = ex.Message; }
        catch (Exception) { if (!_disposed) Status.Text = L.T("Não foi possível carregar os dados desta conta. Tente atualizar novamente."); }
        finally { _operation = null; if (!_disposed) UpdateActions(); }
    }
    private void UpdateActions()
    {
        Refresh.IsEnabled = Equip.IsEnabled = Remove.IsEnabled = More.IsEnabled = Category.IsEnabled = !IsBusy;
        ApplyBody.IsEnabled = !IsBusy && _bodyDirty && DraftBody() is not null;
        ColorsForm.IsEnabled = ScalesForm.IsEnabled = RigType.IsEnabled = !IsBusy;
        Apply.IsEnabled = !IsBusy && _dirty && _equipped.Count > 0;
        More.Visibility = _cursor is null ? Visibility.Collapsed : Visibility.Visible;
        Stop.Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed;
        BusyChanged?.Invoke(this, EventArgs.Empty);
    }
    private void Equip_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        foreach (var item in Inventory.SelectedItems.OfType<AvatarEditorItem>())
            if (_equipped.Count < 50 && !_equipped.Any(x => x.Id == item.Id)) { _equipped.Add(item); _dirty = true; }
        if (_dirty) Status.Text = L.T("Prévia de itens alterada. Clique em Aplicar itens para confirmar.");
        UpdateActions(); QueuePreview();
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        foreach (var item in Equipped.SelectedItems.OfType<AvatarEditorItem>().ToArray()) { _equipped.Remove(item); _dirty = true; }
        Status.Text = L.T("Prévia de itens alterada. Clique em Aplicar itens para confirmar."); UpdateActions(); QueuePreview();
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !_dirty || _equipped.Count == 0) return;
        if (AppDialog.Show(Window.GetWindow(this), L.F("Aplicar {0} item(ns) em {1}? Nenhum item será comprado.", _equipped.Count, AccountCaption.Text), L.T("Aplicar itens"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await WorkAsync(async token =>
        {
            await _service.ApplyEditorItemsAsync(_equipped.Select(x => x.Id), token);
            if (_disposed) return; _dirty = false;
            Status.Text = L.T("Itens aplicados e confirmados pelo Roblox. Um jogo aberto pode exigir renascer ou entrar novamente.");
            AvatarChanged?.Invoke(this, EventArgs.Empty); QueuePreview();
        });
    }
    private async void More_Click(object sender, RoutedEventArgs e)
    {
        if (_cursor is null) return;
        await WorkAsync(async token => { var page = await _service.GetAvatarInventoryAsync(_cursor, token, (Category.SelectedItem as AvatarCategory)?.Type ?? 0); if (_disposed) return; foreach (var item in page.Items) if (!_inventory.Any(i => i.Id == item.Id)) _inventory.Add(item); _cursor = page.Cursor; Status.Text = ""; });
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Stop_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    public void Dispose() { _disposed = true; _operation?.Cancel(); _previewCancellation?.Cancel(); }
}
