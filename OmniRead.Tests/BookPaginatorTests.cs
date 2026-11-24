using OmniRead.Models;
using OmniRead.Services;

using Xunit;

namespace OmniRead.Tests;

/// <summary>
/// The page map, checked with a measurer in which every character is half an em wide. At a type size of 10 on a
/// column 100 wide that is 20 characters to a line; a column 50 high with a line height of 10 holds 5 lines.
/// </summary>
public sealed class BookPaginatorTests {
    private static readonly PageLayout Small = new(100, 50, 1, 10, 10, true);

    private sealed class FixedMeasurer : ITextMeasurer {
        public double Advance(char character, InlineStyle style) {
            return 0.5;
        }
    }

    /// <summary>A paragraph of the given number of full lines at the small size: five-character words, no indent.</summary>
    private static ParagraphBlock Paragraph(int lines) {
        BlockStyle plain = new(null, null, true, 0, 0, 0, 1);
        return new ParagraphBlock([new InlineRun(string.Concat(Enumerable.Repeat("abcd ", lines * 4)))], Style: plain);
    }

    private static Chapter ChapterOf(params ContentBlock[] blocks) {
        Chapter chapter = new("Chapter");
        chapter.Blocks.AddRange(blocks);
        return chapter;
    }

    private static PaginationResult Paginate(PageLayout layout, params Chapter[] chapters) {
        return BookPaginator.Paginate(chapters, layout, new FixedMeasurer());
    }

    [Fact]
    public void EveryPageMapsToItsChapterBlockAndElement() {
        // Three chapters of three two-line paragraphs: six lines each, so two pages each.
        Chapter[] chapters = [.. Enumerable.Range(0, 3).Select(static _ => ChapterOf(Paragraph(2), Paragraph(2), Paragraph(2)))];
        PaginationResult result = Paginate(Small, chapters);

        Assert.Equal(6, result.TotalPages);
        for (int page = 1; page <= result.TotalPages; page++) {
            PageAnchor anchor = result.Pages[page - 1];
            Assert.Equal(page, anchor.PageNumber);
            Assert.Equal((page - 1) / 2, anchor.ChapterIndex);
            Assert.Equal($"reader-ch-{anchor.ChapterIndex}-blk-{anchor.BlockIndex}", anchor.DomAnchorId);
        }
        // A chapter's first page starts at its first block; its second where the sixth line falls.
        Assert.Equal((0, 0, 0.0), (result.Pages[2].BlockIndex, result.Pages[2].CharacterOffset, result.Pages[2].RelativeBlockProgress));
        Assert.Equal((2, 20, 0.5), (result.Pages[3].BlockIndex, result.Pages[3].CharacterOffset, result.Pages[3].RelativeBlockProgress));
        Assert.Equal(new ChapterPageRange(1, 2), result.ChapterRanges[0]);
        Assert.Equal(new ChapterPageRange(3, 4), result.ChapterRanges[1]);
        Assert.Equal(new ChapterPageRange(5, 6), result.ChapterRanges[2]);
    }

    [Fact]
    public void PageThatStartsBetweenBlocksHasNoOffset() {
        // Five lines fill the first page exactly, so the second starts with the next paragraph.
        PaginationResult result = Paginate(Small, ChapterOf(Paragraph(5), Paragraph(3)));

        Assert.Equal(2, result.TotalPages);
        Assert.Equal((1, 0, 0.0), (result.Pages[1].BlockIndex, result.Pages[1].CharacterOffset, result.Pages[1].RelativeBlockProgress));
    }

    [Fact]
    public void BreakInsideParagraphRecordsOffsetAndProgress() {
        PaginationResult result = Paginate(Small, ChapterOf(Paragraph(12)));

        Assert.Equal(3, result.TotalPages);
        Assert.All(result.Pages, static anchor => Assert.Equal(0, anchor.BlockIndex));
        Assert.Equal([0, 100, 200], result.Pages.Select(static anchor => anchor.CharacterOffset));
        Assert.Equal(5.0 / 12, result.Pages[1].RelativeBlockProgress, 6);
        Assert.Equal(10.0 / 12, result.Pages[2].RelativeBlockProgress, 6);
    }

    [Fact]
    public void WrapLinesGivesTheStartOfEveryLine() {
        FixedMeasurer measurer = new();
        // Breaks after spaces, at a line break in the text, and inside a word too long for a line.
        List<int> words = BookPaginator.WrapLines([new InlineRun("abcd abcd abcd abcd abcd abcd")], 100, 0, 10, measurer);
        List<int> broken = BookPaginator.WrapLines([new InlineRun("one\ntwo"), new InlineRun(" three", InlineStyle.Bold)], 100, 0, 10, measurer);
        List<int> longWord = BookPaginator.WrapLines([new InlineRun(new string('x', 45))], 100, 0, 10, measurer);
        List<int> indented = BookPaginator.WrapLines([new InlineRun("abcd abcd abcd abcd abcd")], 100, 15, 10, measurer);

        Assert.Equal([0, 20], words);
        Assert.Equal([0, 4], broken);
        Assert.Equal([0, 20, 40], longWord);
        Assert.Equal([0, 15], indented);
        Assert.Equal([0], BookPaginator.WrapLines([], 100, 0, 10, measurer));
    }

    [Fact]
    public void EmptyChaptersGetNoPagesAndNoAnchors() {
        Chapter[] chapters = [ChapterOf(), ChapterOf(Paragraph(2)), ChapterOf(), ChapterOf(), ChapterOf(Paragraph(7))];
        PaginationResult result = Paginate(Small, chapters);

        Assert.Equal(3, result.TotalPages);
        Assert.Equal([1, 4, 4], result.Pages.Select(static anchor => anchor.ChapterIndex));
        Assert.Equal([1, 4], result.ChapterRanges.Keys.Order());
        Assert.All(result.Pages, anchor => {
            Assert.InRange(anchor.BlockIndex, 0, chapters[anchor.ChapterIndex].Blocks.Count - 1);
            Assert.InRange(anchor.RelativeBlockProgress, 0.0, 1.0);
        });
        // A place in an empty chapter belongs to the page before it; the book's first chapter being empty changes nothing.
        Assert.Equal(1, result.FindPageForLocation(0, 0, 0));
        Assert.Equal(1, result.FindPageForLocation(2, 0, 0));
        Assert.Equal(0, Paginate(Small, ChapterOf(), ChapterOf()).TotalPages);
        Assert.Equal(0, Paginate(Small).FindPageForLocation(0, 0, 0));
    }

    [Fact]
    public void FindPageForLocationMatchesStartsAndPlacesBetweenThem() {
        // Pages start at (0,0,0), (0,0,100), (0,1,60), (1,0,0), (1,0,100).
        PaginationResult result = Paginate(Small, ChapterOf(Paragraph(7), Paragraph(4)), ChapterOf(Paragraph(8)));

        Assert.Equal(5, result.TotalPages);
        foreach (PageAnchor anchor in result.Pages) {
            Assert.Equal(anchor.PageNumber, result.FindPageForLocation(anchor.ChapterIndex, anchor.BlockIndex, anchor.CharacterOffset));
        }
        Assert.Equal(1, result.FindPageForLocation(0, 0, 99));
        Assert.Equal(2, result.FindPageForLocation(0, 0, 139));
        Assert.Equal(2, result.FindPageForLocation(0, 1, 59));
        Assert.Equal(3, result.FindPageForLocation(0, 1, 61));
        Assert.Equal(3, result.FindPageForLocation(0, 9, 0));
        Assert.Equal(4, result.FindPageForLocation(1, 0, 99));
        Assert.Equal(5, result.FindPageForLocation(1, 0, 5000));
        Assert.Equal(5, result.FindPageForLocation(7, 0, 0));
    }

    [Fact]
    public void PlaceIsKeptWhenTheFontSizeChanges() {
        Chapter[] chapters = [ChapterOf(Paragraph(9), Paragraph(9)), ChapterOf(Paragraph(6))];
        PaginationResult before = Paginate(Small, chapters);
        // Twice the type size: half as many characters to a line and half as many lines to a page.
        PaginationResult after = Paginate(new PageLayout(100, 50, 1, 20, 20, true), chapters);
        Assert.True(after.TotalPages > before.TotalPages);

        foreach (PageAnchor place in before.Pages) {
            int page = after.FindPageForLocation(place.ChapterIndex, place.BlockIndex, place.CharacterOffset);
            PageAnchor start = after.Pages[page - 1];
            // The page found starts at or before the place, in the same chapter, and the next page starts after it.
            Assert.Equal(place.ChapterIndex, start.ChapterIndex);
            Assert.True((start.BlockIndex, start.CharacterOffset).CompareTo((place.BlockIndex, place.CharacterOffset)) <= 0);
            if (page < after.TotalPages) {
                PageAnchor next = after.Pages[page];
                Assert.True((next.ChapterIndex, next.BlockIndex, next.CharacterOffset)
                    .CompareTo((place.ChapterIndex, place.BlockIndex, place.CharacterOffset)) > 0);
            }
        }
        // Going to that page and asking where it is gives the same page back.
        PageAnchor? target = after.AnchorOf(after.FindPageForLocation(0, 1, 100));
        Assert.NotNull(target);
        Assert.Equal(target.PageNumber, after.FindPageForLocation(target.ChapterIndex, target.BlockIndex, target.CharacterOffset));
    }

    [Fact]
    public void TwoColumnsMakeOnePage() {
        PaginationResult result = Paginate(new PageLayout(100, 50, 2, 10, 10, true), ChapterOf(Paragraph(12)));

        Assert.Equal(2, result.TotalPages);
        Assert.Equal(200, result.Pages[1].CharacterOffset);
    }

    [Fact]
    public void HeadingsPicturesAndTablesStayWhole() {
        Chapter chapter = ChapterOf(
            new HeadingBlock(2, [new InlineRun("Title")]),
            Paragraph(2),
            new ImageBlock(static () => Task.FromResult<byte[]?>(null), true),
            new TableBlock([[[new InlineRun("a")], [new InlineRun("b")]], [[new InlineRun("c")], [new InlineRun("d")]]], false));
        PaginationResult result = Paginate(new PageLayout(100, 100, 1, 10, 10, true), chapter);

        // The heading and the paragraph share the first page. The full-page picture takes a page of its own and the table follows on the next.
        Assert.Equal([0, 2, 3], result.Pages.Select(static anchor => anchor.BlockIndex));
        Assert.All(result.Pages, static anchor => Assert.Equal(0, anchor.CharacterOffset));
    }

    [Fact]
    public void AnchorOfClampsThePageNumber() {
        PaginationResult result = Paginate(Small, ChapterOf(Paragraph(12)));

        Assert.Equal(1, result.AnchorOf(-4)?.PageNumber);
        Assert.Equal(3, result.AnchorOf(99)?.PageNumber);
        Assert.Null(PaginationResult.Empty.AnchorOf(1));
    }

    [Fact]
    public void CancellationStopsTheWork() {
        using CancellationTokenSource cancel = new();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => BookPaginator.Paginate([ChapterOf(Paragraph(3))], Small, new FixedMeasurer(), cancel.Token));
    }
}