using System.Globalization;
using System.Text.Json.Serialization;

namespace OmniRead.Services;

/// <summary>How much, and how fast, the reader reads. Saved with the settings.</summary>
public sealed class ReadingStats {
    private const double DefaultPace = 25;
    private const double SecondsBeforeTrusted = 120;
    private const int DaysKept = 90;

    public double CharactersRead { get; set; }

    public double SecondsRead { get; set; }

    /// <summary>Seconds spent reading on each day, keyed by date (yyyy-MM-dd).</summary>
    public Dictionary<string, double> DailySeconds { get; set; } = [];

    /// <summary>Characters per second; a typical pace (about 250 words a minute) until enough has been measured.</summary>
    [JsonIgnore]
    public double Pace => (SecondsRead >= SecondsBeforeTrusted) ? Math.Clamp(CharactersRead / SecondsRead, 3, 120) : DefaultPace;

    [JsonIgnore]
    public double TodaySeconds => DailySeconds.GetValueOrDefault(Key(DateTime.Now));

    /// <summary>Records text that was read and the time it took.</summary>
    public void Add(double characters, double seconds) {
        CharactersRead += characters;
        SecondsRead += seconds;
        AddTime(seconds);
    }

    /// <summary>Records reading time that cannot be measured in characters (PDF pages).</summary>
    public void AddTime(double seconds) {
        string today = Key(DateTime.Now);
        DailySeconds[today] = DailySeconds.GetValueOrDefault(today) + seconds;
        if (DailySeconds.Count > DaysKept) {
            foreach (string old in DailySeconds.Keys.Order(StringComparer.Ordinal).Take(DailySeconds.Count - DaysKept).ToList()) {
                DailySeconds.Remove(old);
            }
        }
    }

    private static string Key(DateTime day) {
        return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}