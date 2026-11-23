using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using Microsoft.Win32;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

using MessageBox = HandyControl.Controls.MessageBox;

namespace OmniRead;

/// <summary>Bookmarks and highlights.</summary>
public partial class MainWindow {
    private const int SnippetLength = 140;

    private List<Annotation> _annotations = [];

    private void LoadAnnotations() {
        _annotations = (_book is null) ? [] : AnnotationStore.ForBook(_book.FilePath);
        RefreshAnnotations();
    }

    private void RefreshAnnotations() {
        if (_book is not null) {
            foreach (Annotation annotation in _annotations) {
                annotation.Where = _book.Chapters[Math.Clamp(annotation.ChapterIndex, 0, _book.Chapters.Count - 1)].Title;
            }
        }
        _suppressEvents = true;
        NotesList.ItemsSource = _annotations
            .OrderBy(static annotation => annotation.ChapterIndex)
            .ThenBy(static annotation => annotation.BlockIndex)
            .ThenBy(static annotation => annotation.Offset)
            .ToList();
        _suppressEvents = false;
        NotesEmpty.Visibility = (_annotations.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        NotesExportButton.IsEnabled = _annotations.Count > 0;
    }

    // ---------------------------------------------------------------- export

    private void OnExportNotesClick(object sender, RoutedEventArgs e) {
        if ((_book is null) || (_annotations.Count == 0)) {
            return;
        }
        // Notes being typed are only stored when their box loses focus.
        AnnotationStore.Save();
        string name = string.Concat(_book.Title.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
        SaveFileDialog dialog = new() {
            Title = Localizer.Get("Notes_ExportDialog"),
            Filter = $"Markdown|*.md|{Localizer.Get("Filter_Text")}|*.txt",
            FileName = Localizer.Format("Notes_ExportFile", name),
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) {
            return;
        }
        bool markdown = !dialog.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        try {
            File.WriteAllText(dialog.FileName, ExportNotes(_book, markdown), new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) {
            MessageBox.Show(this, Localizer.Format("Notes_ExportFailed", ex.Message), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>The book's bookmarks, highlights and notes as one document, grouped by chapter in reading order.</summary>
    private string ExportNotes(Book book, bool markdown) {
        StringBuilder text = new();
        text.AppendLine(markdown ? $"# {book.Title}" : book.Title);
        if (!string.IsNullOrEmpty(book.Author)) {
            text.AppendLine().AppendLine(markdown ? $"*{book.Author}*" : book.Author);
        }
        IEnumerable<IGrouping<int, Annotation>> chapters = _annotations
            .OrderBy(static annotation => annotation.ChapterIndex)
            .ThenBy(static annotation => annotation.BlockIndex)
            .ThenBy(static annotation => annotation.Offset)
            .GroupBy(annotation => Math.Clamp(annotation.ChapterIndex, 0, book.Chapters.Count - 1));
        foreach (IGrouping<int, Annotation> chapter in chapters) {
            string title = book.Chapters[chapter.Key].Title;
            text.AppendLine().AppendLine(markdown ? $"## {title}" : title);
            if (!markdown) {
                text.AppendLine(new string('-', Math.Min(title.Length, 60)));
            }
            foreach (Annotation annotation in chapter) {
                text.AppendLine();
                if (annotation.Kind == AnnotationKind.Highlight) {
                    string passage = PassageOf(book, annotation);
                    text.AppendLine(markdown
                        ? string.Join("\n", passage.Split('\n').Select(static line => $"> {line}"))
                        : $"“{passage}”");
                }
                else {
                    string label = Localizer.Get("Notes_Export_Bookmark");
                    text.AppendLine(markdown ? $"- **{label}:** {annotation.Text}" : $"{label}: {annotation.Text}");
                }
                if (annotation.Note.Trim().Length > 0) {
                    string note = $"{Localizer.Get("Notes_Export_Note")}: {annotation.Note.Trim()}";
                    text.AppendLine().AppendLine((markdown && (annotation.Kind == AnnotationKind.Bookmark)) ? $"  {note}" : note);
                }
            }
        }
        return text.ToString().ReplaceLineEndings();
    }

    /// <summary>The whole highlighted passage, read back from the book; the stored snippet when the place no longer exists.</summary>
    private static string PassageOf(Book book, Annotation annotation) {
        List<ContentBlock> blocks = book.Chapters[Math.Clamp(annotation.ChapterIndex, 0, book.Chapters.Count - 1)].Blocks;
        if ((annotation.BlockIndex < 0) || (annotation.EndBlockIndex >= blocks.Count) || (annotation.EndBlockIndex < annotation.BlockIndex)) {
            return annotation.Text;
        }
        List<string> parts = [];
        for (int block = annotation.BlockIndex; block <= annotation.EndBlockIndex; block++) {
            if (TextOf(blocks[block]) is not string text) {
                continue;
            }
            int from = (block == annotation.BlockIndex) ? Math.Clamp(annotation.Offset, 0, text.Length) : 0;
            int to = (block == annotation.EndBlockIndex) ? Math.Clamp(annotation.EndOffset, from, text.Length) : text.Length;
            string part = text[from..to].Trim();
            if (part.Length > 0) {
                parts.Add(part);
            }
        }
        return (parts.Count > 0) ? string.Join("\n", parts) : annotation.Text;
    }

    private void AddBookmark() {
        if ((_book is null) || !_shown) {
            return;
        }
        // The bookmark is labelled with the words that follow it, or the chapter's name when it sits on a picture.
        List<ContentBlock> blocks = _book.Chapters[_chapterIndex].Blocks;
        int block = Math.Clamp(_topBlock, 0, blocks.Count - 1);
        string text = TextOf(blocks[block]) ?? "";
        int offset = Math.Clamp(_topOffset, 0, text.Length);
        string snippet = text[offset..].Replace('\n', ' ').Trim();
        AddAnnotation(new Annotation {
            Kind = AnnotationKind.Bookmark,
            ChapterIndex = _chapterIndex,
            BlockIndex = block,
            Offset = offset,
            EndBlockIndex = block,
            EndOffset = offset,
            Text = Shorten((snippet.Length > 0) ? snippet : _book.Chapters[_chapterIndex].Title)
        });
    }

    private void HighlightSelection() {
        if ((_book is null) || (_selection is not ReaderSelection selection)) {
            return;
        }
        AddAnnotation(new Annotation {
            Kind = AnnotationKind.Highlight,
            ChapterIndex = _chapterIndex,
            BlockIndex = selection.Block,
            Offset = selection.Start,
            EndBlockIndex = selection.EndBlock,
            EndOffset = selection.End,
            Text = Shorten(selection.Text.ReplaceLineEndings(" ").Trim())
        });
        _selection = null;
        ShowMarkedPassages();
    }

    /// <summary>Repaints this chapter's highlights on the page; the selection is dropped so the tint can be seen.</summary>
    private void ShowMarkedPassages() {
        PostToReader(new Dictionary<string, object?> { ["t"] = "marked", ["list"] = MarkedPassages() });
    }

    private static string Shorten(string text) {
        return (text.Length <= SnippetLength) ? text : text[..SnippetLength].TrimEnd() + "…";
    }

    private void AddAnnotation(Annotation annotation) {
        annotation.Created = DateTime.Now;
        _annotations.Add(annotation);
        AnnotationStore.Save();
        RefreshAnnotations();
        if (NotesPanel.Visibility != Visibility.Visible) {
            ShowSidePanel(NotesPanel, true);
        }
    }

    private void OnAddBookmarkClick(object sender, RoutedEventArgs e) {
        AddBookmark();
    }

    private void OnHighlightClick(object sender, RoutedEventArgs e) {
        HighlightSelection();
    }

    private void OnNoteSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_suppressEvents || (NotesList.SelectedItem is not Annotation annotation)) {
            return;
        }
        ShowChapter(annotation.ChapterIndex, 0, annotation.BlockIndex, true, annotation.Offset);
        _suppressEvents = true;
        NotesList.SelectedItem = null;
        _suppressEvents = false;
    }

    private void OnDeleteNoteClick(object sender, RoutedEventArgs e) {
        if (((FrameworkElement)sender).DataContext is not Annotation annotation) {
            return;
        }
        _annotations.Remove(annotation);
        AnnotationStore.Save();
        RefreshAnnotations();
        if ((annotation.Kind == AnnotationKind.Highlight) && (annotation.ChapterIndex == _chapterIndex)) {
            ShowMarkedPassages();
        }
    }

    private void OnNoteLostFocus(object sender, RoutedEventArgs e) {
        AnnotationStore.Save();
    }

    /// <summary>Contents, bookmarks and search share the left side, so showing one hides the others.</summary>
    private void ShowSidePanel(FrameworkElement panel, bool show) {
        (FrameworkElement Panel, ToggleButton Toggle)[] all = [(TocPanel, TocToggle), (NotesPanel, NotesToggle), (SearchPanel, SearchToggle)];
        foreach ((FrameworkElement other, ToggleButton toggle) in all) {
            bool visible = show && ReferenceEquals(other, panel);
            other.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            toggle.IsChecked = visible;
        }
    }
}