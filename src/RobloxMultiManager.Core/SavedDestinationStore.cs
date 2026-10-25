using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed class SavedDestinationStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string FilePath { get; } = Path.Combine(paths.Root, "saved-destinations.json");
    public async Task<IReadOnlyList<SavedLaunchDestination>> LoadAsync()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            await using var stream = SecureJsonFiles.OpenRead(FilePath);
            var entries = await JsonSerializer.DeserializeAsync<SavedLaunchDestination[]>(stream, Options).ConfigureAwait(false);
            return Validate(entries ?? throw new InvalidDataException(RobloxMultiManager.Core.L.T("Arquivo de destinos inválido.")));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        { throw new InvalidDataException(RobloxMultiManager.Core.L.T("Não foi possível ler os destinos salvos."), ex); }
    }
    public async Task SaveAsync(IEnumerable<SavedLaunchDestination> entries)
    {
        var snapshot = Validate(entries);
        Directory.CreateDirectory(paths.Root);
        var temp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, Options).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            File.Move(temp, FilePath, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static SavedLaunchDestination[] Validate(IEnumerable<SavedLaunchDestination> entries)
    {
        var result = new List<SavedLaunchDestination>();
        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.Name is null || entry.Link is null || !ids.Add(entry.Id)
                || !SavedLaunchDestination.TryCreate(entry.Name, entry.Link, entry.Id, out var normalized, out _))
                throw new ArgumentException(RobloxMultiManager.Core.L.T("Há destinos salvos inválidos ou repetidos."));
            result.Add(normalized);
        }
        return result.ToArray();
    }
}
