using System.Text.Json;

namespace RobloxMultiManager.Core;

public sealed record UiPreferences(string Language = "en", bool Animations = true)
{
    public static UiPreferences Current { get; private set; } = new();
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RobloxMultiManager", "interface.json");
    public static UiPreferences Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var saved = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(path)) ?? new();
            return saved with { Language = NormalizeLanguage(saved.Language) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public static string NormalizeLanguage(string? language) => language == "pt-BR" ? "pt-BR" : "en";
    public static void Initialize() { Current = Read(DefaultPath); L.Language = Current.Language; }
    public static void Apply(UiPreferences preferences) => Current = preferences with { Language = NormalizeLanguage(preferences.Language) };
    public static void Save(UiPreferences preferences, string? path = null, bool applyCurrent = true)
    {
        path ??= DefaultPath;
        var normalized = preferences with { Language = NormalizeLanguage(preferences.Language) };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(normalized));
        File.Move(temporary, path, true);
        if (applyCurrent) Current = normalized;
    }
}
