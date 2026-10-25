using System.Globalization;
using System.Text.Json;

namespace RobloxMultiManager.Core;

/// <summary>App-owned text only. Account names, URLs, protocol values and external responses are never translated.</summary>
public static class L
{
    private static readonly IReadOnlyDictionary<string, string> English = Load();
    private static string _language = "en";
    public static event EventHandler? LanguageChanged;
    public static string Language
    {
        get => _language;
        set
        {
            var next = UiPreferences.NormalizeLanguage(value);
            if (_language == next) return;
            _language = next;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "pt-BR" ? "pt-BR" : "en-US");
    public static IReadOnlyDictionary<string, string> Catalog => English;
    public static string T(string portuguese) => Language == "pt-BR" ? portuguese : English.TryGetValue(portuguese, out var english) ? english : portuguese;
    public static string F(string portuguese, params object?[] values) => string.Format(Culture, T(portuguese), values);
    private static IReadOnlyDictionary<string, string> Load()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("RobloxMultiManager.Core.Localization.en.json")
            ?? throw new InvalidOperationException("Missing English localization catalog.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
