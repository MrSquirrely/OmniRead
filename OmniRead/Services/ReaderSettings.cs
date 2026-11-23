using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using OmniRead.Localization;

namespace OmniRead.Services;

public enum ViewMode {
    Paged,
    Scroll
}

public enum ColumnMode {
    Auto,
    One,
    Two
}

public enum AppTheme {
    System,
    Light,
    Dark
}

public enum LibrarySort {
    LastRead,
    Title,
    Author
}

public enum FontOrigin {
    Bundled,
    Folder,
    System
}

public sealed class RecentBook {
    public string Path { get; set; } = "";

    public string Title { get; set; } = "";

    public int ChapterIndex { get; set; }

    public double Fraction { get; set; }

    public double Progress { get; set; }

    public DateTime LastOpened { get; set; }

    /// <summary>Set once the last page of the book has been reached.</summary>
    public bool Finished { get; set; }

    [JsonIgnore]
    public string ProgressText => Localizer.Format("Format_Percent", Progress);
}

/// <summary>A shelf made by the reader. A book may be in any number of them.</summary>
public sealed class BookCollection {
    public string Name { get; set; } = "";

    public List<string> Paths { get; set; } = [];

    public bool Contains(string path) {
        return Paths.Contains(path, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Details of a book corrected by hand. Empty text means "as the file says".</summary>
public sealed class BookOverride {
    public string Title { get; set; } = "";

    public string Author { get; set; } = "";

    /// <summary>File name of a chosen cover in the custom covers folder, or null to keep the book's own.</summary>
    public string? Cover { get; set; }
}

public sealed class ReaderSettings : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    [SettingCategory("Settings_Text"), SettingName("Settings_Font"), SettingOrder(10)]
    [SettingHelp("Settings_FontsHint")]
    [Editor(typeof(FontEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public string FontName { get; set => Set(ref field, value); } = "Gentium Book Plus";

    [Browsable(false)]
    public FontOrigin FontOrigin { get; set => Set(ref field, value); } = FontOrigin.Bundled;

    [SettingCategory("Settings_Text"), SettingName("Settings_Size"), SettingOrder(11)]
    [SettingRange(12, 40, 1, "Format_Pixels")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double FontSize { get; set => Set(ref field, Math.Clamp(Math.Round(value), 10, 48)); } = 19;

    [SettingCategory("Settings_Text"), SettingName("Settings_LineSpacing"), SettingOrder(12)]
    [SettingRange(1.1, 2.4, 0.05, "Format_Ratio")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double LineSpacing { get; set => Set(ref field, Math.Clamp(Math.Round(value, 2), 1, 2.6)); } = 1.5;

    /// <summary>Maximum width of a text column, in em.</summary>
    [SettingCategory("Settings_Text"), SettingName("Settings_PageWidth"), SettingOrder(13)]
    [SettingRange(22, 60, 1, "Format_Em")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double PageWidth { get; set => Set(ref field, Math.Clamp(Math.Round(value), 20, 70)); } = 34;

    [SettingCategory("Settings_Layout"), SettingName("Settings_Justify"), SettingOrder(23)]
    public bool Justify { get; set => Set(ref field, value); } = true;

    [SettingCategory("Settings_Layout"), SettingName("Settings_Hyphenate"), SettingOrder(24)]
    public bool Hyphenate { get; set => Set(ref field, value); } = true;

    [SettingCategory("Settings_Layout"), SettingName("Settings_DropCaps"), SettingOrder(25)]
    public bool DropCaps { get; set => Set(ref field, value); } = true;

    /// <summary>Restructure plain documents (text, Markdown, HTML, Word, RTF) into a typeset book.</summary>
    [SettingCategory("Settings_Layout"), SettingName("Settings_BookFormat"), SettingOrder(22)]
    [SettingHelp("Settings_BookFormatHint")]
    public bool BookFormatting { get; set => Set(ref field, value); } = true;

    [SettingCategory("Settings_Layout"), SettingName("Settings_View"), SettingOrder(20)]
    [Editor(typeof(ViewModeEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public ViewMode ViewMode { get; set => Set(ref field, value); } = ViewMode.Paged;

    [SettingCategory("Settings_Layout"), SettingName("Settings_Columns"), SettingOrder(21)]
    [Editor(typeof(ColumnsEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public ColumnMode Columns { get; set => Set(ref field, value); } = ColumnMode.Auto;

    [SettingCategory("Settings_Appearance"), SettingName("Settings_Theme"), SettingOrder(60)]
    [SettingHelp("Settings_ThemeHint")]
    [Editor(typeof(ThemeEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public AppTheme Theme { get; set => Set(ref field, value); } = AppTheme.System;

    /// <summary>Interface language as a culture code such as "es"; empty to follow Windows.</summary>
    [SettingCategory("Settings_Appearance"), SettingName("Settings_Language"), SettingOrder(61)]
    [SettingHelp("Settings_LanguageHint")]
    [Editor(typeof(LanguageEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public string Language { get; set => Set(ref field, value); } = "";

    [SettingCategory("Settings_EyeComfort"), SettingName("Settings_EyeComfortMode"), SettingOrder(30)]
    public bool EyeComfort { get; set => Set(ref field, value); }

    [SettingCategory("Settings_EyeComfort"), SettingName("Settings_Profile"), SettingOrder(31)]
    [Editor(typeof(ProfileEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public string EyeProfile { get; set => Set(ref field, value); } = "Warm Paper";

    /// <summary>Blue-light reduction, 0 to 1.</summary>
    [SettingCategory("Settings_EyeComfort"), SettingName("Settings_Warmth"), SettingOrder(32)]
    [SettingRange(0, 1, 0.05, "Format_Percent")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double Warmth { get; set => Set(ref field, Math.Clamp(value, 0, 1)); } = 0.2;

    /// <summary>Brightness reduction, 0 to 1.</summary>
    [SettingCategory("Settings_EyeComfort"), SettingName("Settings_Dim"), SettingOrder(33)]
    [SettingRange(0, 1, 0.05, "Format_Percent")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double Dim { get; set => Set(ref field, Math.Clamp(value, 0, 1)); }

    [SettingCategory("Settings_EyeComfort"), SettingName("Settings_BreakReminders"), SettingOrder(34)]
    [SettingHelp("Settings_BreakHint")]
    public bool BreakReminders { get; set => Set(ref field, value); }

    [SettingCategory("Settings_EyeComfort"), SettingName("Settings_BreakEvery"), SettingOrder(35)]
    [SettingRange(5, 60, 5, "Format_Minutes")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double BreakIntervalMinutes { get; set => Set(ref field, Math.Clamp(Math.Round(value), 5, 90)); } = 20;

    /// <summary>Folders whose books fill the library, one shelf each; empty to show recently opened books instead.</summary>
    [Browsable(false)]
    public List<string> LibraryFolders { get; set; } = [];

    /// <summary>The single folder that earlier versions stored. Read from old settings files, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Browsable(false)]
    public string? LibraryFolder {
        get => null;
        set {
            if (!string.IsNullOrWhiteSpace(value) && !LibraryFolders.Contains(value, StringComparer.OrdinalIgnoreCase)) {
                LibraryFolders.Add(value);
            }
        }
    }

    [Browsable(false)]
    public LibrarySort LibrarySort { get; set => Set(ref field, value); } = LibrarySort.LastRead;

    /// <summary>Id of the read-aloud voice; empty for the Windows default.</summary>
    [SettingCategory("Settings_ReadAloud"), SettingName("Settings_Voice"), SettingOrder(50)]
    [SettingHelp("Settings_ReadAloudHint")]
    [Editor(typeof(VoiceEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public string SpeechVoice { get; set => Set(ref field, value); } = "";

    [SettingCategory("Settings_ReadAloud"), SettingName("Settings_Speed"), SettingOrder(51)]
    [SettingRange(0.5, 2.5, 0.1, "Format_Speed")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double SpeechRate { get; set => Set(ref field, Math.Clamp(Math.Round(value, 1), 0.5, 3)); } = 1;

    [Browsable(false)]
    public ReadingStats Stats { get; set; } = new();

    /// <summary>Dims everything except the paragraph being read.</summary>
    [SettingCategory("Settings_Layout"), SettingName("Settings_FocusMode"), SettingOrder(26)]
    [SettingHelp("Settings_FocusHint")]
    public bool FocusMode { get; set => Set(ref field, value); }

    /// <summary>Minutes of reading aimed for each day; zero for no goal.</summary>
    [SettingCategory("Settings_Goal"), SettingName("Settings_GoalDaily"), SettingOrder(40)]
    [SettingHelp("Settings_GoalHint")]
    [SettingRange(0, 120, 5, "Format_Minutes")]
    [Editor(typeof(SliderEditor), typeof(HandyControl.Controls.PropertyEditorBase))]
    public double DailyGoalMinutes { get; set => Set(ref field, Math.Clamp(Math.Round(value / 5) * 5, 0, 180)); }

    /// <summary>Shelves the reader has made, each holding books from anywhere.</summary>
    [Browsable(false)]
    public List<BookCollection> Collections { get; set; } = [];

    /// <summary>Titles, authors and covers the reader has set by hand, keyed by the book's path.</summary>
    [Browsable(false)]
    public Dictionary<string, BookOverride> Overrides {
        get;
        set => field = new Dictionary<string, BookOverride>(value, StringComparer.OrdinalIgnoreCase);
    } = new(StringComparer.OrdinalIgnoreCase);

    [Browsable(false)]
    public double WindowWidth { get; set; } = 1280;

    [Browsable(false)]
    public double WindowHeight { get; set; } = 860;

    [Browsable(false)]
    public bool WindowMaximized { get; set; }

    [Browsable(false)]
    public List<RecentBook> Recent { get; set; } = [];

    private void Set<T>(ref T storage, T value, [CallerMemberName] string? name = null) {
        if (EqualityComparer<T>.Default.Equals(storage, value)) {
            return;
        }
        storage = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public static class SettingsStore {
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string DataFolder { get; } = ResolveDataFolder();

    /// <summary>
    /// Set once a backup has been restored: until the restart, nothing may be saved over the restored files.
    /// </summary>
    public static bool Frozen { get; set; }

    private static string FilePath => Path.Combine(DataFolder, "settings.json");

    /// <summary>Returns the data folder, first carrying over settings and fonts saved under the app's former name.</summary>
    private static string ResolveDataFolder() {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(root, "OmniRead");
        string legacy = Path.Combine(root, "BookReader");
        try {
            if (!Directory.Exists(folder) && Directory.Exists(legacy)) {
                Directory.Move(legacy, folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The old folder is left where it is; the reader simply starts with default settings.
        }
        return folder;
    }

    public static ReaderSettings Load() {
        try {
            if (File.Exists(FilePath)) {
                return JsonSerializer.Deserialize<ReaderSettings>(File.ReadAllText(FilePath), Options) ?? new ReaderSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // A damaged settings file should never stop the reader from starting.
        }
        return new ReaderSettings();
    }

    public static void Save(ReaderSettings settings) {
        if (Frozen) {
            return;
        }
        try {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Settings are a convenience; failing to persist them is not worth interrupting reading.
        }
    }
}