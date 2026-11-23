using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using OmniRead.Models;

namespace OmniRead.Formats;

internal static partial class TextLoader {
    [GeneratedRegex(@"^\s*(Title|Author)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataLine();

    public static LoadedContent Load(string path, bool bookMode) {
        string text = FileHelpers.ReadText(path).Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');
        LoadedContent content = new();
        foreach (string line in lines.Take(80)) {
            Match match = MetadataLine().Match(line);
            if (!match.Success) {
                continue;
            }
            if (match.Groups[1].Value.Equals("Title", StringComparison.OrdinalIgnoreCase)) {
                content.Title ??= match.Groups[2].Value;
            }
            else {
                content.Author ??= match.Groups[2].Value;
            }
        }

        List<List<string>> groups = GroupLines(lines);
        Chapter chapter = new(content.Title ?? Path.GetFileNameWithoutExtension(path));
        if (bookMode) {
            AddBookParagraphs(groups, chapter.Blocks);
        }
        else {
            foreach (List<string> group in groups) {
                string joined = string.Join('\n', group.Select(static line => line.Replace("\t", "    ")));
                chapter.Blocks.Add(new ParagraphBlock([new InlineRun(joined)]));
            }
        }
        content.Chapters.Add(chapter);
        return content;
    }

    /// <summary>Splits lines into groups separated by blank lines.</summary>
    private static List<List<string>> GroupLines(string[] lines) {
        List<List<string>> groups = [];
        List<string>? current = null;
        foreach (string raw in lines) {
            string line = raw.TrimEnd();
            if (line.Length == 0) {
                current = null;
                continue;
            }
            if (current is null) {
                current = [];
                groups.Add(current);
            }
            current.Add(line);
        }
        return groups;
    }

    /// <summary>
    /// Rebuilds real paragraphs from plain text. Files wrapped at a fixed column (Project Gutenberg style) have
    /// their lines rejoined; short-lined groups such as verse and headings keep their line breaks.
    /// </summary>
    private static void AddBookParagraphs(List<List<string>> groups, List<ContentBlock> output) {
        if (groups.Count == 0) {
            return;
        }
        List<int> lengths = [.. groups.SelectMany(static group => group).Select(static line => line.Trim().Length).Order()];
        int typicalWidth = lengths[(int)((lengths.Count - 1) * 0.9)];
        int multiLine = groups.Count(static group => group.Count > 1);
        bool hardWrapped = (multiLine >= (groups.Count * 0.3)) && (typicalWidth <= 100);

        foreach (List<string> group in groups) {
            List<string> trimmed = [.. group.Select(static line => line.Trim())];
            if (trimmed.TrueForAll(static line => MetadataLine().IsMatch(line))) {
                // "Title:" and "Author:" lines become the title page instead of body text.
                continue;
            }
            if (!hardWrapped) {
                foreach (string line in trimmed) {
                    output.Add(new ParagraphBlock([new InlineRun(line)]));
                }
                continue;
            }
            bool prose = (trimmed.Count > 1) && (trimmed.Max(static line => line.Length) >= (typicalWidth * 0.6));
            output.Add(new ParagraphBlock([new InlineRun(prose ? Unwrap(trimmed) : string.Join('\n', trimmed))]));
        }
    }

    private static string Unwrap(List<string> lines) {
        StringBuilder builder = new();
        foreach (string line in lines) {
            if (builder.Length > 0) {
                // A word broken across lines with a hyphen is glued back together; everything else gets a space.
                bool brokenWord = (builder.Length > 1) && (builder[^1] == '-') && char.IsLetter(builder[^2])
                    && char.IsLower(line[0]);
                if (!brokenWord) {
                    builder.Append(' ');
                }
            }
            builder.Append(line);
        }
        return builder.ToString();
    }
}