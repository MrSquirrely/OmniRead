using OmniRead.Models;

namespace OmniRead.Services;

/// <summary>
/// Where a page of the book begins: the chapter, the block within it, and how far into that block. A page that
/// begins between two blocks has offset 0; one that begins part-way through a long paragraph carries the
/// character its first line starts at.
/// </summary>
public sealed record PageAnchor(int PageNumber, int ChapterIndex, int BlockIndex, int CharacterOffset, double RelativeBlockProgress) {
    /// <summary>The id of the block's element on the page, the same one <see cref="Rendering.ChapterHtml"/> writes.</summary>
    public string DomAnchorId => BookPaginator.AnchorId(ChapterIndex, BlockIndex);
}

/// <summary>The first and last page of a chapter, both counted from 1.</summary>
public readonly record struct ChapterPageRange(int StartPage, int EndPage);

/// <summary>The page map of a whole book at one page size and one set of type settings.</summary>
public sealed class PaginationResult(List<PageAnchor> pages, Dictionary<int, ChapterPageRange> chapterRanges) {
    public static PaginationResult Empty { get; } = new([], []);

    public int TotalPages => Pages.Count;

    /// <summary>Page starts in reading order; the entry at <c>pageNumber - 1</c> is where that page begins.</summary>
    public List<PageAnchor> Pages { get; } = pages;

    /// <summary>The pages each chapter covers, by chapter index. Chapters without content have no entry.</summary>
    public Dictionary<int, ChapterPageRange> ChapterRanges { get; } = chapterRanges;

    /// <summary>The start of a page; numbers outside the book are taken as its first or last page.</summary>
    public PageAnchor? AnchorOf(int pageNumber) {
        return (TotalPages == 0) ? null : Pages[Math.Clamp(pageNumber, 1, TotalPages) - 1];
    }

    /// <summary>
    /// The page a place in the book is on: the last page that starts at or before it, found by binary search.
    /// Returns 0 only when the book has no pages at all.
    /// </summary>
    public int FindPageForLocation(int chapterIndex, int blockIndex, int characterOffset) {
        int low = 0;
        int high = Pages.Count - 1;
        int found = -1;
        while (low <= high) {
            int middle = low + ((high - low) / 2);
            PageAnchor anchor = Pages[middle];
            int order = (anchor.ChapterIndex != chapterIndex) ? anchor.ChapterIndex.CompareTo(chapterIndex)
                : (anchor.BlockIndex != blockIndex) ? anchor.BlockIndex.CompareTo(blockIndex)
                : anchor.CharacterOffset.CompareTo(characterOffset);
            if (order <= 0) {
                found = middle;
                low = middle + 1;
            }
            else {
                high = middle - 1;
            }
        }
        return (Pages.Count == 0) ? 0 : (found < 0) ? 1 : Pages[found].PageNumber;
    }
}

/// <summary>
/// The page the paginator fills, in CSS pixels: the size of one column of text, how many columns a page shows,
/// and the type size and line height the chapter is typeset with.
/// </summary>
public sealed record PageLayout(double ColumnWidth, double ColumnHeight, int Columns, double FontSize, double LineHeight, bool BookLayout);

/// <summary>Widths of characters as a share of the font size, so the paginator can be run without real fonts.</summary>
public interface ITextMeasurer {
    double Advance(char character, InlineStyle style);
}

/// <summary>
/// Works out where every page of a book begins by setting the text line by line, with the same margins, type
/// sizes and line heights the page stylesheet uses. The web view draws the real pages; this gives each of them
/// a number and an address without having to lay the whole book out there.
/// </summary>
public static class BookPaginator {
    private static readonly double[] HeadingScale = [1.7, 1.4, 1.2, 1.08, 1, 1];

    /// <summary>The element id of a block on the page.</summary>
    public static string AnchorId(int chapterIndex, int blockIndex) {
        return FormattableString.Invariant($"reader-ch-{chapterIndex}-blk-{blockIndex}");
    }

    public static PaginationResult Paginate(
        IReadOnlyList<Chapter> chapters, PageLayout layout, ITextMeasurer measurer, CancellationToken cancellation = default) {
        Flow flow = new(layout);
        Dictionary<int, ChapterPageRange> ranges = [];
        for (int chapter = 0; chapter < chapters.Count; chapter++) {
            List<ContentBlock> blocks = chapters[chapter].Blocks;
            if (blocks.Count == 0) {
                // Nothing to show, so no page and nothing for an anchor to point at.
                continue;
            }
            int first = flow.Pages.Count + 1;
            flow.StartChapter(chapter);
            bool indentNext = false;
            for (int block = 0; block < blocks.Count; block++) {
                cancellation.ThrowIfCancellationRequested();
                Place(flow, blocks[block], block, layout, measurer, ref indentNext);
            }
            ranges[chapter] = new ChapterPageRange(first, flow.Pages.Count);
        }
        return new PaginationResult(flow.Pages, ranges);
    }

    /// <summary>
    /// Breaks text into lines no wider than <paramref name="width"/> and returns the character each line starts
    /// at. Lines break after spaces and hyphens, at line breaks in the text, and inside a word only when the
    /// word alone is too wide.
    /// </summary>
    public static List<int> WrapLines(
        IReadOnlyList<InlineRun> inlines, double width, double firstIndent, double fontSize, ITextMeasurer measurer,
        InlineStyle forced = InlineStyle.None) {
        List<int> starts = [0];
        int lineStart = 0;
        int breakAt = -1;
        double x = firstIndent;
        double xAtBreak = 0;
        int index = 0;
        foreach (InlineRun run in inlines) {
            InlineStyle style = run.Style | forced;
            double scale = fontSize * (((style & (InlineStyle.Superscript | InlineStyle.Subscript)) != 0) ? 0.7 : 1);
            foreach (char character in run.Text) {
                if (character == '\n') {
                    index++;
                    starts.Add(index);
                    lineStart = index;
                    breakAt = -1;
                    x = 0;
                    continue;
                }
                double advance = measurer.Advance(character, style) * scale;
                if (((x + advance) > width) && (character != ' ') && (index > lineStart)) {
                    if (breakAt > lineStart) {
                        lineStart = breakAt;
                        x -= xAtBreak;
                    }
                    else {
                        lineStart = index;
                        x = 0;
                    }
                    starts.Add(lineStart);
                    breakAt = -1;
                }
                x += advance;
                index++;
                if (character is ' ' or '-') {
                    breakAt = index;
                    xAtBreak = x;
                }
            }
        }
        return starts;
    }

    private static void Place(Flow flow, ContentBlock block, int index, PageLayout layout, ITextMeasurer measurer, ref bool indentNext) {
        double size = layout.FontSize;
        double line = layout.LineHeight;
        double width = layout.ColumnWidth;
        double height = layout.ColumnHeight;
        switch (block) {
            case HeadingBlock heading:
                bool opener = layout.BookLayout && (index == 0);
                double headingSize = Math.Round(size * HeadingScale[Math.Clamp(heading.Level, 1, 6) - 1]);
                int headingLines = WrapLines(heading.Inlines, width, 0, headingSize, measurer, InlineStyle.Bold).Count;
                // A chapter opener starts some way down the page; other headings have space above them.
                double sink = opener ? Math.Clamp(height * 0.1, line, line * 4) : 0;
                flow.Solid(index,
                    opener ? 0 : line * (layout.BookLayout ? 1.6 : 1.1),
                    sink + (headingLines * headingSize * 1.3),
                    line * (opener ? 1.8 : 0.6));
                indentNext = false;
                break;
            case ParagraphBlock paragraph:
                PlaceParagraph(flow, paragraph, index, layout, measurer, indentNext);
                indentNext = paragraph.Kind == ParagraphKind.Normal;
                break;
            case SceneBreakBlock:
                if (layout.BookLayout) {
                    flow.Solid(index, line, line, line);
                }
                else {
                    flow.Solid(index, line * 0.6, 1, line * 1.2);
                }
                indentNext = false;
                break;
            case ImageBlock image:
                // Pictures are not decoded here: a full-page one fills a column, any other is taken as two fifths of one.
                if (image.FullPage) {
                    flow.Solid(index, 0, height, 0);
                }
                else {
                    flow.Solid(index, line * 0.5, height * 0.4, line * 0.5);
                }
                indentNext = false;
                break;
            case TableBlock table:
                PlaceTable(flow, table, index, layout, measurer);
                indentNext = false;
                break;
        }
    }

    private static void PlaceParagraph(Flow flow, ParagraphBlock block, int index, PageLayout layout, ITextMeasurer measurer, bool indent) {
        double size = layout.FontSize;
        double line = layout.LineHeight;
        double textSize = size;
        double textLine = line;
        double left = 0;
        double right = 0;
        double top = 0;
        double bottom = 0;
        double padding = 0;
        double firstIndent = 0;
        InlineStyle forced = InlineStyle.None;
        switch (block.Kind) {
            case ParagraphKind.Normal:
                if (layout.BookLayout) {
                    firstIndent = (indent && (block.Style is not { NoIndent: true }) && (block.Style?.Align is null)) ? size * 1.5 : 0;
                }
                else {
                    bottom = line * 0.65;
                }
                break;
            case ParagraphKind.Quote:
                double inset = size * 1.6 * Math.Max(1, block.Depth);
                if (layout.BookLayout) {
                    (left, right, top, bottom) = (inset, size * 1.6, line * 0.45, line * 0.45);
                }
                else {
                    (left, top, bottom) = (inset - (size * 0.2) + 3, line * 0.3, line * 0.3);
                }
                break;
            case ParagraphKind.Code:
                textSize = Math.Round(size * 0.82);
                textLine = Math.Round(textSize * 1.45);
                padding = size * 0.5;
                (left, right, top, bottom) = ((size * 1.6 * block.Depth) + (size * 0.8), size * 0.8, line * 0.4, line * 0.7);
                forced = InlineStyle.Code;
                break;
            case ParagraphKind.Centered:
                (top, bottom) = (line * 0.4, line * 0.4);
                break;
            case ParagraphKind.ListItem:
                double hanging = (block.Marker is null) ? size * 0.95
                    : Math.Max(size * 0.95, (size * 0.55 * (block.Marker.Length + 1)) + (size * 0.15));
                left = (size * 1.4 * Math.Max(1, block.Depth)) + hanging;
                bottom = line * 0.25;
                break;
        }
        if (block.Style is BlockStyle style) {
            if ((style.Scale != 1) && (block.Kind != ParagraphKind.Code)) {
                textSize = Math.Round(size * style.Scale);
                textLine = Math.Round(line * Math.Max(style.Scale, 0.75));
            }
            left += style.InsetLeft * size;
            right += style.InsetRight * size;
            top += style.SpaceBefore * size;
        }
        double width = Math.Max(textSize * 4, layout.ColumnWidth - left - right);
        List<int> lines = WrapLines(block.Inlines, width, firstIndent, textSize, measurer, forced);
        flow.Lines(index, top, padding, lines, textLine, bottom);
    }

    private static void PlaceTable(Flow flow, TableBlock table, int index, PageLayout layout, ITextMeasurer measurer) {
        double size = layout.FontSize;
        double textSize = Math.Round(size * 0.9);
        double textLine = Math.Round(textSize * 1.4);
        int offset = 0;
        for (int row = 0; row < table.Rows.Count; row++) {
            IReadOnlyList<IReadOnlyList<InlineRun>> cells = table.Rows[row];
            double cellWidth = Math.Max(textSize * 2, (layout.ColumnWidth / Math.Max(1, cells.Count)) - size);
            int lines = 1;
            int characters = 0;
            foreach (IReadOnlyList<InlineRun> cell in cells) {
                lines = Math.Max(lines, WrapLines(cell, cellWidth, 0, textSize, measurer).Count);
                characters += ContentBlock.PlainText(cell).Length;
            }
            // A row stays in one piece. A page that starts inside the table is addressed by the text before the row.
            flow.Solid(index,
                (row == 0) ? layout.LineHeight * 0.5 : 0,
                (lines * textLine) + (size * 0.6) + 1,
                (row == (table.Rows.Count - 1)) ? layout.LineHeight * 0.8 : 0,
                Math.Max(offset, row),
                (double)row / table.Rows.Count);
            offset += characters;
        }
    }

    /// <summary>The column being filled, and the pages begun so far.</summary>
    private sealed class Flow(PageLayout layout) {
        private readonly int _columns = Math.Max(1, layout.Columns);
        private readonly double _height = Math.Max(1, layout.ColumnHeight);
        private int _chapter;
        private int _column;
        private double _y;
        private double _marginBelow;
        private bool _filled;

        public List<PageAnchor> Pages { get; } = [];

        /// <summary>Every chapter starts on a page of its own, which begins at the chapter's first block.</summary>
        public void StartChapter(int chapter) {
            _chapter = chapter;
            _column = 0;
            _y = 0;
            _marginBelow = 0;
            _filled = false;
            Pages.Add(new PageAnchor(Pages.Count + 1, chapter, 0, 0, 0));
        }

        /// <summary>Places something that cannot be split across columns: a heading, a picture, a table row.</summary>
        public void Solid(int block, double top, double height, double bottom, int offset = 0, double progress = 0) {
            double gap = _filled ? Math.Max(_marginBelow, top) : 0;
            if (_filled && ((_y + gap + height) > (_height + 0.5))) {
                NextColumn(block, offset, progress);
                gap = 0;
            }
            _y = Math.Min(_height, _y + gap + height);
            _filled = true;
            _marginBelow = bottom;
        }

        /// <summary>Places lines of text, moving to the next column, and so perhaps the next page, whenever one is full.</summary>
        public void Lines(int block, double top, double padding, List<int> starts, double lineHeight, double bottom) {
            _y += (_filled ? Math.Max(_marginBelow, top) : 0) + padding;
            for (int line = 0; line < starts.Count; line++) {
                if (_filled && ((_y + lineHeight) > (_height + 0.5))) {
                    NextColumn(block, starts[line], (double)line / starts.Count);
                }
                _y += lineHeight;
                _filled = true;
            }
            _y += padding;
            _marginBelow = bottom;
        }

        private void NextColumn(int block, int offset, double progress) {
            _y = 0;
            _marginBelow = 0;
            _filled = false;
            _column++;
            if (_column >= _columns) {
                _column = 0;
                Pages.Add(new PageAnchor(Pages.Count + 1, _chapter, block, offset, progress));
            }
        }
    }
}