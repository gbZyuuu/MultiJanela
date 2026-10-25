namespace RobloxMultiManager.Core;

public sealed record ReconnectRequest(Guid ProfileId, string SourceProcessKey, RobloxLaunchTarget? Target);
public sealed record ReconnectBudget(Guid ProfileId, int Attempts, DateTime LastUtc);

public sealed class ReconnectCoordinator
{
    private sealed record Pending(ReconnectRequest Request, DateTime DueUtc, int MaxAttempts, int Epoch);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Pending> _pending = [];
    private readonly Dictionary<Guid, (int Attempts, DateTime LastUtc)> _budgets = [];
    private readonly HashSet<Guid> _running = [];
    private readonly Func<ReconnectRequest, bool> _hasReplacement, _canLaunch;
    private readonly Func<ReconnectRequest, CancellationToken, Task<bool>> _launch;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _cooldown;
    private bool _pumping;
    private int _epoch;
    public ReconnectCoordinator(Func<ReconnectRequest, bool> hasReplacement,
        Func<ReconnectRequest, bool> canLaunch, Func<ReconnectRequest, CancellationToken, Task<bool>> launch,
        TimeSpan? cooldown = null, Func<DateTime>? utcNow = null)
    { _hasReplacement = hasReplacement; _canLaunch = canLaunch; _launch = launch; _cooldown = cooldown ?? TimeSpan.FromSeconds(12); _now = utcNow ?? (() => DateTime.UtcNow); }
    public bool HasWork { get { lock (_gate) return _pending.Count > 0 || _running.Count > 0; } }
    public int Attempts(Guid profileId) { lock (_gate) return _budgets.GetValueOrDefault(profileId).Attempts; }
    public ReconnectBudget[] ExportBudgets()
    { lock (_gate) return _budgets.Select(p => new ReconnectBudget(p.Key, p.Value.Attempts, p.Value.LastUtc)).ToArray(); }
    public void ImportBudgets(IEnumerable<ReconnectBudget> budgets)
    {
        lock (_gate)
            foreach (var item in budgets.Where(b => b.ProfileId != Guid.Empty && b.Attempts is >= 0 and <= 10))
                if (!_budgets.ContainsKey(item.ProfileId)) _budgets[item.ProfileId] = (item.Attempts, item.LastUtc);
    }
    public bool Enqueue(ReconnectRequest request, int maxAttempts, bool previousSessionStable)
    {
        lock (_gate)
        {
            if (_pending.ContainsKey(request.ProfileId) || _running.Contains(request.ProfileId)) return false;
            if (previousSessionStable) _budgets.Remove(request.ProfileId);
            var budget = _budgets.GetValueOrDefault(request.ProfileId);
            if (budget.Attempts >= maxAttempts) return false;
            var due = budget.LastUtc + _cooldown;
            _pending[request.ProfileId] = new(request, due > _now() ? due : _now(), Math.Clamp(maxAttempts, 1, 10), _epoch);
            return true;
        }
    }
    public void CancelAll()
    { lock (_gate) { _epoch++; _pending.Clear(); _budgets.Clear(); } }
    public async Task TickAsync(CancellationToken token = default)
    {
        Pending[] candidates;
        lock (_gate)
        {
            if (_pumping || token.IsCancellationRequested) return;
            _pumping = true;
            candidates = _pending.Values.Where(p => p.DueUtc <= _now()).OrderBy(p => p.DueUtc).ToArray();
        }
        try
        {
            List<Task> tasks = [];
            foreach (var item in candidates)
            {
                if (token.IsCancellationRequested) break;
                if (_hasReplacement(item.Request)) { lock (_gate) _pending.Remove(item.Request.ProfileId); continue; }
                if (!_canLaunch(item.Request)) continue;
                lock (_gate)
                {
                    if (!_pending.Remove(item.Request.ProfileId) || item.Epoch != _epoch) continue;
                    var budget = _budgets.GetValueOrDefault(item.Request.ProfileId);
                    _budgets[item.Request.ProfileId] = (budget.Attempts + 1, _now());
                    _running.Add(item.Request.ProfileId);
                }
                tasks.Add(RunAsync(item, token));
                if (tasks.Count == 2) break;
            }
            await Task.WhenAll(tasks);
        }
        finally { lock (_gate) _pumping = false; }
    }
    private async Task RunAsync(Pending item, CancellationToken token)
    {
        var success = false;
        try { success = await _launch(item.Request, token); }
        catch (Exception) { /* Retry budget and cancellation remain authoritative. */ }
        finally
        {
            lock (_gate)
            {
                _running.Remove(item.Request.ProfileId);
                var budget = _budgets.GetValueOrDefault(item.Request.ProfileId);
                if (!success && !token.IsCancellationRequested && item.Epoch == _epoch && budget.Attempts < item.MaxAttempts)
                    _pending[item.Request.ProfileId] = item with { DueUtc = _now() + _cooldown };
            }
        }
    }
}
