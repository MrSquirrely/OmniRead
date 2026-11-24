using System.Windows;
using System.Windows.Media;

using HandyControl.Themes;

using Windows.UI.ViewManagement;

namespace OmniRead.Services;

/// <summary>The colours and type adjustments currently in effect.</summary>
public sealed record Palette(Color Page, Color Text, Color Accent, bool IsDark, double FontScale, double LineBoost, double ImageOpacity);

/// <summary>
/// Computes the active palette from the theme or eye-comfort profile and publishes it: as the app's own brushes,
/// and as the light or dark theme and accent colour of the HandyControls styles. Title bars are drawn in the
/// colour of their window.
/// </summary>
public static class ThemeManager {
    private static readonly UISettings SystemSettings = new();

    public static Palette Current { get; private set; } = new(Colors.White, Colors.Black, Colors.SteelBlue, false, 1, 1, 1);

    /// <summary>Raised (on an arbitrary thread) when the Windows theme or accent colour changes.</summary>
    public static event Action? SystemThemeChanged;

    static ThemeManager() {
        SystemSettings.ColorValuesChanged += static (_, _) => SystemThemeChanged?.Invoke();
    }

    public static void Apply(ReaderSettings settings) {
        Palette palette = Compute(settings);
        Current = palette;

        ResourceDictionary resources = Application.Current.Resources;
        resources["PageBrush"] = Brush(palette.Page);
        resources["ChromeBrush"] = Brush(Mix(palette.Page, palette.Text, palette.IsDark ? 0.07 : 0.05));
        resources["PanelBrush"] = Brush(Mix(palette.Page, palette.Text, palette.IsDark ? 0.03 : 0.02));
        // Title bars are the colour of the window under them. (The main window has no colour of its own: it shows
        // the Mica backdrop.)
        resources["TitleBarBrush"] = resources["PanelBrush"];
        resources["TextBrush"] = Brush(palette.Text);
        resources["MutedBrush"] = Brush(Mix(palette.Text, palette.Page, 0.42));
        resources["DividerBrush"] = Brush(Mix(palette.Page, palette.Text, 0.16));
        resources["HoverBrush"] = Brush(Mix(palette.Page, palette.Text, 0.12));
        resources["CodeBrush"] = Brush(Mix(palette.Page, palette.Text, 0.07));
        resources["AccentBrush"] = Brush(palette.Accent);

        // The control library's own theme: light or dark to suit the page, tinted with the palette's accent.
        HandyControl.Themes.ThemeManager controls = HandyControl.Themes.ThemeManager.Current;
        ApplicationTheme theme = palette.IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        if (controls.ApplicationTheme != theme) {
            controls.ApplicationTheme = theme;
        }
        controls.AccentColor = Brush(palette.Accent);
    }

    private static Palette Compute(ReaderSettings settings) {
        if (settings.EyeComfort) {
            EyeProfile profile = EyeProfile.Find(settings.EyeProfile);
            Color page = Filter(profile.Page, settings.Warmth, settings.Dim);
            return new Palette(
                page,
                Filter(profile.Text, settings.Warmth, settings.Dim),
                Filter(profile.Accent, settings.Warmth, settings.Dim),
                Luminance(page) < 0.4,
                profile.FontScale,
                profile.LineBoost,
                profile.ImageOpacity * (1 - (0.4 * settings.Dim)));
        }
        bool dark = settings.Theme switch {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemIsDark()
        };
        Windows.UI.Color accent = SystemSettings.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        return new Palette(
            dark ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xFD, 0xFD, 0xFC),
            dark ? Color.FromRgb(0xE2, 0xE2, 0xE2) : Color.FromRgb(0x1C, 0x1C, 0x1C),
            Color.FromRgb(accent.R, accent.G, accent.B),
            dark,
            1,
            1,
            dark ? 0.9 : 1);
    }

    private static bool SystemIsDark() {
        Windows.UI.Color background = SystemSettings.GetColorValue(UIColorType.Background);
        return (background.R + background.G + background.B) < 384;
    }

    /// <summary>Removes blue (and a little green) for warmth, then scales everything down for dimming.</summary>
    private static Color Filter(Color color, double warmth, double dim) {
        double brightness = 1 - (0.6 * dim);
        return Color.FromRgb(
            (byte)(color.R * brightness),
            (byte)(color.G * (1 - (0.16 * warmth)) * brightness),
            (byte)(color.B * (1 - (0.55 * warmth)) * brightness));
    }

    private static Color Mix(Color from, Color to, double amount) {
        return Color.FromRgb(
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
    }

    private static double Luminance(Color color) {
        return ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255;
    }

    private static SolidColorBrush Brush(Color color) {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}