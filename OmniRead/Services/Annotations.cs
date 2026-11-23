using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniRead.Services;

public enum AnnotationKind {
    Bookmark,
    Highlight
}

/// <summary>
/// A bookmark or a highlighted passage. Places are stored as a block within a chapter plus a character
/// offset, which survives changes of font, size and window but not a change of the "format like a book" option.
/// </summary>
public sealed class Annotation {
    public AnnotationKind Kind { get; set; }

    public int ChapterIndex { get; set; }

    public int BlockIndex { get; set; }

    public int Offset { get; set; }

    public int EndBlockIndex { get; set; }

    public int EndOffset { get; set; }

    /// <summary>The marked passage, or the first words after a bookmark.</summary>
    public string Text { get; set; } = "";

    public string Note { get; set; } = "";

    public DateTime Created { get; set; }

    [JsonIgnore]
    public string Glyph => (Kind == AnnotationKind.Bookmark) ? "" : "";

    /// <summary>The chapter title, filled in when the book is open.</summary>
    [JsonIgnore]
    public string Where { get; set; } = "";
}

/// <summary>Keeps every book's bookmarks and highlights in one file beside the settings.</summary>
public static class AnnotationStore {
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static Dictionary<string, List<Annotation>>? _all;

    private static string FilePath => Path.Combine(SettingsStore.DataFolder, "annotations.json");

    public static List<Annotation> ForBook(string bookPath) {
        Dictionary<string, List<Annotation>> all = LoadAll();
        if (!all.TryGetValue(bookPath, out List<Annotation>? annotations)) {
            annotations = [];
            all[bookPath] = annotations;
        }
        return annotations;
    }

    public static void Save() {
        if (_all is null) {
            return;
        }
        try {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            Dictionary<string, List<Annotation>> used = _all
                .Where(static entry => entry.Value.Count > 0)
                .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(used, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Losing a save is better than interrupting reading; the next change tries again.
        }
    }

    private static Dictionary<string, List<Annotation>> LoadAll() {
        if (_all is not null) {
            return _all;
        }
        Dictionary<string, List<Annotation>>? loaded = null;
        try {
            if (File.Exists(FilePath)) {
                loaded = JsonSerializer.Deserialize<Dictionary<string, List<Annotation>>>(File.ReadAllText(FilePath), Options);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // A damaged file starts an empty collection rather than stopping the reader.
        }
        _all = new Dictionary<string, List<Annotation>>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
        return _all;
    }
}