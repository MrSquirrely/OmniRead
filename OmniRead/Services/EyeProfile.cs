using System.Windows.Media;

using OmniRead.Localization;

namespace OmniRead.Services;

/// <summary>
/// A reading profile aimed at a particular kind of eye strain. Besides colours, a profile sets a starting
/// blue-light reduction and brightness, and may enlarge type or open up line spacing.
/// </summary>
/// <param name="Id">The name stored in the settings file. Never translated.</param>
/// <param name="ResourceKey">Prefix of the profile's name and description in the string resources.</param>
public sealed record EyeProfile(
    string Id,
    string ResourceKey,
    Color Page,
    Color Text,
    Color Accent,
    double Warmth = 0,
    double Dim = 0,
    double FontScale = 1,
    double LineBoost = 1,
    double ImageOpacity = 1) {
    public string Name => Localizer.Get($"Profile_{ResourceKey}_Name");

    public string Description => Localizer.Get($"Profile_{ResourceKey}_Description");

    public Brush PageBrush { get; } = Frozen(Page);

    public Brush TextBrush { get; } = Frozen(Text);

    public static IReadOnlyList<EyeProfile> All { get; } = [
        new("Warm Paper", "WarmPaper", Hex("#F7F0E0"), Hex("#3A3128"), Hex("#9A5B2E"), Warmth: 0.15),
        new("Sepia", "Sepia", Hex("#EBDCBC"), Hex("#46382A"), Hex("#8A4F24"), Warmth: 0.3, LineBoost: 1.04),
        new("Soft Green", "SoftGreen", Hex("#D6E6D0"), Hex("#26352A"), Hex("#2F6B4A"), Warmth: 0.1),
        new("Grey Mist", "GreyMist", Hex("#D4D4D0"), Hex("#3C3C3C"), Hex("#4A5A6A"), Warmth: 0.1, Dim: 0.08, LineBoost: 1.05),
        new("Dusk", "Dusk", Hex("#24272C"), Hex("#C8CCD2"), Hex("#8FB4E0"), Warmth: 0.15, ImageOpacity: 0.85),
        new("Night Amber", "NightAmber", Hex("#17120D"), Hex("#E2AC6C"), Hex("#F0C080"), Warmth: 0.6, LineBoost: 1.05, ImageOpacity: 0.7),
        new("Midnight", "Midnight", Hex("#000000"), Hex("#8C8C8C"), Hex("#9A9A9A"), Warmth: 0.25, LineBoost: 1.05, ImageOpacity: 0.6),
        new("High Contrast", "HighContrast", Hex("#000000"), Hex("#FFE94A"), Hex("#FFFFFF"), FontScale: 1.15, LineBoost: 1.12, ImageOpacity: 0.9)
    ];

    public static EyeProfile Find(string id) {
        return All.FirstOrDefault(profile => profile.Id == id) ?? All[0];
    }

    private static Color Hex(string value) {
        return (Color)ColorConverter.ConvertFromString(value);
    }

    private static SolidColorBrush Frozen(Color color) {
        SolidColorBrush brush = new(color);
        brush.Freeze();
        return brush;
    }
}