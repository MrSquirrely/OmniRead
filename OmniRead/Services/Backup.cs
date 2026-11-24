using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using OmniRead.Localization;

namespace OmniRead.Services;

/// <summary>
/// Saves everything the reader remembers (settings, library folders, collections, reading positions, statistics,
/// bookmarks, highlights, corrected book details and the user's own fonts and covers) as one zip file, and puts such a file back. Books themselves, the cover
/// cache, Piper and the browser profile are not included: the first are the user's files and the rest can be
/// rebuilt or downloaded again.
/// </summary>
public static class Backup {
    public const string Extension = ".omnibackup";

    private const string Manifest = "manifest.json";
    private const string Settings = "settings.json";
    private const string Annotations = "annotations.json";
    private const string Fonts = "Fonts/";
    private const string Covers = "CustomCovers/";

    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Writes a backup. The caller saves the current settings and annotations first.</summary>
    public static void Create(string file) {
        string folder = SettingsStore.DataFolder;
        using FileStream output = File.Create(file);
        using ZipArchive zip = new(output, ZipArchiveMode.Create);
        JsonObject manifest = new() {
            ["app"] = "OmniRead",
            ["format"] = 1,
            ["created"] = DateTime.Now.ToString("s"),
            // Kept so that paths under the user's own folder can be carried over to a different user name.
            ["profile"] = Profile
        };
        WriteText(zip, Manifest, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        foreach (string name in (string[])[Settings, Annotations]) {
            string source = Path.Combine(folder, name);
            if (File.Exists(source)) {
                zip.CreateEntryFromFile(source, name);
            }
        }
        foreach ((string prefix, string source) in Folders()) {
            if (Directory.Exists(source)) {
                foreach (string item in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
                    zip.CreateEntryFromFile(item, prefix + Path.GetRelativePath(source, item).Replace('\\', '/'));
                }
            }
        }
    }

    /// <summary>The folders kept whole in a backup: the reader's own fonts and the covers chosen by hand.</summary>
    private static (string Prefix, string Folder)[] Folders() {
        return [(Fonts, FontLibrary.UserFolder), (Covers, Library.CustomCoverFolder)];
    }

    /// <summary>
    /// Replaces the saved settings, annotations and fonts with those in a backup. Nothing is changed unless the
    /// whole backup can be read; the files it replaces are kept beside the new ones with ".bak" added. The app
    /// must be restarted afterwards, and must not save its own state over the result in the meantime.
    /// </summary>
    public static void Restore(string file) {
        using ZipArchive zip = ZipFile.OpenRead(file);
        JsonNode? manifest = ReadJson(zip, Manifest);
        if (manifest?["app"]?.GetValue<string>() != "OmniRead") {
            throw new InvalidDataException(Localizer.Get("Backup_NotBackup"));
        }
        string? oldProfile = manifest["profile"]?.GetValue<string>();
        JsonNode? settings = ReadJson(zip, Settings);
        JsonNode? annotations = ReadJson(zip, Annotations);
        if (settings is not JsonObject) {
            throw new InvalidDataException(Localizer.Get("Backup_NotBackup"));
        }

        // Books are found by their full path, so paths under the old user folder are moved to this one.
        if (settings["LibraryFolders"] is JsonArray folders) {
            for (int i = 0; i < folders.Count; i++) {
                folders[i] = Rebase(folders[i]?.GetValue<string>(), oldProfile);
            }
        }
        foreach (JsonNode? recent in (settings["Recent"] as JsonArray) ?? []) {
            if (recent is JsonObject book) {
                book["Path"] = Rebase(book["Path"]?.GetValue<string>(), oldProfile);
            }
        }
        foreach (JsonNode? collection in (settings["Collections"] as JsonArray) ?? []) {
            if (collection?["Paths"] is JsonArray paths) {
                for (int i = 0; i < paths.Count; i++) {
                    paths[i] = Rebase(paths[i]?.GetValue<string>(), oldProfile);
                }
            }
        }
        if (settings["ShelfOrder"] is JsonArray shelves) {
            string folderKey = Library.FolderKey("");
            for (int i = 0; i < shelves.Count; i++) {
                // Folder shelves are remembered by their path; collections by their name, which does not move.
                if ((shelves[i]?.GetValue<string>() is string key) && key.StartsWith(folderKey, StringComparison.Ordinal)) {
                    shelves[i] = Library.FolderKey(Rebase(key[folderKey.Length..], oldProfile) ?? "");
                }
            }
        }
        if (settings["Overrides"] is JsonObject overrides) {
            JsonObject moved = [];
            foreach ((string path, JsonNode? details) in overrides) {
                moved[Rebase(path, oldProfile) ?? path] = details?.DeepClone();
            }
            settings["Overrides"] = moved;
        }
        JsonObject? rebased = null;
        if (annotations is JsonObject books) {
            rebased = [];
            foreach ((string path, JsonNode? marks) in books) {
                rebased[Rebase(path, oldProfile) ?? path] = marks?.DeepClone();
            }
        }

        string folder = SettingsStore.DataFolder;
        Directory.CreateDirectory(folder);
        JsonSerializerOptions indented = new() { WriteIndented = true };
        Replace(Path.Combine(folder, Settings), settings.ToJsonString(indented));
        if (rebased is not null) {
            Replace(Path.Combine(folder, Annotations), rebased.ToJsonString(indented));
        }
        foreach ((string prefix, string source) in Folders()) {
            string root = Path.GetFullPath(source) + Path.DirectorySeparatorChar;
            foreach (ZipArchiveEntry entry in zip.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && (entry.Name.Length > 0))) {
                string target = Path.GetFullPath(Path.Combine(root, entry.FullName[prefix.Length..]));
                // An entry must not be able to write outside its folder.
                if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
            }
        }
    }

    /// <summary>Moves a path that lies under the backup's user folder to the same place under this user's folder.</summary>
    private static string? Rebase(string? path, string? oldProfile) {
        if ((path is null) || string.IsNullOrEmpty(oldProfile) || string.Equals(oldProfile, Profile, StringComparison.OrdinalIgnoreCase)) {
            return path;
        }
        string prefix = oldProfile.TrimEnd('\\') + "\\";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? Path.Combine(Profile, path[prefix.Length..]) : path;
    }

    private static void Replace(string file, string content) {
        if (File.Exists(file)) {
            File.Copy(file, file + ".bak", true);
        }
        File.WriteAllText(file, content, new UTF8Encoding(false));
    }

    private static JsonNode? ReadJson(ZipArchive zip, string name) {
        ZipArchiveEntry? entry = zip.GetEntry(name);
        if (entry is null) {
            return null;
        }
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        try {
            return JsonNode.Parse(reader.ReadToEnd());
        }
        catch (JsonException) {
            throw new InvalidDataException(Localizer.Get("Backup_NotBackup"));
        }
    }

    private static void WriteText(ZipArchive zip, string name, string content) {
        using StreamWriter writer = new(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}