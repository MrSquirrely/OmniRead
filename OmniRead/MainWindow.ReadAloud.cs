using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

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

    /// <summary>Where the sentence being spoken starts; "previous sentence" is worked out from here.</summary>
    private int _spokenBlock;
    private int _spokenStart;

    private readonly DispatcherTimer _sleepTimer = new();
    private DateTime? _sleepAt;
    private bool _sleepAtChapterEnd;
    private bool _sleepDue;

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
        ReadControls.Visibility = Visibility.Visible;
        SetPaused(false);
        _ = ReadOnAsync();
    }

    private void StopReading() {
        _reading = false;
        _readToken++;
        _narrator.Stop();
        SetPaused(false);
        SetSleep(null, false);
        ReadToggle.IsChecked = false;
        ReadControls.Visibility = Visibility.Collapsed;
        PostToReader(new Dictionary<string, object?> { ["t"] = "quiet" });
    }

    private async Task ReadOnAsync() {
        int token = ++_readToken;
        while (_reading && (token == _readToken) && (_book is not null) && _shown) {
            // Positions are kept as indexes, so the loop survives the chapter being typeset again mid-sentence.
            List<ContentBlock> blocks = _book.Chapters[_chapterIndex].Blocks;
            if (_readBlock >= blocks.Count) {
                if (_sleepAtChapterEnd || (_chapterIndex >= (_book.Chapters.Count - 1))) {
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
            _spokenBlock = _readBlock;
            _spokenStart = sentence.Index;
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
            if (_sleepDue) {
                // The sleep timer ran out during this sentence; it was allowed to finish.
                break;
            }
        }
        if (token == _readToken) {
            StopReading();
        }
    }

    // ---------------------------------------------------------------- pause, skip, sleep

    private void SetPaused(bool paused) {
        _narrator.Paused = paused;
        ReadPauseButton.Content = paused ? "" : "";
        ReadPauseButton.ToolTip = Localizer.Get(paused ? "Read_Resume" : "Read_Pause");
    }

    private void TogglePause() {
        if (!_reading) {
            return;
        }
        if (_narrator.Paused && _sleepDue) {
            // The timer ran out while paused; resuming would only speak the rest of one sentence.
            StopReading();
            return;
        }
        SetPaused(!_narrator.Paused);
    }

    /// <summary>Jumps to the next sentence (1) or back to the one before the sentence being spoken (-1).</summary>
    private void SkipSentence(int direction) {
        if (!_reading || (_book is null)) {
            return;
        }
        if (direction < 0) {
            (_readBlock, _readOffset) = PreviousSentence(_book.Chapters[_chapterIndex].Blocks);
        }
        // Going forward needs no change: the reading position is already past the current sentence.
        SetPaused(false);
        _ = ReadOnAsync();
    }

    /// <summary>The start of the sentence before the one being spoken, or of that one at the top of the chapter.</summary>
    private (int Block, int Start) PreviousSentence(List<ContentBlock> blocks) {
        for (int block = Math.Min(_spokenBlock, blocks.Count - 1); block >= 0; block--) {
            if (TextOf(blocks[block]) is not string text) {
                continue;
            }
            int limit = (block == _spokenBlock) ? _spokenStart : int.MaxValue;
            Match? last = Sentence().Matches(text)
                .LastOrDefault(sentence => (sentence.Index < limit) && sentence.Value.Any(char.IsLetterOrDigit));
            if (last is not null) {
                return (block, last.Index);
            }
        }
        return (_spokenBlock, _spokenStart);
    }

    /// <summary>Sets the sleep timer: a number of minutes, the end of the chapter, or neither to turn it off.</summary>
    private void SetSleep(int? minutes, bool chapterEnd) {
        _sleepTimer.Stop();
        _sleepTimer.Tick -= OnSleepTimer;
        _sleepDue = false;
        _sleepAtChapterEnd = chapterEnd;
        _sleepAt = null;
        if (minutes is int wait) {
            _sleepAt = DateTime.Now.AddMinutes(wait);
            _sleepTimer.Interval = TimeSpan.FromMinutes(wait);
            _sleepTimer.Tick += OnSleepTimer;
            _sleepTimer.Start();
        }
        SleepButton.IsChecked = (minutes is not null) || chapterEnd;
    }

    private void OnSleepTimer(object? sender, EventArgs e) {
        _sleepTimer.Stop();
        if (_narrator.Paused) {
            StopReading();
        }
        else {
            // Reading stops at the end of the sentence being spoken.
            _sleepDue = true;
        }
    }

    private void OnSleepClick(object sender, RoutedEventArgs e) {
        // The click toggled the button; it shows whether a timer is set, not whether it was pressed.
        SleepButton.IsChecked = (_sleepAt is not null) || _sleepAtChapterEnd;
        ContextMenu menu = new() { PlacementTarget = SleepButton, Placement = PlacementMode.Bottom };
        if (_sleepAt is DateTime due) {
            int left = Math.Max(1, (int)Math.Ceiling((due - DateTime.Now).TotalMinutes));
            menu.Items.Add(new MenuItem { Header = Localizer.Format("Sleep_Remaining", left), IsEnabled = false });
            menu.Items.Add(new Separator());
        }
        void Add(string header, bool current, int? minutes, bool chapterEnd) {
            MenuItem item = new() { Header = header, IsChecked = current };
            item.Click += (_, _) => SetSleep(minutes, chapterEnd);
            menu.Items.Add(item);
        }
        Add(Localizer.Get("Sleep_Off"), (_sleepAt is null) && !_sleepAtChapterEnd, null, false);
        foreach (int minutes in (int[])[15, 30, 45, 60]) {
            Add(Localizer.Format("Sleep_Minutes", minutes), false, minutes, false);
        }
        Add(Localizer.Get("Sleep_ChapterEnd"), _sleepAtChapterEnd, null, true);
        menu.IsOpen = true;
    }

    private void OnReadPauseClick(object sender, RoutedEventArgs e) {
        TogglePause();
    }

    private void OnReadPreviousClick(object sender, RoutedEventArgs e) {
        SkipSentence(-1);
    }

    private void OnReadNextClick(object sender, RoutedEventArgs e) {
        SkipSentence(1);
    }

    /// <summary>Tints the sentence being spoken and turns to the page it is on.</summary>
    private void ShowSpoken(int block, int start, int length) {
        PostToReader(new Dictionary<string, object?> { ["t"] = "speak", ["id"] = _showId, ["b"] = block, ["s"] = start, ["n"] = length });
    }

}