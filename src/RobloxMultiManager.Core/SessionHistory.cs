using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record SessionHistoryEntry(string Id, DateTime TimestampUtc, string Kind, string ProcessKey,
    int ProcessId, Guid? ProfileId, string? AccountName, string? ClientVersion, string Reason)
{
    public string AccountCaption => DisplayPrivacy.Enabled || string.IsNullOrEmpty(AccountName)
        ? ProfileId is Guid id ? DisplayPrivacy.AccountName(id, L.T("Conta")) : L.T("Sessão") : AccountName;
    public string ActionCaption => Kind switch { "opened" => L.T("Aberta"), "closed" => L.T("Encerrada"), "authentication" => L.T("Autenticação recusada"), _ => L.T("Falha") };
    public string LocalTime => TimestampUtc.ToLocalTime().ToString("g");
}

public sealed class SessionHistoryStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Root, "session-history.json");
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<SessionHistoryEntry[]> LoadAsync()
    {
        if (!File.Exists(FilePath)) return [];
        if (new FileInfo(FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException();
        var result = JsonSerializer.Deserialize<SessionHistoryEntry[]>(await SecureJsonFiles.ReadAllTextAsync(FilePath));
        if (result is null || result.Length > 500 || result.Any(e => e is null || string.IsNullOrEmpty(e.Id) || e.Id.Length > 160)) throw new InvalidDataException();
        return result;
    }
    public async Task AppendAsync(IEnumerable<SessionHistoryEntry> entries)
    {
        var snapshot = entries.ToArray(); if (snapshot.Length == 0) return;
        await _gate.WaitAsync();
        try
        {
            var result = (await LoadAsync()).Concat(snapshot).DistinctBy(e => e.Id)
                .OrderByDescending(e => e.TimestampUtc).Take(500).ToArray();
            await WriteAsync(result);
        }
        finally { _gate.Release(); }
    }
    public async Task ClearAsync()
    { await _gate.WaitAsync(); try { await WriteAsync([]); } finally { _gate.Release(); } }
    private async Task WriteAsync(SessionHistoryEntry[] entries)
    {
        Directory.CreateDirectory(paths.Root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await SecureJsonFiles.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string ExportCsv(IEnumerable<SessionHistoryEntry> entries, bool includeNames = false)
    {
        static string Escape(string? text)
        {
            text ??= "";
            if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string> { "TimeUtc,Action,PID,Account,Version,Reason" };
        lines.AddRange(entries.Select(e => string.Join(",", new[] { e.TimestampUtc.ToString("O"), e.Kind, e.ProcessId.ToString(),
            includeNames ? e.AccountName ?? "" : e.ProfileId is Guid id ? "Account " + id.ToString("N")[..4] : "Session", e.ClientVersion ?? "", e.Reason }.Select(Escape))));
        return string.Join(Environment.NewLine, lines);
    }
}

public sealed class ResourceHistory
{
    private readonly Dictionary<string, Queue<ProcessResourceUsage>> _series = new(StringComparer.Ordinal);
    public void Add(IReadOnlyDictionary<string, ProcessResourceUsage> sample)
    {
        foreach (var (key, value) in sample)
        {
            if (!_series.TryGetValue(key, out var data))
            {
                if (_series.Count >= 64) _series.Remove(_series.MinBy(p => p.Value.LastOrDefault()?.SampledUtc ?? DateTime.MinValue).Key);
                _series[key] = data = new();
            }
            if (data.LastOrDefault()?.SampledUtc == value.SampledUtc) continue;
            data.Enqueue(value); while (data.Count > 180) data.Dequeue();
        }
    }
    public ProcessResourceUsage[] Get(string key) => _series.TryGetValue(key, out var values) ? values.ToArray() : [];
    public void Clear() => _series.Clear();
}
