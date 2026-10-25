using RobloxMultiManager.Core;

namespace RobloxMultiManager;

public partial class MainWindow
{
    private ProcessTuningPanel? _tuningPanel;
    private readonly ProcessTuningService _tuning = new();
    private ProcessTuningPreferences _tuningPreferences = new();
    private Task _tuningWork = Task.CompletedTask;
    private DateTime _lastTuningUtc;
    private PreferenceFile<ProcessTuningPreferences> TuningStore => new(_paths, "performance.json", () => new(), p => p);
    private async Task InitializeTuningAsync()
    {
        try { _tuningPreferences = await TuningStore.LoadAsync(); } catch (Exception) { _tuningPreferences = new(); }
        _tuningPanel = new ProcessTuningPanel(_tuningPreferences, async preferences =>
        { await TuningStore.SaveAsync(preferences); _tuningPreferences = preferences; await QueueTuningAsync(force: true); },
            async () =>
            {
                await _tuningWork;
                var sessions = Instances.ToArray();
                return await Task.Run(() => sessions.Count(s => _tuning.TrimMemory(s).Success));
            });
        TuningSettingsHost.Content = _tuningPanel;
    }
    private Task QueueTuningAsync(bool force = false)
    {
        if (_shutdownStarted || _storageMaintenanceRequested || !_tuningWork.IsCompleted) return _tuningWork;
        if (!force && (!_tuningPreferences.Enabled || DateTime.UtcNow - _lastTuningUtc < TimeSpan.FromSeconds(10))) return _tuningWork;
        _lastTuningUtc = DateTime.UtcNow;
        var sessions = Instances.Where(s => s.HasWindow).ToArray(); var preferences = _tuningPreferences;
        _tuningWork = Task.Run(() => _tuning.Reconcile(sessions, preferences));
        return _tuningWork;
    }
}
