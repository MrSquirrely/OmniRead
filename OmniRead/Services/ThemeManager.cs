using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using Windows.UI.ViewManagement;

namespace OmniRead.Services;

/// <summary>The colours and type adjustments currently in effect.</summary>
public sealed record Palette(Color Page, Color Text, Color Accent, bool IsDark, double FontScale, double LineBoost, double ImageOpacity);

/// <summary>
/// Computes the active palette from the theme or eye-comfort profile, publishes it as application brushes and
/// colours the Windows 11 title bar to match.
/// </summary>
public static class ThemeManager {
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private static readonly UISettings SystemSettings = new();

    public static Palette Current { get; private set; } = new(Colors.White, Colors.Black, Colors.SteelBlue, false, 1, 1, 1);

    /// <summary>Raised (on an arbitrary thread) when the Windows theme or accent colour changes.</summary>
    public static event Action? SystemThemeChanged;

    static ThemeManager() {
        SystemSettings.ColorValuesChanged += static (_, _) => SystemThemeChanged?.Invoke();
    }

    public static void Apply(ReaderSettings settings, Window? window) {
        Palette palette = Compute(settings);
        Current = palette;

        Color chrome = Mix(palette.Page, palette.Text, palette.IsDark ? 0.07 : 0.05);
        ResourceDictionary resources = Application.Current.Resources;
        resources["PageBrush"] = Brush(palette.Page);
        resources["ChromeBrush"] = Brush(chrome);
        resources["PanelBrush"] = Brush(Mix(palette.Page, palette.Text, palette.IsDark ? 0.03 : 0.02));
        resources["TextBrush"] = Brush(palette.Text);
        resources["MutedBrush"] = Brush(Mix(palette.Text, palette.Page, 0.42));
        resources["DividerBrush"] = Brush(Mix(palette.Page, palette.Text, 0.16));
        resources["HoverBrush"] = Brush(Mix(palette.Page, palette.Text, 0.12));
        resources["CodeBrush"] = Brush(Mix(palette.Page, palette.Text, 0.07));
        resources["AccentBrush"] = Brush(palette.Accent);

        ThemeMode mode = palette.IsDark ? ThemeMode.Dark : ThemeMode.Light;
        if (Application.Current.ThemeMode != mode) {
            Application.Current.ThemeMode = mode;
        }
        if (window is not null) {
            ApplyTitleBar(window, chrome, palette);
        }
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

    private static void ApplyTitleBar(Window window, Color chrome, Palette palette) {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) {
            return;
        }
        int dark = palette.IsDark ? 1 : 0;
        int caption = ColorRef(chrome);
        int text = ColorRef(palette.Text);
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref caption, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
    }

    private static int ColorRef(Color color) {
        return color.R | (color.G << 8) | (color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}