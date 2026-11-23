using System.IO;
using System.IO.Compression;
using System.Xml.Linq;

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

using OmniRead.Localization;
using OmniRead.Models;

namespace OmniRead.Formats;

internal static class EpubLoader {
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    private sealed record ManifestItem(string Path, string MediaType, string Properties);

    public static LoadedContent Load(string path) {
        using ZipArchive zip = ZipFile.OpenRead(path);
        XDocument container = ReadXml(zip, "META-INF/container.xml")
            ?? throw new InvalidDataException(Localizer.Get("Epub_NoContainer"));
        string opfPath = container.Descendants(Container + "rootfile").FirstOrDefault()?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException(Localizer.Get("Epub_NoPackage"));
        XDocument opf = ReadXml(zip, opfPath) ?? throw new InvalidDataException(Localizer.Get("Epub_PackageMissing"));
        XNamespace ns = opf.Root!.Name.Namespace;
        string baseDirectory = FileHelpers.ZipDirectory(opfPath);

        LoadedContent content = new() {
            IsNativeBook = true,
            Title = opf.Descendants(DublinCore + "title").FirstOrDefault()?.Value.Trim(),
            Author = opf.Descendants(DublinCore + "creator").FirstOrDefault()?.Value.Trim()
        };

        Dictionary<string, ManifestItem> manifest = [];
        foreach (XElement item in opf.Descendants(ns + "item")) {
            string? id = item.Attribute("id")?.Value;
            string? href = item.Attribute("href")?.Value;
            if ((id is null) || (href is null)) {
                continue;
            }
            manifest[id] = new ManifestItem(
                FileHelpers.ResolveZipPath(baseDirectory, href),
                item.Attribute("media-type")?.Value ?? "",
                item.Attribute("properties")?.Value ?? "");
        }

        Dictionary<string, string> titles = ReadTocTitles(zip, opf, ns, manifest);
        HtmlParser parser = new();
        Dictionary<string, string?> stylesheets = new(StringComparer.OrdinalIgnoreCase);
        Chapter? previous = null;
        int section = 0;
        foreach (XElement itemRef in opf.Descendants(ns + "itemref")) {
            string? idRef = itemRef.Attribute("idref")?.Value;
            if ((idRef is null) || !manifest.TryGetValue(idRef, out ManifestItem? item)) {
                continue;
            }
            ZipArchiveEntry? entry = FileHelpers.FindEntry(zip, item.Path);
            if (entry is null) {
                continue;
            }
            IHtmlDocument document;
            using (Stream stream = entry.Open()) {
                document = parser.ParseDocument(stream);
            }
            string documentDirectory = FileHelpers.ZipDirectory(item.Path);
            (IDocument styled, StyleResolver styles) = StyledHtml.Open(
                document, href => ReadStylesheet(zip, FileHelpers.ResolveZipPath(documentDirectory, href), stylesheets, 0));
            HtmlConverter converter = new(
                styles,
                source => source.Contains("://") || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : FileHelpers.ZipImage(path, FileHelpers.ResolveZipPath(documentDirectory, source)),
                item.Path,
                href => ResolveLink(item.Path, documentDirectory, href));
            List<ContentBlock> blocks = converter.Convert(styled.Body ?? (INode)styled);
            if (blocks.Count == 0) {
                continue;
            }
            // The file itself is a link target too: contents pages and cross-references often point at whole files.
            blocks[0] = blocks[0] with { Anchors = [item.Path, .. blocks[0].Anchors ?? []] };
            section++;
            bool listed = titles.TryGetValue(item.Path, out string? title);
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

    /// <summary>Reads a stylesheet from the archive, with its @import rules resolved. Cached per book.</summary>
    private static string? ReadStylesheet(ZipArchive zip, string path, Dictionary<string, string?> cache, int depth) {
        if (cache.TryGetValue(path, out string? cached)) {
            return cached;
        }
        cache[path] = null;
        ZipArchiveEntry? entry = FileHelpers.FindEntry(zip, path);
        if ((entry is null) || (depth > 3)) {
            return null;
        }
        string css;
        using (StreamReader reader = new(entry.Open())) {
            css = reader.ReadToEnd();
        }
        string directory = FileHelpers.ZipDirectory(path);
        css = StyledHtml.InlineImports(
            css, href => ReadStylesheet(zip, FileHelpers.ResolveZipPath(directory, href), cache, depth + 1));
        cache[path] = css;
        return css;
    }

    private static Dictionary<string, string> ReadTocTitles(
        ZipArchive zip, XDocument opf, XNamespace ns, Dictionary<string, ManifestItem> manifest) {
        Dictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);

        ManifestItem? nav = manifest.Values.FirstOrDefault(
            static item => item.Properties.Split(' ').Contains("nav"));
        if ((nav is not null) && (FileHelpers.FindEntry(zip, nav.Path) is ZipArchiveEntry navEntry)) {
            using Stream stream = navEntry.Open();
            IHtmlDocument document = new HtmlParser().ParseDocument(stream);
            string directory = FileHelpers.ZipDirectory(nav.Path);
            foreach (IElement link in document.QuerySelectorAll("nav a[href]")) {
                string text = link.TextContent.Trim();
                if (text.Length > 0) {
                    titles.TryAdd(FileHelpers.ResolveZipPath(directory, link.GetAttribute("href")!), text);
                }
            }
        }
        if (titles.Count > 0) {
            return titles;
        }

        string? ncxId = opf.Descendants(ns + "spine").FirstOrDefault()?.Attribute("toc")?.Value;
        ManifestItem? ncx = ((ncxId is not null) && manifest.TryGetValue(ncxId, out ManifestItem? byId))
            ? byId
            : manifest.Values.FirstOrDefault(static item => item.MediaType == "application/x-dtbncx+xml");
        if ((ncx is not null) && (ReadXml(zip, ncx.Path) is XDocument ncxDocument)) {
            string directory = FileHelpers.ZipDirectory(ncx.Path);
            foreach (XElement point in ncxDocument.Descendants(Ncx + "navPoint")) {
                string? text = point.Element(Ncx + "navLabel")?.Element(Ncx + "text")?.Value.Trim();
                string? source = point.Element(Ncx + "content")?.Attribute("src")?.Value;
                if (!string.IsNullOrEmpty(text) && (source is not null)) {
                    titles.TryAdd(FileHelpers.ResolveZipPath(directory, source), text);
                }
            }
        }
        return titles;
    }

    private static XDocument? ReadXml(ZipArchive zip, string name) {
        ZipArchiveEntry? entry = FileHelpers.FindEntry(zip, name);
        if (entry is null) {
            return null;
        }
        using Stream stream = entry.Open();
        return FileHelpers.LoadXml(stream);
    }
}