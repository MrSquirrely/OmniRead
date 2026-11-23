using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using OmniRead.Localization;
using OmniRead.Models;

namespace OmniRead.Formats;

internal sealed partial class DocxLoader(string path, Dictionary<string, string> relationships) {
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";

    [GeneratedRegex(@"^heading\s*(\d)$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyle();

    public static LoadedContent Load(string path) {
        using ZipArchive zip = ZipFile.OpenRead(path);
        XDocument document = ReadXml(zip, "word/document.xml")
            ?? throw new InvalidDataException(Localizer.Get("Docx_NotWord"));
        Dictionary<string, string> relationships = [];
        if (ReadXml(zip, "word/_rels/document.xml.rels") is XDocument rels) {
            foreach (XElement relationship in rels.Root!.Elements()) {
                string? id = relationship.Attribute("Id")?.Value;
                string? target = relationship.Attribute("Target")?.Value;
                if ((id is not null) && (target is not null)) {
                    relationships[id] = target;
                }
            }
        }
        LoadedContent content = new();
        if (ReadXml(zip, "docProps/core.xml") is XDocument core) {
            string? title = core.Descendants(DublinCore + "title").FirstOrDefault()?.Value.Trim();
            string? author = core.Descendants(DublinCore + "creator").FirstOrDefault()?.Value.Trim();
            content.Title = string.IsNullOrEmpty(title) ? null : title;
            content.Author = string.IsNullOrEmpty(author) ? null : author;
        }
        Chapter chapter = new(content.Title ?? Path.GetFileNameWithoutExtension(path));
        XElement? body = document.Root?.Element(W + "body");
        if (body is not null) {
            new DocxLoader(path, relationships).WalkBody(body, chapter.Blocks);
        }
        content.Chapters.Add(chapter);
        return content;
    }

    private void WalkBody(XElement container, List<ContentBlock> output) {
        foreach (XElement element in container.Elements()) {
            if (element.Name == (W + "p")) {
                AddParagraph(element, output);
            }
            else if (element.Name == (W + "tbl")) {
                List<IReadOnlyList<IReadOnlyList<InlineRun>>> rows = [];
                foreach (XElement row in element.Elements(W + "tr")) {
                    List<IReadOnlyList<InlineRun>> cells = [];
                    foreach (XElement cell in row.Elements(W + "tc")) {
                        List<InlineRun> inlines = [];
                        foreach (XElement paragraph in cell.Descendants(W + "p")) {
                            if (inlines.Count > 0) {
                                inlines.Add(new InlineRun("\n"));
                            }
                            inlines.AddRange(Runs(paragraph, []));
                        }
                        cells.Add(inlines);
                    }
                    rows.Add(cells);
                }
                if (rows.Count > 0) {
                    output.Add(new TableBlock(rows, false));
                }
            }
            else if (element.Name == (W + "sdt")) {
                XElement? inner = element.Element(W + "sdtContent");
                if (inner is not null) {
                    WalkBody(inner, output);
                }
            }
        }
    }

    private void AddParagraph(XElement paragraph, List<ContentBlock> output) {
        List<string> images = [];
        List<InlineRun> inlines = Runs(paragraph, images);
        XElement? properties = paragraph.Element(W + "pPr");
        if (!string.IsNullOrWhiteSpace(ContentBlock.PlainText(inlines))) {
            string style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            string? outline = properties?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value;
            Match headingStyle = HeadingStyle().Match(style);
            int level = headingStyle.Success ? int.Parse(headingStyle.Groups[1].Value)
                : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1
                : style.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) ? 2
                : (int.TryParse(outline, out int outlineLevel) && (outlineLevel < 6)) ? outlineLevel + 1
                : 0;
            XElement? numbering = properties?.Element(W + "numPr");
            if (level > 0) {
                output.Add(new HeadingBlock(Math.Clamp(level, 1, 6), inlines));
            }
            else if (numbering is not null) {
                _ = int.TryParse(numbering.Element(W + "ilvl")?.Attribute(W + "val")?.Value, out int indent);
                output.Add(new ParagraphBlock(inlines, ParagraphKind.ListItem, indent + 1, "•"));
            }
            else {
                output.Add(new ParagraphBlock(inlines));
            }
        }
        foreach (string relationshipId in images) {
            if (relationships.TryGetValue(relationshipId, out string? target)) {
                output.Add(new ImageBlock(FileHelpers.ZipImage(path, FileHelpers.ResolveZipPath("word", target))));
            }
        }
    }

    private static List<InlineRun> Runs(XElement paragraph, List<string> images) {
        List<InlineRun> inlines = [];
        foreach (XElement run in paragraph.Descendants(W + "r")) {
            if (run.Ancestors(W + "del").Any()) {
                continue;
            }
            XElement? properties = run.Element(W + "rPr");
            InlineStyle style = InlineStyle.None;
            if (IsOn(properties, "b")) {
                style |= InlineStyle.Bold;
            }
            if (IsOn(properties, "i")) {
                style |= InlineStyle.Italic;
            }
            if (IsOn(properties, "strike")) {
                style |= InlineStyle.Strike;
            }
            if (properties?.Element(W + "u")?.Attribute(W + "val")?.Value is not (null or "none")) {
                style |= InlineStyle.Underline;
            }
            style |= properties?.Element(W + "vertAlign")?.Attribute(W + "val")?.Value switch {
                "superscript" => InlineStyle.Superscript,
                "subscript" => InlineStyle.Subscript,
                _ => InlineStyle.None
            };
            foreach (XElement child in run.Elements()) {
                if (child.Name == (W + "t")) {
                    inlines.Add(new InlineRun(child.Value, style));
                }
                else if (child.Name == (W + "tab")) {
                    inlines.Add(new InlineRun(" ", style));
                }
                else if ((child.Name == (W + "br")) && (child.Attribute(W + "type")?.Value is not "page")) {
                    inlines.Add(new InlineRun("\n", style));
                }
                else if (child.Name == (W + "drawing")) {
                    images.AddRange(child.Descendants(A + "blip")
                        .Select(static blip => blip.Attribute(R + "embed")?.Value)
                        .OfType<string>());
                }
            }
        }
        return inlines;
    }

    private static bool IsOn(XElement? properties, string name) {
        XElement? flag = properties?.Element(W + name);
        return (flag is not null) && (flag.Attribute(W + "val")?.Value is not ("0" or "false"));
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