using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using OmniRead.Localization;
using OmniRead.Models;

namespace OmniRead.Formats;

internal sealed partial class Fb2Loader {
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";

    private readonly LoadedContent _content = new() { IsNativeBook = true };
    private readonly Dictionary<string, string> _binaries = new(StringComparer.OrdinalIgnoreCase);
    private Chapter? _current;

    /// <summary>Ids met since the last block; they become link targets (for footnotes) on the next block produced.</summary>
    private readonly List<string> _pendingIds = [];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static LoadedContent Load(string path) {
        XDocument document;
        using (FileStream file = File.OpenRead(path)) {
            bool zipped = (file.ReadByte() == 'P') && (file.ReadByte() == 'K');
            file.Position = 0;
            if (zipped) {
                using ZipArchive zip = new(file, ZipArchiveMode.Read);
                ZipArchiveEntry entry = zip.Entries.FirstOrDefault(
                        static candidate => candidate.Name.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(Localizer.Get("Fb2_NoBook"));
                using Stream stream = entry.Open();
                document = FileHelpers.LoadXml(stream);
            }
            else {
                document = FileHelpers.LoadXml(file);
            }
        }
        Fb2Loader loader = new();
        loader.Read(document);
        return loader._content;
    }

    private void Read(XDocument document) {
        XElement root = document.Root ?? throw new InvalidDataException(Localizer.Get("Fb2_Empty"));
        XElement? info = Child(Child(root, "description"), "title-info");
        _content.Title = Child(info, "book-title")?.Value.Trim();
        _content.Language = Child(info, "lang")?.Value.Trim();
        XElement? author = Child(info, "author");
        if (author is not null) {
            string name = string.Join(' ', new[] { "first-name", "middle-name", "last-name" }
                .Select(part => Child(author, part)?.Value.Trim())
                .Where(static part => !string.IsNullOrEmpty(part)));
            _content.Author = (name.Length > 0) ? name : Child(author, "nickname")?.Value.Trim();
        }
        foreach (XElement binary in Children(root, "binary")) {
            string? id = binary.Attribute("id")?.Value;
            if (id is not null) {
                _binaries[id] = binary.Value;
            }
        }
        foreach (XElement body in Children(root, "body")) {
            _current = null;
            bool notes = body.Attribute("name")?.Value is "notes" or "comments";
            if (notes) {
                BeginChapter("Notes");
            }
            foreach (XElement child in body.Elements()) {
                if (child.Name.LocalName == "section") {
                    WalkSection(child, notes ? 3 : 1);
                }
                else {
                    AddBlock(child, 1);
                }
            }
        }
    }

    private void WalkSection(XElement section, int level) {
        NoteId(section);
        string? title = TitleText(Child(section, "title"));
        if ((_current is null) || ((level <= 2) && (title is not null))) {
            BeginChapter(title ?? Localizer.Format("Section_Numbered", _content.Chapters.Count + 1));
        }
        foreach (XElement child in section.Elements()) {
            if (child.Name.LocalName == "section") {
                WalkSection(child, level + 1);
            }
            else {
                AddBlock(child, level);
            }
        }
    }

    private void BeginChapter(string title) {
        _current = new Chapter(title);
        _content.Chapters.Add(_current);
    }

    private void AddBlock(XElement element, int level, ParagraphKind kind = ParagraphKind.Normal) {
        if (_current is null) {
            BeginChapter(_content.Title ?? Localizer.Get("Fb2_FrontMatter"));
        }
        List<ContentBlock> blocks = _current!.Blocks;
        NoteId(element);
        int depth = (kind == ParagraphKind.Quote) ? 1 : 0;
        switch (element.Name.LocalName) {
            case "title":
                string? title = TitleText(element);
                if (title is not null) {
                    Add(blocks, new HeadingBlock(Math.Clamp(level, 1, 6), [new InlineRun(title)]));
                }
                break;
            case "subtitle":
                AddParagraph(blocks, new HeadingBlock(Math.Clamp(level + 1, 2, 6), Inlines(element, InlineStyle.None)));
                break;
            case "p":
                AddParagraph(blocks, new ParagraphBlock(Inlines(element, InlineStyle.None), kind, depth));
                break;
            case "text-author":
                AddParagraph(blocks, new ParagraphBlock(Inlines(element, InlineStyle.Italic), ParagraphKind.Quote, 1));
                break;
            case "epigraph" or "cite" or "annotation" or "poem":
                foreach (XElement child in element.Elements()) {
                    AddBlock(child, level + 1, ParagraphKind.Quote);
                }
                break;
            case "stanza":
                List<InlineRun> verse = [];
                foreach (XElement line in Children(element, "v")) {
                    if (verse.Count > 0) {
                        verse.Add(new InlineRun("\n"));
                    }
                    verse.AddRange(Inlines(line, InlineStyle.None));
                }
                AddParagraph(blocks, new ParagraphBlock(verse, ParagraphKind.Quote, 1));
                break;
            case "image":
                string? reference = element.Attribute(XLink + "href")?.Value ?? element.Attribute("href")?.Value;
                if ((reference is not null) && _binaries.TryGetValue(reference.TrimStart('#'), out string? base64)) {
                    Add(blocks, new ImageBlock(() => Task.Run(() => {
                        try {
                            return (byte[]?)Convert.FromBase64String(base64);
                        }
                        catch (FormatException) {
                            return null;
                        }
                    })));
                }
                break;
            case "table":
                List<IReadOnlyList<IReadOnlyList<InlineRun>>> rows = [];
                foreach (XElement row in Children(element, "tr")) {
                    rows.Add([.. row.Elements().Select(cell => (IReadOnlyList<InlineRun>)Inlines(cell, InlineStyle.None))]);
                }
                if (rows.Count > 0) {
                    Add(blocks, new TableBlock(rows, element.Descendants().Any(static cell => cell.Name.LocalName == "th")));
                }
                break;
        }
    }

    private void NoteId(XElement element) {
        string? id = element.Attribute("id")?.Value;
        if (!string.IsNullOrWhiteSpace(id)) {
            _pendingIds.Add("#" + id);
        }
    }

    private void Add(List<ContentBlock> blocks, ContentBlock block) {
        if (_pendingIds.Count > 0) {
            block = block with { Anchors = [.. _pendingIds] };
            _pendingIds.Clear();
        }
        blocks.Add(block);
    }

    private void AddParagraph(List<ContentBlock> blocks, ContentBlock block) {
        IReadOnlyList<InlineRun> inlines = block switch {
            HeadingBlock heading => heading.Inlines,
            ParagraphBlock paragraph => paragraph.Inlines,
            _ => []
        };
        if (!string.IsNullOrWhiteSpace(ContentBlock.PlainText(inlines))) {
            Add(blocks, block);
        }
    }

    private static List<InlineRun> Inlines(XElement element, InlineStyle style, string? href = null) {
        List<InlineRun> runs = [];
        Collect(element, style, href, runs);
        if (runs.Count > 0) {
            runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
            runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        }
        return runs;
    }

    private static void Collect(XElement element, InlineStyle style, string? href, List<InlineRun> output) {
        foreach (XNode node in element.Nodes()) {
            if (node is XText text) {
                output.Add(new InlineRun(Whitespace().Replace(text.Value, " "), style, href));
            }
            else if (node is XElement child) {
                InlineStyle childStyle = style | child.Name.LocalName switch {
                    "emphasis" => InlineStyle.Italic,
                    "strong" => InlineStyle.Bold,
                    "strikethrough" => InlineStyle.Strike,
                    "sub" => InlineStyle.Subscript,
                    "sup" => InlineStyle.Superscript,
                    "code" => InlineStyle.Code,
                    _ => InlineStyle.None
                };
                string? link = child.Attribute(XLink + "href")?.Value;
                bool external = (link is not null) && link.StartsWith("http", StringComparison.OrdinalIgnoreCase);
                // A link to "#id" points at a note or section in this file; the reader follows "book:" addresses itself.
                bool local = (link is not null) && (link.Length > 1) && link.StartsWith('#');
                Collect(child, childStyle, external ? link : (local ? "book:" + link : href), output);
            }
        }
    }

    private static string? TitleText(XElement? title) {
        if (title is null) {
            return null;
        }
        List<string> lines = [.. Children(title, "p")
            .Select(static p => Whitespace().Replace(p.Value, " ").Trim())
            .Where(static line => line.Length > 0)];
        if (lines.Count == 0) {
            string value = Whitespace().Replace(title.Value, " ").Trim();
            return (value.Length > 0) ? value : null;
        }
        return string.Join('\n', lines);
    }

    private static XElement? Child(XElement? parent, string name) {
        return parent?.Elements().FirstOrDefault(element => element.Name.LocalName == name);
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) {
        return parent.Elements().Where(element => element.Name.LocalName == name);
    }
}