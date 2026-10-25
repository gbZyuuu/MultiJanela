using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace RobloxMultiManager;

internal sealed record ThemePreset(string Id, string Name, string Background, string Surface, string Raised, string Inset, string Border, string SoftBorder, string Accent, bool Light = false);

internal static class ThemePresets
{
    public static readonly ThemePreset[] All =
    [
        new("midnight", "Noturno", "#10141C", "#1A202B", "#252E3C", "#151B25", "#4A586F", "#303C50", "#98B6FF"),
        new("graphite", "Grafite", "#141516", "#202224", "#2D3033", "#191B1D", "#60676D", "#383D42", "#D1D7DE"),
        new("ocean", "Oceano", "#0D1922", "#142733", "#203A49", "#101F2A", "#4B7285", "#2A4758", "#76D3F0"),
        new("forest", "Floresta", "#111D19", "#1A2C24", "#2A4034", "#14221C", "#567966", "#324F3F", "#9AE0AF"),
        new("amethyst", "Ametista", "#191422", "#272033", "#382E49", "#201A2B", "#78658F", "#493B5B", "#C9ADFA"),
        new("rose", "Rosé", "#21171C", "#32232B", "#46313D", "#281D23", "#876473", "#533A47", "#F1B1C5"),
        new("sand", "Areia", "#F2EEE6", "#FFFCF5", "#E8E1D5", "#F6F2E9", "#9A8D78", "#D5CABB", "#79582D", true),
        new("light", "Claro", "#F1F4F8", "#FFFFFF", "#E4EBF3", "#F6F8FC", "#8A9BB3", "#CBD5E3", "#315EA0", true),
        new("contrast", "Alto contraste", "#000000", "#080808", "#171717", "#000000", "#FFFFFF", "#999999", "#FFFF00")
    ];
    public static ThemePreset Find(string id) => All.First(p => p.Id == id);
    public static SolidColorBrush Brush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
}
