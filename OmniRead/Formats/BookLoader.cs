using System.IO;

using OmniRead.Localization;
using OmniRead.Models;

namespace OmniRead.Formats;

public static class BookLoader {
    private const int MaxBlocksPerChapter = 800;

    private static readonly string[] BookExtensions = [".epub", ".fb2", ".fbz", ".pdf"];
    private static readonly string[] DocumentExtensions = [".txt", ".text", ".md", ".markdown", ".html", ".htm", ".xhtml", ".docx", ".rtf"];

    public static string DialogFilter => BuildFilter();

    public static bool IsSupported(string path) {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return BookExtensions.Contains(extension) || DocumentExtensions.Contains(extension);
    }

    /// <summary>
    /// Loads any supported file. <paramref name="bookFormatting"/> only affects document formats: when set,
    /// they are restructured into chapters and typeset like a book.
    /// </summary>
    public static async Task<Book> LoadAsync(string path, bool bookFormatting) {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (path.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase)) {
            extension = ".fbz";
        }
        LoadedContent content = (extension == ".pdf")
            ? await PdfLoader.LoadAsync(path)
            : await Task.Run(() => extension switch {
                ".epub" => EpubLoader.Load(path),
                ".fb2" or ".fbz" => Fb2Loader.Load(path),
                ".md" or ".markdown" => MarkdownLoader.Load(path),
                ".html" or ".htm" or ".xhtml" => HtmlLoader.Load(path),
                ".docx" => DocxLoader.Load(path),
                ".rtf" => RtfLoader.Load(path),
                _ => TextLoader.Load(path, bookFormatting)
            });
        return await Task.Run(() => Assemble(path, content, bookFormatting));
    }

    private static Book Assemble(string path, LoadedContent content, bool bookFormatting) {
        string title = string.IsNullOrWhiteSpace(content.Title) ? Path.GetFileNameWithoutExtension(path) : content.Title;
        List<Chapter> chapters;
        bool bookLayout;
        if (content.IsNativeBook) {
            chapters = content.Chapters;
            bookLayout = true;
            foreach (Chapter chapter in chapters) {
                BookFormatter.MarkSceneBreaks(chapter.Blocks);
            }
        }
        else if (bookFormatting) {
            chapters = BookFormatter.Format([.. content.Chapters.SelectMany(static chapter => chapter.Blocks)], ref title, content.Author);
            bookLayout = true;
        }
        else {
            chapters = content.Chapters;
            bookLayout = false;
        }

        chapters = [.. chapters.Where(static chapter => chapter.Blocks.Count > 0)];
        foreach (Chapter chapter in chapters) {
            chapter.Title = chapter.Title.Replace("\n", " · ").Trim();
            if (chapter.Title.Length == 0) {
                chapter.Title = title;
            }
        }
        if (chapters.Count == 0) {
            Chapter empty = new(title);
            empty.Blocks.Add(new ParagraphBlock([new InlineRun(Localizer.Get("Book_Empty"), InlineStyle.Italic)]));
            chapters.Add(empty);
        }
        if (!content.FixedLayout) {
            chapters = SplitOversized(chapters);
        }

        Dictionary<string, BookAnchor> anchors = new(StringComparer.Ordinal);
        for (int c = 0; c < chapters.Count; c++) {
            for (int b = 0; b < chapters[c].Blocks.Count; b++) {
                foreach (string anchor in chapters[c].Blocks[b].Anchors ?? []) {
                    anchors.TryAdd(anchor, new BookAnchor(c, b));
                }
            }
        }

        return new Book {
            Title = title,
            Author = content.Author,
            FilePath = path,
            Chapters = chapters,
            Toc = BuildToc(chapters),
            Anchors = anchors,
            BookLayout = bookLayout,
            FixedLayout = content.FixedLayout,
            CanBookFormat = !content.IsNativeBook
        };
    }

    /// <summary>Very long chapters are cut into parts so that pagination stays quick.</summary>
    private static List<Chapter> SplitOversized(List<Chapter> chapters) {
        List<Chapter> result = [];
        foreach (Chapter chapter in chapters) {
            if (chapter.Blocks.Count <= MaxBlocksPerChapter) {
                result.Add(chapter);
                continue;
            }
            int part = 1;
            for (int start = 0; start < chapter.Blocks.Count; start += MaxBlocksPerChapter) {
                Chapter piece = new((part == 1) ? chapter.Title : Localizer.Format("Book_Part", chapter.Title, part));
                piece.Blocks.AddRange(chapter.Blocks.Skip(start).Take(MaxBlocksPerChapter));
                result.Add(piece);
                part++;
            }
        }
        return result;
    }

    private static List<TocEntry> BuildToc(List<Chapter> chapters) {
        List<TocEntry> toc = [];
        List<HeadingBlock> allHeadings = [.. chapters.SelectMany(static chapter => chapter.Blocks).OfType<HeadingBlock>()];
        int minLevel = (allHeadings.Count > 0) ? allHeadings.Min(static heading => heading.Level) : 1;
        for (int c = 0; c < chapters.Count; c++) {
            Chapter chapter = chapters[c];
            toc.Add(new TocEntry(chapter.Title, 0, c, 0));
            bool first = true;
            for (int b = 0; b < chapter.Blocks.Count; b++) {
                if (chapter.Blocks[b] is not HeadingBlock heading) {
                    continue;
                }
                string text = heading.Text.Replace("\n", " · ").Trim();
                bool isChapterTitle = first && (b <= 2) && (text == chapter.Title);
                first = false;
                if (!isChapterTitle && (text.Length > 0)) {
                    toc.Add(new TocEntry(text, Math.Clamp(heading.Level - minLevel + 1, 1, 3), c, b));
                }
            }
        }
        return toc;
    }

    private static string BuildFilter() {
        string all = string.Join(';', BookExtensions.Concat(DocumentExtensions).Select(static extension => $"*{extension}"));
        return $"{Localizer.Get("Filter_All")}|{all}"
            + $"|{Localizer.Get("Filter_Epub")}|*.epub|FictionBook|*.fb2;*.fbz|{Localizer.Get("Filter_Pdf")}|*.pdf"
            + $"|Markdown|*.md;*.markdown|{Localizer.Get("Filter_Text")}|*.txt;*.text|{Localizer.Get("Filter_Web")}|*.html;*.htm;*.xhtml"
            + $"|{Localizer.Get("Filter_Word")}|*.docx|{Localizer.Get("Filter_Rtf")}|*.rtf|{Localizer.Get("Filter_AllFiles")}|*.*";
    }
}