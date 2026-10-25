using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RobloxMultiManager.Core;
using Button = System.Windows.Controls.Button;
using Point = System.Windows.Point;

namespace RobloxMultiManager;

internal sealed record LaunchSequencePlan(AccountProfile[] Profiles, RobloxLaunchTarget? Target);

public partial class MainWindow
{
    // Queue membership and order belong to the sequence, independently of the
    // library selection/filter, process snapshots and numeric window target.
    private readonly List<Guid> _sequenceIds = [];
    private Point _queueDragOrigin;
    private Guid? _queueDragProfile;
    private const string QueueDragFormat = "MultiJanela.SequenceProfile";

    private void AddSequenceAccounts(IEnumerable<AccountProfile> accounts)
    {
        if (_launchBatchCancellation is not null) return;
        var current = Profiles.ToDictionary(profile => profile.Id);
        var existing = _sequenceIds.ToHashSet();
        foreach (var requested in accounts)
            if (current.TryGetValue(requested.Id, out var profile) && profile.IsSignedIn && existing.Add(profile.Id))
                _sequenceIds.Add(profile.Id);
        UpdateQueuePreview();
    }
    private void UpdateQueuePreview()
    {
        var profiles = Profiles.ToDictionary(profile => profile.Id);
        _sequenceIds.RemoveAll(id => !profiles.ContainsKey(id));
        var wanted = _sequenceIds.Select((id, index) => new QueuePreviewEntry(index + 1, profiles[id], index > 0, index < _sequenceIds.Count - 1)).ToArray();
        for (var i = QueuePreview.Count - 1; i >= wanted.Length; i--) QueuePreview.RemoveAt(i);
        for (var i = 0; i < wanted.Length; i++)
        {
            if (i == QueuePreview.Count) QueuePreview.Add(wanted[i]);
            else if (QueuePreview[i] != wanted[i]) QueuePreview[i] = wanted[i];
        }
        if (LaunchLinkProfileCombo.SelectedItem is null)
            LaunchLinkProfileCombo.SelectedItem = Profiles.FirstOrDefault(profile => profile.IsSignedIn);
        var busy = _launchBatchCancellation is not null;
        QueuePreviewEmptyText.Visibility = wanted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueuePlanKindText.Text = wanted.Length == 0 ? RobloxMultiManager.Core.L.T("Monte sua sequência") : RobloxMultiManager.Core.L.F("{0} conta(s) · na sua ordem",wanted.Length);
        QueueScopeText.Text = wanted.Length == 0 ? RobloxMultiManager.Core.L.T("Adicione contas para começar.") : RobloxMultiManager.Core.L.T("Arraste para ordenar · ↑ / ↓ também movem");
        QueueEditControls.IsEnabled = QueuePreviewList.IsEnabled = !busy;
        LaunchDestinationSequenceButton.IsEnabled = !busy && wanted.Length > 0;
        LaunchLinkButton.IsEnabled = LaunchLinkProfileCombo.IsEnabled = LaunchLinkInput.IsEnabled = !busy;
        LaunchAllButton.IsEnabled = busy || wanted.Length > 0;
        LaunchAllButton.Content = busy ? RobloxMultiManager.Core.L.T("Cancelar sequência") : RobloxMultiManager.Core.L.T("Abrir na home");
        QueueClearButton.IsEnabled = !busy && wanted.Length > 0;
        QueueFillButton.IsEnabled = !busy;
        UpdateSavedDestinationButtons();
    }
    private void OrbitClearSelection_Click(object sender, RoutedEventArgs e)
    {
        if (_launchBatchCancellation is not null) return;
        _sequenceIds.Clear(); UpdateQueuePreview();
    }
    private void QueueAddAccounts_Click(object sender, RoutedEventArgs e)
    {
        if (_launchBatchCancellation is not null || DrawerHost.Visibility == Visibility.Visible && !ReferenceEquals(DrawerBody.Content, NexusLaunchDock)) return;
        var panel = new QueueAccountsPanel(Profiles, _sequenceIds.ToHashSet());
        panel.AddRequested += (_, _) => { AddSequenceAccounts(panel.SelectedProfiles); ShowComfortLaunchPanel(replace: true); };
        panel.CancelRequested += (_, _) => ShowComfortLaunchPanel(replace: true);
        OpenDrawer(RobloxMultiManager.Core.L.T("Adicionar contas"), RobloxMultiManager.Core.L.T("Escolha as contas que farão parte da sequência."), panel);
    }
    private void QueueFill_Click(object sender, RoutedEventArgs e)
    {
        var active = ActiveSequenceProfileIds();
        var matching = _installation?.VersionId is { } version
            ? Instances.Count(instance => string.Equals(instance.VersionId, version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(instance.ExecutablePath, _installation.ExecutablePath, StringComparison.OrdinalIgnoreCase)) : 0;
        var remaining = Math.Max(0, GetTarget() - matching - _sequenceIds.Count);
        AddSequenceAccounts(Profiles.Where(profile => profile.IsSignedIn && !active.Contains(profile.Id) && !_sequenceIds.Contains(profile.Id)).Take(remaining).ToArray());
    }
    private HashSet<Guid> ActiveSequenceProfileIds()
    {
        var keys = Instances.Select(instance => instance.Key).ToHashSet(StringComparer.Ordinal);
        return _launchOwnership.Where(entry => keys.Contains(entry.Key)).Select(entry => entry.Value.ProfileId).ToHashSet();
    }
    private void MoveSequenceAccount(Guid id, int target)
    {
        if (_launchBatchCancellation is not null) return;
        var source = _sequenceIds.IndexOf(id);
        if (source < 0 || target < 0 || target >= _sequenceIds.Count || source == target) return;
        _sequenceIds.RemoveAt(source); _sequenceIds.Insert(target, id); UpdateQueuePreview();
    }
    private void QueueMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: QueuePreviewEntry row, CommandParameter: string direction })
            MoveSequenceAccount(row.Profile.Id, _sequenceIds.IndexOf(row.Profile.Id) + (direction == "up" ? -1 : 1));
        e.Handled = true;
    }
    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_launchBatchCancellation is null && sender is Button { DataContext: QueuePreviewEntry row })
        { _sequenceIds.Remove(row.Profile.Id); UpdateQueuePreview(); }
        e.Handled = true;
    }
    private void QueueDragStart(object sender, MouseButtonEventArgs e)
    {
        _queueDragProfile = null;
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node is not null && node != QueuePreviewList; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Button) return;
            if (node is ListBoxItem { DataContext: QueuePreviewEntry row })
            { _queueDragProfile = row.Profile.Id; _queueDragOrigin = e.GetPosition(QueuePreviewList); return; }
        }
    }
    private void QueueDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_launchBatchCancellation is not null || e.LeftButton != MouseButtonState.Pressed || _queueDragProfile is not { } id) return;
        var point = e.GetPosition(QueuePreviewList);
        if (Math.Abs(point.X - _queueDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(point.Y - _queueDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _queueDragProfile = null;
        System.Windows.DragDrop.DoDragDrop(QueuePreviewList, new System.Windows.DataObject(QueueDragFormat, id), System.Windows.DragDropEffects.Move);
    }
    private void QueueDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(QueueDragFormat) || e.Data.GetData(QueueDragFormat) is not Guid id) return;
        var hit = QueuePreviewList.InputHitTest(e.GetPosition(QueuePreviewList)) as DependencyObject;
        var container = hit is null ? null : ItemsControl.ContainerFromElement(QueuePreviewList, hit) as ListBoxItem;
        var target = container?.DataContext is QueuePreviewEntry row ? _sequenceIds.IndexOf(row.Profile.Id) : _sequenceIds.Count - 1;
        MoveSequenceAccount(id, target); e.Effects = System.Windows.DragDropEffects.Move; e.Handled = true;
    }
    private void QueueDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = _launchBatchCancellation is null && e.Data.GetDataPresent(QueueDragFormat)
            ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }
    private bool TryCreateSequencePlan(bool useDestination, out LaunchSequencePlan plan, out string error)
    {
        plan = new([], null); error = string.Empty;
        var profiles = _sequenceIds.Select(id => Profiles.FirstOrDefault(profile => profile.Id == id)).OfType<AccountProfile>().ToArray();
        if (profiles.Length == 0) { error = RobloxMultiManager.Core.L.T("Adicione contas em ‘Na sequência’ antes de abrir."); return false; }
        if (profiles.Any(profile => !profile.IsSignedIn)) { error = RobloxMultiManager.Core.L.T("Uma conta da sequência precisa de login. Atualize o login ou remova essa conta."); return false; }
        RobloxLaunchTarget? target = null;
        if (useDestination && !RobloxLinkParser.TryParse(LaunchLinkInput.Text, out target, out error)) return false;
        plan = new(profiles, target); return true;
    }
    private void SequenceDestination_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // A saved selection describes exactly the visible link. Any edit starts a
        // separate draft; clearing the selection must never erase that draft.
        if (SavedDestinationCombo?.SelectedItem is SavedLaunchDestination saved
            && !string.Equals(LaunchLinkInput.Text, saved.Link, StringComparison.Ordinal))
            SavedDestinationCombo.SelectedItem = null;
        if (DestinationHintText is null) return;
        DestinationHintText.Text = RobloxLinkParser.TryParse(LaunchLinkInput.Text, out var target, out _)
            ? target.Kind == RobloxLaunchTargetKind.PrivateServer
                ? target.PlaceId is { } privatePlace ? RobloxMultiManager.Core.L.F("Servidor privado · PlaceId {0}",privatePlace) : RobloxMultiManager.Core.L.T("Convite de servidor privado")
                : target.PlaceId is { } place ? RobloxMultiManager.Core.L.F("Jogo · PlaceId {0}",place) : RobloxMultiManager.Core.L.T("Link de experiência")
            : RobloxMultiManager.Core.L.T("PlaceId, link do jogo ou servidor privado.");
    }
    private async void LaunchDestinationSequence_Click(object sender, RoutedEventArgs e)
    {
        if (_launchBatchCancellation is not null) return;
        if (!TryCreateSequencePlan(true, out var plan, out var error))
        { AppDialog.Show(this, error, RobloxMultiManager.Core.L.T("Abrir sequência"), MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await RunLaunchSequenceAsync(plan);
    }
}
