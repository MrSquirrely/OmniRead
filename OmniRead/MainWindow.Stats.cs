using System.Windows;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Book-wide page numbers, time-left estimates and the reading log.</summary>
public partial class MainWindow {
    private const double CharactersPerWord = 6;
    private const double LongestCountedPause = 240;
    private const double LargestCountedStep = 12000;

    private double[] _chapterCharacters = [];
    private double _lastCharacterPosition = -1;
    private int _lastChapter = -1;
    private DateTime _lastMove;

    /// <summary>Measures the text of each chapter; page estimates and reading time are based on it.</summary>
    private void MeasureBook() {
        _chapterCharacters = (_book is null) ? [] : [.. _book.Chapters.Select(static chapter => chapter.Blocks.Sum(static block => block switch {
            ParagraphBlock paragraph => (double)paragraph.Text.Length,
            HeadingBlock heading => heading.Text.Length,
            TableBlock table => table.Rows.Sum(static row => row.Sum(static cell => ContentBlock.PlainText(cell).Length)),
            _ => 0
        }))];
        _lastCharacterPosition = -1;
        _lastChapter = -1;
    }

    /// <summary>Share of the current chapter that lies before what is on screen.</summary>
    private double ChapterShare() {
        return Paged ? PageShare() : _fraction;
    }

    /// <summary>The right-hand status text: percentage and time left in the chapter. The page number has its own box beside it.</summary>
    private string DescribePosition(string percent) {
        if ((_book is null) || !_shown) {
            return "";
        }
        if (_book.FixedLayout) {
            PageText.ToolTip = TodayLine();
            return percent;
        }
        double pace = _settings.Stats.Pace;
        double share = ChapterShare();
        double chapterLeft = _chapterCharacters[_chapterIndex] * (1 - share);
        double bookLeft = chapterLeft + _chapterCharacters.Skip(_chapterIndex + 1).Sum();
        PageText.ToolTip = string.Join('\n',
            Localizer.Format("Stats_BookLeft", Duration(bookLeft / pace)),
            TodayLine(),
            Localizer.Format("Stats_Pace", Math.Round(pace * 60 / CharactersPerWord)));
        string timeLeft = Localizer.Format("Stats_ChapterLeft", Duration(chapterLeft / pace));
        return $"{percent}  ·  {timeLeft}";
    }

    private string TodayLine() {
        double goal = _settings.DailyGoalMinutes * 60;
        if (goal <= 0) {
            return Localizer.Format("Stats_Today", Duration(_settings.Stats.TodaySeconds));
        }
        // With a goal set, today's reading is shown against it, with the run of days the goal has been met.
        return Localizer.Format("Stats_TodayGoal", Duration(_settings.Stats.TodaySeconds), Duration(goal))
            + "\n" + Localizer.Format("Stats_Streak", _settings.Stats.Streak(goal));
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e) {
        new StatsWindow(_settings) { Owner = this }.ShowDialog();
        ScheduleSave();
    }

    private static string Duration(double seconds) {
        int minutes = (int)Math.Round(seconds / 60);
        return (minutes < 1) ? Localizer.Get("Format_UnderMinute")
            : (minutes < 60) ? Localizer.Format("Format_ShortMinutes", minutes)
            : Localizer.Format("Format_HoursMinutes", minutes / 60, minutes % 60);
    }

    /// <summary>Logs reading whenever the reader moves forward by a believable amount in a believable time.</summary>
    private void TrackReading() {
        if ((_book is null) || (_chapterCharacters.Length != _book.Chapters.Count)) {
            return;
        }
        DateTime now = DateTime.UtcNow;
        double position = _chapterCharacters.Take(_chapterIndex).Sum() + (_chapterCharacters[_chapterIndex] * ChapterShare());
        double seconds = Math.Min((now - _lastMove).TotalSeconds, LongestCountedPause);
        if ((_lastCharacterPosition >= 0) && (seconds >= 1)) {
            double step = position - _lastCharacterPosition;
            if (_book.FixedLayout) {
                if (_chapterIndex == (_lastChapter + 1)) {
                    _settings.Stats.AddTime(seconds);
                }
            }
            else if ((step > 0) && (step <= LargestCountedStep)) {
                _settings.Stats.Add(step, seconds);
            }
        }
        _lastCharacterPosition = position;
        _lastChapter = _chapterIndex;
        _lastMove = now;
    }
}