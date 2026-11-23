using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Rendering;

namespace OmniRead;

/// <summary>One place the search text was found. <see cref="Ordinal"/> is its position among the matches in its block.</summary>
public sealed record SearchHit(int ChapterIndex, int BlockIndex, int Ordinal, string Before, string Match, string After, string Where);

/// <summary>Searching the whole book.</summary>
public partial class MainWindow {
    private const int MaxHits = 500;
    private const int ContextLength = 44;

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private SearchHit? _pendingMatch;

    private void OpenSearch() {
        ShowSidePanel(SearchPanel, true);
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) {
        // Searching waits for a pause in typing.
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTimer;
        _searchTimer.Tick += OnSearchTimer;
        _searchTimer.Start();
    }

    private void OnSearchTimer(object? sender, EventArgs e) {
        _searchTimer.Stop();
        RunSearch();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            _searchTimer.Stop();
            RunSearch();
            if (SearchList.Items.Count > 0) {
                SearchList.SelectedIndex = 0;
            }
            e.Handled = true;
        }
    }

    private void RunSearch() {
        string query = SearchBox.Text.Trim();
        List<SearchHit> hits = [];
        if ((_book is not null) && (query.Length >= 2)) {
            CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
            for (int c = 0; (c < _book.Chapters.Count) && (hits.Count < MaxHits); c++) {
                Chapter chapter = _book.Chapters[c];
                for (int b = 0; (b < chapter.Blocks.Count) && (hits.Count < MaxHits); b++) {
                    string? text = chapter.Blocks[b] switch {
                        ParagraphBlock paragraph => paragraph.Text,
                        HeadingBlock heading => heading.Text,
                        _ => null
                    };
                    if (text is null) {
                        continue;
                    }
                    int ordinal = 0;
                    for (int at = compare.IndexOf(text, query, CompareOptions.IgnoreCase);
                        (at >= 0) && (hits.Count < MaxHits);
                        at = compare.IndexOf(text, query, at + query.Length, CompareOptions.IgnoreCase)) {
                        int from = Math.Max(0, at - ContextLength);
                        int to = Math.Min(text.Length, at + query.Length + ContextLength);
                        hits.Add(new SearchHit(
                            c,
                            b,
                            ordinal++,
                            ((from > 0) ? "…" : "") + text[from..at].Replace('\n', ' '),
                            text.Substring(at, query.Length),
                            text[(at + query.Length)..to].Replace('\n', ' ') + ((to < text.Length) ? "…" : ""),
                            chapter.Title));
                        if ((at + query.Length) >= text.Length) {
                            break;
                        }
                    }
                }
            }
        }
        _suppressEvents = true;
        SearchList.ItemsSource = hits;
        _suppressEvents = false;
        SearchStatus.Text = (query.Length < 2) ? Localizer.Get("Search_Hint")
            : (hits.Count == 0) ? Localizer.Get("Search_None")
            : (hits.Count >= MaxHits) ? Localizer.Format("Search_TooMany", MaxHits)
            : Localizer.Format("Search_Count", hits.Count);
    }

    private void OnSearchSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_suppressEvents || (SearchList.SelectedItem is not SearchHit hit)) {
            return;
        }
        _pendingMatch = hit;
        ShowChapter(hit.ChapterIndex, 0);
    }

    /// <summary>Selects the chosen search result and turns to its page, once the chapter has been laid out.</summary>
    private void ApplyPendingMatch() {
        if ((_pendingMatch is not SearchHit hit) || (_built is null)) {
            return;
        }
        if ((hit.ChapterIndex != _chapterIndex) || (hit.BlockIndex >= _built.Elements.Count)
            || (_built.Elements[hit.BlockIndex] is not TextElement element)) {
            _pendingMatch = null;
            return;
        }
        DynamicDocumentPaginator paginator = Paginator(_built);
        if (Paged && !paginator.IsPageCountValid) {
            return;
        }
        _pendingMatch = null;
        // The typeset text can differ slightly from the source (a list number in front, say), so the match is
        // found again here by counting occurrences.
        string text = TextPositions.TextOf(element);
        CompareInfo compare = CultureInfo.CurrentCulture.CompareInfo;
        int at = -1;
        for (int i = 0; i <= hit.Ordinal; i++) {
            at = ((at + 1) < text.Length) ? compare.IndexOf(text, hit.Match, at + 1, CompareOptions.IgnoreCase) : -1;
            if (at < 0) {
                return;
            }
        }
        TextPointer from = TextPositions.PointerAt(element, at);
        TextPointer to = TextPositions.PointerAt(element, at + hit.Match.Length);
        if (Paged) {
            int page = paginator.GetPageNumber(from);
            if (page >= 0) {
                PageViewer.GoToPage(page + 1);
            }
        }
        else {
            element.BringIntoView();
        }
        CurrentSelection?.Select(from, to);
    }
}