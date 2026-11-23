using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead.Rendering;

public sealed class BuiltDocument(FlowDocument document, bool fixedLayout, bool titlePage) {
    /// <summary>A page holding only a title and byline; always set as a single centred column.</summary>
    public bool TitlePage { get; } = titlePage;

    public FlowDocument Document { get; } = document;

    public bool FixedLayout { get; } = fixedLayout;

    /// <summary>The flow element created for each source block, index-aligned with the chapter's blocks.</summary>
    public List<TextElement?> Elements { get; } = [];

    public List<Image> Images { get; } = [];
}

/// <summary>Typesets one chapter as a WPF <see cref="FlowDocument"/>.</summary>
public sealed class DocumentBuilder(ReaderSettings settings, FontFamily font, Palette palette) {
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Courier New");
    private static readonly double[] HeadingScale = [1.7, 1.4, 1.2, 1.08, 1, 1];

    private readonly Typeface _typeface = new(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Called when a link that points inside the book (a footnote reference, say) is clicked.</summary>
    public Action<InlineRun>? InternalLink { get; init; }

    private double BaseSize => settings.FontSize * palette.FontScale;

    private double LineHeight => Math.Round(BaseSize * settings.LineSpacing * palette.LineBoost);

    public BuiltDocument Build(Book book, Chapter chapter, Size viewport) {
        bool bookLayout = book.BookLayout;
        bool justify = bookLayout && settings.Justify;
        FlowDocument document = new() {
            FontFamily = font,
            FontSize = BaseSize,
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = justify ? TextAlignment.Justify : TextAlignment.Left,
            IsHyphenationEnabled = justify && settings.Hyphenate,
            IsOptimalParagraphEnabled = justify,
            Background = Brushes.Transparent,
            ColumnRuleWidth = 0
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        bool titlePage = bookLayout
            && chapter.Blocks.TrueForAll(static block => block is HeadingBlock or ParagraphBlock { Kind: ParagraphKind.Centered });
        BuiltDocument built = new(document, book.FixedLayout, titlePage);

        bool indentNext = false;
        bool seenHeading = false;
        bool dropCapPending = bookLayout && settings.DropCaps;
        for (int i = 0; i < chapter.Blocks.Count; i++) {
            Block? element = null;
            switch (chapter.Blocks[i]) {
                case HeadingBlock heading:
                    element = MakeHeading(heading, bookLayout, i == 0, titlePage, viewport);
                    indentNext = false;
                    seenHeading = true;
                    break;
                case ParagraphBlock { Kind: ParagraphKind.Normal } paragraph:
                    bool dropCap = dropCapPending && seenHeading && (paragraph.Text.Length >= 140);
                    dropCapPending = false;
                    element = MakeParagraph(paragraph, bookLayout, indentNext, dropCap);
                    indentNext = true;
                    break;
                case ParagraphBlock paragraph:
                    element = MakeParagraph(paragraph, bookLayout, false, false);
                    indentNext = false;
                    break;
                case SceneBreakBlock:
                    element = MakeSceneBreak(bookLayout);
                    indentNext = false;
                    break;
                case ImageBlock image:
                    element = MakeImage(image, built, viewport);
                    indentNext = false;
                    break;
                case TableBlock table:
                    element = MakeTable(table);
                    indentNext = false;
                    break;
            }
            if (element is not null) {
                document.Blocks.Add(element);
            }
            built.Elements.Add(element);
        }
        ApplyLayout(built, viewport);
        return built;
    }

    /// <summary>Sets margins and columns for the viewport. Cheap enough to call on every resize.</summary>
    public void ApplyLayout(BuiltDocument built, Size viewport) {
        FlowDocument document = built.Document;
        double width = Math.Max(viewport.Width, 200);
        double height = Math.Max(viewport.Height, 200);
        if (built.FixedLayout) {
            document.PagePadding = new Thickness(12);
            document.ColumnWidth = 100000;
            foreach (Image image in built.Images) {
                image.Height = Math.Max(100, height - 40);
            }
            return;
        }

        bool paged = settings.ViewMode == ViewMode.Paged;
        double columnWidth = settings.PageWidth * BaseSize;
        double gap = BaseSize * 3.2;
        int columns = (!paged || built.TitlePage || (settings.Columns == ColumnMode.One)) ? 1
            : (settings.Columns == ColumnMode.Two) ? 2
            : (width >= ((columnWidth * 1.5) + gap + 96)) ? 2 : 1;
        double contentWidth = Math.Min(width - (paged ? 112 : 56), (columns * columnWidth) + ((columns - 1) * gap));
        contentWidth = Math.Max(contentWidth, 160);
        double horizontal = Math.Max(paged ? 56 : 28, (width - contentWidth) / 2);
        double vertical = paged ? Math.Clamp(height * 0.07, 24, 72) : 40;
        document.PagePadding = new Thickness(horizontal, vertical, horizontal, vertical);
        document.ColumnGap = gap;
        document.ColumnWidth = (columns == 1) ? 100000 : ((contentWidth - gap) / 2) * 0.92;
        document.IsColumnWidthFlexible = true;
        foreach (Image image in built.Images) {
            image.MaxHeight = Math.Max(100, (height - (2 * vertical)) * 0.92);
        }
    }

    private Paragraph MakeHeading(HeadingBlock heading, bool bookLayout, bool opener, bool titlePage, Size viewport) {
        double scale = HeadingScale[Math.Clamp(heading.Level, 1, 6) - 1];
        if (bookLayout && opener) {
            scale = titlePage ? 2.1 : HeadingScale[0];
        }
        double size = Math.Round(BaseSize * scale);
        Paragraph paragraph = new() {
            FontSize = size,
            LineHeight = Math.Round(size * 1.3),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = bookLayout ? TextAlignment.Center : TextAlignment.Left,
            IsHyphenationEnabled = false,
            KeepWithNext = true,
            KeepTogether = true,
            TextIndent = 0
        };
        if (bookLayout && opener) {
            // Chapter openers start well down the page, as in a printed book.
            double sink = titlePage
                ? viewport.Height * 0.22
                : Math.Clamp(viewport.Height * 0.1, LineHeight, LineHeight * 4);
            // Padding, not margin: a top margin is discarded at the start of a page.
            paragraph.Padding = new Thickness(0, sink, 0, 0);
            paragraph.Margin = new Thickness(0, 0, 0, LineHeight * 1.8);
        }
        else {
            paragraph.Margin = new Thickness(0, LineHeight * (bookLayout ? 1.6 : 1.1), 0, LineHeight * 0.6);
        }
        AddInlines(paragraph.Inlines, heading.Inlines);
        return paragraph;
    }

    private Paragraph MakeParagraph(ParagraphBlock block, bool bookLayout, bool indent, bool dropCap) {
        Paragraph paragraph = new() { Margin = new Thickness(0), TextIndent = 0 };
        IReadOnlyList<InlineRun> inlines = block.Inlines;
        switch (block.Kind) {
            case ParagraphKind.Normal:
                if (!bookLayout) {
                    paragraph.Margin = new Thickness(0, 0, 0, LineHeight * 0.65);
                }
                else if (dropCap && TryAddDropCap(paragraph, ref inlines)) {
                    paragraph.TextIndent = 0;
                }
                else if (indent) {
                    paragraph.TextIndent = BaseSize * 1.5;
                }
                break;
            case ParagraphKind.Quote:
                double inset = BaseSize * 1.6 * Math.Max(1, block.Depth);
                paragraph.Margin = new Thickness(inset, LineHeight * 0.45, bookLayout ? BaseSize * 1.6 : 0, LineHeight * 0.45);
                if (!bookLayout) {
                    paragraph.Margin = new Thickness(inset - (BaseSize * 1.1), LineHeight * 0.3, 0, LineHeight * 0.3);
                    paragraph.Padding = new Thickness(BaseSize * 0.9, 0, 0, 0);
                    paragraph.BorderThickness = new Thickness(3, 0, 0, 0);
                    paragraph.SetResourceReference(Block.BorderBrushProperty, "DividerBrush");
                }
                break;
            case ParagraphKind.Code:
                double codeSize = Math.Round(BaseSize * 0.82);
                paragraph.FontFamily = MonoFont;
                paragraph.FontSize = codeSize;
                paragraph.LineHeight = Math.Round(codeSize * 1.45);
                paragraph.TextAlignment = TextAlignment.Left;
                paragraph.IsHyphenationEnabled = false;
                paragraph.Padding = new Thickness(BaseSize * 0.8, BaseSize * 0.5, BaseSize * 0.8, BaseSize * 0.5);
                paragraph.Margin = new Thickness(BaseSize * 1.6 * block.Depth, LineHeight * 0.4, 0, LineHeight * 0.7);
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "CodeBrush");
                break;
            case ParagraphKind.Centered:
                paragraph.TextAlignment = TextAlignment.Center;
                paragraph.Margin = new Thickness(0, LineHeight * 0.4, 0, LineHeight * 0.4);
                break;
            case ParagraphKind.ListItem:
                double hanging = Measure("• ", BaseSize) + (BaseSize * 0.35);
                if (block.Marker is not null) {
                    hanging = Math.Max(hanging, Measure(block.Marker + " ", BaseSize) + (BaseSize * 0.15));
                    paragraph.TextIndent = -hanging;
                    paragraph.Inlines.Add(new Run(block.Marker + " "));
                }
                paragraph.Margin = new Thickness((BaseSize * 1.4 * Math.Max(1, block.Depth)) + hanging, 0, 0, LineHeight * 0.25);
                paragraph.TextAlignment = TextAlignment.Left;
                break;
        }
        double scale = ((block.Style is BlockStyle css) && (block.Kind != ParagraphKind.Code)) ? css.Scale : 1;
        AddInlines(paragraph.Inlines, inlines, block.Kind == ParagraphKind.Code, scale);
        ApplyBlockStyle(paragraph, block);
        return paragraph;
    }

    /// <summary>Floats an enlarged initial letter, three lines tall, at the start of the paragraph.</summary>
    private bool TryAddDropCap(Paragraph paragraph, ref IReadOnlyList<InlineRun> inlines) {
        if ((inlines.Count == 0) || inlines[0].Style.HasFlag(InlineStyle.Code)) {
            return false;
        }
        string text = inlines[0].Text;
        int length = 0;
        while ((length < text.Length) && (length < 2) && (text[length] is '“' or '‘' or '"' or '\'' or '«')) {
            length++;
        }
        if ((length >= text.Length) || !char.IsLetterOrDigit(text[length]) || char.IsSurrogate(text[length])) {
            return false;
        }
        length++;
        string initial = text[..length];
        List<InlineRun> rest = [inlines[0] with { Text = text[length..] }, .. inlines.Skip(1)];
        inlines = rest;

        const int Lines = 3;
        // BaseSize the letter so its cap height spans from the top of line one to the baseline of line three.
        double capsRatio = (_typeface.CapsHeight > 0.3) ? _typeface.CapsHeight : 0.68;
        double dropSize = (((Lines - 1) * LineHeight) + (capsRatio * BaseSize)) / capsRatio;
        Paragraph letter = new(new Run(initial)) {
            FontSize = dropSize,
            // One body line high, pushed down two lines: the glyph overflows upwards and its baseline lands on line three.
            LineHeight = LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0),
            TextIndent = 0,
            TextAlignment = TextAlignment.Left,
            FontWeight = FontWeights.Normal,
            IsHyphenationEnabled = false
        };
        Floater floater = new(letter) {
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Ceiling(Measure(initial, dropSize) + (BaseSize * 0.3)),
            Margin = new Thickness(0),
            Padding = new Thickness(0, (Lines - 1) * LineHeight, 0, 0)
        };
        paragraph.Inlines.Add(floater);
        return true;
    }

    private Paragraph MakeSceneBreak(bool bookLayout) {
        if (bookLayout) {
            Paragraph ornament = new(new Run("* * *")) {
                TextAlignment = TextAlignment.Center,
                TextIndent = 0,
                Margin = new Thickness(0, LineHeight, 0, LineHeight),
                KeepWithNext = true
            };
            ornament.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            return ornament;
        }
        Paragraph rule = new() {
            FontSize = 1,
            LineHeight = 1,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Margin = new Thickness(0, LineHeight * 0.6, 0, LineHeight * 1.2)
        };
        rule.SetResourceReference(Block.BorderBrushProperty, "DividerBrush");
        return rule;
    }

    private BlockUIContainer MakeImage(ImageBlock block, BuiltDocument built, Size viewport) {
        Image image = new() {
            Stretch = Stretch.Uniform,
            StretchDirection = block.FullPage ? StretchDirection.Both : StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = palette.ImageOpacity
        };
        if (block.FullPage) {
            image.Height = Math.Max(100, viewport.Height - 40);
        }
        built.Images.Add(image);
        _ = LoadImageAsync(image, block);
        return new BlockUIContainer(image) {
            Margin = block.FullPage ? new Thickness(0) : new Thickness(0, LineHeight * 0.5, 0, LineHeight * 0.5)
        };
    }

    private static async Task LoadImageAsync(Image target, ImageBlock block) {
        try {
            target.Source = await block.Load();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // A picture that cannot be decoded is left blank rather than failing the chapter.
        }
    }

    private Table MakeTable(TableBlock block) {
        Table table = new() {
            CellSpacing = 0,
            FontSize = Math.Round(BaseSize * 0.9),
            LineHeight = Math.Round(BaseSize * 0.9 * 1.4),
            TextAlignment = TextAlignment.Left,
            IsHyphenationEnabled = false,
            Margin = new Thickness(0, LineHeight * 0.5, 0, LineHeight * 0.8),
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        table.SetResourceReference(Block.BorderBrushProperty, "DividerBrush");
        int columns = block.Rows.Max(static row => row.Count);
        for (int i = 0; i < columns; i++) {
            table.Columns.Add(new TableColumn());
        }
        TableRowGroup group = new();
        for (int r = 0; r < block.Rows.Count; r++) {
            TableRow row = new();
            foreach (IReadOnlyList<InlineRun> cellInlines in block.Rows[r]) {
                Paragraph paragraph = new() { Margin = new Thickness(0), TextIndent = 0 };
                AddInlines(paragraph.Inlines, cellInlines);
                TableCell cell = new(paragraph) {
                    Padding = new Thickness(BaseSize * 0.5, BaseSize * 0.3, BaseSize * 0.5, BaseSize * 0.3),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontWeight = (block.HasHeader && (r == 0)) ? FontWeights.SemiBold : FontWeights.Normal
                };
                cell.SetResourceReference(Block.BorderBrushProperty, "DividerBrush");
                row.Cells.Add(cell);
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<InlineRun> inlines, bool codeBlock = false, double scale = 1) {
        double size = BaseSize * scale;
        foreach (InlineRun inline in inlines) {
            string[] lines = inline.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++) {
                if (i > 0) {
                    target.Add(new LineBreak());
                }
                if (lines[i].Length == 0) {
                    continue;
                }
                if (!inline.Style.HasFlag(InlineStyle.SmallCaps)) {
                    target.Add(MakeRun(lines[i], inline, codeBlock, size, 1));
                    continue;
                }
                // Small capitals are synthesised (capitals at a reduced size) so they work in every font.
                foreach ((string text, bool lower) in SplitByCase(lines[i])) {
                    target.Add(MakeRun(lower ? text.ToUpper(CultureInfo.CurrentCulture) : text, inline, codeBlock, size, lower ? 0.8 : 1));
                }
            }
        }
    }

    private Inline MakeRun(string text, InlineRun inline, bool codeBlock, double size, double factor) {
        Run run = new(text);
        InlineStyle style = inline.Style;
        if (factor != 1) {
            run.FontSize = Math.Round(size * factor);
        }
        if (style.HasFlag(InlineStyle.Bold)) {
            run.FontWeight = FontWeights.Bold;
        }
        if (style.HasFlag(InlineStyle.Italic)) {
            run.FontStyle = FontStyles.Italic;
        }
        if (style.HasFlag(InlineStyle.Code) && !codeBlock) {
            run.FontFamily = MonoFont;
            run.FontSize = Math.Round(size * 0.86);
            run.SetResourceReference(TextElement.BackgroundProperty, "CodeBrush");
        }
        if (style.HasFlag(InlineStyle.Underline) || style.HasFlag(InlineStyle.Strike)) {
            TextDecorationCollection decorations = [];
            if (style.HasFlag(InlineStyle.Underline)) {
                decorations.Add(TextDecorations.Underline);
            }
            if (style.HasFlag(InlineStyle.Strike)) {
                decorations.Add(TextDecorations.Strikethrough);
            }
            run.TextDecorations = decorations;
        }
        if (style.HasFlag(InlineStyle.Superscript) || style.HasFlag(InlineStyle.Subscript)) {
            run.BaselineAlignment = style.HasFlag(InlineStyle.Superscript)
                ? BaselineAlignment.Superscript
                : BaselineAlignment.Subscript;
            run.FontSize = Math.Round(size * 0.7);
        }
        if (inline.Href is null) {
            return run;
        }
        Hyperlink link = new(run) { ToolTip = inline.Href };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        string address = inline.Href;
        link.Click += (_, _) => {
            if (address.StartsWith("book:", StringComparison.Ordinal)) {
                InternalLink?.Invoke(inline);
            }
            else {
                OpenExternal(address);
            }
        };
        return link;
    }

    /// <summary>Cuts text into alternating stretches of lowercase letters and everything else.</summary>
    private static IEnumerable<(string Text, bool Lower)> SplitByCase(string text) {
        int start = 0;
        for (int i = 1; i <= text.Length; i++) {
            if ((i == text.Length) || (char.IsLower(text[i]) != char.IsLower(text[start]))) {
                yield return (text[start..i], char.IsLower(text[start]));
                start = i;
            }
        }
    }

    /// <summary>Applies what the book's stylesheet asked for on this block, on top of the reader's own layout.</summary>
    private void ApplyBlockStyle(Paragraph paragraph, ParagraphBlock block) {
        if (block.Style is not BlockStyle style) {
            return;
        }
        bool normal = block.Kind == ParagraphKind.Normal;
        if (style.Align is TextAlignment align) {
            paragraph.TextAlignment = align;
            if (normal) {
                paragraph.TextIndent = 0;
            }
        }
        if (style.NoIndent && normal && (paragraph.TextIndent > 0)) {
            paragraph.TextIndent = 0;
        }
        if ((style.Scale != 1) && (block.Kind != ParagraphKind.Code)) {
            paragraph.FontSize = Math.Round(BaseSize * style.Scale);
            paragraph.LineHeight = Math.Round(LineHeight * Math.Max(style.Scale, 0.75));
        }
        Thickness margin = paragraph.Margin;
        paragraph.Margin = new Thickness(
            margin.Left + (style.InsetLeft * BaseSize),
            margin.Top + (style.SpaceBefore * BaseSize),
            margin.Right + (style.InsetRight * BaseSize),
            margin.Bottom);
    }

    private static void OpenExternal(string address) {
        try {
            Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
            // No handler for the link; nothing useful to do.
        }
    }

    private double Measure(string text, double size) {
        FormattedText formatted = new(
            text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, size, Brushes.Black, 1);
        return formatted.WidthIncludingTrailingWhitespace;
    }
}