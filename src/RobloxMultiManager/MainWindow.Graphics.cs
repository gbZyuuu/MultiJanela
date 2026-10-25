using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private GraphicsPanel? _graphicsPanel;
    private GraphicsLaunchPolicy _graphicsLaunchPolicy = new();
    private RobloxGraphicsSettingsService? _nativeGraphics;
    private PreferenceFile<GraphicsLaunchPolicy>? _graphicsStore;
    private readonly SemaphoreSlim _graphicsSaveGate = new(1);
    private async Task InitializeGraphicsAsync()
    {
        _nativeGraphics = new RobloxGraphicsSettingsService(isRunning: () => _activeLaunches > 0 || _launchBatchCancellation is not null || RobloxProcessMonitor.CountPlayerProcesses() > 0);
        var store = _graphicsStore = new PreferenceFile<GraphicsLaunchPolicy>(_paths, "graphics-policy.json", () => new(), GraphicsLaunchPolicy.Validate);
        try { _graphicsLaunchPolicy = await store.LoadAsync(); }
        catch (Exception) { AppendLog(L.T("Não foi possível carregar os gráficos de abertura. O arquivo original foi preservado.")); }
        _graphicsPanel = new GraphicsPanel(_nativeGraphics, Profiles, _graphicsLaunchPolicy, policy => SaveGraphicsPolicyAsync(_ => policy));
        HomeGraphics.SaveRequested = (fps, quality, enabled) => SaveGraphicsPolicyAsync(current => current with { GlobalFps = fps, GlobalQuality = quality, Enabled = enabled });
        HomeGraphics.Synchronize(_graphicsLaunchPolicy); HomeGraphics.IsEnabled = true; RefreshGraphicsRows();
        GraphicsSettingsHost.Content = _graphicsPanel;
    }
    private async Task SaveGraphicsPolicyAsync(Func<GraphicsLaunchPolicy, GraphicsLaunchPolicy> change)
    {
        if (_graphicsStore is null) throw new InvalidOperationException();
        await _graphicsSaveGate.WaitAsync();
        try
        {
            var next = GraphicsLaunchPolicy.Validate(change(_graphicsLaunchPolicy));
            await _graphicsStore.SaveAsync(next); _graphicsLaunchPolicy = next;
            HomeGraphics.Synchronize(next); _graphicsPanel?.Synchronize(next); RefreshGraphicsRows();
        }
        finally { _graphicsSaveGate.Release(); }
    }
    private void RefreshGraphicsRows()
    {
        foreach (var row in WorkspaceAccounts.Concat(SavedAccounts)) row.UpdateGraphics(_graphicsLaunchPolicy);
    }
    private async void AccountGraphics_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement { DataContext: WorkspaceAccountRow { Profile: { } profile } }) return;
        await HomeGraphics.FlushAsync();
        var editor = new GraphicsPanel(_nativeGraphics ?? new(), Profiles, _graphicsLaunchPolicy, policy => SaveGraphicsPolicyAsync(_ => policy));
        editor.SelectAccount(profile.Id);
        OpenDrawer(profile.PublicName, L.T("FPS e qualidade desta conta"), editor);
    }
    private async Task<IAsyncDisposable?> PrepareLaunchGraphicsAsync(AccountProfile profile, CancellationToken token)
    {
        if (!_graphicsLaunchPolicy.Enabled || _nativeGraphics is null) return null;
        var lease = await _nativeGraphics.BeginLaunchAsync(_graphicsLaunchPolicy.ForAccount(profile.Id), token);
        return new GraphicsRestoreLease(lease, () => AppendLog(L.T("Não foi possível restaurar as preferências gráficas anteriores. O backup foi preservado.")));
    }
    private sealed class GraphicsRestoreLease(IAsyncDisposable inner, Action failed) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync(); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { failed(); }
        }
    }
}
