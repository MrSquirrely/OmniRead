using System.Windows;
using System.Windows.Media;

using Microsoft.Win32;

using OmniRead.Localization;
using OmniRead.Services;

using MessageBox = HandyControl.Controls.MessageBox;

namespace OmniRead;

/// <summary>
/// Corrects a book's title, author and cover as the library shows them. The book file is never changed: the
/// corrections are kept with the settings.
/// </summary>
public partial class BookDetailsWindow : HandyControl.Controls.Window {
    private readonly ImageSource? _shownCover;
    private string? _cover;

    public BookDetailsWindow(LibraryItem book, BookOverride? current) {
        InitializeComponent();
        TitleBox.Text = book.Title;
        AuthorBox.Text = book.Author;
        PathText.Text = book.Path;
        _shownCover = book.Cover;
        _cover = current?.Cover;
        CoverImage.Source = book.Cover;
        OwnCoverButton.IsEnabled = _cover is not null;
        Loaded += (_, _) => {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    /// <summary>The details to keep after OK, or null when the book should go back to what its file says.</summary>
    public BookOverride? Result { get; private set; }

    private void OnChooseCoverClick(object sender, RoutedEventArgs e) {
        OpenFileDialog dialog = new() {
            Title = Localizer.Get("Details_ChooseCover"),
            Filter = $"{Localizer.Get("Details_Images")}|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp"
        };
        if (dialog.ShowDialog(this) != true) {
            return;
        }
        if (Library.SaveCustomCover(dialog.FileName) is not string name) {
            MessageBox.Show(this, Localizer.Get("Details_BadImage"), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _cover = name;
        CoverImage.Source = Library.LoadCustomCover(name);
        OwnCoverButton.IsEnabled = true;
    }

    private void OnOwnCoverClick(object sender, RoutedEventArgs e) {
        // The book's own cover comes back with the next look at the shelves; until then the picture is cleared
        // unless the one on show already was the book's own.
        CoverImage.Source = (_cover is null) ? _shownCover : null;
        _cover = null;
        OwnCoverButton.IsEnabled = false;
    }

    private void OnResetClick(object sender, RoutedEventArgs e) {
        Result = null;
        DialogResult = true;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e) {
        Result = new BookOverride { Title = TitleBox.Text.Trim(), Author = AuthorBox.Text.Trim(), Cover = _cover };
        DialogResult = true;
    }
}