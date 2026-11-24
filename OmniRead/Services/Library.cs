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

/// <summary>
/// A shelf heading: a folder of books, or a collection the reader made. The key names the shelf in the saved order
/// of the shelves, and the index is its place before the reader rearranges anything.
/// </summary>
public sealed record ShelfGroup(string Key, string Name, int Index, bool IsCollection);

/// <summary>One book on the library shelf.</summary>
public sealed class LibraryItem {
    public required string Path { get; init; }

    public required string Title { get; init; }

    public string Author { get; init; } = "";

    public ImageSource? Cover { get; init; }

    /// <summary>The book's place in its series, when the reader has given it one.</summary>
    public double? SeriesNumber { get; init; }

    public DateTime? LastOpened { get; init; }

    public double Progress { get; init; }

    /// <summary>True once the book has been read to its last page.</summary>
    public bool Finished { get; init; }

    public ReadState State => Finished ? ReadState.Finished : (LastOpened is null) ? ReadState.Unread : ReadState.Reading;

    /// <summary>Label of the shelf the book is on: its library folder, followed by the subfolders down to the book's own.</summary>
    public string Shelf { get; init; } = "";

    /// <summary>What the shelf is remembered by when the reader rearranges the shelves.</summary>
    public string ShelfKey { get; init; } = "";

    /// <summary>
    /// Position of the shelf before the reader rearranges anything: library folders in the order they were added,
    /// each followed by its subfolders. Collections come first, with negative positions.
    /// </summary>
    public int ShelfIndex { get; init; }

    /// <summary>True when this entry stands on a collection's shelf rather than a folder's.</summary>
    public bool IsCollection { get; init; }

    /// <summary>What the shelves are grouped by; a collection and a folder of the same name stay apart.</summary>
    public ShelfGroup Group => field ??= new ShelfGroup(ShelfKey, Shelf, ShelfIndex, IsCollection);

    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    /// <summary>The line under the title: the book's number in its series, then its author (or how far it has been read).</summary>
    public string Subtitle {
        get {
            string detail = (Author.Length > 0) ? Author
                : (LastOpened is not null) ? Localizer.Format("Format_Percent", Progress)
                : "";
            if (SeriesNumber is not double number) {
                return detail;
            }
            string series = Localizer.Format("Library_SeriesNumber", number);
            return (detail.Length > 0) ? $"{series} · {detail}" : series;
        }
    }
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

    /// <summary>Covers the reader chose by hand. Unlike the cache in Covers, these cannot be rebuilt from the books.</summary>
    public static string CustomCoverFolder => System.IO.Path.Combine(SettingsStore.DataFolder, "CustomCovers");

    /// <summary>Stores a picture as a custom cover and returns its file name, or null when it cannot be read.</summary>
    public static string? SaveCustomCover(string imageFile) {
        try {
            string name = Guid.NewGuid().ToString("N") + ".jpg";
            Directory.CreateDirectory(CustomCoverFolder);
            return SaveThumbnail(File.ReadAllBytes(imageFile), System.IO.Path.Combine(CustomCoverFolder, name)) ? name : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public static ImageSource? LoadCustomCover(string? name) {
        return string.IsNullOrEmpty(name) ? null : LoadImage(System.IO.Path.Combine(CustomCoverFolder, System.IO.Path.GetFileName(name)));
    }

    private static string CachePath => System.IO.Path.Combine(SettingsStore.DataFolder, "library.json");

    /// <summary>
    /// Lists the books in each of <paramref name="folders"/>, with one shelf for the folder and one for each
    /// subfolder that holds books, or the recently opened books when no folder is set. Safe to call from a
    /// background thread; the covers it returns are frozen.
    /// </summary>
    public static async Task<List<LibraryItem>> ScanAsync(
        IReadOnlyList<string> folders,
        IReadOnlyList<RecentBook> recent,
        IReadOnlyList<BookCollection> collections,
        IReadOnlyDictionary<string, BookOverride> overrides) {
        List<(string Path, string Key, string Shelf, int ShelfIndex, bool Collection)> found = [];
        // Collections stand before the folder shelves, in the order they were made.
        for (int i = 0; i < collections.Count; i++) {
            string key = CollectionKey(collections[i].Name);
            found.AddRange(collections[i].Paths.Where(File.Exists).Select(path => (path, key, collections[i].Name, i - collections.Count, true)));
        }
        List<string> existing = [.. folders.Where(Directory.Exists)];
        if (existing.Count > 0) {
            IReadOnlyList<string> labels = ShelfLabels(existing);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<(string Path, string Directory)> books = [];
            // The library folder each folder of books was found under.
            Dictionary<string, int> roots = new(StringComparer.OrdinalIgnoreCase);
            EnumerationOptions options = new() { RecurseSubdirectories = true, IgnoreInaccessible = true };
            // Longest paths first: a book in a folder that sits inside another listed folder goes on the inner one's shelf.
            foreach (int index in Enumerable.Range(0, existing.Count).OrderByDescending(index => existing[index].Length)) {
                try {
                    foreach (string path in Directory.EnumerateFiles(existing[index], "*", options).Where(BookLoader.IsSupported)) {
                        if ((found.Count + books.Count) >= MaxBooks) {
                            break;
                        }
                        if (seen.Add(path)) {
                            string directory = System.IO.Path.GetDirectoryName(path) ?? existing[index];
                            books.Add((path, directory));
                            roots.TryAdd(directory, index);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // An unreadable folder simply shows no shelf.
                }
            }
            // Every folder that holds books is a shelf of its own: the library folder first, then its subfolders by name.
            Dictionary<string, (string Label, int Index)> shelves = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string directory, int root, string relative) in roots
                .Select(pair => (Directory: pair.Key, Root: pair.Value, Relative: Relative(existing[pair.Value], pair.Key)))
                .OrderBy(static shelf => shelf.Root)
                .ThenBy(static shelf => shelf.Relative, StringComparer.OrdinalIgnoreCase)) {
                string[] parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                shelves[directory] = (string.Join(" - ", [labels[root], .. parts]), shelves.Count);
            }
            found.AddRange(books.Select(book => (book.Path, FolderKey(book.Directory), shelves[book.Directory].Label, shelves[book.Directory].Index, false)));
        }
        else {
            string shelf = Localizer.Get("Library_Shelf_Recent");
            found.AddRange(recent.Select(static book => book.Path).Where(File.Exists).Select(path => (path, "R|", shelf, 0, false)));
        }

        Dictionary<string, CacheEntry> cache = LoadCache();
        Dictionary<string, RecentBook> history = recent
            .GroupBy(static book => book.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        List<LibraryItem> items = [];
        bool cacheChanged = false;
        foreach ((string path, string shelfKey, string shelfName, int shelfIndex, bool collection) in found) {
            overrides.TryGetValue(path, out BookOverride? custom);
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
                Title = !string.IsNullOrWhiteSpace(custom?.Title) ? custom.Title
                    : (entry.Title.Length > 0) ? entry.Title
                    : !string.IsNullOrEmpty(read?.Title) ? read.Title
                    : System.IO.Path.GetFileNameWithoutExtension(path),
                Author = !string.IsNullOrWhiteSpace(custom?.Author) ? custom.Author : entry.Author,
                Cover = LoadCustomCover(custom?.Cover) ?? (entry.HasCover ? LoadImage(coverFile) : null),
                SeriesNumber = custom?.SeriesNumber,
                LastOpened = read?.LastOpened,
                Progress = read?.Progress ?? 0,
                Finished = read?.Finished ?? false,
                Shelf = shelfName,
                ShelfKey = shelfKey,
                ShelfIndex = shelfIndex,
                IsCollection = collection
            });
        }
        if (cacheChanged) {
            SaveCache(cache);
        }
        return items;
    }

    /// <summary>The key of a collection's shelf in the saved order of the shelves.</summary>
    public static string CollectionKey(string name) {
        return "C|" + name;
    }

    /// <summary>The key of a folder's shelf in the saved order of the shelves.</summary>
    public static string FolderKey(string folder) {
        return "F|" + folder;
    }

    /// <summary>The path from a library folder down to one of its subfolders; empty for the library folder itself.</summary>
    private static string Relative(string root, string directory) {
        string relative = System.IO.Path.GetRelativePath(root, directory);
        return (relative == ".") ? "" : relative;
    }

    /// <summary>
    /// The keys of the shelves in the order they are shown: the order the reader arranged them in, with shelves
    /// that order does not know (a new subfolder, say) placed after the shelf they would otherwise follow.
    /// </summary>
    public static List<string> ShelfOrder(IEnumerable<LibraryItem> items, IReadOnlyList<string> saved) {
        StringComparer comparer = StringComparer.OrdinalIgnoreCase;
        List<string> natural = [.. items.Select(static item => item.Group).Distinct()
            .OrderBy(static shelf => shelf.Index).Select(static shelf => shelf.Key).Distinct(comparer)];
        HashSet<string> present = new(natural, comparer);
        List<string> order = [.. saved.Where(present.Contains).Distinct(comparer)];
        HashSet<string> placed = new(order, comparer);
        string? previous = null;
        foreach (string key in natural) {
            if (placed.Add(key)) {
                order.Insert((previous is null) ? 0 : (order.FindIndex(candidate => comparer.Equals(candidate, previous)) + 1), key);
            }
            previous = key;
        }
        return order;
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

    /// <summary>Orders books shelf by shelf (shelves as the reader arranged them), and within each shelf by the chosen order.</summary>
    public static IEnumerable<LibraryItem> Sort(IReadOnlyCollection<LibraryItem> items, LibrarySort order, IReadOnlyList<string> shelfOrder) {
        StringComparer text = StringComparer.CurrentCultureIgnoreCase;
        Dictionary<string, int> rank = new(StringComparer.OrdinalIgnoreCase);
        foreach (string key in ShelfOrder(items, shelfOrder)) {
            rank[key] = rank.Count;
        }
        IOrderedEnumerable<LibraryItem> shelves = items.OrderBy(item => rank[item.ShelfKey]);
        return order switch {
            // A folder's shelf is a series: its numbered books come first, in series order. A collection goes by title alone.
            LibrarySort.Title => shelves
                .ThenBy(static item => item.IsCollection || (item.SeriesNumber is null))
                .ThenBy(static item => item.IsCollection ? 0 : (item.SeriesNumber ?? 0))
                .ThenBy(static item => item.Title, text),
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