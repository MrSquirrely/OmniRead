using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Book-wide page numbers, time-left estimates and the reading log.</summary>
public partial class MainWindow {
    private const double CharactersPerWord = 6;
    private const double LongestCountedPause = 240;
    private const double LargestCountedStep = 12000;

    /// <summary>Page counts of the chapters that have been laid out at the current size and type settings.</summary>
    private readonly Dictionary<int, int> _pageCounts = [];
    private string _layoutKey = "";
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
        _pageCounts.Clear();
        _lastCharacterPosition = -1;
        _lastChapter = -1;
    }

    /// <summary>Forgets measured page counts when something that changes pagination has changed.</summary>
    private void CheckLayout() {
        Palette palette = ThemeManager.Current;
        string key = FormattableString.Invariant(
            $"{Viewport.Width:0}x{Viewport.Height:0}|{_settings.FontName}|{_settings.FontOrigin}|{_settings.FontSize}|{_settings.LineSpacing}|{_settings.PageWidth}|{_settings.Columns}|{_settings.ViewMode}|{_settings.Justify}|{_settings.Hyphenate}|{_settings.DropCaps}|{palette.FontScale}|{palette.LineBoost}");
        if (key != _layoutKey) {
            _layoutKey = key;
            _pageCounts.Clear();
        }
    }

    /// <summary>Share of the current chapter that lies before what is on screen.</summary>
    private double ChapterShare() {
        return Paged ? PageShare() : _fraction;
    }

    /// <summary>The right-hand status text: page in the whole book, percentage and time left in the chapter.</summary>
    private string DescribePosition(string percent) {
        if ((_book is null) || !_shown) {
            return "";
        }
        if (_book.FixedLayout) {
            PageText.ToolTip = TodayLine();
            return Localizer.Format("Status_Page", _chapterIndex + 1, _book.Chapters.Count, percent);
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
        if (!Paged || (_pages == 0)) {
            return $"{percent}  ·  {timeLeft}";
        }

        _pageCounts[_chapterIndex] = _pages;
        // Chapters not yet laid out are estimated from their length, at the pages-per-character of those that have been.
        double knownPages = _pageCounts.Values.Sum();
        double knownCharacters = _pageCounts.Keys.Sum(index => Math.Max(1, _chapterCharacters[index]));
        double pagesPerCharacter = (knownCharacters > 0) ? knownPages / knownCharacters : 0;
        int Pages(int chapter) => _pageCounts.TryGetValue(chapter, out int counted) ? counted
            : Math.Max(1, (int)Math.Round(_chapterCharacters[chapter] * pagesPerCharacter));
        int before = Enumerable.Range(0, _chapterIndex).Sum(Pages);
        int total = Enumerable.Range(0, _book.Chapters.Count).Sum(Pages);
        bool exact = _pageCounts.Count >= _book.Chapters.Count;
        string totalText = exact ? total.ToString() : Localizer.Format("Format_Approximate", total);
        return Localizer.Format("Status_Page", before + _page + 1, totalText, percent) + $"  ·  {timeLeft}";
    }

    private string TodayLine() {
        return Localizer.Format("Stats_Today", Duration(_settings.Stats.TodaySeconds));
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