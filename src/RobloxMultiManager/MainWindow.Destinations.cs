using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    public ObservableCollection<SavedLaunchDestination> SavedDestinations { get; } = [];
    private readonly SemaphoreSlim _savedDestinationsSaveGate = new(1, 1);
    private bool _savedDestinationsReady;
    private SavedDestinationStore? _savedDestinationStore;
    private SavedDestinationStore DestinationStore => _savedDestinationStore ??= new(_paths);
    private async Task LoadSavedDestinationsAsync()
    {
        try
        {
            var saved = await DestinationStore.LoadAsync();
            SavedDestinations.Clear();
            foreach (var destination in saved) SavedDestinations.Add(destination);
            _savedDestinationsReady = true;
            SavedDestinationsStatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception)
        {
            // Keep an unreadable file intact instead of silently replacing it.
            _savedDestinationsReady = false;
            SavedDestinationsStatusText.Text = RobloxMultiManager.Core.L.T("Não foi possível carregar os destinos salvos.");
            SavedDestinationsStatusText.Visibility = Visibility.Visible;
        }
        UpdateSavedDestinationButtons();
    }
    private void UpdateSavedDestinationButtons()
    {
        if (SavedDestinationEditButton is null) return;
        var editable = _savedDestinationsReady && _launchBatchCancellation is null;
        SavedDestinationCombo.IsEnabled = SavedDestinationSaveButton.IsEnabled = editable;
        SavedDestinationEditButton.IsEnabled = editable && SavedDestinationCombo.SelectedItem is SavedLaunchDestination;
        SavedDestinationDeleteButton.IsEnabled = SavedDestinationEditButton.IsEnabled;
    }
    private void SavedDestination_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SavedDestinationCombo.SelectedItem is SavedLaunchDestination destination) LaunchLinkInput.Text = destination.Link;
        UpdateSavedDestinationButtons();
    }
    private void SaveCurrentDestination_Click(object sender, RoutedEventArgs e) => OpenDestinationEditor(null);
    private async void DeleteSavedDestination_Click(object sender, RoutedEventArgs e)
    {
        if (!_savedDestinationsReady || _launchBatchCancellation is not null
            || SavedDestinationCombo.SelectedItem is not SavedLaunchDestination selected) return;
        if (AppDialog.Show(this, RobloxMultiManager.Core.L.F("Excluir o destino salvo ‘{0}’?",selected.Name), RobloxMultiManager.Core.L.T("Excluir destino"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await RemoveSavedDestinationAsync(selected); }
        catch (Exception)
        { AppDialog.Show(this, RobloxMultiManager.Core.L.T("Não foi possível excluir o destino neste computador."), RobloxMultiManager.Core.L.T("Destinos salvos"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private async Task RemoveSavedDestinationAsync(SavedLaunchDestination destination)
    {
        await _savedDestinationsSaveGate.WaitAsync();
        try
        {
            var existing = SavedDestinations.FirstOrDefault(item => item.Id == destination.Id);
            if (existing is null) return;
            await DestinationStore.SaveAsync(SavedDestinations.Where(item => item.Id != destination.Id).ToArray());
            SavedDestinations.Remove(existing);
            UpdateSavedDestinationButtons();
        }
        finally { _savedDestinationsSaveGate.Release(); }
    }
    private void EditSavedDestination_Click(object sender, RoutedEventArgs e)
    {
        if (SavedDestinationCombo.SelectedItem is SavedLaunchDestination selected) OpenDestinationEditor(selected);
    }
    private void OpenDestinationEditor(SavedLaunchDestination? original)
    {
        if (!_savedDestinationsReady || _launchBatchCancellation is not null || DrawerHost.Visibility == Visibility.Visible) return;
        var editor = new DestinationEditorPanel(original, LaunchLinkInput.Text, PersistDestinationAsync);
        editor.Completed += (_, _) => { if (ReferenceEquals(DrawerBody.Content, editor)) CloseDrawer(); };
        editor.CancelRequested += (_, _) => CloseDrawer();
        OpenDrawer(original is null ? RobloxMultiManager.Core.L.T("Salvar destino") : RobloxMultiManager.Core.L.T("Editar destino"), RobloxMultiManager.Core.L.T("Escolha um nome e um link para reutilizar nas próximas aberturas."), editor);
    }
    private async Task PersistDestinationAsync(SavedLaunchDestination destination)
    {
        await _savedDestinationsSaveGate.WaitAsync();
        try
        {
            var next = SavedDestinations.ToList();
            var index = next.FindIndex(item => item.Id == destination.Id);
            if (index < 0) next.Add(destination); else next[index] = destination;
            await DestinationStore.SaveAsync(next);
            if (index < 0) SavedDestinations.Add(destination); else SavedDestinations[index] = destination;
            SavedDestinationCombo.SelectedItem = destination;
        }
        finally { _savedDestinationsSaveGate.Release(); }
    }
}
