using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;

using AngleSharp.Dom;

using OmniRead.Models;

namespace OmniRead.Formats;

/// <summary>
/// Flattens an HTML tree into reader blocks, driven by each element's computed CSS. Shared by the HTML,
/// Markdown and EPUB loaders. Run <see cref="CssNormalizer"/> over the result before showing it.
/// </summary>
internal sealed partial class HtmlConverter(StyleResolver styles, Func<string, Func<Task<ImageSource?>>?>? imageResolver = null,
    string anchorPrefix = "",
    Func<string, string?>? linkResolver = null) {
    private static readonly HashSet<string> SkipTags = [
        "script", "style", "head", "template", "noscript", "nav", "iframe", "object", "audio", "video", "button",
        "input", "select", "textarea", "title", "meta", "link"
    ];

    private static readonly HashSet<string> BlockTags = [
        "html", "body", "div", "section", "article", "main", "header", "footer", "aside", "figure", "figcaption",
        "center", "li", "dl", "dt", "dd", "form", "fieldset", "details", "summary", "address", "p", "h1", "h2", "h3",
        "h4", "h5", "h6", "blockquote", "pre", "ul", "ol", "table", "hr"
    ];

    private static readonly string BlockSelector = string.Join(',', BlockTags.Where(static tag => tag is not ("html" or "body")));

    /// <summary>A line break requested by a block-level box inside running text; adjoining ones merge.</summary>
    private const char SoftBreakChar = (char)0x2028;

    private static readonly string SoftBreak = SoftBreakChar.ToString();

    private readonly List<IElement> _pendingImages = [];

    /// <summary>Ids of elements met since the last block; they become link targets on the next block produced.</summary>
    private readonly List<string> _pendingIds = [];

    /// <summary>Vertical margin waiting to be applied to the next block; adjoining margins collapse to the largest.</summary>
    private double _pendingSpace;

    private readonly record struct Context(ParagraphKind Kind, int Depth, double InsetLeft, double InsetRight);

    [GeneratedRegex(@"[ \t\r\n\f]+")]
    private static partial Regex Whitespace();

    public List<ContentBlock> Convert(INode root) {
        List<ContentBlock> blocks = [];
        // The page's own margins are the reader's business, so the root contributes text properties only.
        ElementStyle rootStyle = ((root is IElement element) ? ChainStyle(element) : ElementStyle.Root).Inherit();
        WalkContainer(root, blocks, new Context(ParagraphKind.Normal, 0, 0, 0), rootStyle);
        return blocks;
    }

    private ElementStyle ChainStyle(IElement element) {
        ElementStyle parent = (element.ParentElement is IElement parentElement) ? ChainStyle(parentElement) : ElementStyle.Root;
        return styles.Resolve(element, parent);
    }

    /// <summary>Walks the children of a block container. <paramref name="style"/> is the container's own style.</summary>
    private void WalkContainer(INode node, List<ContentBlock> output, Context context, ElementStyle style) {
        List<InlineRun> buffer = [];

        void Emit(ElementStyle blockStyle) {
            List<InlineRun> inlines = Normalize(buffer);
            buffer.Clear();
            if (inlines.Count > 0) {
                double space = Math.Max(_pendingSpace, blockStyle.MarginTop);
                _pendingSpace = blockStyle.MarginBottom;
                BlockStyle css = new(
                    blockStyle.Align,
                    blockStyle.TextIndent,
                    false,
                    space,
                    context.InsetLeft + blockStyle.MarginLeft,
                    context.InsetRight + blockStyle.MarginRight,
                    blockStyle.FontScale);
                Add(output, new ParagraphBlock(inlines, context.Kind, context.Depth, null, css));
            }
            FlushImages(output);
        }

        // Loose text directly inside the container forms a paragraph without margins of its own.
        void Flush() {
            Emit(style.Inherit());
        }

        foreach (INode child in node.ChildNodes) {
            if (child is IText text) {
                AppendText(buffer, text.Data, style, null);
                continue;
            }
            if (child is not IElement element) {
                continue;
            }
            string tag = element.LocalName.ToLowerInvariant();
            if (SkipTags.Contains(tag)) {
                continue;
            }
            ElementStyle childStyle = styles.Resolve(element, style);
            if (childStyle.Hidden) {
                continue;
            }
            NoteId(element);
            switch (tag) {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                    Flush();
                    List<InlineRun> heading = [];
                    // Headings take their weight and size from the reader, so only emphasis within them is kept.
                    CollectInlines(element, childStyle with { Inline = childStyle.Inline & ~InlineStyle.Bold }, null, heading);
                    heading = Normalize(heading);
                    if (heading.Count > 0) {
                        Add(output, new HeadingBlock(tag[1] - '0', heading));
                    }
                    _pendingSpace = 0;
                    FlushImages(output);
                    break;
                case "p":
                    Flush();
                    CollectInlines(element, childStyle, null, buffer);
                    Emit(childStyle);
                    break;
                case "br":
                    buffer.Add(new InlineRun("\n"));
                    break;
                case "hr":
                    Flush();
                    _pendingSpace = 0;
                    Add(output, new SceneBreakBlock());
                    break;
                case "blockquote":
                    Flush();
                    WalkContainer(element, output, context with { Kind = ParagraphKind.Quote, Depth = context.Depth + 1 }, childStyle);
                    break;
                case "pre":
                    Flush();
                    _pendingSpace = 0;
                    string code = element.TextContent.Replace("\r\n", "\n").Trim('\n');
                    if (code.Length > 0) {
                        Add(output, new ParagraphBlock([new InlineRun(code, InlineStyle.Code)], ParagraphKind.Code, context.Depth));
                    }
                    break;
                case "ul" or "ol":
                    Flush();
                    WalkList(element, output, context, childStyle, tag == "ol");
                    break;
                case "table":
                    Flush();
                    _pendingSpace = 0;
                    WalkTable(element, output, childStyle);
                    break;
                case "img" or "image":
                    _pendingImages.Add(element);
                    Flush();
                    break;
                case "svg":
                    _pendingImages.AddRange(element.QuerySelectorAll("image"));
                    Flush();
                    break;
                default:
                    bool block = childStyle.IsBlock ?? BlockTags.Contains(tag);
                    if (block || BlockTags.Contains(tag) || (element.QuerySelector(BlockSelector) is not null)) {
                        Flush();
                        _pendingSpace = Math.Max(_pendingSpace, childStyle.MarginTop);
                        Context inner = context with {
                            InsetLeft = context.InsetLeft + childStyle.MarginLeft,
                            InsetRight = context.InsetRight + childStyle.MarginRight
                        };
                        WalkContainer(element, output, inner, childStyle);
                        _pendingSpace = Math.Max(_pendingSpace, childStyle.MarginBottom);
                    }
                    else {
                        CollectInlines(element, childStyle, HrefOf(element, null), buffer);
                    }
                    break;
            }
        }
        Flush();
    }

    private void WalkList(IElement list, List<ContentBlock> output, Context context, ElementStyle style, bool ordered) {
        int number = int.TryParse(list.GetAttribute("start"), out int start) ? start : 1;
        foreach (IElement item in list.Children) {
            if (!string.Equals(item.LocalName, "li", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            ElementStyle itemStyle = styles.Resolve(item, style);
            if (itemStyle.Hidden) {
                continue;
            }
            List<ContentBlock> itemBlocks = [];
            WalkContainer(item, itemBlocks, new Context(ParagraphKind.ListItem, context.Depth + 1, 0, 0), itemStyle);
            string marker = ordered ? $"{number}." : "•";
            number++;
            if ((itemBlocks.Count > 0) && (itemBlocks[0] is ParagraphBlock { Kind: ParagraphKind.ListItem, Marker: null } first)) {
                itemBlocks[0] = first with { Marker = marker };
            }
            output.AddRange(itemBlocks);
        }
    }

    private void WalkTable(IElement table, List<ContentBlock> output, ElementStyle style) {
        List<IReadOnlyList<IReadOnlyList<InlineRun>>> rows = [];
        bool hasHeader = false;
        foreach (IElement row in table.QuerySelectorAll("tr")) {
            List<IReadOnlyList<InlineRun>> cells = [];
            foreach (IElement cell in row.Children) {
                string name = cell.LocalName.ToLowerInvariant();
                if (name is not ("td" or "th")) {
                    continue;
                }
                if ((rows.Count == 0) && (name == "th")) {
                    hasHeader = true;
                }
                List<InlineRun> inlines = [];
                // Header cells are emboldened by the renderer, so their default bold is not repeated per run.
                ElementStyle cellStyle = styles.Resolve(cell, style);
                if (name == "th") {
                    cellStyle = cellStyle with { Inline = cellStyle.Inline & ~InlineStyle.Bold };
                }
                CollectInlines(cell, cellStyle, null, inlines);
                cells.Add(Normalize(inlines));
            }
            if (cells.Count > 0) {
                rows.Add(cells);
            }
        }
        if (rows.Count > 0) {
            Add(output, new TableBlock(rows, hasHeader));
        }
        FlushImages(output);
    }

    private void CollectInlines(INode node, ElementStyle style, string? href, List<InlineRun> output) {
        foreach (INode child in node.ChildNodes) {
            if (child is IText text) {
                AppendText(output, text.Data, style, href);
                continue;
            }
            if (child is not IElement element) {
                continue;
            }
            string tag = element.LocalName.ToLowerInvariant();
            if (SkipTags.Contains(tag)) {
                continue;
            }
            ElementStyle childStyle = styles.Resolve(element, style);
            if (childStyle.Hidden) {
                continue;
            }
            NoteId(element);
            switch (tag) {
                case "br":
                    output.Add(new InlineRun("\n", style.Inline, href));
                    break;
                case "img" or "image":
                    _pendingImages.Add(element);
                    break;
                case "svg":
                    _pendingImages.AddRange(element.QuerySelectorAll("image"));
                    break;
                default:
                    // A block-level child inside running text (a verse line set as a span, say) starts a new line.
                    bool lineBox = childStyle.IsBlock == true;
                    if (lineBox && (output.Count > 0)) {
                        output.Add(new InlineRun(SoftBreak));
                    }
                    CollectInlines(element, childStyle, HrefOf(element, href), output);
                    if (lineBox) {
                        output.Add(new InlineRun(SoftBreak));
                    }
                    break;
            }
        }
    }

    private void NoteId(IElement element) {
        string? id = element.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id)) {
            _pendingIds.Add($"{anchorPrefix}#{id}");
        }
    }

    private void Add(List<ContentBlock> output, ContentBlock block) {
        if (_pendingIds.Count > 0) {
            block = block with { Anchors = [.. _pendingIds] };
            _pendingIds.Clear();
        }
        output.Add(block);
    }

    private void FlushImages(List<ContentBlock> output) {
        foreach (IElement element in _pendingImages) {
            string? source = element.GetAttribute("src") ?? element.GetAttribute("xlink:href") ?? element.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(source)) {
                continue;
            }
            Func<Task<ImageSource?>>? loader = imageResolver?.Invoke(source);
            if (loader is not null) {
                Add(output, new ImageBlock(loader));
                _pendingSpace = 0;
            }
        }
        _pendingImages.Clear();
    }

    private static void AppendText(List<InlineRun> output, string text, ElementStyle style, string? href) {
        if (text.Length == 0) {
            return;
        }
        string collapsed = Whitespace().Replace(text, " ");
        output.Add(new InlineRun(style.Uppercase ? collapsed.ToUpperInvariant() : collapsed, style.Inline, href));
    }

    private string? HrefOf(IElement element, string? current) {
        if (!string.Equals(element.LocalName, "a", StringComparison.OrdinalIgnoreCase)) {
            return current;
        }
        string? href = element.GetAttribute("href");
        bool external = (href is not null)
            && (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
        // Links into the book itself are rewritten by the loader into a "book:" address the reader can follow.
        return external ? href : (string.IsNullOrWhiteSpace(href) ? current : (linkResolver?.Invoke(href) ?? current));
    }

    /// <summary>Collapses whitespace across run boundaries and trims the paragraph.</summary>
    private static List<InlineRun> Normalize(List<InlineRun> runs) {
        List<InlineRun> result = [];
        bool lastWasSpace = true;
        foreach (InlineRun run in runs) {
            StringBuilder builder = new(run.Text.Length);
            foreach (char c in run.Text) {
                if (c is '\n' or SoftBreakChar) {
                    while ((builder.Length > 0) && (builder[^1] == ' ')) {
                        builder.Length--;
                    }
                    // Line boxes ask for a break on both sides; those never double up or lead the paragraph.
                    bool duplicate = (c == SoftBreakChar) && ((builder.Length == 0)
                        ? ((result.Count == 0) || result[^1].Text.EndsWith('\n'))
                        : (builder[^1] == '\n'));
                    if (!duplicate) {
                        builder.Append('\n');
                    }
                    lastWasSpace = true;
                }
                else if (c == ' ') {
                    if (!lastWasSpace) {
                        builder.Append(' ');
                    }
                    lastWasSpace = true;
                }
                else {
                    builder.Append(c);
                    lastWasSpace = false;
                }
            }
            if (builder.Length > 0) {
                result.Add(run with { Text = builder.ToString() });
            }
        }
        while (result.Count > 0) {
            InlineRun last = result[^1];
            string trimmed = last.Text.TrimEnd(' ', '\n');
            if (trimmed.Length == last.Text.Length) {
                break;
            }
            if (trimmed.Length == 0) {
                result.RemoveAt(result.Count - 1);
            }
            else {
                result[^1] = last with { Text = trimmed };
            }
        }
        bool blank = result.All(static run => run.Text.All(static c => char.IsWhiteSpace(c) || (c == ' ')));
        return blank ? [] : result;
    }
}