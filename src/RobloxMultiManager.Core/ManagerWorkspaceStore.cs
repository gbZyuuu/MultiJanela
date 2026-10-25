using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record ManagerWorkspaceState(Guid[] Sequence, string Destination, Guid? SavedDestination,
    Guid? SelectedAccount, string Search, string Page, double Width, double Height, bool Maximized,
    string[] SelectedSessions, ReconnectBudget[]? ReconnectBudgets = null,
    double? Left = null, double? Top = null, Guid? LibrarySelection = null, Dictionary<string, Dictionary<string, string>>? Drafts = null);

public sealed class ManagerWorkspaceStore(string root)
{
    private readonly string _path = Path.Combine(root, "manager-workspace.json");
    public async Task SaveAsync(ManagerWorkspaceState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await SecureJsonFiles.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<ManagerWorkspaceState?> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 8 * 1024 * 1024) return null;
            return JsonSerializer.Deserialize<ManagerWorkspaceState>(await SecureJsonFiles.ReadAllTextAsync(_path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
