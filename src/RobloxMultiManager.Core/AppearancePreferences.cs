using System.Text.Json;
using System.Text.RegularExpressions;

namespace RobloxMultiManager.Core;

public sealed record AppearancePreferences(string Theme = "midnight", string Accent = "#98B6FF", int TextScale = 100,
    string Font = "Segoe UI", bool StreamerMode = false, int UiGeneration = 1)
{
    public static AppearancePreferences Validate(AppearancePreferences data)
    {
        if (data.Theme is not ("midnight" or "graphite" or "light" or "contrast" or "forest" or "ocean" or "amethyst" or "rose" or "sand") || data.TextScale is < 100 or > 150
            || data.Font is not ("Segoe UI" or "Arial" or "Tahoma" or "Verdana")
            || !Regex.IsMatch(data.Accent ?? "", "^#[0-9A-Fa-f]{6}$"))
            throw new InvalidDataException(L.T("Tema, cor ou tamanho de texto inválido."));
        return string.Equals(data.Accent, "#B9ADFF", StringComparison.OrdinalIgnoreCase) ? data with { Accent = "#65DAC8" } : data;
    }
}

public static class DisplayPrivacy
{
    public static bool Enabled { get; set; }
    public static string AccountName(Guid id, string name) => Enabled ? L.F("Conta {0}", id.ToString("N")[..4].ToUpperInvariant()) : name;
}

public sealed class AppearancePreferencesStore(AppPaths paths)
{
    private string FilePath => Path.Combine(paths.Root, "appearance.json");
    public async Task<AppearancePreferences> LoadAsync()
    {
        if (!File.Exists(FilePath)) return new();
        if (new FileInfo(FilePath).Length > 16384) throw new InvalidDataException();
        var json = await SecureJsonFiles.ReadAllTextAsync(FilePath);
        var value = AppearancePreferences.Validate(JsonSerializer.Deserialize<AppearancePreferences>(json) ?? throw new InvalidDataException());
        using var parsed = JsonDocument.Parse(json);
        // Refresh legacy dark defaults once; preserve accessible themes, text size,
        // fonts and custom colors. A subsequently saved choice stays unchanged.
        if (!parsed.RootElement.TryGetProperty(nameof(AppearancePreferences.UiGeneration), out _) && value.Theme is "graphite" or "forest")
            value = value with { Theme = "midnight", Accent = value.Accent == "#65DAC8" ? "#98B6FF" : value.Accent };
        return value;
    }
    public async Task SaveAsync(AppearancePreferences preferences)
    {
        AppearancePreferences.Validate(preferences); Directory.CreateDirectory(paths.Root);
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await SecureJsonFiles.WriteAllTextAsync(temporary, JsonSerializer.Serialize(preferences)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string Export(AppearancePreferences data) => JsonSerializer.Serialize(AppearancePreferences.Validate(data) with { StreamerMode = false }, new JsonSerializerOptions { WriteIndented = true });
    public static AppearancePreferences Import(string json) => json.Length <= 16384
        ? AppearancePreferences.Validate(JsonSerializer.Deserialize<AppearancePreferences>(json) ?? throw new InvalidDataException())
        : throw new InvalidDataException();
}
