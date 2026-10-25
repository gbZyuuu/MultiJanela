using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

/// <summary>
/// One row of the deployment list: either a version present on disk, a published
/// deployment that can still be downloaded, or both.
/// </summary>
public sealed class RobloxVersionRow
{
    public required string VersionId { get; init; }
    public string? Version { get; init; }
    public DateTime? PublishedAtUtc { get; init; }
    public RobloxInstalledVersionInfo? Installed { get; set; }

    /// <summary>
    /// Pre-computed sort key so the DataGrid never re-parses version strings
    /// during sort or filter operations. Computed once in CreateRow.
    /// </summary>
    public Version? ParsedVersion { get; init; }

    public bool IsInstalled => Installed is not null;
    public bool IsManaged => Installed?.IsManaged == true;
    public bool AppearsSigned => Installed?.AppearsSigned == true;

    public string VersionText => Version ?? Installed?.FileVersion ?? RobloxMultiManager.Core.L.T("versão n/d");

    public string StateText => Installed is null
        ? RobloxMultiManager.Core.L.T("Disponível para baixar")
        : Installed.StatusText;

    /// <summary>
    /// Drives the cell colour.  A deployment that is not on disk has no signature
    /// to report, which is a third state rather than a failure.
    /// </summary>
    public string SignatureState => Installed is null
        ? "absent"
        : Installed.AppearsSigned ? "valid" : "invalid";

    public string SignatureText => Installed is null
        ? RobloxMultiManager.Core.L.T("não baixada")
        : Installed.AppearsSigned ? RobloxMultiManager.Core.L.T("Roblox válida") : RobloxMultiManager.Core.L.T("Não confirmada");

    public string WhenText => PublishedAtUtc is not null
        ? PublishedAtUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
        : Installed?.InstalledAtText ?? "—";

    public bool Matches(string query) =>
        VersionId.Contains(query, StringComparison.OrdinalIgnoreCase)
        || VersionText.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The deployment browser, hosted inside the main window's sliding panel instead
/// of a separate window.
/// </summary>
/// <remarks>
/// The three pieces of window machinery the dialog used to rely on are replaced by
/// host-neutral equivalents: <c>DialogResult = true</c> became
/// <see cref="Confirmed"/> plus <see cref="CloseRequested"/>, <c>Close()</c> became
/// a close request the host honours, and the cancellable <c>Closing</c> handler
/// became a check inside <see cref="RequestClose"/>, which is the single place a
/// dismissal can be refused while a download is running.
/// </remarks>
// Fully qualified: WinForms is referenced too for the tray icon, so UserControl
// is ambiguous here.
public sealed partial class VersionsPanel : System.Windows.Controls.UserControl
{
    private readonly RobloxInstallationLocator _locator;
    private readonly RobloxVersionChecker _checker;
    private readonly RobloxDeploymentCatalog _catalog;
    private readonly RobloxDeploymentDownloader _downloader;
    private readonly bool _canChangeSelection;
    private readonly string? _initialSelectedVersionId;

    private IReadOnlyList<RobloxInstalledVersionInfo> _installed = [];
    private IReadOnlyList<RobloxDeploymentEntry> _catalogEntries = [];
    private readonly VersionRowCollection _rowCollection = new();
    private bool _initialized;
    private bool _dismissed;
    private Task? _localRefresh;
    private readonly CancellationTokenSource _lifetime = new();
    private ICollectionView? _rowView;
    private RobloxRemoteVersionInfo? _latest;
    private string _latestBaseStatus = string.Empty;
    private string _catalogBaseStatus = string.Empty;
    private string? _pendingSelectionId;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isDownloading;
    private bool _isDeleting;
    private DispatcherTimer? _filterDebounce;
    private string _currentFilterText = string.Empty;
    private bool _installedOnlyFilter;

    public VersionsPanel(
        RobloxInstallationLocator locator,
        RobloxVersionChecker checker,
        RobloxDeploymentCatalog catalog,
        RobloxDeploymentDownloader downloader,
        string? selectedVersionId,
        string? selectedChannel,
        bool canChangeSelection)
    {
        InitializeComponent();
        _locator = locator;
        _checker = checker;
        _catalog = catalog;
        _downloader = downloader;
        _initialSelectedVersionId = selectedVersionId;
        _canChangeSelection = canChangeSelection;
        SelectedVersionId = selectedVersionId;
        SelectedChannel = selectedChannel;
        _pendingSelectionId = selectedVersionId;
        ChannelInput.Text = string.IsNullOrWhiteSpace(selectedChannel)
            ? locator.GetCurrentChannel()
            : selectedChannel;
        Loaded += VersionsPanel_Loaded;
        Unloaded += (_, _) => { _dismissed = true; _filterDebounce?.Stop(); _lifetime.Cancel(); };
    }

    /// <summary>
    /// Raised when the panel wants its host to dismiss it.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// True when the user picked a version or chose Automático, which is what the
    /// old modal window reported as <c>DialogResult == true</c>.
    /// </summary>
    public bool Confirmed { get; private set; }

    public string? SelectedVersionId { get; private set; }
    public string? SelectedChannel { get; private set; }

    /// <summary>
    /// The hosting window, used as the dialog owner.  A control that is not in a
    /// window yet has none, and <see cref="AppDialog.Show"/> accepts a null owner.
    /// </summary>
    private Window? HostWindow => Window.GetWindow(this);

    private async void VersionsPanel_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        // Initialize the collection view once so filter/sort changes reuse
        // the same virtualized container pool instead of rebuilding it.
        _rowView = CollectionViewSource.GetDefaultView(_rowCollection);
        _rowView.Filter = RowFilterPredicate;
        VersionsGrid.ItemsSource = _rowView;

        // Initialize debounce timer for filter input — avoids re-filtering
        // on every keystroke during rapid typing.
        _filterDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            ApplyFilter();
        };

        if (!_canChangeSelection)
        {
            SelectionWarningText.Text =
                RobloxMultiManager.Core.L.T("Para trocar a versão, cancele a fila e feche todas as janelas do Roblox. ")
                + RobloxMultiManager.Core.L.T("A consulta e o download continuam disponíveis.");
        }

        CurrentVersionText.Text = RobloxMultiManager.Core.L.T("Localizando…");
        CurrentIdText.Text = RobloxMultiManager.Core.L.T("Verificando instalações locais");
        CatalogStatusText.Text = RobloxMultiManager.Core.L.T("Carregando catálogo…");
        // Each source paints as soon as it finishes; local rows never wait for the network.
        await Task.WhenAll(RefreshInstalledAsync(), CheckLatestAsync(), LoadCatalogAsync());
    }

    /// <summary>
    /// Asks the host to dismiss the panel, refusing while a download would be
    /// abandoned.  Returns false when the dismissal was refused, so a caller that
    /// already set <see cref="Confirmed"/> can undo it.
    /// </summary>
    /// <summary>
    /// Lets the host's close button and scrim go through the same refusal check as
    /// the panel's own Fechar button.
    /// </summary>
    public void RequestCloseFromHost() => RequestClose();

    private bool RequestClose()
    {
        if (_isDownloading)
        {
            // Closing mid-download would leave a staging folder behind with no
            // owner, so the panel asks instead of tearing the transfer down
            // silently.
            var answer = AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.T("Um download está em andamento. Cancelar e fechar?"),
                RobloxMultiManager.Core.L.T("Versões"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return false;
            }

            _downloadCancellation?.Cancel();
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ==================================================================
    // Catalog and installed versions
    // ==================================================================

    private Task RefreshInstalledAsync()
    {
        if (_localRefresh is { IsCompleted: false }) return _localRefresh;
        return _localRefresh = RefreshInstalledCoreAsync();
    }

    private async Task RefreshInstalledCoreAsync()
    {
        try
        {
            var snapshot = await Task.Run(() => _locator.Inspect(_initialSelectedVersionId));
            if (_dismissed) return;
            _installed = snapshot.Installed;
            var effective = snapshot.Effective;
            CurrentVersionText.Text = effective.IsFound ? effective.Version ?? RobloxMultiManager.Core.L.T("Versão desconhecida") : RobloxMultiManager.Core.L.T("Cliente indisponível");
            CurrentIdText.Text = effective.VersionId ?? RobloxMultiManager.Core.L.T("Nenhum deployment resolvido");
            SelectionModeText.Text = string.IsNullOrWhiteSpace(_initialSelectedVersionId)
                ? RobloxMultiManager.Core.L.F("Automático · {0}",effective.Source) : effective.IsFound ? RobloxMultiManager.Core.L.F("Versão fixada · canal {0}",ChannelInput.Text) : effective.Source;
            _pendingSelectionId ??= effective.VersionId;
            RebuildRows(); UpdateLatestComparison();
        }
        catch (Exception ex)
        {
            if (!_dismissed) { CurrentVersionText.Text = RobloxMultiManager.Core.L.T("Não foi possível ler o cliente"); CurrentIdText.Text = ToSafeMessage(ex); }
        }
    }

    private async void RefreshCatalogButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_isDeleting) return;
        await Task.WhenAll(RefreshInstalledAsync(), LoadCatalogAsync());
    }

    private async Task LoadCatalogAsync()
    {
        RefreshCatalogButton.IsEnabled = false;
        _catalogBaseStatus = RobloxMultiManager.Core.L.T("Carregando o histórico de deployments...");
        CatalogStatusText.Text = _catalogBaseStatus;
        try
        {
            _catalogEntries = await _catalog.GetWindowsPlayerAsync(_lifetime.Token);
            if (_dismissed) return;
            RebuildRows();
            _catalogBaseStatus =
                RobloxMultiManager.Core.L.F("{0} deployments · histórico de {1} · ",_rowCollection.Count,_catalog.Source)
                + RobloxMultiManager.Core.L.T("pacotes baixados de setup.rbxcdn.com");
        }
        catch (Exception ex)
        {
            _catalogEntries = [];
            RebuildRows();
            _catalogBaseStatus =
                RobloxMultiManager.Core.L.F("Histórico indisponível ({0}). Mostrando o que está ",ToSafeMessage(ex))
                + RobloxMultiManager.Core.L.T("no disco; baixar por hash continua funcionando.");
        }
        finally
        {
            RefreshCatalogButton.IsEnabled = !_isDeleting;
            AppendChannelNote();
        }
    }

    /// <summary>
    /// The published history only covers LIVE, so a non-LIVE channel must say so
    /// rather than let an empty list read as "no versions exist".
    /// </summary>
    private void AppendChannelNote()
    {
        var channel = ChannelInput.Text?.Trim() ?? string.Empty;
        var isLive = channel.Length == 0
                     || channel.Equals("LIVE", StringComparison.OrdinalIgnoreCase);
        CatalogStatusText.Text = isLive
            ? _catalogBaseStatus
            : _catalogBaseStatus
              + RobloxMultiManager.Core.L.F(" · a lista é do canal LIVE; o canal {0} não publica histórico, ",channel)
              + RobloxMultiManager.Core.L.T("use o campo de hash.");
    }

    /// <summary>
    /// Rebuilds the version rows into the shared ObservableCollection that backs
    /// the ICollectionView. Sort keys are pre-computed once per row so the DataGrid
    /// never re-parses version strings during sort or filter operations.
    /// </summary>
    private void RebuildRows()
    {
        if (_dismissed) return;
        var byId = new Dictionary<string, RobloxVersionRow>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _catalogEntries)
        {
            byId[entry.VersionId] = CreateRow(entry.VersionId, entry.Version, entry.DeployedAtUtc);
        }

        // The history refreshes on a timer, so the live endpoint can already know
        // about a deployment the history has not recorded yet.
        if (_latest is not null && !byId.ContainsKey(_latest.VersionId))
        {
            byId[_latest.VersionId] = CreateRow(_latest.VersionId, _latest.Version, null);
        }

        foreach (var item in _installed)
        {
            byId.TryGetValue(item.VersionId, out var existing);
            var row = CreateRow(
                item.VersionId,
                existing?.Version ?? item.FileVersion,
                existing?.PublishedAtUtc);
            row.Installed = item;
            byId[item.VersionId] = row;
        }

        // Pre-sort once and populate the observable collection. The ICollectionView
        // preserves virtualized containers across refreshes, unlike reassigning
        // ItemsSource which tears down the entire visual tree.
        var sorted = byId.Values
            .OrderByDescending(row => row.ParsedVersion)
            .ThenByDescending(row => row.PublishedAtUtc ?? DateTime.MinValue)
            .ThenBy(row => row.VersionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var wanted = _pendingSelectionId;
        using (_rowView?.DeferRefresh()) _rowCollection.ReplaceAll(sorted);
        _pendingSelectionId = wanted;
        RestoreSelection();
    }

    /// <summary>
    /// Creates a version row with the sort key pre-computed so the DataGrid never
    /// re-parses version strings during sort or filter operations.
    /// </summary>
    private static RobloxVersionRow CreateRow(string versionId, string? version, DateTime? publishedAtUtc)
    {
        var versionText = version ?? versionId;
        RobloxVersionChecker.TryParseClientVersion(versionText, out var parsed);
        return new RobloxVersionRow
        {
            VersionId = versionId,
            Version = version,
            PublishedAtUtc = publishedAtUtc,
            ParsedVersion = parsed
        };
    }

    /// <summary>
    /// Applies the current filter text and installed-only toggle to the collection
    /// view's Filter predicate. This runs on the UI thread but only evaluates the
    /// predicate against model rows; the virtualized container pool is reused.
    /// </summary>
    private void ApplyFilter()
    {
        _currentFilterText = FilterInput.Text?.Trim() ?? string.Empty;
        _installedOnlyFilter = InstalledOnlyCheck.IsChecked == true;
        _rowView?.Refresh();
        RestoreSelection();
    }

    /// <summary>
    /// Filter predicate for the ICollectionView. Returns true if the row should be
    /// visible given the current search text and installed-only toggle.
    /// </summary>
    private bool RowFilterPredicate(object obj)
    {
        if (obj is not RobloxVersionRow row)
        {
            return false;
        }

        if (_installedOnlyFilter && !row.IsInstalled)
        {
            return false;
        }

        if (_currentFilterText.Length > 0 && !row.Matches(_currentFilterText))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Restores the pending selection or falls back to the first installed/latest row
    /// after a rebuild or filter change.
    /// </summary>
    private void RestoreSelection()
    {
        var wanted = _pendingSelectionId;
        var visible = _rowView?.Cast<RobloxVersionRow>().ToArray() ?? _rowCollection.ToArray();
        RobloxVersionRow? selected = null;
        if (wanted is not null)
        {
            foreach (var row in visible)
            {
                if (row.VersionId.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    selected = row;
                    break;
                }
            }
        }

        selected ??= visible.FirstOrDefault(row => row.IsInstalled)
                     ?? visible.FirstOrDefault();

        VersionsGrid.SelectedItem = selected;
        if (selected is not null)
        {
            VersionsGrid.ScrollIntoView(selected);
        }

        UpdateSelectionButtons();
    }

    private void FilterInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounce: restart the timer on every keystroke so filtering only runs
        // 250ms after the user stops typing, avoiding per-keystroke LINQ scans.
        _filterDebounce?.Stop();
        _filterDebounce?.Start();
    }

    private void InstalledOnlyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    // ==================================================================
    // Latest published version
    // ==================================================================

    private async void CheckButton_Click(object sender, RoutedEventArgs e) =>
        await CheckLatestAsync();

    private async Task CheckLatestAsync()
    {
        CheckButton.IsEnabled = false;
        _latest = null;
        LatestVersionText.Text = RobloxMultiManager.Core.L.T("Verificando...");
        LatestIdText.Text = string.Empty;
        _latestBaseStatus = RobloxMultiManager.Core.L.T("Consultando clientsettingscdn.roblox.com");
        LatestStatusText.Text = _latestBaseStatus;
        try
        {
            _latest = await _checker.GetLatestAsync(ChannelInput.Text, _lifetime.Token);
            if (_dismissed) return;
            ChannelInput.Text = _latest.Channel;
            LatestVersionText.Text = _latest.Version;
            LatestIdText.Text = _latest.VersionId;
            _latestBaseStatus =
                RobloxMultiManager.Core.L.F("Fonte oficial · verificado às {0:HH:mm:ss}",_latest.CheckedAtUtc.ToLocalTime());
            LatestStatusText.Text = _latestBaseStatus;
            RebuildRows();
            UpdateLatestComparison();
        }
        catch (OperationCanceledException)
        {
            LatestVersionText.Text = RobloxMultiManager.Core.L.T("Consulta expirou");
            _latestBaseStatus =
                RobloxMultiManager.Core.L.T("A consulta é apenas informativa e não impede abrir contas.");
            LatestStatusText.Text = _latestBaseStatus;
        }
        catch (Exception ex)
        {
            LatestVersionText.Text = RobloxMultiManager.Core.L.T("Não foi possível verificar");
            _latestBaseStatus =
                RobloxMultiManager.Core.L.F("Consulta informativa falhou: {0}",ToSafeMessage(ex));
            LatestStatusText.Text = _latestBaseStatus;
        }
        finally
        {
            CheckButton.IsEnabled = true;
            AppendChannelNote();
        }
    }

    private void UpdateLatestComparison()
    {
        if (_latest is null)
        {
            return;
        }

        var installedLatest = _installed.FirstOrDefault(item =>
            item.VersionId.Equals(
                _latest.VersionId,
                StringComparison.OrdinalIgnoreCase));
        LatestStatusText.Text = installedLatest is null
            ? _latestBaseStatus
              + RobloxMultiManager.Core.L.T(" · ainda não está no disco; selecione a linha e use Baixar.")
            : _latestBaseStatus
              + (installedLatest.AppearsSigned
                  ? RobloxMultiManager.Core.L.T(" · instalada e com assinatura válida.")
                  : RobloxMultiManager.Core.L.T(" · arquivo local encontrado, mas a assinatura não foi confirmada."));
    }

    // ==================================================================
    // Selection
    // ==================================================================

    private void VersionsGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (VersionsGrid.SelectedItem is RobloxVersionRow row)
        {
            _pendingSelectionId = row.VersionId;
        }

        UpdateSelectionButtons();
    }

    private void UpdateSelectionButtons()
    {
        var selected = VersionsGrid.SelectedItem as RobloxVersionRow;
        var downloadable = selected is not null && !selected.IsInstalled;
        var idle = !_isDownloading && !_isDeleting;

        UseSelectedButton.Content = selected is null || selected.IsInstalled
            ? RobloxMultiManager.Core.L.T("Aplicar selecionada")
            : _canChangeSelection
                ? RobloxMultiManager.Core.L.T("Baixar e aplicar")
                : RobloxMultiManager.Core.L.T("Baixar selecionada");
        UseSelectedButton.IsEnabled = idle
                                      && selected is not null
                                      && (_canChangeSelection || downloadable);
        AutomaticButton.IsEnabled = _canChangeSelection && idle;
        DownloadHashButton.IsEnabled = idle;
        RemoveDownloadButton.IsEnabled = idle
                                         && selected?.IsManaged == true;

        if (!_canChangeSelection)
        {
            return;
        }

        SelectionWarningText.Text = selected is null
            ? RobloxMultiManager.Core.L.T("Nenhum deployment selecionado.")
            : !selected.IsInstalled
                ? RobloxMultiManager.Core.L.F("{0} não está no disco. ",selected.VersionId)
                  + RobloxMultiManager.Core.L.T("Baixar e aplicar busca os pacotes na CDN da Roblox e valida a assinatura antes de usar.")
                : !selected.AppearsSigned
                    ? RobloxMultiManager.Core.L.T("Esta instalação não possui uma assinatura Roblox válida e não pode ser selecionada.")
                    : _latest is not null
                      && selected.VersionId.Equals(
                          _latest.VersionId,
                          StringComparison.OrdinalIgnoreCase)
                        ? RobloxMultiManager.Core.L.F("Compatível com o estado atual do canal {0}: ",_latest.Channel)
                          + $"{selected.VersionText}."
                        : selected.IsManaged
                            ? RobloxMultiManager.Core.L.T("Baixada pelo app. Fica fora de %LOCALAPPDATA%\\Roblox, ")
                              + RobloxMultiManager.Core.L.T("portanto o atualizador oficial não a substitui.")
                            : RobloxMultiManager.Core.L.T("Pode ser aplicada, mas o atualizador oficial da Roblox ")
                              + RobloxMultiManager.Core.L.T("ainda pode substituí-la nesta pasta.");
    }

    private void AutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDeleting) return;
        SelectedVersionId = null;
        SelectedChannel = null;
        // The host reads Confirmed from the CloseRequested handler, so the flag has
        // to be set before the request is made and undone if it is refused.
        Confirmed = true;
        if (!RequestClose())
        {
            Confirmed = false;
        }
    }

    private async void UseSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDeleting) return;
        if (VersionsGrid.SelectedItem is not RobloxVersionRow selected)
        {
            return;
        }

        var versionId = selected.VersionId;
        if (!selected.IsInstalled)
        {
            if (!await DownloadAsync(versionId) || !_canChangeSelection)
            {
                return;
            }
        }

        var installed = _installed.FirstOrDefault(item =>
            item.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase));
        if (installed is null || !installed.AppearsSigned)
        {
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.T("Esta versão não pode ser aplicada porque a assinatura Roblox não ")
                + RobloxMultiManager.Core.L.T("foi confirmada no arquivo local."),
                RobloxMultiManager.Core.L.T("Versões"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            SelectedVersionId = versionId;
            SelectedChannel = RobloxVersionChecker.NormalizeChannel(
                ChannelInput.Text);
            Confirmed = true;
            if (!RequestClose())
            {
                Confirmed = false;
            }
        }
        catch (ArgumentException)
        {
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.T("Informe um canal Roblox válido e verifique novamente."),
                RobloxMultiManager.Core.L.T("Canal inválido"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ==================================================================
    // Download
    // ==================================================================

    private async void DownloadHashButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await DownloadFromHashInputAsync();

    private async void HashInput_KeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await DownloadFromHashInputAsync();
    }

    private async Task DownloadFromHashInputAsync()
    {
        if (_isDownloading || _isDeleting) return;
        var versionId = RobloxDeploymentDownloader.NormalizeVersionId(
            HashInput.Text);
        if (versionId is null)
        {
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.T("Informe um hash de deployment, por exemplo ")
                + RobloxMultiManager.Core.L.T("version-d584fb6c717a43d9 (16 caracteres hexadecimais)."),
                RobloxMultiManager.Core.L.T("Hash inválido"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_downloader.IsDownloaded(versionId))
        {
            _pendingSelectionId = versionId;
            await RefreshInstalledAsync();
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.F("{0} já está baixada e foi selecionada na lista.",versionId),
                RobloxMultiManager.Core.L.T("Versões"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await DownloadAsync(versionId);
    }

    private async Task<bool> DownloadAsync(string versionId)
    {
        if (_isDownloading || _isDeleting)
        {
            return false;
        }

        string channel;
        try
        {
            channel = RobloxVersionChecker.NormalizeChannel(ChannelInput.Text);
        }
        catch (ArgumentException)
        {
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.T("Informe um canal Roblox válido antes de baixar."),
                RobloxMultiManager.Core.L.T("Canal inválido"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _isDownloading = true;
        _downloadCancellation = new CancellationTokenSource();
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadStatusText.Text = RobloxMultiManager.Core.L.F("Consultando o manifesto de {0}",versionId);
        DownloadSizeText.Text = string.Empty;
        CancelDownloadButton.IsEnabled = true;
        UpdateSelectionButtons();
        try
        {
            var progress = new Progress<RobloxDownloadProgress>(report =>
            {
                DownloadStatusText.Text =
                    RobloxMultiManager.Core.L.F("{0} · pacote ",report.Stage)
                    + $"{Math.Min(report.CompletedPackages + 1, report.TotalPackages)}"
                    + $"/{report.TotalPackages}";
                DownloadProgressBar.Value = report.Fraction;
                DownloadSizeText.Text = report.SizeText;
            });
            await _downloader.DownloadAsync(
                versionId,
                channel,
                progress,
                _downloadCancellation.Token);
            DownloadProgressBar.Value = 1;
            DownloadStatusText.Text = RobloxMultiManager.Core.L.F("{0} instalada e pronta para usar.",versionId);
            _pendingSelectionId = versionId;
            await RefreshInstalledAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            DownloadStatusText.Text = RobloxMultiManager.Core.L.T("Download cancelado.");
            DownloadSizeText.Text = RobloxMultiManager.Core.L.T("Nada foi gravado na pasta final.");
            return false;
        }
        catch (Exception ex)
        {
            DownloadStatusText.Text = RobloxMultiManager.Core.L.T("O download falhou.");
            DownloadSizeText.Text = string.Empty;
            AppDialog.Show(
                HostWindow,
                ex is RobloxDeploymentDownloadException
                    ? ex.Message
                    : RobloxMultiManager.Core.L.F("Não foi possível baixar {0}.\n\n{1}",versionId,ToSafeMessage(ex)),
                "Download",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _isDownloading = false;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            CancelDownloadButton.IsEnabled = false;
            UpdateSelectionButtons();
        }
    }

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        _downloadCancellation?.Cancel();
    }

    private async void RemoveDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading || _isDeleting) return;
        if (VersionsGrid.SelectedItem is not RobloxVersionRow selected
            || !selected.IsManaged)
        {
            return;
        }

        var answer = AppDialog.Show(
            HostWindow,
            RobloxMultiManager.Core.L.F("Apagar a pasta baixada de {0}?",selected.VersionId),
            RobloxMultiManager.Core.L.T("Remover versão baixada"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _isDeleting = true;
            RefreshCatalogButton.IsEnabled = false;
            UpdateSelectionButtons();
            // Finish an older snapshot before removing files, then discover again.
            if (_localRefresh is { IsCompleted: false }) await _localRefresh;
            await Task.Run(() => _downloader.Delete(selected.VersionId));
            _pendingSelectionId = selected.VersionId;
            await RefreshInstalledAsync();
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.F("Não foi possível remover a pasta.\n\n{0}",ToSafeMessage(ex)),
                RobloxMultiManager.Core.L.T("Remover versão baixada"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _isDeleting = false;
            RefreshCatalogButton.IsEnabled = true;
            UpdateSelectionButtons();
        }
    }

    private void RddButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = VersionsGrid.SelectedItem as RobloxVersionRow;
        var versionId = selected?.VersionId ?? _latest?.VersionId;
        try
        {
            var url = RobloxVersionChecker.BuildRddUrl(
                ChannelInput.Text,
                versionId);
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                HostWindow,
                RobloxMultiManager.Core.L.F("Não foi possível abrir o RDD.\n\n{0}",ToSafeMessage(ex)),
                "RDD",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        RequestClose();

    private static string ToSafeMessage(Exception exception) =>
        exception switch
        {
            RobloxDeploymentDownloadException download => download.Message,
            HttpRequestException request when request.StatusCode is not null =>
                $"HTTP {(int)request.StatusCode.Value}",
            ArgumentException => RobloxMultiManager.Core.L.T("canal inválido"),
            _ => exception.GetType().Name
        };
}

// One reset instead of one UI notification for every historical deployment.
internal sealed class VersionRowCollection : ObservableCollection<RobloxVersionRow>
{
    public void ReplaceAll(IEnumerable<RobloxVersionRow> rows)
    {
        Items.Clear(); foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
    }
}
