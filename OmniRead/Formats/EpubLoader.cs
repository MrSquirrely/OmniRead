using System.IO;
using System.IO.Compression;

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

using OmniRead.Localization;
using OmniRead.Models;

using VersOne.Epub;
using VersOne.Epub.Options;

namespace OmniRead.Formats;

/// <summary>
/// Reads EPUB 2 and 3 books. The package itself (metadata, reading order, contents, stylesheets) is read by
/// VersOne.Epub; each text file is then flattened into reader blocks like any other HTML.
/// </summary>
internal static class EpubLoader {
    /// <summary>Opens a book leniently: real-world EPUBs break the standard in many small ways.</summary>
    public static EpubBookRef Open(string path) {
        EpubReaderOptions options = new(EpubReaderOptionsPreset.IGNORE_ALL_ERRORS);
        EpubBookRef? book = HasBackslashNames(path)
            ? EpubReader.OpenBook(Repack(path), options)
            : EpubReader.OpenBook(path, options);
        return book ?? throw new InvalidDataException(Localizer.Get("Epub_NoText"));
    }

    /// <summary>Some Windows tools write archive paths with backslashes, which no file in the book can then be found under.</summary>
    private static bool HasBackslashNames(string path) {
        using ZipArchive zip = ZipFile.OpenRead(path);
        return zip.Entries.Any(static entry => entry.FullName.Contains('\\'));
    }

    /// <summary>Copies the archive into memory with its paths corrected to forward slashes.</summary>
    private static MemoryStream Repack(string path) {
        MemoryStream repacked = new();
        using (ZipArchive source = ZipFile.OpenRead(path))
        using (ZipArchive target = new(repacked, ZipArchiveMode.Create, true)) {
            foreach (ZipArchiveEntry entry in source.Entries) {
                using Stream input = entry.Open();
                using Stream output = target.CreateEntry(entry.FullName.Replace('\\', '/'), CompressionLevel.NoCompression).Open();
                input.CopyTo(output);
            }
        }
        repacked.Position = 0;
        return repacked;
    }

    public static LoadedContent Load(string path) {
        using EpubBookRef book = Open(path);
        LoadedContent content = new() {
            IsNativeBook = true,
            Title = book.Title?.Trim(),
            Author = book.AuthorList?.FirstOrDefault()?.Trim() ?? book.Author?.Trim(),
            Language = book.Schema?.Package?.Metadata?.Languages?.FirstOrDefault()?.Language
        };

        Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
        AddTitles(titles, book.GetNavigation());
        HtmlParser parser = new();
        Dictionary<string, string?> stylesheets = new(StringComparer.OrdinalIgnoreCase);
        Chapter? previous = null;
        int section = 0;
        foreach (EpubLocalTextContentFileRef file in book.GetReadingOrder() ?? []) {
            string filePath = file.FilePath;
            IHtmlDocument document;
            try {
                // Parsed from bytes so that the file's own encoding declaration is honoured.
                using MemoryStream stream = new(file.ReadContentAsBytes());
                document = parser.ParseDocument(stream);
            }
            catch (Exception ex) when (ex is EpubReaderException or IOException or InvalidDataException) {
                // A file the package lists but the archive lacks is skipped; the rest of the book still opens.
                continue;
            }
            string documentDirectory = FileHelpers.ZipDirectory(filePath);
            (IDocument styled, StyleResolver styles) = StyledHtml.Open(
                document, href => ReadStylesheet(book, FileHelpers.ResolveZipPath(documentDirectory, href), stylesheets, 0));
            HtmlConverter converter = new(
                styles,
                source => source.Contains("://") || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : FileHelpers.ZipImage(path, FileHelpers.ResolveZipPath(documentDirectory, source)),
                filePath,
                href => ResolveLink(filePath, documentDirectory, href));
            List<ContentBlock> blocks = converter.Convert(styled.Body ?? (INode)styled);
            if (blocks.Count == 0) {
                continue;
            }
            // The file itself is a link target too: contents pages and cross-references often point at whole files.
            blocks[0] = blocks[0] with { Anchors = [filePath, .. blocks[0].Anchors ?? []] };
            section++;
            bool listed = titles.TryGetValue(filePath, out string? title);
            if (!listed && (titles.Count > 0) && (previous is not null)) {
                // Publishers often split one chapter over several files; only the first is in the contents.
                previous.Blocks.AddRange(blocks);
                continue;
            }
            title ??= blocks.OfType<HeadingBlock>().FirstOrDefault()?.Text.Replace('\n', ' ');
            title ??= blocks.All(static block => block is ImageBlock) && (section == 1) ? Localizer.Get("Epub_Cover") : Localizer.Format("Section_Numbered", section);
            previous = new Chapter(title);
            previous.Blocks.AddRange(blocks);
            content.Chapters.Add(previous);
        }
        if (content.Chapters.Count == 0) {
            throw new InvalidDataException(Localizer.Get("Epub_NoText"));
        }
        // Judged across the whole book, so a short chapter is measured against the book's ordinary paragraph.
        CssNormalizer.Normalize(content.Chapters.Select(static chapter => chapter.Blocks));
        return content;
    }

    /// <summary>Collects the contents entries, nested ones included; the first title given for a file wins.</summary>
    private static void AddTitles(Dictionary<string, string> titles, List<EpubNavigationItemRef>? items) {
        foreach (EpubNavigationItemRef item in items ?? []) {
            string? file = item.Link?.ContentFilePath;
            string title = item.Title?.Trim() ?? "";
            if ((file is not null) && (title.Length > 0)) {
                titles.TryAdd(file, title);
            }
            AddTitles(titles, item.NestedItems);
        }
    }

    /// <summary>Turns a link between files of the book into "book:path#id", matching the anchors the converter records.</summary>
    private static string? ResolveLink(string documentPath, string documentDirectory, string href) {
        if (href.Contains("://") || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        int hash = href.IndexOf('#');
        string file = (hash >= 0) ? href[..hash] : href;
        string fragment = (hash >= 0) ? href[hash..] : "";
        string path = (file.Length == 0) ? documentPath : FileHelpers.ResolveZipPath(documentDirectory, file);
        return $"book:{path}{((fragment.Length > 1) ? fragment : "")}";
    }

    /// <summary>Reads a stylesheet from the book, with its @import rules resolved. Cached per book.</summary>
    private static string? ReadStylesheet(EpubBookRef book, string path, Dictionary<string, string?> cache, int depth) {
        if (cache.TryGetValue(path, out string? cached)) {
            return cached;
        }
        cache[path] = null;
        if (depth > 3) {
            return null;
        }
        // Stylesheets declared with an unexpected media type are still in the list of all files.
        EpubLocalContentFileRef? file = book.Content.Css.TryGetLocalFileRefByFilePath(path, out EpubLocalTextContentFileRef? sheet)
            ? sheet
            : book.Content.AllFiles.TryGetLocalFileRefByFilePath(path, out EpubLocalContentFileRef? any) ? any : null;
        if (file is null) {
            return null;
        }
        string css;
        try {
            css = file.ReadContentAsText();
        }
        catch (Exception ex) when (ex is EpubReaderException or IOException or InvalidDataException) {
            return null;
        }
        string directory = FileHelpers.ZipDirectory(path);
        css = StyledHtml.InlineImports(
            css, href => ReadStylesheet(book, FileHelpers.ResolveZipPath(directory, href), cache, depth + 1));
        cache[path] = css;
        return css;
    }
}