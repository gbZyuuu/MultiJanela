using System.Windows.Threading;
using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private IReadOnlyDictionary<string, ProcessResourceUsage> _resourceUsage = new Dictionary<string, ProcessResourceUsage>();
    private void ProcessMonitor_ResourcesChanged(object? sender, IReadOnlyDictionary<string, ProcessResourceUsage> resources)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_shutdownStarted) return;
            _resourceUsage = resources;
            _resourceHistory.Add(resources);
            if (_workspacePage == WorkspacePage.History) _historyPanel?.RefreshResources();
            RefreshResourceRows();
            QueueCpuReconcile();
            _ = QueueTuningAsync();
        }));
    }
    private void RefreshResourceRows()
    {
        foreach (var row in WorkspaceAccounts)
            row.UpdateResources(row.Session is { } session && _resourceUsage.TryGetValue(session.Key, out var usage) ? usage : null);
    }
}
