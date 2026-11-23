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

    [JsonIgnore]
    public string ProgressText => Localizer.Format("Format_Percent", Progress);
}

public sealed class ReaderSettings : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    public string FontName { get; set => Set(ref field, value); } = "Gentium Book Plus";

    public FontOrigin FontOrigin { get; set => Set(ref field, value); } = FontOrigin.Bundled;

    public double FontSize { get; set => Set(ref field, Math.Clamp(Math.Round(value), 10, 48)); } = 19;

    public double LineSpacing { get; set => Set(ref field, Math.Clamp(Math.Round(value, 2), 1, 2.6)); } = 1.5;

    /// <summary>Maximum width of a text column, in em.</summary>
    public double PageWidth { get; set => Set(ref field, Math.Clamp(Math.Round(value), 20, 70)); } = 34;

    public bool Justify { get; set => Set(ref field, value); } = true;

    public bool Hyphenate { get; set => Set(ref field, value); } = true;

    public bool DropCaps { get; set => Set(ref field, value); } = true;

    /// <summary>Restructure plain documents (text, Markdown, HTML, Word, RTF) into a typeset book.</summary>
    public bool BookFormatting { get; set => Set(ref field, value); } = true;

    public ViewMode ViewMode { get; set => Set(ref field, value); } = ViewMode.Paged;

    public ColumnMode Columns { get; set => Set(ref field, value); } = ColumnMode.Auto;

    public AppTheme Theme { get; set => Set(ref field, value); } = AppTheme.System;

    /// <summary>Interface language as a culture code such as "es"; empty to follow Windows.</summary>
    public string Language { get; set => Set(ref field, value); } = "";

    public bool EyeComfort { get; set => Set(ref field, value); }

    public string EyeProfile { get; set => Set(ref field, value); } = "Warm Paper";

    /// <summary>Blue-light reduction, 0 to 1.</summary>
    public double Warmth { get; set => Set(ref field, Math.Clamp(value, 0, 1)); } = 0.2;

    /// <summary>Brightness reduction, 0 to 1.</summary>
    public double Dim { get; set => Set(ref field, Math.Clamp(value, 0, 1)); }

    public bool BreakReminders { get; set => Set(ref field, value); }

    public double BreakIntervalMinutes { get; set => Set(ref field, Math.Clamp(Math.Round(value), 5, 90)); } = 20;

    /// <summary>Folders whose books fill the library, one shelf each; empty to show recently opened books instead.</summary>
    public List<string> LibraryFolders { get; set; } = [];

    /// <summary>The single folder that earlier versions stored. Read from old settings files, never written.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LibraryFolder {
        get => null;
        set {
            if (!string.IsNullOrWhiteSpace(value) && !LibraryFolders.Contains(value, StringComparer.OrdinalIgnoreCase)) {
                LibraryFolders.Add(value);
            }
        }
    }

    public LibrarySort LibrarySort { get; set => Set(ref field, value); } = LibrarySort.LastRead;

    /// <summary>Id of the read-aloud voice; empty for the Windows default.</summary>
    public string SpeechVoice { get; set => Set(ref field, value); } = "";

    public double SpeechRate { get; set => Set(ref field, Math.Clamp(Math.Round(value, 1), 0.5, 3)); } = 1;

    public ReadingStats Stats { get; set; } = new();

    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 860;

    public bool WindowMaximized { get; set; }

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
        try {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Settings are a convenience; failing to persist them is not worth interrupting reading.
        }
    }
}