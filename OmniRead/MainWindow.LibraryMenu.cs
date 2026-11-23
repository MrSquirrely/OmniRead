using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using OmniRead.Services;

namespace OmniRead;

/// <summary>Opening books from the shelf and the right-click menu on each one.</summary>
public partial class MainWindow {
    /// <summary>Opens the book under a left click. Right clicks only bring up the menu.</summary>
    private void OnLibraryMouseUp(object sender, MouseButtonEventArgs e) {
        if ((e.OriginalSource is DependencyObject source)
            && (ItemsControl.ContainerFromElement(LibraryList, source) is ListBoxItem { DataContext: LibraryItem item })) {
            _ = OpenAsync(item.Path);
        }
    }

    private void OnLibraryKeyDown(object sender, KeyEventArgs e) {
        if ((e.Key == Key.Enter) && (LibraryList.SelectedItem is LibraryItem item)) {
            e.Handled = true;
            _ = OpenAsync(item.Path);
        }
    }

    private static LibraryItem? MenuBook(object sender) {
        return (sender as FrameworkElement)?.DataContext as LibraryItem;
    }

    private void OnLibraryMenuOpen(object sender, RoutedEventArgs e) {
        if (MenuBook(sender) is LibraryItem item) {
            _ = OpenAsync(item.Path);
        }
    }

    private void OnLibraryMenuShowInFolder(object sender, RoutedEventArgs e) {
        if (MenuBook(sender) is not LibraryItem item) {
            return;
        }
        try {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) {
            // Explorer could not be started; there is nothing useful to add.
        }
    }

    private void OnLibraryMenuCopyPath(object sender, RoutedEventArgs e) {
        if (MenuBook(sender) is LibraryItem item) {
            Clipboard.SetText(item.Path);
        }
    }

    /// <summary>Clears the reading position and "last read" date; the file and its bookmarks are left alone.</summary>
    private void OnLibraryMenuForget(object sender, RoutedEventArgs e) {
        if (MenuBook(sender) is not LibraryItem item) {
            return;
        }
        _settings.Recent.RemoveAll(recent => SamePath(recent.Path, item.Path));
        SettingsStore.Save(_settings);
        _ = LoadLibraryAsync();
    }
}