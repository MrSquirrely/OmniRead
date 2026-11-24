using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead.Rendering;

/// <summary>A chapter typeset as HTML: the markup, its stylesheet and the numbers the page script lays it out with.</summary>
public sealed record ChapterPage(string Html, string Css, bool TitlePage, double Size, double Line, double Measure, double Gap);

/// <summary>
/// Typesets one chapter as HTML for the web view. Every source block becomes one element carrying its index in
/// <c>data-b</c>, and its text nodes hold exactly the block's text (a line break is a <c>br</c>), so a character
/// offset means the same thing here, in the page script and in saved bookmarks.
/// </summary>
public sealed class ChapterHtml(ReaderSettings settings, string fontName, IReadOnlyList<FontFace> faces, int fontToken, Palette palette) {
    private const string MonoFont = "'Cascadia Mono',Consolas,'Courier New',monospace";
    private const string BundledFamily = "OmniReadText";

    private static readonly double[] HeadingScale = [1.7, 1.4, 1.2, 1.08, 1, 1];

    private double BaseSize => settings.FontSize * palette.FontScale;

    private double LineHeight => Math.Round(BaseSize * settings.LineSpacing * palette.LineBoost);

    /// <summary>The address the web view asks for an image at; <paramref name="token"/> changes with every opened book.</summary>
    public static string ImageUrl(int token, int chapter, int block) {
        return FormattableString.Invariant($"/img/{token}/{chapter}/{block}");
    }

    /// <summary>Marks where words may break; null when hyphenation is off or there are no patterns for the book's language.</summary>
    private Hyphenator? _hyphenator;

    private int _chapter;

    /// <summary>
    /// What identifies a block's element: the id page numbers point at, the chapter and block it stands for, and
    /// the short index the page script works with. The block index is the block's place in the chapter, the same
    /// one the paginator counts.
    /// </summary>
    private string Mark(int block) {
        return Invariant($"id=\"{BookPaginator.AnchorId(_chapter, block)}\" data-chapter-index=\"{_chapter}\" data-block-index=\"{block}\" data-b=\"{block}\"");
    }

    public ChapterPage Build(Book book, int chapterIndex, int imageToken) {
        Chapter chapter = book.Chapters[chapterIndex];
        _chapter = chapterIndex;
        bool bookLayout = book.BookLayout;
        _hyphenator = (bookLayout && settings.Justify && settings.Hyphenate)
            ? Hyphenator.For(book.Language ?? CultureInfo.CurrentUICulture.Name)
            : null;
        bool titlePage = bookLayout
            && chapter.Blocks.TrueForAll(static block => block is HeadingBlock or ParagraphBlock { Kind: ParagraphKind.Centered });
        StringBuilder html = new();
        bool indentNext = false;
        bool seenHeading = false;
        bool dropCapPending = bookLayout && settings.DropCaps;
        for (int i = 0; i < chapter.Blocks.Count; i++) {
            switch (chapter.Blocks[i]) {
                case HeadingBlock heading:
                    string opener = (bookLayout && (i == 0)) ? " op" : "";
                    html.Append(Invariant($"<div class=\"h h{Math.Clamp(heading.Level, 1, 6)}{opener}\" {Mark(i)}>"));
                    AppendInlines(html, heading.Inlines);
                    html.Append("</div>");
                    indentNext = false;
                    seenHeading = true;
                    break;
                case ParagraphBlock { Kind: ParagraphKind.Normal } paragraph:
                    bool dropCap = dropCapPending && seenHeading && (paragraph.Text.Length >= 140) && CanDropCap(paragraph);
                    dropCapPending = false;
                    AppendParagraph(html, paragraph, i, bookLayout, indentNext, dropCap);
                    indentNext = true;
                    break;
                case ParagraphBlock paragraph:
                    AppendParagraph(html, paragraph, i, bookLayout, false, false);
                    indentNext = false;
                    break;
                case SceneBreakBlock:
                    html.Append(bookLayout
                        ? Invariant($"<p class=\"sb\" {Mark(i)}><span data-x>* * *</span></p>")
                        : Invariant($"<hr {Mark(i)}>"));
                    indentNext = false;
                    break;
                case ImageBlock image:
                    html.Append(Invariant($"<div class=\"im{(image.FullPage ? " full" : "")}\" {Mark(i)}>"))
                        .Append(Invariant($"<img src=\"{ImageUrl(imageToken, chapterIndex, i)}\" alt=\"\" draggable=\"false\"></div>"));
                    indentNext = false;
                    break;
                case TableBlock table:
                    AppendTable(html, table, i);
                    indentNext = false;
                    break;
                default:
                    html.Append(Invariant($"<div {Mark(i)}></div>"));
                    break;
            }
        }
        return new ChapterPage(html.ToString(), Css(bookLayout, titlePage), titlePage, BaseSize, LineHeight, settings.PageWidth * BaseSize, BaseSize * 3.2);
    }

    /// <summary>The @font-face rules and family name for the chosen font; fonts that are not installed are served from their files.</summary>
    private string FontCss(out string family) {
        if (faces.Count == 0) {
            family = $"'{fontName.Replace("'", "\\'")}',Georgia,serif";
            return "";
        }
        family = $"{BundledFamily},Georgia,serif";
        StringBuilder css = new();
        for (int i = 0; i < faces.Count; i++) {
            css.Append(Invariant($"@font-face{{font-family:{BundledFamily};src:url(/font/{fontToken}/{i});font-weight:{faces[i].Weight};"))
                .Append(faces[i].Italic ? "font-style:italic}" : "font-style:normal}");
        }
        return css.ToString();
    }

    private string Css(bool bookLayout, bool titlePage) {
        double size = BaseSize;
        double line = LineHeight;
        bool justify = bookLayout && settings.Justify;
        StringBuilder css = new(FontCss(out string family));
        // Words break only at the soft hyphens the app puts in: the web view has no hyphenation dictionaries.
        css.Append(Invariant($"#content{{font-family:{family};font-size:{Px(size)};line-height:{Px(line)};hyphens:manual;"))
            .Append(justify ? "text-align:justify}" : "text-align:left}");
        css.Append("p{margin:0;text-indent:0}");

        // Headings
        css.Append(".h{font-weight:600;line-height:1.3;hyphens:manual;text-indent:0;break-after:avoid;break-inside:avoid;")
            .Append(bookLayout ? "text-align:center;" : "text-align:left;")
            .Append(Invariant($"margin:{Px(line * (bookLayout ? 1.6 : 1.1))} 0 {Px(line * 0.6)}}}"));
        for (int level = 1; level <= 6; level++) {
            css.Append(Invariant($".h{level}{{font-size:{Px(Math.Round(size * HeadingScale[level - 1]))}}}"));
        }
        // Chapter openers start well down the page, as in a printed book.
        string sink = titlePage ? "22vh" : Invariant($"clamp({Px(line)},10vh,{Px(line * 4)})");
        css.Append(Invariant($".h.op{{font-size:{Px(Math.Round(size * (titlePage ? 2.1 : HeadingScale[0])))};padding-top:{sink};margin:0 0 {Px(line * 1.8)}}}"));

        // Paragraphs
        if (bookLayout) {
            css.Append(Invariant($".in{{text-indent:{Px(size * 1.5)}}}"));
            css.Append(".dc::first-letter{initial-letter:3;font-weight:400;margin-right:.06em}");
        }
        else {
            css.Append(Invariant($".n{{margin-bottom:{Px(line * 0.65)}}}"));
        }
        css.Append(Invariant($".qb{{padding-left:{Px(size * 0.9)};border-left:3px solid var(--divider)}}"));
        double codeSize = Math.Round(size * 0.82);
        css.Append(Invariant($".c{{font-family:{MonoFont};font-size:{Px(codeSize)};line-height:{Px(Math.Round(codeSize * 1.45))};"))
            .Append("text-align:left;hyphens:manual;white-space:pre-wrap;overflow-wrap:anywhere;background:var(--code);border-radius:4px;")
            .Append(Invariant($"padding:{Px(size * 0.5)} {Px(size * 0.8)}}}"));
        css.Append(Invariant($".ct{{text-align:center;margin:{Px(line * 0.4)} 0}}"));
        css.Append(Invariant($".li{{text-align:left;margin-bottom:{Px(line * 0.25)}}}"));
        css.Append(".mk{display:inline-block;text-indent:0}");
        css.Append(Invariant($".sb{{text-align:center;color:var(--muted);margin:{Px(line)} 0;break-after:avoid}}"));
        css.Append(Invariant($"hr{{border:0;border-top:1px solid var(--divider);margin:{Px(line * 0.6)} 0 {Px(line * 1.2)}}}"));

        // Pictures and tables
        css.Append(Invariant($".im{{text-align:center;text-indent:0;break-inside:avoid;margin:{Px(line * 0.5)} 0}}"));
        css.Append(".im img{max-width:100%;max-height:var(--img-max);opacity:var(--img-opacity);vertical-align:top}");
        css.Append(".im.full{margin:0;height:var(--full-height)}.im.full img{width:100%;height:100%;max-height:none;object-fit:contain}");
        double tableSize = Math.Round(size * 0.9);
        css.Append(Invariant($"table{{border-collapse:collapse;width:100%;font-size:{Px(tableSize)};line-height:{Px(Math.Round(tableSize * 1.4))};"))
            .Append("text-align:left;hyphens:manual;border-top:1px solid var(--divider);")
            .Append(Invariant($"margin:{Px(line * 0.5)} 0 {Px(line * 0.8)}}}"));
        css.Append(Invariant($"td{{padding:{Px(size * 0.3)} {Px(size * 0.5)};border-bottom:1px solid var(--divider);vertical-align:top}}"));
        css.Append("tr.hd{font-weight:600}");

        // Inline styling
        css.Append(Invariant($"code{{font-family:{MonoFont};font-size:.86em;background:var(--code)}}"));
        css.Append("sup,sub{font-size:.7em;line-height:0}.sc{font-variant-caps:small-caps}");
        css.Append("a{color:var(--accent);text-decoration:none;cursor:pointer}a:hover{text-decoration:underline}");
        return css.ToString();
    }

    /// <summary>A drop cap needs a plain letter or digit to enlarge, after at most two opening quotation marks.</summary>
    private static bool CanDropCap(ParagraphBlock paragraph) {
        if ((paragraph.Inlines.Count == 0) || paragraph.Inlines[0].Style.HasFlag(InlineStyle.Code)) {
            return false;
        }
        string text = paragraph.Inlines[0].Text;
        int length = 0;
        while ((length < text.Length) && (length < 2) && (text[length] is '“' or '‘' or '"' or '\'' or '«')) {
            length++;
        }
        return (length < text.Length) && char.IsLetterOrDigit(text[length]) && !char.IsSurrogate(text[length]);
    }

    private void AppendParagraph(StringBuilder html, ParagraphBlock block, int index, bool bookLayout, bool indent, bool dropCap) {
        double size = BaseSize;
        double line = LineHeight;
        string kind = "";
        Thickness margin = default;
        double textIndent = -1;
        string? marker = null;
        double hanging = 0;
        switch (block.Kind) {
            case ParagraphKind.Normal:
                kind = !bookLayout ? "n" : dropCap ? "dc" : indent ? "in" : "";
                break;
            case ParagraphKind.Quote:
                double inset = size * 1.6 * Math.Max(1, block.Depth);
                if (bookLayout) {
                    margin = new Thickness(inset, line * 0.45, size * 1.6, line * 0.45);
                }
                else {
                    kind = "qb";
                    margin = new Thickness(inset - (size * 1.1), line * 0.3, 0, line * 0.3);
                }
                break;
            case ParagraphKind.Code:
                kind = "c";
                margin = new Thickness(size * 1.6 * block.Depth, line * 0.4, 0, line * 0.7);
                break;
            case ParagraphKind.Centered:
                kind = "ct";
                break;
            case ParagraphKind.ListItem:
                kind = "li";
                // The marker hangs in the margin; its width is estimated, as numbers and bullets are narrow.
                hanging = size * 0.95;
                if (block.Marker is not null) {
                    marker = block.Marker;
                    hanging = Math.Max(hanging, (size * 0.55 * (marker.Length + 1)) + (size * 0.15));
                }
                margin = new Thickness((size * 1.4 * Math.Max(1, block.Depth)) + hanging, 0, 0, 0);
                break;
        }

        StringBuilder style = new();
        bool normal = block.Kind == ParagraphKind.Normal;
        if (block.Style is BlockStyle css) {
            // What the book's stylesheet asked for on this block, on top of the reader's own layout.
            if (css.Align is TextAlignment align) {
                style.Append("text-align:").Append(align switch {
                    TextAlignment.Center => "center",
                    TextAlignment.Right => "right",
                    TextAlignment.Justify => "justify",
                    _ => "left"
                }).Append(';');
                if (normal) {
                    textIndent = 0;
                }
            }
            if (css.NoIndent && normal) {
                textIndent = 0;
            }
            if ((css.Scale != 1) && (block.Kind != ParagraphKind.Code)) {
                style.Append(Invariant($"font-size:{Px(Math.Round(size * css.Scale))};line-height:{Px(Math.Round(line * Math.Max(css.Scale, 0.75)))};"));
            }
            margin = new Thickness(
                margin.Left + (css.InsetLeft * size), margin.Top + (css.SpaceBefore * size), margin.Right + (css.InsetRight * size), margin.Bottom);
        }
        if (textIndent >= 0) {
            style.Append(Invariant($"text-indent:{Px(textIndent)};"));
        }
        if (margin != default) {
            // Margins the kind's own rule sets (space under a paragraph, say) are kept by only overriding the sides in use.
            if (margin.Left != 0) {
                style.Append(Invariant($"margin-left:{Px(margin.Left)};"));
            }
            if (margin.Top != 0) {
                style.Append(Invariant($"margin-top:{Px(margin.Top)};"));
            }
            if (margin.Right != 0) {
                style.Append(Invariant($"margin-right:{Px(margin.Right)};"));
            }
            if (margin.Bottom != 0) {
                style.Append(Invariant($"margin-bottom:{Px(margin.Bottom)};"));
            }
        }

        // Paragraphs that already carry soft hyphens keep their own; mixing the two would upset the offsets.
        bool hyphenate = (_hyphenator is not null) && (block.Kind is ParagraphKind.Normal or ParagraphKind.Quote)
            && !block.Text.Contains(Hyphenator.SoftHyphen);
        html.Append(Invariant($"<p {Mark(index)}"));
        if (hyphenate) {
            // Tells the page script that the soft hyphens in this block are not part of its text.
            html.Append(" data-hy");
        }
        if (kind.Length > 0) {
            html.Append(" class=\"").Append(kind).Append('"');
        }
        if (style.Length > 0) {
            html.Append(" style=\"").Append(style).Append('"');
        }
        html.Append('>');
        if (marker is not null) {
            html.Append(Invariant($"<span class=\"mk\" data-x style=\"width:{Px(hanging)};margin-left:-{Px(hanging)}\">"))
                .Append(Escape(marker)).Append("</span>");
        }
        AppendInlines(html, block.Inlines, block.Kind == ParagraphKind.Code, hyphenate ? _hyphenator : null);
        html.Append("</p>");
    }

    private void AppendTable(StringBuilder html, TableBlock table, int index) {
        html.Append(Invariant($"<table {Mark(index)}>"));
        for (int r = 0; r < table.Rows.Count; r++) {
            html.Append((table.HasHeader && (r == 0)) ? "<tr class=\"hd\">" : "<tr>");
            foreach (IReadOnlyList<InlineRun> cell in table.Rows[r]) {
                html.Append("<td>");
                AppendInlines(html, cell);
                html.Append("</td>");
            }
            html.Append("</tr>");
        }
        html.Append("</table>");
    }

    private static void AppendInlines(StringBuilder html, IReadOnlyList<InlineRun> inlines, bool codeBlock = false, Hyphenator? hyphenator = null) {
        foreach (InlineRun inline in inlines) {
            if (inline.Text.Length == 0) {
                continue;
            }
            InlineStyle style = inline.Style;
            List<string> tags = [];
            if (inline.Href is not null) {
                html.Append("<a data-h=\"").Append(Escape(inline.Href)).Append("\">");
                tags.Add("a");
            }
            void Open(InlineStyle flag, string tag, string attributes = "") {
                if (style.HasFlag(flag)) {
                    html.Append('<').Append(tag).Append(attributes).Append('>');
                    tags.Add(tag);
                }
            }
            Open(InlineStyle.Superscript, "sup");
            Open(InlineStyle.Subscript, "sub");
            Open(InlineStyle.Bold, "b");
            Open(InlineStyle.Italic, "i");
            Open(InlineStyle.Underline, "u");
            Open(InlineStyle.Strike, "s");
            Open(InlineStyle.SmallCaps, "span", " class=\"sc\"");
            if (!codeBlock) {
                Open(InlineStyle.Code, "code");
            }
            bool breakable = (hyphenator is not null) && !style.HasFlag(InlineStyle.Code);
            html.Append(Escape(breakable ? hyphenator!.Hyphenate(inline.Text) : inline.Text).Replace("\n", "<br>"));
            for (int i = tags.Count - 1; i >= 0; i--) {
                html.Append("</").Append(tags[i]).Append('>');
            }
        }
    }

    /// <summary>
    /// Escapes text for HTML without changing its length in characters once parsed: control characters, which
    /// the parser would drop or merge, become spaces.
    /// </summary>
    private static string Escape(string text) {
        StringBuilder escaped = new(text.Length + 16);
        foreach (char c in text) {
            switch (c) {
                case '&':
                    escaped.Append("&amp;");
                    break;
                case '<':
                    escaped.Append("&lt;");
                    break;
                case '>':
                    escaped.Append("&gt;");
                    break;
                case '"':
                    escaped.Append("&quot;");
                    break;
                case '\n':
                    escaped.Append('\n');
                    break;
                default:
                    escaped.Append((c < ' ') ? ' ' : c);
                    break;
            }
        }
        return escaped.ToString();
    }

    private static string Px(double value) {
        return Math.Round(value, 2).ToString(CultureInfo.InvariantCulture) + "px";
    }

    private static string Invariant(FormattableString text) {
        return FormattableString.Invariant(text);
    }

    /// <summary>The theme colours as CSS custom properties, sent on their own when only the colours change.</summary>
    public static Dictionary<string, string> Variables(Palette palette) {
        static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
            (byte)(from.R + ((to.R - from.R) * amount)),
            (byte)(from.G + ((to.G - from.G) * amount)),
            (byte)(from.B + ((to.B - from.B) * amount)));
        return new Dictionary<string, string> {
            ["page"] = Hex(palette.Page),
            ["text"] = Hex(palette.Text),
            ["accent"] = Hex(palette.Accent),
            ["muted"] = Hex(Mix(palette.Text, palette.Page, 0.42)),
            ["divider"] = Hex(Mix(palette.Page, palette.Text, 0.16)),
            ["code"] = Hex(Mix(palette.Page, palette.Text, 0.07)),
            ["highlight"] = palette.IsDark ? "rgba(200,150,0,.47)" : "rgba(255,214,64,.51)",
            ["img-opacity"] = palette.ImageOpacity.ToString("0.###", CultureInfo.InvariantCulture),
            ["scheme"] = palette.IsDark ? "dark" : "light"
        };
    }
}