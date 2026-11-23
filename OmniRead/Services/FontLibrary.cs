using System.IO;
using System.Windows.Markup;
using System.Windows.Media;

using OmniRead.Localization;

namespace OmniRead.Services;

public sealed record FontChoice(string Name, FontFamily Family, FontOrigin Origin) {
    public string Group => Localizer.Get(Origin switch {
        FontOrigin.Bundled => "Fonts_Bundled",
        FontOrigin.Folder => "Fonts_Folder",
        _ => "Fonts_System"
    });
}

/// <summary>Finds fonts from three places: the ones shipped with the app, the user's fonts folder, and Windows.</summary>
public static class FontLibrary {
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    /// <summary>Fonts shipped next to the executable.</summary>
    public static string BundledFolder { get; } = Path.Combine(AppContext.BaseDirectory, "Fonts");

    /// <summary>Drop .ttf / .otf files (or folders of them) here to make them available in the reader.</summary>
    public static string UserFolder { get; } = Path.Combine(SettingsStore.DataFolder, "Fonts");

    public static List<FontChoice> Load() {
        List<FontChoice> fonts = [];
        fonts.AddRange(Scan(BundledFolder, FontOrigin.Bundled));
        try {
            Directory.CreateDirectory(UserFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Without the folder there are simply no user fonts.
        }
        fonts.AddRange(Scan(UserFolder, FontOrigin.Folder));
        fonts.AddRange(Fonts.SystemFontFamilies
            .Select(static family => new FontChoice(DisplayName(family), family, FontOrigin.System))
            .OrderBy(static font => font.Name, StringComparer.CurrentCultureIgnoreCase));
        return fonts;
    }

    public static FontChoice Find(IReadOnlyList<FontChoice> fonts, string name, FontOrigin origin) {
        return fonts.FirstOrDefault(font => (font.Origin == origin) && Same(font.Name, name))
            ?? fonts.FirstOrDefault(font => Same(font.Name, name))
            ?? fonts.FirstOrDefault(static font => font.Origin == FontOrigin.Bundled)
            ?? fonts.FirstOrDefault(static font => Same(font.Name, "Georgia"))
            ?? fonts[0];
    }

    private static bool Same(string a, string b) {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<FontChoice> Scan(string root, FontOrigin origin) {
        if (!Directory.Exists(root)) {
            return [];
        }
        Dictionary<string, FontChoice> found = new(StringComparer.OrdinalIgnoreCase);
        try {
            IEnumerable<string> directories = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root);
            foreach (string directory in directories) {
                bool hasFonts = Directory.EnumerateFiles(directory)
                    .Any(static file => FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()));
                if (!hasFonts) {
                    continue;
                }
                // The trailing separator tells WPF this is a folder of font files.
                Uri location = new(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                foreach (FontFamily family in Fonts.GetFontFamilies(location)) {
                    string name = DisplayName(family);
                    found.TryAdd(name, new FontChoice(name, family, origin));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException) {
            // An unreadable folder only means fewer fonts in the list.
        }
        return found.Values.OrderBy(static font => font.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string DisplayName(FontFamily family) {
        if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-us"), out string? english)) {
            return english;
        }
        string? first = family.FamilyNames.Values.FirstOrDefault();
        if (first is not null) {
            return first;
        }
        int hash = family.Source.LastIndexOf('#');
        return (hash >= 0) ? family.Source[(hash + 1)..] : family.Source;
    }
}