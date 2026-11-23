using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

using Microsoft.Win32;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead;

/// <summary>
/// Turns a chapter or the whole book into audio files with the read-aloud voice. The window does not block the
/// reader: the export runs in the background and the book can be read meanwhile. Closing the window cancels it.
/// </summary>
public partial class AudiobookWindow : HandyControl.Controls.Window {
    private readonly Book _book;
    private readonly int _chapter;
    private readonly string _voiceId;
    private readonly double _rate;
    private CancellationTokenSource? _cancel;

    public AudiobookWindow(Book book, string title, int chapter, string voiceId, string voiceName, double rate) {
        _book = book;
        _chapter = chapter;
        _voiceId = voiceId;
        _rate = rate;
        InitializeComponent();
        BookText.Text = title;
        VoiceText.Text = Localizer.Format("Audiobook_Voice", voiceName, rate);
        ChapterChoice.Content = Localizer.Format("Audiobook_ThisChapter", book.Chapters[chapter].Title);
        BookChoice.Content = Localizer.Format("Audiobook_WholeBook", book.Chapters.Count);
        string name = string.Concat(title.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim().TrimEnd('.');
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), name);
        StatusText.Text = Localizer.Get("Audiobook_Ready");
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e) {
        OpenFolderDialog dialog = new() { Title = Localizer.Get("Audiobook_Folder") };
        if (dialog.ShowDialog(this) == true) {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e) {
        string folder = FolderBox.Text.Trim();
        if (folder.Length == 0) {
            return;
        }
        IReadOnlyList<int> chapters = (BookChoice.IsChecked == true) ? [.. Enumerable.Range(0, _book.Chapters.Count)] : [_chapter];
        bool encode = EncodedChoice.IsChecked == true;
        _cancel = new CancellationTokenSource();
        Choices.IsEnabled = false;
        StartButton.IsEnabled = false;
        CloseButton.Content = Localizer.Get("Dialog_Cancel");
        Progress<AudiobookProgress> progress = new(step => {
            Progress.Value = (step.Chapter - 1 + step.Fraction) / step.Chapters;
            StatusText.Text = Localizer.Format("Audiobook_Working", step.Chapter, step.Chapters, step.Title);
        });
        Stopwatch watch = Stopwatch.StartNew();
        try {
            int written = await Task.Run(() => AudiobookExporter.ExportAsync(_book, chapters, folder, _voiceId, _rate, encode, progress, _cancel.Token));
            Progress.Value = 1;
            StatusText.Text = (written == 0)
                ? Localizer.Get("Audiobook_Nothing")
                : Localizer.Format("Audiobook_Done", written, folder, Math.Max(1, Math.Round(watch.Elapsed.TotalMinutes)));
        }
        catch (OperationCanceledException) {
            StatusText.Text = Localizer.Get("Audiobook_Cancelled");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // No usable voice, a folder that cannot be written, or an encoder Windows does not have.
            StatusText.Text = Localizer.Format("Audiobook_Failed", ex.Message);
        }
        _cancel.Dispose();
        _cancel = null;
        Choices.IsEnabled = true;
        StartButton.IsEnabled = true;
        CloseButton.Content = Localizer.Get("Piper_Close");
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) {
        if (_cancel is not null) {
            // While an export runs the button is "Cancel"; the window stays to show what happened.
            _cancel.Cancel();
        }
        else {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e) {
        base.OnClosing(e);
        _cancel?.Cancel();
    }
}