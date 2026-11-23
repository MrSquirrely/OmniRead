using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

using OmniRead.Services;

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
    }

    private void AddBookmark() {
        if ((_book is null) || !_shown) {
            return;
        }
        // The bookmark is labelled with the words that follow it, or the chapter's name when it sits on a picture.
        List<Models.ContentBlock> blocks = _book.Chapters[_chapterIndex].Blocks;
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