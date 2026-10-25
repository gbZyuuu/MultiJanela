using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed class PreferenceFile<T> where T : class
{
    private readonly string _path;
    private readonly Func<T> _defaults;
    private readonly Func<T, T> _validate;
    private readonly SemaphoreSlim _gate = new(1);
    public PreferenceFile(AppPaths paths, string name, Func<T> defaults, Func<T, T> validate)
    {
        if (!Regex.IsMatch(name, "^[a-z-]+\\.json$")) throw new ArgumentException();
        _path = Path.Combine(paths.Root, name); _defaults = defaults; _validate = validate;
    }
    public async Task<T> LoadAsync()
    {
        if (!File.Exists(_path)) return _defaults();
        if (new FileInfo(_path).Length > 1024 * 1024) throw new InvalidDataException();
        return _validate(JsonSerializer.Deserialize<T>(await SecureJsonFiles.ReadAllTextAsync(_path)) ?? throw new InvalidDataException());
    }
    private static string BoundedJson(T value)
    { var json = JsonSerializer.Serialize(value); if (System.Text.Encoding.UTF8.GetByteCount(json) > 1024 * 1024) throw new InvalidDataException(); return json; }
    public async Task SaveAsync(T value)
    {
        _validate(value); await _gate.WaitAsync();
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); await SecureJsonFiles.WriteAllTextAsync(temporary, BoundedJson(value)); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
    public async Task<T> UpdateAsync(Func<T, T> change)
    {
        await _gate.WaitAsync();
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var value = _validate(change(await LoadAsync()));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await SecureJsonFiles.WriteAllTextAsync(temporary, BoundedJson(value)); File.Move(temporary, _path, true);
            return value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); _gate.Release(); }
    }
}
