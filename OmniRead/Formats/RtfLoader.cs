using System.IO;
using System.Windows;
using System.Windows.Documents;

using OmniRead.Models;

namespace OmniRead.Formats;

/// <summary>Reads RTF through WPF's own converter, then maps the resulting flow content to reader blocks.</summary>
internal static class RtfLoader {
    public static LoadedContent Load(string path) {
        // FlowDocument content has thread affinity, so the conversion runs on the UI thread.
        return Application.Current.Dispatcher.Invoke(() => {
            FlowDocument document = new();
            TextRange range = new(document.ContentStart, document.ContentEnd);
            using (FileStream stream = File.OpenRead(path)) {
                range.Load(stream, DataFormats.Rtf);
            }
            LoadedContent content = new();
            Chapter chapter = new(Path.GetFileNameWithoutExtension(path));
            Walk(document.Blocks, chapter.Blocks, 0);
            content.Chapters.Add(chapter);
            return content;
        });
    }

    private static void Walk(IEnumerable<Block> blocks, List<ContentBlock> output, int listDepth) {
        foreach (Block block in blocks) {
            switch (block) {
                case Paragraph paragraph:
                    List<InlineRun> inlines = Inlines(paragraph.Inlines);
                    if (!string.IsNullOrWhiteSpace(ContentBlock.PlainText(inlines))) {
                        output.Add(new ParagraphBlock(inlines));
                    }
                    break;
                case Section section:
                    Walk(section.Blocks, output, listDepth);
                    break;
                case List list:
                    int number = list.StartIndex;
                    foreach (ListItem item in list.ListItems) {
                        List<ContentBlock> itemBlocks = [];
                        Walk(item.Blocks, itemBlocks, listDepth + 1);
                        string marker = (list.MarkerStyle == TextMarkerStyle.Decimal) ? $"{number}." : "•";
                        number++;
                        for (int i = 0; i < itemBlocks.Count; i++) {
                            if (itemBlocks[i] is ParagraphBlock { Kind: ParagraphKind.Normal } paragraph) {
                                itemBlocks[i] = paragraph with {
                                    Kind = ParagraphKind.ListItem,
                                    Depth = listDepth + 1,
                                    Marker = (i == 0) ? marker : null
                                };
                            }
                        }
                        output.AddRange(itemBlocks);
                    }
                    break;
                case Table table:
                    List<IReadOnlyList<IReadOnlyList<InlineRun>>> rows = [];
                    foreach (TableRow row in table.RowGroups.SelectMany(static group => group.Rows)) {
                        List<IReadOnlyList<InlineRun>> cells = [];
                        foreach (TableCell cell in row.Cells) {
                            List<InlineRun> cellInlines = [];
                            foreach (Paragraph cellParagraph in cell.Blocks.OfType<Paragraph>()) {
                                if (cellInlines.Count > 0) {
                                    cellInlines.Add(new InlineRun("\n"));
                                }
                                cellInlines.AddRange(Inlines(cellParagraph.Inlines));
                            }
                            cells.Add(cellInlines);
                        }
                        rows.Add(cells);
                    }
                    if (rows.Count > 0) {
                        output.Add(new TableBlock(rows, false));
                    }
                    break;
            }
        }
    }

    private static List<InlineRun> Inlines(InlineCollection source) {
        List<InlineRun> runs = [];
        Collect(source, runs);
        return runs;
    }

    private static void Collect(InlineCollection source, List<InlineRun> output) {
        foreach (Inline inline in source) {
            switch (inline) {
                case Run run when run.Text.Length > 0:
                    InlineStyle style = InlineStyle.None;
                    if (run.FontWeight.ToOpenTypeWeight() >= 600) {
                        style |= InlineStyle.Bold;
                    }
                    if (run.FontStyle != FontStyles.Normal) {
                        style |= InlineStyle.Italic;
                    }
                    foreach (TextDecoration decoration in run.TextDecorations ?? []) {
                        style |= (decoration.Location == TextDecorationLocation.Strikethrough)
                            ? InlineStyle.Strike
                            : InlineStyle.Underline;
                    }
                    style |= run.BaselineAlignment switch {
                        BaselineAlignment.Superscript => InlineStyle.Superscript,
                        BaselineAlignment.Subscript => InlineStyle.Subscript,
                        _ => InlineStyle.None
                    };
                    output.Add(new InlineRun(run.Text, style));
                    break;
                case Span span:
                    Collect(span.Inlines, output);
                    break;
                case LineBreak:
                    output.Add(new InlineRun("\n"));
                    break;
            }
        }
    }
}