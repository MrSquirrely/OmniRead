using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

using OmniRead.Formats;
using OmniRead.Localization;

using VersOne.Epub;

using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OmniRead.Services;

/// <summary>How far a book has been read, for filtering the shelves.</summary>
public enum ReadState {
    Any,
    Unread,
    Reading,
    Finished
}

/// <summary>One book on the library shelf.</summary>
public sealed class LibraryItem {
    public required string Path { get; init; }

    public required string Title { get; init; }

    public string Author { get; init; } = "";

    public ImageSource? Cover { get; init; }

    public DateTime? LastOpened { get; init; }

    public double Progress { get; init; }

    /// <summary>True once the book has been read to its last page.</summary>
    public bool Finished { get; init; }

    public ReadState State => Finished ? ReadState.Finished : (LastOpened is null) ? ReadState.Unread : ReadState.Reading;

    /// <summary>Label of the shelf the book is on: the name of its library folder.</summary>
    public string Shelf { get; init; } = "";

    /// <summary>Position of the shelf, in the order the folders were added.</summary>
    public int ShelfIndex { get; init; }

    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    public string Subtitle => (Author.Length > 0) ? Author
        : (LastOpened is not null) ? Localizer.Format("Format_Percent", Progress)
        : "";
}

/// <summary>
/// Finds the books in the library folders and reads their title, author and cover. What it learns is cached beside the
/// settings (with covers as small JPEG files), so only new or changed books are opened on later scans.
/// </summary>
public static class Library {
    private const int MaxBooks = 3000;
    private const int CoverWidth = 200;

    private sealed class CacheEntry {
        public long Stamp { get; set; }

        public string Title { get; set; } = "";

        public string Author { get; set; } = "";

        public bool HasCover { get; set; }
    }

    private static string CoverFolder => System.IO.Path.Combine(SettingsStore.DataFolder, "Covers");

    private static string CachePath => System.IO.Path.Combine(SettingsStore.DataFolder, "library.json");

    /// <summary>
    /// Lists the books in each of <paramref name="folders"/> (including subfolders) as one shelf per folder, or
    /// the recently opened books when no folder is set. Safe to call from a background thread; the covers it
    /// returns are frozen.
    /// </summary>
    public static async Task<List<LibraryItem>> ScanAsync(IReadOnlyList<string> folders, IReadOnlyList<RecentBook> recent) {
        List<(string Path, string Shelf, int ShelfIndex)> found = [];
        List<string> existing = [.. folders.Where(Directory.Exists)];
        if (existing.Count > 0) {
            IReadOnlyList<string> labels = ShelfLabels(existing);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
            // Longest paths first: a book in a folder that sits inside another listed folder goes on the inner one's shelf.
            foreach (int index in Enumerable.Range(0, existing.Count).OrderByDescending(index => existing[index].Length)) {
                try {
                    foreach (string path in Directory.EnumerateFiles(existing[index], "*", options).Where(BookLoader.IsSupported)) {
                        if (found.Count >= MaxBooks) {
                            break;
                        }
                        if (seen.Add(path)) {
                            found.Add((path, labels[index], index));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // An unreadable folder simply shows no shelf.
                }
            }
        }
        else {
            string shelf = Localizer.Get("Library_Shelf_Recent");
            found.AddRange(recent.Select(static book => book.Path).Where(File.Exists).Select(path => (path, shelf, 0)));
        }

        Dictionary<string, CacheEntry> cache = LoadCache();
        Dictionary<string, RecentBook> history = recent
            .GroupBy(static book => book.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<LibraryItem> items = [];
        bool cacheChanged = false;
        foreach ((string path, string shelfName, int shelfIndex) in found) {
            history.TryGetValue(path, out RecentBook? read);
            FileInfo file = new(path);
            long stamp = file.LastWriteTimeUtc.Ticks ^ file.Length;
            string coverFile = System.IO.Path.Combine(CoverFolder, Hash(path) + ".jpg");
            if (!cache.TryGetValue(path, out CacheEntry? entry) || (entry.Stamp != stamp)) {
                entry = await ReadMetadataAsync(path, stamp, coverFile);
                cache[path] = entry;
                cacheChanged = true;
            }
            items.Add(new LibraryItem {
                Path = path,
                // Formats without metadata get the title the reader worked out when the book was last opened.
                Title = (entry.Title.Length > 0) ? entry.Title
                    : !string.IsNullOrEmpty(read?.Title) ? read.Title
                    : System.IO.Path.GetFileNameWithoutExtension(path),
                Author = entry.Author,
                Cover = entry.HasCover ? LoadImage(coverFile) : null,
                LastOpened = read?.LastOpened,
                Progress = read?.Progress ?? 0,
                Finished = read?.Finished ?? false,
                Shelf = shelfName,
                ShelfIndex = shelfIndex
            });
        }
        if (cacheChanged) {
            SaveCache(cache);
        }
        return items;
    }

    /// <summary>The folder's own name, which labels its shelf.</summary>
    public static string FolderName(string folder) {
        string name = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        // A drive root has no name of its own.
        return (name.Length > 0) ? name : folder;
    }

    /// <summary>Shelf labels for the folders; two folders with the same name are told apart by their parent folder.</summary>
    private static IReadOnlyList<string> ShelfLabels(IReadOnlyList<string> folders) {
        List<string> names = [.. folders.Select(FolderName)];
        return [.. folders.Select((folder, index) => {
            bool shared = names.Count(name => string.Equals(name, names[index], StringComparison.OrdinalIgnoreCase)) > 1;
            string? parent = System.IO.Path.GetDirectoryName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            return (shared && !string.IsNullOrEmpty(parent)) ? $"{names[index]} ({FolderName(parent)})" : names[index];
        })];
    }

    /// <summary>Orders books shelf by shelf, and within each shelf by the chosen order.</summary>
    public static IEnumerable<LibraryItem> Sort(IEnumerable<LibraryItem> items, LibrarySort order) {
        StringComparer text = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<LibraryItem> shelves = items.OrderBy(static item => item.ShelfIndex);
        return order switch {
            LibrarySort.Title => shelves.ThenBy(static item => item.Title, text),
            LibrarySort.Author => shelves
                .ThenBy(static item => item.Author.Length == 0)
                .ThenBy(static item => item.Author, text)
                .ThenBy(static item => item.Title, text),
            _ => shelves
                .ThenByDescending(static item => item.LastOpened ?? DateTime.MinValue)
                .ThenBy(static item => item.Title, text)
        };
    }

    private static async Task<CacheEntry> ReadMetadataAsync(string path, long stamp, string coverFile) {
        CacheEntry entry = new() { Stamp = stamp };
        byte[]? cover = null;
        try {
            switch (System.IO.Path.GetExtension(path).ToLowerInvariant()) {
                case ".epub":
                    cover = ReadEpub(path, entry);
                    break;
                case ".fb2":
                    cover = ReadFb2(path, entry);
                    break;
                case ".pdf":
                    cover = await RenderPdfCoverAsync(path);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // A book that cannot be inspected still appears, under its file name and without a cover.
        }
        entry.HasCover = (cover is not null) && SaveThumbnail(cover, coverFile);
        return entry;
    }

    private static byte[]? ReadEpub(string path, CacheEntry entry) {
        using EpubBookRef book = EpubLoader.Open(path);
        entry.Title = book.Title?.Trim() ?? "";
        entry.Author = book.AuthorList?.FirstOrDefault()?.Trim() ?? book.Author?.Trim() ?? "";
        byte[]? cover = book.ReadCover();
        if (cover is not null) {
            return cover;
        }
        // Books that do not declare a cover usually still name the image after it.
        EpubLocalByteContentFileRef? image = book.Content.Images.Local.FirstOrDefault(
            static candidate => candidate.FilePath.Contains("cover", StringComparison.OrdinalIgnoreCase));
        return image?.ReadContentAsBytes();
    }

    private static byte[]? ReadFb2(string path, CacheEntry entry) {
        XDocument document;
        using (FileStream stream = File.OpenRead(path)) {
            document = FileHelpers.LoadXml(stream);
        }
        XElement? info = document.Descendants().FirstOrDefault(static element => element.Name.LocalName == "title-info");
        if (info is null) {
            return null;
        }
        entry.Title = Child(info, "book-title")?.Value.Trim() ?? "";
        XElement? author = Child(info, "author");
        if (author is not null) {
            entry.Author = string.Join(' ', new[] { "first-name", "middle-name", "last-name" }
                .Select(name => Child(author, name)?.Value.Trim())
                .Where(static part => !string.IsNullOrEmpty(part)));
        }
        string? reference = Child(info, "coverpage")?.Elements().FirstOrDefault()?.Attributes()
            .FirstOrDefault(static attribute => attribute.Name.LocalName == "href")?.Value.TrimStart('#');
        XElement? binary = (reference is null) ? null : document.Descendants()
            .FirstOrDefault(element => (element.Name.LocalName == "binary") && (element.Attribute("id")?.Value == reference));
        try {
            return (binary is null) ? null : Convert.FromBase64String(binary.Value);
        }
        catch (FormatException) {
            return null;
        }
    }

    private static XElement? Child(XElement parent, string name) {
        return parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);
    }

    private static async Task<byte[]?> RenderPdfCoverAsync(string path) {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);
        if (pdf.PageCount == 0) {
            return null;
        }
        using PdfPage page = pdf.GetPage(0);
        using InMemoryRandomAccessStream stream = new();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = CoverWidth * 2 });
        using Stream managed = stream.AsStreamForRead();
        using MemoryStream buffer = new();
        await managed.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static bool SaveThumbnail(byte[] image, string coverFile) {
        try {
            BitmapImage source = new();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelWidth = CoverWidth;
            source.StreamSource = new MemoryStream(image);
            source.EndInit();
            source.Freeze();
            Directory.CreateDirectory(CoverFolder);
            JpegBitmapEncoder encoder = new() { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using FileStream output = File.Create(coverFile);
            encoder.Save(output);
            return true;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException) {
            return false;
        }
    }

    private static ImageSource? LoadImage(string file) {
        try {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException or UriFormatException) {
            return null;
        }
    }

    private static string Hash(string path) {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..20];
    }

    private static Dictionary<string, CacheEntry> LoadCache() {
        Dictionary<string, CacheEntry>? loaded = null;
        try {
            if (File.Exists(CachePath)) {
                loaded = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // A damaged cache is rebuilt from the books themselves.
        }
        return new Dictionary<string, CacheEntry>(loaded ?? [], StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCache(Dictionary<string, CacheEntry> cache) {
        try {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Without the cache the next scan only takes longer.
        }
    }
}