using System.Windows;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private readonly ResourceHistory _resourceHistory = new();
    private SessionHistoryStore? _sessionHistoryStore;
    private HistoryPanel? _historyPanel;
    private Task _historyWrite = Task.CompletedTask;
    private readonly List<SessionHistoryEntry> _suspendedHistory = [];
    private SessionHistoryStore HistoryStore => _sessionHistoryStore ??= new(_paths);
    private void EnsureHistoryPanel()
    {
        if (_historyPanel is not null) return;
        _historyPanel = new HistoryPanel(HistoryStore, _resourceHistory, () => WorkspaceAccounts);
        HistoryPageHost.Content = _historyPanel;
    }
    private void History_Click(object sender, RoutedEventArgs e) { EnsureHistoryPanel(); ShowWorkspacePage(WorkspacePage.History); }
    private void RecordSessionChanges(IReadOnlyList<RobloxInstanceInfo> current)
    {
        if (_shutdownStarted) return;
        var keys = current.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var entries = current.Where(s => !_lastObservedProcesses.ContainsKey(s.Key)).Select(s => HistoryEntry(s, "opened", "observed"))
            .Concat(_lastObservedProcesses.Values.Where(s => !keys.Contains(s.Key)).Select(s => HistoryEntry(s, "closed",
                _intentionalClosures.Contains(s.Key) ? "requested" : "observed-end"))).ToArray();
        if (entries.Any(e => e.Kind == "closed")) NotifyEvent("session-closed");
        QueueHistory(entries);
    }
    private SessionHistoryEntry HistoryEntry(RobloxInstanceInfo session, string kind, string reason)
    {
        var profileId = _launchOwnership.TryGetValue(session.Key, out var owner) ? owner.ProfileId : (Guid?)null;
        return new(kind + ":" + session.Key, DateTime.UtcNow, kind, session.Key, session.ProcessId, profileId,
            session.ProfileName, session.FileVersion ?? session.VersionId, reason);
    }
    private void QueueHistory(SessionHistoryEntry[] entries)
    {
        if (entries.Length == 0) return;
        if (_storageMaintenance)
        { _suspendedHistory.AddRange(entries); if (_suspendedHistory.Count > 500) _suspendedHistory.RemoveRange(0, _suspendedHistory.Count - 500); return; }
        var store = HistoryStore;
        _historyWrite = _historyWrite.ContinueWith(async _ =>
        {
            try { await store.AppendAsync(entries); }
            catch (Exception) { /* Keep the existing history file untouched if it cannot be read or written. */ }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
    private void ResumeHistoryWrites()
    {
        var pending = _suspendedHistory.ToArray(); _suspendedHistory.Clear(); QueueHistory(pending);
    }
}
