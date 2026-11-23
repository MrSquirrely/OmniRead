using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

using OmniRead.Rendering;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Bookmarks and highlights.</summary>
public partial class MainWindow {
    private const int SnippetLength = 140;

    private List<Annotation> _annotations = [];

    private TextSelection? CurrentSelection => Paged ? PageViewer.Selection : ScrollView.Selection;

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

    /// <summary>The place in the text at the top of what is on screen.</summary>
    private TextPointer? CurrentPosition() {
        if (_built is null) {
            return null;
        }
        if (Paged) {
            DynamicDocumentPaginator paginator = Paginator(_built);
            int page = PageViewer.MasterPageNumber - 1;
            return (page >= 0) ? paginator.GetPagePosition(paginator.GetPage(page)) as TextPointer : null;
        }
        // In scrolling view character rectangles are relative to the visible area.
        return _built.Elements
            .OfType<TextElement>()
            .FirstOrDefault(static element => element.ContentEnd.GetCharacterRect(LogicalDirection.Backward) is { IsEmpty: false, Bottom: > 0 })
            ?.ContentStart;
    }

    /// <summary>Finds which source block a place in the document belongs to, and how far into it.</summary>
    private (int Block, int Offset)? Locate(TextPointer? pointer) {
        if ((pointer is null) || (_built is null)) {
            return null;
        }
        if (pointer.Parent is FlowDocument) {
            pointer = pointer.GetNextInsertionPosition(LogicalDirection.Forward) ?? pointer;
        }
        for (DependencyObject? current = pointer.Parent; current is not null; current = LogicalTreeHelper.GetParent(current)) {
            int index = (current is TextElement element) ? _built.Elements.IndexOf(element) : -1;
            if (index >= 0) {
                return (index, TextPositions.OffsetOf(_built.Elements[index]!, pointer));
            }
        }
        return null;
    }

    private void AddBookmark() {
        if ((_book is null) || (_built is null) || (Locate(CurrentPosition()) is not (int block, int offset))) {
            return;
        }
        string text = TextPositions.TextOf(_built.Elements[block]!);
        string snippet = text[Math.Min(offset, text.Length)..].Replace('\n', ' ').Trim();
        AddAnnotation(new Annotation {
            Kind = AnnotationKind.Bookmark,
            ChapterIndex = _chapterIndex,
            BlockIndex = block,
            Offset = offset,
            EndBlockIndex = block,
            EndOffset = offset,
            Text = Shorten(snippet)
        });
    }

    private void HighlightSelection() {
        TextSelection? selection = CurrentSelection;
        if ((_book is null) || (selection is null) || selection.IsEmpty
            || (Locate(selection.Start) is not (int startBlock, int startOffset))
            || (Locate(selection.End) is not (int endBlock, int endOffset))) {
            return;
        }
        AddAnnotation(new Annotation {
            Kind = AnnotationKind.Highlight,
            ChapterIndex = _chapterIndex,
            BlockIndex = startBlock,
            Offset = startOffset,
            EndBlockIndex = endBlock,
            EndOffset = endOffset,
            Text = Shorten(selection.Text.ReplaceLineEndings(" ").Trim())
        });
        Rebuild();
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

    /// <summary>Paints this chapter's highlights onto the freshly typeset document.</summary>
    private void ApplyHighlights() {
        if (_built is null) {
            return;
        }
        Color tint = ThemeManager.Current.IsDark ? Color.FromArgb(120, 200, 150, 0) : Color.FromArgb(130, 255, 214, 64);
        SolidColorBrush brush = new(tint);
        brush.Freeze();
        foreach (Annotation highlight in _annotations.Where(annotation =>
            (annotation.Kind == AnnotationKind.Highlight) && (annotation.ChapterIndex == _chapterIndex))) {
            if ((highlight.BlockIndex >= _built.Elements.Count) || (highlight.EndBlockIndex >= _built.Elements.Count)
                || (_built.Elements[highlight.BlockIndex] is not TextElement first)
                || (_built.Elements[highlight.EndBlockIndex] is not TextElement last)) {
                continue;
            }
            TextRange range = new(TextPositions.PointerAt(first, highlight.Offset), TextPositions.PointerAt(last, highlight.EndOffset));
            range.ApplyPropertyValue(TextElement.BackgroundProperty, brush);
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
        ShowChapter(annotation.ChapterIndex, 0, annotation.BlockIndex, true);
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
            Rebuild();
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