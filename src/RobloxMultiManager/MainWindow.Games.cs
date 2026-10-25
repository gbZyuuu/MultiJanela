using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private GamesHub? _gamesHub;
    private GamesHub EnsureGamesHub()
    {
        if (_gamesHub is not null) return _gamesHub;
        _gamesHub = new GamesHub(new RobloxGameService(VersionHttpClient), new GameLibraryStore(_paths),
            UseLibraryDestination, () => _sequenceIds.Count);
        _gamesHub.RestoreDraft(_formDrafts.GetValueOrDefault("games"));
        GamesPageHost.Content = _gamesHub;
        Closed += (_, _) => _gamesHub.Dispose();
        return _gamesHub;
    }
    private void Games_Click(object sender, RoutedEventArgs e)
    { EnsureGamesHub(); ShowWorkspacePage(WorkspacePage.Games); }
    private void UseLibraryDestination(string link, bool launch)
    {
        if (_launchBatchCancellation is not null || _handoffPrepared || _shutdownStarted) return;
        LaunchLinkInput.Text = link;
        ShowComfortLaunchPanel(replace: true);
        if (launch) LaunchDestinationSequence_Click(this, new RoutedEventArgs());
    }
    private async Task RecordGameLaunchAsync(long? placeId)
    {
        if (placeId is not > 0) return;
        try { await EnsureGamesHub().RecordLaunchAsync(placeId.Value); }
        catch (Exception) { /* A history failure must never interrupt a successful launch. */ }
    }
}
