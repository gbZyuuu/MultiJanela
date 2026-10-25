using System.Windows;
using System.Windows.Media;
using RobloxMultiManager.Core;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;

namespace RobloxMultiManager;

internal static class AppearanceService
{
    private static Dictionary<string, Color>? _original;
    public static AppearancePreferences Current { get; private set; } = new();
    public static event EventHandler? Changed;
    public static void Apply(AppearancePreferences preferences)
    {
        preferences = AppearancePreferences.Validate(preferences);
        var app = System.Windows.Application.Current;
        _original ??= new ResourceDictionary { Source = new Uri("pack://application:,,,/RobloxMultiManager;component/Theme/Palette.xaml") }
            .Cast<System.Collections.DictionaryEntry>().Where(e => e.Value is SolidColorBrush)
            .ToDictionary(e => (string)e.Key, e => ((SolidColorBrush)e.Value!).Color);
        var colors = new Dictionary<string, Color>(_original);
        void Set(string key, string hex) => colors[key] = (Color)ColorConverter.ConvertFromString(hex);
        var theme = ThemePresets.Find(preferences.Theme);
        Set("WindowBackground", theme.Background); Set("Surface", theme.Surface); Set("SurfaceRaised", theme.Raised); Set("SurfaceInset", theme.Inset);
        Set("Hairline", theme.Border); Set("HairlineSoft", theme.SoftBorder); Set("HoverOverlay", theme.Raised);
        Set("GlassBorder", theme.SoftBorder); Set("GlassBorderHover", theme.Border); Set("ScrollThumb", theme.Border); Set("ScrollThumbHover", theme.Accent);
        if (theme.Light)
        {
            Set("TextPrimary", "#202936"); Set("TextMuted", "#465366"); Set("TextFaint", "#526075");
            Set("Info", "#235A92"); Set("Success", "#14623D"); Set("Danger", "#AA2944"); Set("DangerText", "#8E1835"); Set("Warning", "#795000");
            Set("DangerSurface", "#F9E2E7"); Set("DangerBorder", "#D9A2AE"); Set("InfoSurface", "#E2ECFA"); Set("SuccessSurface", "#DDEFE5"); Set("WarningSurface", "#FAEFD3");
        }
        if (theme.Id == "contrast") { Set("TextPrimary", "#FFFFFF"); Set("TextMuted", "#EEEEEE"); Set("TextFaint", "#DDDDDD"); }
        var accent = (Color)ColorConverter.ConvertFromString(preferences.Accent is "#65DAC8" or "#98B6FF" ? theme.Accent : preferences.Accent);
        colors["Accent"] = accent;
        static Color Mix(Color a, Color b, double amount) => Color.FromRgb((byte)(a.R+(b.R-a.R)*amount), (byte)(a.G+(b.G-a.G)*amount), (byte)(a.B+(b.B-a.B)*amount));
        colors["AccentHover"] = Mix(accent, theme.Light ? Colors.Black : Colors.White, .12);
        colors["AccentPressed"] = Mix(accent, Colors.Black, .12);
        colors["TextSelection"] = Mix(accent, colors["Surface"], .65);
        colors["AccentInk"] = accent.R * .299 + accent.G * .587 + accent.B * .114 > 150 ? Colors.Black : Colors.White;
        colors["AccentBorder"] = Color.FromArgb(170, accent.R, accent.G, accent.B);
        colors["AccentSurface"] = colors["SelectionRow"] = Color.FromArgb(45, accent.R, accent.G, accent.B);
        colors["LaunchSurface"] = Color.FromArgb(24, accent.R, accent.G, accent.B);
        foreach (var (key, color) in colors)
        { var brush = new SolidColorBrush(color); brush.Freeze(); app.Resources[key] = brush; }
        app.Resources["AccentGradient"] = app.Resources["Accent"];
        app.Resources["AccentGradientHover"] = app.Resources["AccentHover"];
        app.Resources["AccentGradientPressed"] = app.Resources["AccentPressed"];
        app.Resources["SurfaceGlass"] = app.Resources["Surface"];
        app.Resources["SurfaceGlassRaised"] = app.Resources["SurfaceRaised"];
        foreach (var size in new[] { 10d, 11, 12, 12.5, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 30, 32, 34, 36, 38, 40 })
            app.Resources["Font" + size.ToString(System.Globalization.CultureInfo.InvariantCulture)] = size * preferences.TextScale / 100d;
        app.Resources["StudioTileHeight"] = 190d * preferences.TextScale / 100d;
        app.Resources["StudioTileWidth"] = 340d * preferences.TextScale / 100d;
        app.Resources["GameTileHeight"] = 226d * preferences.TextScale / 100d;
        app.Resources["ServerTileWidth"] = 220d * preferences.TextScale / 100d;
        app.Resources["ServerTileHeight"] = 130d * preferences.TextScale / 100d;
        app.Resources["ServerIdHeight"] = 32d * preferences.TextScale / 100d;
        app.Resources["GameTileWidth"] = 184d * preferences.TextScale / 100d;
        app.Resources["NavigationExpandedWidth"] = 220d * preferences.TextScale / 100d;
        app.Resources["InterfaceFontFamily"] = new FontFamily(preferences.Font);
        DisplayPrivacy.Enabled = preferences.StreamerMode;
        Current = preferences;
        foreach (Window window in app.Windows) WindowThemeService.ApplyDarkTitleBar(window);
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
