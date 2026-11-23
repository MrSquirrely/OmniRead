using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Reading the book aloud, one sentence at a time, with the spoken sentence tinted on the page.</summary>
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
        if ((_book is null) || _book.FixedLayout || !_shown) {
            ReadToggle.IsChecked = false;
            return;
        }
        // Reading starts at the selection when there is one, otherwise at the top of what is on screen.
        _readBlock = _selection?.Block ?? _topBlock;
        _readOffset = _selection?.Start ?? _topOffset;
        _reading = true;
        ReadToggle.IsChecked = true;
        _ = ReadOnAsync();
    }

    private void StopReading() {
        _reading = false;
        _readToken++;
        _narrator.Stop();
        ReadToggle.IsChecked = false;
        PostToReader(new Dictionary<string, object?> { ["t"] = "quiet" });
    }

    private async Task ReadOnAsync() {
        int token = ++_readToken;
        while (_reading && (token == _readToken) && (_book is not null) && _shown) {
            // Positions are kept as indexes, so the loop survives the chapter being typeset again mid-sentence.
            List<ContentBlock> blocks = _book.Chapters[_chapterIndex].Blocks;
            if (_readBlock >= blocks.Count) {
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
            if (TextOf(blocks[_readBlock]) is not string text) {
                _readBlock++;
                _readOffset = 0;
                continue;
            }
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
            ShowSpoken(_readBlock, sentence.Index, sentence.Length);
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

    /// <summary>Tints the sentence being spoken and turns to the page it is on.</summary>
    private void ShowSpoken(int block, int start, int length) {
        PostToReader(new Dictionary<string, object?> { ["t"] = "speak", ["id"] = _showId, ["b"] = block, ["s"] = start, ["n"] = length });
    }

}