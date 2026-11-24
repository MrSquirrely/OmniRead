using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead;

/// <summary>
/// Page numbers for the whole book. The page map is worked out in the background from the size the page script
/// reports and the type settings; it is what "go to page" and the page number in the status bar go by.
/// </summary>
public partial class MainWindow {
    private readonly DispatcherTimer _paginationTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PageNavigator _navigator = null!;
    private PaginationResult _pagination = PaginationResult.Empty;
    private CancellationTokenSource? _paginationCancel;

    /// <summary>Completes when the page map matches what is on screen; a jump waits for it.</summary>
    private TaskCompletionSource _paginationReady = new();
    private TaskCompletionSource? _jumpDone;
    private string _paginationKey = "";
    private double _columnWidth;
    private double _columnHeight;
    private int _columnCount = 1;

    private void InitializePages() {
        _navigator = new PageNavigator(GoToPageAsync);
        PagePanel.DataContext = _navigator;
        _paginationTimer.Tick += (_, _) => {
            _paginationTimer.Stop();
            _ = PaginateAsync();
        };
    }

    /// <summary>Takes the size of the page as the script laid it out, and renumbers the book when it has changed.</summary>
    private void SetPageGeometry(double columnWidth, double columnHeight, int columns) {
        _columnWidth = columnWidth;
        _columnHeight = columnHeight;
        _columnCount = Math.Max(1, columns);
        CheckPagination();
    }

    /// <summary>Starts a new page map when the book, the page size or the type has changed since the last one.</summary>
    private void CheckPagination() {
        if ((_book is null) || (_columnWidth <= 0) || (_columnHeight <= 0)) {
            return;
        }
        Palette palette = ThemeManager.Current;
        string key = FormattableString.Invariant(
            $"{_imageToken}|{_columnWidth:0}x{_columnHeight:0}x{_columnCount}|{_settings.FontName}|{_settings.FontOrigin}|{_settings.FontSize}|{_settings.LineSpacing}|{palette.FontScale}|{palette.LineBoost}|{_book.BookLayout}");
        if (key == _paginationKey) {
            return;
        }
        _paginationKey = key;
        _paginationCancel?.Cancel();
        if (_paginationReady.Task.IsCompleted) {
            _paginationReady = new TaskCompletionSource();
        }
        _paginationTimer.Stop();
        _paginationTimer.Start();
    }

    private async Task PaginateAsync() {
        if (_book is not Book book) {
            return;
        }
        _paginationCancel?.Cancel();
        CancellationTokenSource cancel = new();
        _paginationCancel = cancel;
        CancellationToken token = cancel.Token;
        Palette palette = ThemeManager.Current;
        double size = _settings.FontSize * palette.FontScale;
        PageLayout layout = new(
            _columnWidth, _columnHeight, _columnCount, size, Math.Round(size * _settings.LineSpacing * palette.LineBoost), book.BookLayout);
        IReadOnlyList<Chapter> chapters = book.Chapters;
        string fontName = _font?.Name ?? _settings.FontName;
        IReadOnlyList<FontFace> faces = _faces;
        PaginationResult result;
        try {
            // The fonts are opened on the worker too, so nothing of this runs on the window's thread.
            result = await Task.Run(() => BookPaginator.Paginate(chapters, layout, new GlyphMeasurer(fontName, faces), token), token);
        }
        catch (OperationCanceledException) {
            return;
        }
        finally {
            cancel.Dispose();
            if (_paginationCancel == cancel) {
                _paginationCancel = null;
            }
        }
        if (token.IsCancellationRequested || !ReferenceEquals(book, _book)) {
            return;
        }
        _pagination = result;
        _navigator.TotalPages = result.TotalPages;
        UpdateCurrentPage();
        _paginationReady.TrySetResult();
    }

    /// <summary>Forgets the page map when the book is closed.</summary>
    private void ClearPagination() {
        _paginationTimer.Stop();
        _paginationCancel?.Cancel();
        _paginationKey = "";
        _pagination = PaginationResult.Empty;
        _navigator.TotalPages = 0;
        _navigator.CurrentPage = 0;
        _navigator.CurrentChapterTitle = "";
    }

    /// <summary>
    /// Works out the page from where the reader is. Nothing is moved; this only updates the number shown. In paged
    /// view the pages drawn on screen are counted from the chapter's first page number, so every turn of the page
    /// is one page; in scrolling view the page is the one whose start was last passed.
    /// </summary>
    private void UpdateCurrentPage() {
        if ((_book is null) || !_shown) {
            return;
        }
        _navigator.CurrentChapterTitle = _book.Chapters[_chapterIndex].Title;
        if (PageBox.IsKeyboardFocusWithin) {
            // A number being typed is not overwritten by the page the reader is still on.
            return;
        }
        _navigator.CurrentPage = (Paged && _located && _pagination.ChapterRanges.TryGetValue(_chapterIndex, out ChapterPageRange range))
            ? Math.Min(range.StartPage + _page, range.EndPage)
            : _pagination.FindPageForLocation(_chapterIndex, _topBlock, _topOffset);
    }

    /// <summary>
    /// Goes to the start of a page. A page in another chapter has that chapter typeset first; the page script
    /// moves there once its fonts are in. The task completes when the page reports the new place.
    /// </summary>
    public async Task GoToPageAsync(int pageNumber) {
        if (_book is null) {
            return;
        }
        // A map that is being redone for a new size would send the reader to the wrong place.
        await Task.WhenAny(_paginationReady.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        if ((_book is null) || (_pagination.AnchorOf(pageNumber) is not PageAnchor anchor)) {
            return;
        }
        if ((anchor.ChapterIndex != _chapterIndex) || !_shown || !_located) {
            // The chapter is typeset at the anchor first; the page script reports once it is laid out.
            TaskCompletionSource loaded = new();
            _jumpDone = loaded;
            ShowChapter(anchor.ChapterIndex, 0, anchor.BlockIndex, true, anchor.CharacterOffset);
            await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(3)));
            if (!Paged || (_book is null) || (_chapterIndex != anchor.ChapterIndex)) {
                return;
            }
        }
        TaskCompletionSource done = new();
        _jumpDone = done;
        PostToReader(new Dictionary<string, object?> {
            ["t"] = "jumpToAnchor",
            ["id"] = _showId,
            ["domAnchorId"] = anchor.DomAnchorId,
            ["blockIndex"] = anchor.BlockIndex,
            ["characterOffset"] = anchor.CharacterOffset,
            ["relativeBlockProgress"] = anchor.RelativeBlockProgress,
            ["pageInChapter"] = Paged ? anchor.PageNumber - _pagination.ChapterRanges[anchor.ChapterIndex].StartPage : -1
        });
        Web.Focus();
        await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(3)));
    }

    /// <summary>Puts the cursor in the page box, ready for a number.</summary>
    private void FocusPageBox() {
        if ((_book is not null) && _navigator.HasPages) {
            PageBox.Focus();
            PageBox.SelectAll();
        }
    }

    private void OnPageBoxKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            e.Handled = true;
            string typed = PageBox.Text;
            Web.Focus();
            _navigator.JumpToPageCommand.Execute(typed);
        }
        else if (e.Key == Key.Escape) {
            e.Handled = true;
            Web.Focus();
        }
    }

    private void OnPageBoxLostFocus(object sender, RoutedEventArgs e) {
        // Whatever was typed and not entered gives way to the page being read.
        PageBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        UpdateCurrentPage();
    }
}