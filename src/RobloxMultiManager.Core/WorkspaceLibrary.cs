using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record AccountGroup(Guid Id, string Name, Guid[] Accounts);
public sealed record AccountMetadata(Guid ProfileId, string Notes, string[] Tags, Dictionary<string, string>? Fields = null);
public sealed record LaunchPreset(Guid Id, string Name, Guid[] Accounts, string Destination,
    int DelaySeconds, int GlobalCpuPercent, int Monitor, bool AutoArrange);
public sealed record WorkspaceLibrary(int Version, AccountGroup[] Groups, LaunchPreset[] Presets, AccountMetadata[] Metadata)
{
    public static WorkspaceLibrary Empty => new(1, [], [], []);
}

public sealed class WorkspaceLibraryStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Root, "workspaces.json");
    private readonly SemaphoreSlim _gate = new(1);
    public async Task<WorkspaceLibrary> LoadAsync()
    {
        if (!File.Exists(FilePath)) return WorkspaceLibrary.Empty;
        if (new FileInfo(FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException();
        var data = JsonSerializer.Deserialize<WorkspaceLibrary>(await SecureJsonFiles.ReadAllTextAsync(FilePath));
        return Validate(data ?? throw new InvalidDataException());
    }
    public async Task<WorkspaceLibrary> UpdateAsync(Func<WorkspaceLibrary, WorkspaceLibrary> change)
    {
        await _gate.WaitAsync();
        try
        {
            var updated = Validate(change(await LoadAsync()));
            var json = JsonSerializer.Serialize(updated);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 2 * 1024 * 1024) throw new InvalidDataException();
            Directory.CreateDirectory(paths.Root);
            var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await SecureJsonFiles.WriteAllTextAsync(temporary, json); File.Move(temporary, FilePath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return updated;
        }
        finally { _gate.Release(); }
    }
    private static WorkspaceLibrary Validate(WorkspaceLibrary data)
    {
        if (data.Version != 1 || data.Groups is null || data.Presets is null || data.Metadata is null
            || data.Groups.Any(g => g is null) || data.Presets.Any(p => p is null) || data.Metadata.Any(m => m is null)
            || data.Groups.Length > 200 || data.Presets.Length > 200 || data.Metadata.Length > 2000
            || data.Groups.Any(g => !ValidName(g.Name) || g.Id == Guid.Empty || g.Accounts is null || g.Accounts.Length > 500 || g.Accounts.Distinct().Count() != g.Accounts.Length)
            || data.Presets.Any(p => !ValidName(p.Name) || p.Id == Guid.Empty || p.Accounts is null || p.Accounts.Length is < 1 or > 500
                || p.Accounts.Distinct().Count() != p.Accounts.Length || p.GlobalCpuPercent is < 1 or > 100 || p.DelaySeconds is < 1 or > 120
                || p.Monitor < -1 || (p.Destination != "https://www.roblox.com/home" && !RobloxLinkParser.TryParse(p.Destination, out _, out _)))
            || data.Metadata.Any(m => m.ProfileId == Guid.Empty || m.Notes is null || m.Notes.Length > 4000 || m.Tags is null || m.Tags.Length > 30 || m.Tags.Any(t => !ValidName(t))
                || m.Fields is { } fields && (fields.Count > 20 || fields.Any(f => !ValidName(f.Key) || f.Value is null || f.Value.Length > 500)))
            || data.Groups.Select(g => g.Id).Distinct().Count() != data.Groups.Length
            || data.Presets.Select(p => p.Id).Distinct().Count() != data.Presets.Length
            || data.Metadata.Select(m => m.ProfileId).Distinct().Count() != data.Metadata.Length)
            throw new InvalidDataException(L.T("Os grupos ou perfis salvos contêm dados inválidos."));
        return data;
    }
    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 && !value.Any(char.IsControl);
}
