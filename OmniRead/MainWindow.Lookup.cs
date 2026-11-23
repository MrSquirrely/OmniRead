using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Footnote pop-ups, links inside the book, dictionary lookup and the reading area's right-click menu.</summary>
public partial class MainWindow {
    private const int MaxNoteLength = 1200;

    private Action? _infoAction;

    [GeneratedRegex(@"^[\[(]?(?:\d{1,4}|[*†‡§]+|[a-z])[\])]?$", RegexOptions.IgnoreCase)]
    private static partial Regex NoteMark();

    /// <summary>Shows a small card at the pointer. It closes when the reader clicks anywhere else.</summary>
    private void ShowInfo(string title, string body, string footer = "", string? actionLabel = null, Action? action = null) {
        InfoTitle.Text = title;
        InfoBody.Text = body;
        InfoFooter.Text = footer;
        InfoFooter.Visibility = (footer.Length > 0) ? Visibility.Visible : Visibility.Collapsed;
        _infoAction = action;
        InfoAction.Content = actionLabel;
        InfoAction.Visibility = (action is not null) ? Visibility.Visible : Visibility.Collapsed;
        InfoPopup.IsOpen = true;
    }

    private void OnInfoActionClick(object sender, RoutedEventArgs e) {
        InfoPopup.IsOpen = false;
        _infoAction?.Invoke();
    }

    /// <summary>A link clicked on the page. Links into the book open notes in place or are followed; others go to the browser.</summary>
    private void OnLink(string href, string linkText, bool superscript) {
        if (!href.StartsWith("book:", StringComparison.Ordinal)) {
            OpenExternal(href);
            return;
        }
        if (_book is null) {
            return;
        }
        string key = href["book:".Length..];
        if (!_book.Anchors.TryGetValue(key, out BookAnchor target)) {
            int fragment = key.IndexOf('#');
            // A link to a part of a file that could not be pinned down still leads to that file.
            if ((fragment <= 0) || !_book.Anchors.TryGetValue(key[..fragment], out target)) {
                return;
            }
        }
        void Go() => ShowChapter(target.ChapterIndex, 0, target.BlockIndex, true);
        string mark = linkText.Trim();
        bool noteReference = superscript || NoteMark().IsMatch(mark);
        string note = noteReference ? NoteText(target) : "";
        if (note.Length == 0) {
            Go();
            return;
        }
        ShowInfo(Localizer.Format("Note_Title", mark), note, "", Localizer.Get("Note_GoTo"), Go);
    }

    /// <summary>The text of the note a reference points at: its block, plus the next when the first is only a number.</summary>
    private string NoteText(BookAnchor target) {
        List<ContentBlock> blocks = _book!.Chapters[target.ChapterIndex].Blocks;
        string text = "";
        for (int i = target.BlockIndex; (i < blocks.Count) && (i <= (target.BlockIndex + 1)); i++) {
            string part = TextOf(blocks[i]) ?? "";
            text = (text + " " + part).Trim();
            if (text.Length > 6) {
                break;
            }
        }
        text = text.TrimEnd('↩', '↑', ' ', '︎');
        return (text.Length > MaxNoteLength) ? text[..MaxNoteLength].TrimEnd() + "…" : text;
    }

    private async Task LookUpSelectionAsync() {
        string? word = (_selection is not null) ? WordLookup.FirstWord(_selection.Text) : null;
        if (word is null) {
            return;
        }
        string source = Localizer.Format("Lookup_Source", WordLookup.SourceName);
        ShowInfo(word, Localizer.Get("Lookup_Working"), source);
        string body = await WordLookup.DefineAsync(word) ?? Localizer.Get("Lookup_NotFound");
        // The card may have been closed, or reused for something else, while the answer was on its way.
        if (InfoPopup.IsOpen && (InfoTitle.Text == word)) {
            InfoBody.Text = body;
        }
    }

    private void OnReaderMenuOpened(object sender, RoutedEventArgs e) {
        bool hasSelection = _selection is not null;
        foreach (MenuItem item in ((ContextMenu)sender).Items.OfType<MenuItem>()) {
            if (item.Tag is "selection") {
                item.IsEnabled = hasSelection;
            }
        }
    }

    private void OnMenuLookUpClick(object sender, RoutedEventArgs e) {
        _ = LookUpSelectionAsync();
    }

    private void OnMenuCopyClick(object sender, RoutedEventArgs e) {
        if (_selection is not null) {
            Clipboard.SetText(_selection.Text);
        }
    }

    private void OnMenuReadFromHereClick(object sender, RoutedEventArgs e) {
        StopReading();
        StartReading();
    }
}