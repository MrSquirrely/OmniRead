using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

using OmniRead.Localization;
using OmniRead.Services;

using MessageBox = HandyControl.Controls.MessageBox;

namespace OmniRead;

/// <summary>Collections (shelves the reader makes) and correcting a book's title, author and cover.</summary>
public partial class MainWindow {
    /// <summary>Fills the "Collections" submenu of a book: one tick box per collection, then "New collection…".</summary>
    private void OnLibraryCollectionsOpened(object sender, RoutedEventArgs e) {
        if ((sender is not MenuItem menu) || (menu.DataContext is not LibraryItem book) || !ReferenceEquals(e.OriginalSource, menu)) {
            return;
        }
        menu.Items.Clear();
        foreach (BookCollection collection in _settings.Collections) {
            MenuItem entry = new() { Header = collection.Name, IsChecked = collection.Contains(book.Path) };
            entry.Click += (_, _) => {
                if (!collection.Contains(book.Path)) {
                    collection.Paths.Add(book.Path);
                }
                else {
                    collection.Paths.RemoveAll(path => SamePath(path, book.Path));
                }
                CollectionsChanged();
            };
            menu.Items.Add(entry);
        }
        if (_settings.Collections.Count > 0) {
            menu.Items.Add(new Separator());
        }
        MenuItem create = new() { Header = Localizer.Get("Collection_New") };
        create.Click += (_, _) => {
            if (AskCollectionName("") is string name) {
                _settings.Collections.Add(new BookCollection { Name = name, Paths = [book.Path] });
                CollectionsChanged();
            }
        };
        menu.Items.Add(create);
    }

    /// <summary>Asks for a collection's name; null when cancelled, left empty, or already used by another collection.</summary>
    private string? AskCollectionName(string current) {
        string? name = TextPrompt.Show(this, Localizer.Get("Collection_NameTitle"), Localizer.Get("Collection_NameLabel"), current)?.Trim();
        if (string.IsNullOrEmpty(name) || (name == current)) {
            return null;
        }
        if (_settings.Collections.Exists(collection => string.Equals(collection.Name, name, StringComparison.CurrentCultureIgnoreCase))) {
            MessageBox.Show(this, Localizer.Format("Collection_Exists", name), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        return name;
    }

    private void CollectionsChanged() {
        SettingsStore.Save(_settings);
        _ = LoadLibraryAsync();
    }

    /// <summary>Right-clicking a collection's heading offers to rename or delete it. Folder shelves have no menu.</summary>
    private void OnShelfRightClick(object sender, MouseButtonEventArgs e) {
        if (((FrameworkElement)sender).DataContext is not CollectionViewGroup { Name: ShelfGroup { IsCollection: true } shelf }
            || (_settings.Collections.Find(candidate => candidate.Name == shelf.Name) is not BookCollection collection)) {
            return;
        }
        e.Handled = true;
        ContextMenu menu = new() { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        MenuItem rename = new() { Header = Localizer.Get("Collection_Rename") };
        rename.Click += (_, _) => {
            if (AskCollectionName(collection.Name) is string name) {
                // The shelf keeps the place it was dragged to under its new name.
                int place = _settings.ShelfOrder.FindIndex(key => SameShelf(key, Library.CollectionKey(collection.Name)));
                if (place >= 0) {
                    _settings.ShelfOrder[place] = Library.CollectionKey(name);
                }
                collection.Name = name;
                CollectionsChanged();
            }
        };
        MenuItem delete = new() { Header = Localizer.Get("Collection_Delete") };
        delete.Click += (_, _) => {
            // Only the shelf goes; the books stay in their folders.
            if (MessageBox.Show(this, Localizer.Format("Collection_DeleteQuestion", collection.Name), "OmniRead",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) {
                _settings.Collections.Remove(collection);
                _settings.ShelfOrder.RemoveAll(key => SameShelf(key, Library.CollectionKey(collection.Name)));
                CollectionsChanged();
            }
        };
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    /// <summary>Opens the dialog for correcting a book's title, author and cover.</summary>
    private void OnLibraryMenuDetails(object sender, RoutedEventArgs e) {
        if (MenuBook(sender) is not LibraryItem book) {
            return;
        }
        BookDetailsWindow dialog = new(book, _settings.Overrides.GetValueOrDefault(book.Path)) { Owner = this };
        if (dialog.ShowDialog() != true) {
            return;
        }
        if (dialog.Result is BookOverride details) {
            _settings.Overrides[book.Path] = details;
        }
        else {
            _settings.Overrides.Remove(book.Path);
        }
        SettingsStore.Save(_settings);
        _ = LoadLibraryAsync();
    }
}