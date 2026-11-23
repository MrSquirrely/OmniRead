using System.Text;
using System.Text.RegularExpressions;

using OmniRead.Models;

namespace OmniRead.Formats;

/// <summary>
/// Turns a flat document (plain text, Markdown, HTML, Word, RTF) into something shaped like a typeset book:
/// chapters are detected and split, scene breaks recognised and punctuation made typographic.
/// </summary>
internal static partial class BookFormatter {
    private const string Numbers = @"\d+|[ivxlcdm]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve"
        + @"|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty(?:[\s-]\w+)?|thirty(?:[\s-]\w+)?"
        + @"|forty(?:[\s-]\w+)?|fifty(?:[\s-]\w+)?|the\s+\w+|first|second|third|last";

    [GeneratedRegex(@"^(?:chapter|chap\.|part|book|volume|act|scene|canto|letter|section|stave)\s+(?:" + Numbers + @")\b.{0,70}$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@"^(?:prologue|epilogue|introduction|preface|foreword|afterword|interlude|contents|table of contents"
        + @"|acknowledge?ments|dedication|appendix(?:\s+\w+)?|author'?s note|about the author|postscript)\s*[.:]?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NamedHeading();

    [GeneratedRegex(@"^(?:[IVXLC]{1,7}|\d{1,3})\.?$")]
    private static partial Regex BareNumber();

    [GeneratedRegex(@"^[\s*\-_~#•·◆♦⁂=–—]{1,40}$")]
    private static partial Regex SceneBreak();

    public static List<Chapter> Format(List<ContentBlock> source, ref string title, string? author) {
        List<ContentBlock> blocks = [.. source];
        MarkSceneBreaks(blocks);
        if (blocks.OfType<HeadingBlock>().Count() < 2) {
            PromoteHeadings(blocks);
        }
        for (int i = 0; i < blocks.Count; i++) {
            blocks[i] = blocks[i] switch {
                HeadingBlock heading => heading with { Inlines = Smarten(heading.Inlines) },
                ParagraphBlock { Kind: not ParagraphKind.Code } paragraph => paragraph with { Inlines = Smarten(paragraph.Inlines) },
                _ => blocks[i]
            };
        }
        List<Chapter> chapters = Split(blocks, ref title);
        AddTitlePage(chapters, title, author);
        return chapters;
    }

    /// <summary>Gives a multi-chapter document a title page, reusing its own title heading when it has one.</summary>
    private static void AddTitlePage(List<Chapter> chapters, string title, string? author) {
        if (chapters.Count < 2) {
            return;
        }
        List<ContentBlock> page = [];
        Chapter first = chapters[0];
        bool hasTitleHeading = (first.Blocks[0] is HeadingBlock heading) && (heading.Text.Replace('\n', ' ') == title);
        if (!hasTitleHeading) {
            page.Add(new HeadingBlock(1, [new InlineRun(title)]));
        }
        if (!string.IsNullOrWhiteSpace(author)) {
            page.Add(new ParagraphBlock([new InlineRun(author, InlineStyle.Italic)], ParagraphKind.Centered));
        }
        if (hasTitleHeading || (first.Title == title)) {
            first.Blocks.InsertRange(hasTitleHeading ? 1 : 0, page);
        }
        else {
            Chapter titlePage = new(title);
            titlePage.Blocks.AddRange(page);
            chapters.Insert(0, titlePage);
        }
    }

    /// <summary>Replaces "* * *"-style separator paragraphs with real scene breaks. Also used for native books.</summary>
    public static void MarkSceneBreaks(List<ContentBlock> blocks) {
        for (int i = 0; i < blocks.Count; i++) {
            if ((blocks[i] is ParagraphBlock { Kind: ParagraphKind.Normal } paragraph)
                && SceneBreak().IsMatch(paragraph.Text)
                && !string.IsNullOrWhiteSpace(paragraph.Text)) {
                blocks[i] = new SceneBreakBlock { Anchors = paragraph.Anchors };
            }
        }
    }

    private static bool LooksLikeHeading(string text) {
        string[] lines = text.Split('\n');
        if ((lines.Length > 3) || (text.Length > 160)) {
            return false;
        }
        string first = lines[0].Trim();
        return NumberedHeading().IsMatch(first)
            || ((lines.Length == 1) && (NamedHeading().IsMatch(first) || BareNumber().IsMatch(first)));
    }

    private static void PromoteHeadings(List<ContentBlock> blocks) {
        HashSet<int> promoted = [];
        for (int i = 0; i < blocks.Count; i++) {
            if ((blocks[i] is ParagraphBlock { Kind: ParagraphKind.Normal } paragraph) && LooksLikeHeading(paragraph.Text)) {
                promoted.Add(i);
            }
        }
        // A run of several "headings" in a row is a table of contents, not a sequence of empty chapters.
        foreach (int index in promoted.ToList()) {
            int run = 1;
            for (int j = index - 1; promoted.Contains(j); j--) {
                run++;
            }
            for (int j = index + 1; promoted.Contains(j); j++) {
                run++;
            }
            if (run >= 3) {
                continue;
            }
            ParagraphBlock paragraph = (ParagraphBlock)blocks[index];
            blocks[index] = new HeadingBlock(1, paragraph.Inlines) { Anchors = paragraph.Anchors };
        }
    }

    private static List<Chapter> Split(List<ContentBlock> blocks, ref string title) {
        List<HeadingBlock> headings = [.. blocks.OfType<HeadingBlock>()];
        List<Chapter> chapters = [];
        if (headings.Count == 0) {
            Chapter only = new(title);
            only.Blocks.AddRange(blocks);
            chapters.Add(only);
            return chapters;
        }

        int chapterLevel = headings.Min(static heading => heading.Level);
        // A single top-level heading that leads the document is the book's title; chapters are one level down.
        int topLevel = chapterLevel;
        if ((headings.Count > 1) && (headings.Count(heading => heading.Level == topLevel) == 1)
            && (headings[0].Level == chapterLevel)) {
            title = headings[0].Text.Replace('\n', ' ');
            chapterLevel = headings.Skip(1).Min(static heading => heading.Level);
        }

        Chapter current = new(title);
        bool titleSeen = false;
        foreach (ContentBlock block in blocks) {
            if ((block is HeadingBlock heading) && (heading.Level <= chapterLevel)
                && (titleSeen || (heading.Level == chapterLevel))) {
                if (current.Blocks.Count > 0) {
                    chapters.Add(current);
                }
                current = new Chapter(heading.Text);
            }
            if (block is HeadingBlock) {
                titleSeen = true;
            }
            current.Blocks.Add(block);
        }
        if (current.Blocks.Count > 0) {
            chapters.Add(current);
        }
        return chapters;
    }

    /// <summary>Curly quotes, real dashes and ellipses.</summary>
    private static List<InlineRun> Smarten(IReadOnlyList<InlineRun> inlines) {
        List<InlineRun> result = new(inlines.Count);
        char previous = '\0';
        foreach (InlineRun run in inlines) {
            if (run.Style.HasFlag(InlineStyle.Code)) {
                result.Add(run);
                previous = 'x';
                continue;
            }
            string text = run.Text;
            StringBuilder builder = new(text.Length);
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                bool opening = (previous == '\0') || char.IsWhiteSpace(previous) || (previous is '(' or '[' or '{' or '—' or '–' or '‘' or '“' or '-');
                switch (c) {
                    case '"':
                        builder.Append(opening ? '“' : '”');
                        break;
                    case '\'':
                        builder.Append((opening && !char.IsLetterOrDigit(previous)) ? '‘' : '’');
                        break;
                    case '-' when (i + 1 < text.Length) && (text[i + 1] == '-'):
                        builder.Append('—');
                        i += ((i + 2 < text.Length) && (text[i + 2] == '-')) ? 2 : 1;
                        c = '—';
                        break;
                    case '.' when (i + 2 < text.Length) && (text[i + 1] == '.') && (text[i + 2] == '.'):
                        builder.Append('…');
                        i += 2;
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
                previous = c;
            }
            result.Add(run with { Text = builder.ToString() });
        }
        return result;
    }
}