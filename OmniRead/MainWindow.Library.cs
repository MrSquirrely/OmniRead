using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

using Microsoft.Win32;

using OmniRead.Localization;
using OmniRead.Services;

namespace OmniRead;

/// <summary>The shelves of books shown when nothing is open: one shelf per library folder.</summary>
public partial class MainWindow {
    private List<LibraryItem> _library = [];
    private ListCollectionView? _libraryView;
    private int _libraryVersion;

    private async Task LoadLibraryAsync() {
        int version = ++_libraryVersion;
        List<string> folders = [.. _settings.LibraryFolders];
        List<RecentBook> recent = [.. _settings.Recent];
        // Copies, so the scan on another thread never sees a list while it is being edited here.
        List<BookCollection> collections = [.. _settings.Collections.Select(static collection =>
            new BookCollection { Name = collection.Name, Paths = [.. collection.Paths] })];
        Dictionary<string, BookOverride> overrides = new(_settings.Overrides, StringComparer.OrdinalIgnoreCase);
        LibraryStatus.Text = Localizer.Get("Library_Scanning");
        List<LibraryItem> items;
        try {
            // Reading titles and covers from many files is slow the first time, so it stays off the UI thread.
            items = await Task.Run(() => Library.ScanAsync(folders, recent, collections, overrides));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // The shelves are a convenience; a failed scan must not stop books from being opened.
            LibraryStatus.Text = ex.Message;
            return;
        }
        if (version != _libraryVersion) {
            return;
        }
        _library = items;
        ShowLibrary();
    }

    private void ShowLibrary() {
        // Grouping keeps the order of the list, so shelves appear in the order the folders were added.
        ListCollectionView view = new(Library.Sort(_library, _settings.LibrarySort).ToList());
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LibraryItem.Group)));
        view.Filter = MatchesLibraryFilter;
        _libraryView = view;
        // The format list offers only the formats that are on the shelves, keeping the current choice when it still exists.
        string? format = SelectedLibraryFormat();
        List<string> formats = [Localizer.Get("Library_Filter_AllFormats"),
            .. _library.Select(static item => item.Format).Distinct().Order(StringComparer.Ordinal)];
        _suppressEvents = true;
        LibraryFormatCombo.ItemsSource = formats;
        LibraryFormatCombo.SelectedIndex = Math.Max(0, (format is null) ? 0 : formats.IndexOf(format));
        LibraryList.ItemsSource = view;
        LibraryList.SelectedItem = null;
        _suppressEvents = false;
        view.Refresh();
        UpdateLibraryStatus();
    }

    private void UpdateLibraryStatus() {
        int folders = _settings.LibraryFolders.Count(Directory.Exists);
        // A book in a collection stands on two shelves but is still one book.
        int books = _library.Select(static item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int shown = (_libraryView is null) ? books
            : _libraryView.Cast<LibraryItem>().Select(static item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        LibraryStatus.Text = (_library.Count == 0)
            ? Localizer.Get((folders > 0) ? "Library_EmptyFolder" : "Welcome_Prompt")
            : (shown < books)
                ? (shown == 0) ? Localizer.Get("Library_NoMatches") : Localizer.Format("Library_Filtered", shown, books)
            : ((folders > 0) || (_settings.Collections.Count > 0))
                ? Localizer.Format("Library_Summary", books, _library.Select(static item => item.Group).Distinct().Count())
                : Localizer.Get("Library_Recent");
    }

    /// <summary>The format chosen in the filter, or null for all formats (the first entry).</summary>
    private string? SelectedLibraryFormat() {
        return (LibraryFormatCombo.SelectedIndex > 0) ? LibraryFormatCombo.SelectedItem as string : null;
    }

    /// <summary>Whether a book passes the search box and the two filters.</summary>
    private bool MatchesLibraryFilter(object candidate) {
        if (candidate is not LibraryItem item) {
            return false;
        }
        string query = LibrarySearchBox.Text.Trim();
        if ((query.Length > 0)
            && !item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            && !item.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase)) {
            return false;
        }
        if ((SelectedLibraryFormat() is string format) && (item.Format != format)) {
            return false;
        }
        return (LibraryStateCombo.SelectedValue is not ReadState state) || (state == ReadState.Any) || (item.State == state);
    }

    private void OnLibraryFilterChanged(object sender, RoutedEventArgs e) {
        if (_suppressEvents || (_libraryView is null)) {
            return;
        }
        _libraryView.Refresh();
        UpdateLibraryStatus();
    }

    /// <summary>Closes the open book and returns to the shelves.</summary>
    private void ShowLibraryView() {
        if (_book is null) {
            return;
        }
        StopReading();
        SaveProgress();
        SettingsStore.Save(_settings);
        _book = null;
        _shown = false;
        ClearPagination();
        Web.Visibility = Visibility.Collapsed;
        TocList.ItemsSource = null;
        LoadAnnotations();
        Title = "OmniRead";
        TitleText.Text = "";
        PreviousChapterButton.IsEnabled = false;
        NextChapterButton.IsEnabled = false;
        UpdateStatus();
        WelcomePanel.Visibility = Visibility.Visible;
        _ = LoadLibraryAsync();
    }

    private void OnLibraryClick(object sender, RoutedEventArgs e) {
        ShowLibraryView();
    }

    /// <summary>Opens a menu for adding folders and removing the ones already in the library.</summary>
    private void OnLibraryFoldersClick(object sender, RoutedEventArgs e) {
        ContextMenu menu = new() { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        MenuItem add = new() { Header = Localizer.Get("Library_AddFolder") };
        add.Click += (_, _) => AddLibraryFolders();
        menu.Items.Add(add);
        if (_settings.LibraryFolders.Count > 0) {
            menu.Items.Add(new Separator());
        }
        foreach (string folder in _settings.LibraryFolders) {
            MenuItem remove = new() { Header = Localizer.Format("Library_RemoveFolder", Library.FolderName(folder)), ToolTip = folder };
            remove.Click += (_, _) => {
                // Only the shelf goes; the folder and its books stay where they are.
                _settings.LibraryFolders.Remove(folder);
                LibraryFoldersChanged();
            };
            menu.Items.Add(remove);
        }
        menu.IsOpen = true;
    }

    private void AddLibraryFolders() {
        OpenFolderDialog dialog = new() { Title = Localizer.Get("Library_AddFolder"), Multiselect = true };
        if (dialog.ShowDialog(this) != true) {
            return;
        }
        foreach (string folder in dialog.FolderNames) {
            if (!_settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) {
                _settings.LibraryFolders.Add(folder);
            }
        }
        LibraryFoldersChanged();
    }

    private void LibraryFoldersChanged() {
        SettingsStore.Save(_settings);
        _ = LoadLibraryAsync();
    }
}