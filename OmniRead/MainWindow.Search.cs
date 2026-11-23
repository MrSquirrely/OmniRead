using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using OmniRead.Localization;
using OmniRead.Models;

namespace OmniRead;

/// <summary>One place the search text was found. <see cref="Offset"/> is where the match starts in its block.</summary>
public sealed record SearchHit(int ChapterIndex, int BlockIndex, int Offset, string Before, string Match, string After, string Where);

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
                    string? text = TextOf(chapter.Blocks[b]);
                    if (text is null) {
                        continue;
                    }
                    for (int at = compare.IndexOf(text, query, CompareOptions.IgnoreCase);
                        (at >= 0) && (hits.Count < MaxHits);
                        at = compare.IndexOf(text, query, at + query.Length, CompareOptions.IgnoreCase)) {
                        int from = Math.Max(0, at - ContextLength);
                        int to = Math.Min(text.Length, at + query.Length + ContextLength);
                        hits.Add(new SearchHit(
                            c,
                            b,
                            at,
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
        // The chapter is shown with the match tinted and its page turned to.
        _pendingMatch = hit;
        ShowChapter(hit.ChapterIndex, 0, hit.BlockIndex, false, hit.Offset);
    }
}