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
    private int _libraryVersion;

    private async Task LoadLibraryAsync() {
        int version = ++_libraryVersion;
        List<string> folders = [.. _settings.LibraryFolders];
        List<RecentBook> recent = [.. _settings.Recent];
        LibraryStatus.Text = Localizer.Get("Library_Scanning");
        List<LibraryItem> items;
        try {
            // Reading titles and covers from many files is slow the first time, so it stays off the UI thread.
            items = await Task.Run(() => Library.ScanAsync(folders, recent));
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
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LibraryItem.Shelf)));
        _suppressEvents = true;
        LibraryList.ItemsSource = view;
        LibraryList.SelectedItem = null;
        _suppressEvents = false;
        int folders = _settings.LibraryFolders.Count(Directory.Exists);
        LibraryStatus.Text = (_library.Count == 0)
            ? Localizer.Get((folders > 0) ? "Library_EmptyFolder" : "Welcome_Prompt")
            : (folders > 0)
                ? Localizer.Format("Library_Summary", _library.Count, _library.Select(static item => item.Shelf).Distinct().Count())
                : Localizer.Get("Library_Recent");
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