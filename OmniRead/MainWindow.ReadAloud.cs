using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

using OmniRead.Localization;
using OmniRead.Rendering;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Reading the book aloud, one sentence at a time, with the spoken sentence selected on the page.</summary>
public partial class MainWindow {
    private readonly Narrator _narrator = new();
    private bool _reading;
    private bool _narratorTurningChapter;
    private int _readToken;
    private int _readBlock;
    private int _readOffset;

    [GeneratedRegex(@"\S.*?(?:[.!?…]+[""”’»)\]]*(?=\s|$)|\n|$)", RegexOptions.Singleline)]
    private static partial Regex Sentence();

    private void LoadVoices() {
        List<NarratorVoice> voices = [new NarratorVoice("", Localizer.Get("Settings_Voice_Default")), .. Narrator.Voices()];
        _suppressEvents = true;
        VoiceCombo.ItemsSource = voices;
        VoiceCombo.SelectedItem = voices.FirstOrDefault(voice => voice.Id == _settings.SpeechVoice) ?? voices[0];
        _suppressEvents = false;
    }

    private void OnVoiceSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (!_suppressEvents && (VoiceCombo.SelectedItem is NarratorVoice voice)) {
            _settings.SpeechVoice = voice.Id;
        }
    }

    private void OnPiperVoicesClick(object sender, RoutedEventArgs e) {
        StopReading();
        new PiperWindow { Owner = this }.ShowDialog();
        // Voices may have been added or removed.
        LoadVoices();
    }

    private void OnReadAloudClick(object sender, RoutedEventArgs e) {
        ToggleReadAloud();
    }

    private void ToggleReadAloud() {
        if (_reading) {
            StopReading();
        }
        else {
            StartReading();
        }
    }

    private void StartReading() {
        TextSelection? selection = CurrentSelection;
        TextPointer? from = (selection is { IsEmpty: false }) ? selection.Start : CurrentPosition();
        if ((_book is null) || _book.FixedLayout || (Locate(from) is not (int block, int offset))) {
            ReadToggle.IsChecked = false;
            return;
        }
        _readBlock = block;
        _readOffset = offset;
        _reading = true;
        ReadToggle.IsChecked = true;
        _ = ReadOnAsync();
    }

    private void StopReading() {
        _reading = false;
        _readToken++;
        _narrator.Stop();
        ReadToggle.IsChecked = false;
    }

    private async Task ReadOnAsync() {
        int token = ++_readToken;
        while (_reading && (token == _readToken) && (_book is not null) && (_built is not null)) {
            // Positions are kept as indexes, so the loop survives the chapter being typeset again mid-sentence.
            if (_readBlock >= _built.Elements.Count) {
                if (_chapterIndex >= (_book.Chapters.Count - 1)) {
                    break;
                }
                _narratorTurningChapter = true;
                ShowChapter(_chapterIndex + 1, 0);
                _narratorTurningChapter = false;
                _readBlock = 0;
                _readOffset = 0;
                continue;
            }
            if (_built.Elements[_readBlock] is not Paragraph paragraph) {
                _readBlock++;
                _readOffset = 0;
                continue;
            }
            string text = TextPositions.TextOf(paragraph);
            Match sentence = (_readOffset < text.Length) ? Sentence().Match(text, _readOffset) : Match.Empty;
            if (!sentence.Success) {
                _readBlock++;
                _readOffset = 0;
                continue;
            }
            _readOffset = sentence.Index + sentence.Length;
            if (!sentence.Value.Any(char.IsLetterOrDigit)) {
                continue;
            }
            ShowSpoken(paragraph, sentence.Index, sentence.Length);
            bool finished;
            try {
                finished = await _narrator.SpeakAsync(sentence.Value, _settings.SpeechVoice, _settings.SpeechRate);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                // No usable voice, or the audio device went away.
                if (_settings.SpeechVoice.StartsWith(Piper.VoicePrefix, StringComparison.Ordinal)) {
                    StopReading();
                    MessageBox.Show(this, Localizer.Format("Piper_Failed", ex.Message), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
            }
            if (!finished || (token != _readToken)) {
                return;
            }
        }
        if (token == _readToken) {
            StopReading();
        }
    }

    /// <summary>Selects the sentence being spoken and turns to the page it is on.</summary>
    private void ShowSpoken(Paragraph paragraph, int start, int length) {
        if (_built is null) {
            return;
        }
        TextPointer from = TextPositions.PointerAt(paragraph, start);
        TextPointer to = TextPositions.PointerAt(paragraph, start + length);
        if (Paged) {
            DynamicDocumentPaginator paginator = Paginator(_built);
            int page = paginator.GetPageNumber(from);
            if ((page >= 0) && ((page + 1) != PageViewer.MasterPageNumber)) {
                PageViewer.GoToPage(page + 1);
            }
        }
        else {
            paragraph.BringIntoView();
        }
        CurrentSelection?.Select(from, to);
    }
}