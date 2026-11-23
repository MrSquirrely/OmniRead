using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using OmniRead.Formats;
using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Services;

using Microsoft.Win32;

namespace OmniRead;

public partial class MainWindow : Window {
    private const int MaxRecentBooks = 12;
    private const int BreakSeconds = 20;

    private readonly ReaderSettings _settings;
    private readonly DispatcherTimer _rebuildTimer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _breakTimer = new();
    private readonly DispatcherTimer _breakCountdown = new() { Interval = TimeSpan.FromSeconds(1) };

    private List<FontChoice> _fonts = [];
    private FontChoice? _font;
    private Book? _book;
    private int _chapterIndex;
    private ViewMode _activeMode;
    private bool _suppressEvents;
    private bool _fullScreen;
    private WindowState _restoreState;
    private int _breakSecondsLeft;

    public MainWindow(string[] args) {
        _settings = SettingsStore.Load();
        ThemeManager.Apply(_settings, null);
        InitializeComponent();
        DataContext = _settings;
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        if (_settings.WindowMaximized) {
            WindowState = WindowState.Maximized;
        }

        LoadFonts();
        ProfileList.ItemsSource = EyeProfile.All;
        LoadLanguages();
        LoadVoices();
        // Deferred until the window is loaded: before that there is no dispatcher context for an await to return to.
        Loaded += (_, _) => RefreshRecent();
        Loaded += async (_, _) => await InitializeReaderAsync();

        _settings.PropertyChanged += OnSettingChanged;
        ThemeManager.SystemThemeChanged += OnSystemThemeChanged;
        _rebuildTimer.Tick += (_, _) => {
            _rebuildTimer.Stop();
            Rebuild();
        };
        _saveTimer.Tick += (_, _) => {
            _saveTimer.Stop();
            SettingsStore.Save(_settings);
        };
        _breakTimer.Tick += (_, _) => ShowBreakReminder();
        _breakCountdown.Tick += (_, _) => OnBreakCountdown();
        RestartBreakTimer();

        SourceInitialized += (_, _) => ApplyTheme();
        string? startupFile = args.FirstOrDefault(static arg => !arg.StartsWith("--", StringComparison.Ordinal) && File.Exists(arg));
        if (startupFile is not null) {
            Loaded += async (_, _) => await OpenAsync(startupFile);
        }
    }

    private bool Paged => _activeMode == ViewMode.Paged;

    // ---------------------------------------------------------------- opening books

    private async Task OpenAsync(string path, bool keepProgress = false) {
        if (!File.Exists(path)) {
            _settings.Recent.RemoveAll(recent => SamePath(recent.Path, path));
            RefreshRecent();
            MessageBox.Show(this, Localizer.Format("Error_FileNotFound", path), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        double overall = ((_book is not null) && keepProgress)
            ? (_chapterIndex + _fraction) / _book.Chapters.Count
            : -1;
        StopReading();
        SaveProgress();

        // The web view is a window of its own and would cover the message, so it steps aside while a book loads.
        Web.Visibility = Visibility.Collapsed;
        LoadingText.Visibility = Visibility.Visible;
        WelcomePanel.Visibility = Visibility.Collapsed;
        Book book;
        try {
            book = await BookLoader.LoadAsync(Path.GetFullPath(path), _settings.BookFormatting);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            LoadingText.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = (_book is null) ? Visibility.Visible : Visibility.Collapsed;
            Web.Visibility = (_book is null) ? Visibility.Collapsed : Visibility.Visible;
            MessageBox.Show(this, Localizer.Format("Error_OpenFailed", ex.Message), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        LoadingText.Visibility = Visibility.Collapsed;

        _book = book;
        _imageToken++;
        LoadAnnotations();
        MeasureBook();
        Title = string.IsNullOrEmpty(book.Author)
            ? Localizer.Format("Title_Book", book.Title)
            : Localizer.Format("Title_BookAuthor", book.Title, book.Author);
        TitleText.Text = string.IsNullOrEmpty(book.Author) ? book.Title : $"{book.Title}  ·  {book.Author}";
        _suppressEvents = true;
        TocList.ItemsSource = book.Toc;
        _suppressEvents = false;
        BookFormatCheck.IsEnabled = book.CanBookFormat;

        int chapter = 0;
        double fraction = 0;
        RecentBook? recent = _settings.Recent.FirstOrDefault(entry => SamePath(entry.Path, book.FilePath));
        if (overall >= 0) {
            double scaled = Math.Clamp(overall, 0, 0.9999) * book.Chapters.Count;
            chapter = (int)scaled;
            fraction = scaled - chapter;
        }
        else if (recent is not null) {
            chapter = Math.Clamp(recent.ChapterIndex, 0, book.Chapters.Count - 1);
            fraction = recent.Fraction;
        }
        if (recent is null) {
            recent = new RecentBook { Path = book.FilePath };
            _settings.Recent.Add(recent);
        }
        recent.Title = book.Title;
        recent.LastOpened = DateTime.Now;
        _settings.Recent = [.. _settings.Recent.OrderByDescending(static entry => entry.LastOpened).Take(MaxRecentBooks)];

        ShowChapter(chapter, fraction, null, true);
        ScheduleSave();
    }

    private void OpenWithDialog() {
        OpenFileDialog dialog = new() { Filter = BookLoader.DialogFilter, Title = Localizer.Get("Dialog_OpenTitle") };
        if (dialog.ShowDialog(this) == true) {
            _ = OpenAsync(dialog.FileName);
        }
    }

    private void RefreshRecent() {
        _ = LoadLibraryAsync();
    }

    private static bool SamePath(string a, string b) {
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- showing chapters

    private Size Viewport => new(ReaderHost.ActualWidth, ReaderHost.ActualHeight);

    private void Rebuild() {
        if (_book is null) {
            return;
        }
        // Until the page has reported where it is, the place it was asked to show still stands.
        if (_located) {
            ShowChapter(_chapterIndex, _fraction, _topBlock, false, _topOffset);
        }
        else {
            ShowChapter(_chapterIndex, _seekFraction, _seekBlock, false, _seekOffset);
        }
    }

    private void ScheduleRebuild() {
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    private void ScheduleSave() {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnPositionChanged() {
        if (_book is null) {
            return;
        }
        TrackReading();
        UpdateStatus();
        SaveProgress();
        ScheduleSave();
    }

    private void UpdateStatus() {
        if ((_book is null) || !_shown) {
            ChapterText.Text = "";
            PageText.Text = "";
            BookProgress.Value = 0;
            return;
        }
        double fraction = _fraction;
        double overall = (_book.Chapters.Count == 1)
            ? fraction
            : (_chapterIndex + (Paged ? PageShare() : fraction)) / _book.Chapters.Count;
        ChapterText.Text = _book.Chapters[_chapterIndex].Title;
        BookProgress.Value = overall;
        string percent = Localizer.Format("Format_Percent", overall);
        PageText.Text = DescribePosition(percent);
    }

    /// <summary>Share of the chapter that has been read once the current page is finished.</summary>
    private double PageShare() {
        return (_located && (_pages > 0)) ? _share : _fraction;
    }

    private void SaveProgress() {
        if (_book is null) {
            return;
        }
        RecentBook? recent = _settings.Recent.FirstOrDefault(entry => SamePath(entry.Path, _book.FilePath));
        if (recent is null) {
            return;
        }
        recent.ChapterIndex = _chapterIndex;
        recent.Fraction = _fraction;
        recent.Progress = BookProgress.Value;
    }

    private void SelectTocEntry() {
        if (_book is null) {
            return;
        }
        TocEntry? entry = _book.Toc.FirstOrDefault(item => (item.ChapterIndex == _chapterIndex) && (item.BlockIndex == 0));
        _suppressEvents = true;
        TocList.SelectedItem = entry;
        if (entry is not null) {
            TocList.ScrollIntoView(entry);
        }
        _suppressEvents = false;
    }

    // ---------------------------------------------------------------- navigation

    private void NextPage() {
        if ((_book is not null) && _shown) {
            // The page knows whether there is another one; at the end of the chapter it asks for the next.
            PostToReader(new Dictionary<string, object?> { ["t"] = "go", ["where"] = "next" });
        }
    }

    private void PreviousPage() {
        if ((_book is not null) && _shown) {
            PostToReader(new Dictionary<string, object?> { ["t"] = "go", ["where"] = "previous" });
        }
    }

    private void NextChapter() {
        if ((_book is not null) && (_chapterIndex < (_book.Chapters.Count - 1))) {
            ShowChapter(_chapterIndex + 1, 0);
        }
    }

    private void PreviousChapter(bool toEnd) {
        if ((_book is not null) && (_chapterIndex > 0)) {
            ShowChapter(_chapterIndex - 1, toEnd ? 1 : 0);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e) {
        base.OnPreviewKeyDown(e);
        if (HandleKey(e.Key, Keyboard.Modifiers)) {
            e.Handled = true;
        }
    }

    /// <summary>Acts on a shortcut or reading key, whether it was pressed in the window or inside the page.</summary>
    private bool HandleKey(Key pressed, ModifierKeys modifiers) {
        bool control = modifiers == ModifierKeys.Control;
        bool handled = true;
        switch (pressed) {
            case Key.O when control:
                OpenWithDialog();
                break;
            case Key.T when control:
                ShowSidePanel(TocPanel, TocPanel.Visibility != Visibility.Visible);
                break;
            case Key.OemComma when control:
                TogglePanel(SettingsPanel, SettingsToggle);
                break;
            case Key.E when control:
                _settings.EyeComfort = !_settings.EyeComfort;
                break;
            case Key.OemPlus or Key.Add when control:
                _settings.FontSize++;
                break;
            case Key.OemMinus or Key.Subtract when control:
                _settings.FontSize--;
                break;
            case Key.D when control:
                AddBookmark();
                break;
            case Key.H when control:
                HighlightSelection();
                break;
            case Key.B when control:
                ShowSidePanel(NotesPanel, NotesPanel.Visibility != Visibility.Visible);
                break;
            case Key.R when control:
                ToggleReadAloud();
                break;
            case Key.L when control:
                ShowLibraryView();
                break;
            case Key.F when control:
                OpenSearch();
                break;
            case Key.K when control:
                _ = LookUpSelectionAsync();
                break;
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.Escape when _fullScreen:
                ToggleFullScreen();
                break;
            default:
                handled = false;
                break;
        }
        if (handled) {
            return true;
        }
        // Leave the keyboard alone while the reader is working in a side panel.
        if ((_book is null) || (modifiers is not (ModifierKeys.None or ModifierKeys.Control))
            || IsFocusWithin(SettingsPanel) || IsFocusWithin(TocPanel) || IsFocusWithin(NotesPanel) || IsFocusWithin(SearchPanel)) {
            return false;
        }
        handled = true;
        switch (pressed) {
            case Key.Right when control:
                NextChapter();
                break;
            case Key.Left when control:
                PreviousChapter(false);
                break;
            case Key.Right or Key.PageDown or Key.Space:
                NextPage();
                break;
            case Key.Left or Key.PageUp or Key.Back:
                PreviousPage();
                break;
            case Key.Home when control:
                ShowChapter(0, 0);
                break;
            case Key.End when control:
                ShowChapter(_book.Chapters.Count - 1, 1);
                break;
            case Key.Home when _shown:
                PostToReader(new Dictionary<string, object?> { ["t"] = "go", ["where"] = "first" });
                break;
            case Key.End when _shown:
                PostToReader(new Dictionary<string, object?> { ["t"] = "go", ["where"] = "last" });
                break;
            default:
                handled = false;
                break;
        }
        return handled;
    }

    private static bool IsFocusWithin(DependencyObject panel) {
        DependencyObject? current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null) {
            if (ReferenceEquals(current, panel)) {
                return true;
            }
            current = ((current is Visual) ? VisualTreeHelper.GetParent(current) : null) ?? LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void OnNestedListMouseWheel(object sender, MouseWheelEventArgs e) {
        // The profile list shows every item, so wheel input belongs to the settings panel around it.
        e.Handled = true;
        MouseWheelEventArgs forwarded = new(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent };
        ((UIElement)((FrameworkElement)sender).Parent).RaiseEvent(forwarded);
    }

    // ---------------------------------------------------------------- settings

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) {
        switch (e.PropertyName) {
            case nameof(ReaderSettings.EyeProfile):
                // Picking a profile resets the two sliders to that profile's recommended values.
                EyeProfile profile = EyeProfile.Find(_settings.EyeProfile);
                _suppressEvents = true;
                _settings.Warmth = profile.Warmth;
                _settings.Dim = profile.Dim;
                _suppressEvents = false;
                ApplyTheme();
                ScheduleRebuild();
                break;
            case nameof(ReaderSettings.EyeComfort) or nameof(ReaderSettings.Theme):
                ApplyTheme();
                ScheduleRebuild();
                break;
            case nameof(ReaderSettings.Warmth) or nameof(ReaderSettings.Dim):
                if (!_suppressEvents) {
                    ApplyTheme();
                }
                break;
            case nameof(ReaderSettings.BookFormatting):
                if (_book is { CanBookFormat: true }) {
                    _ = OpenAsync(_book.FilePath, true);
                }
                break;
            case nameof(ReaderSettings.BreakReminders) or nameof(ReaderSettings.BreakIntervalMinutes):
                RestartBreakTimer();
                break;
            case nameof(ReaderSettings.LibrarySort):
                ShowLibrary();
                break;
            case nameof(ReaderSettings.FontName) or nameof(ReaderSettings.FontOrigin) or nameof(ReaderSettings.Language)
                or nameof(ReaderSettings.SpeechVoice) or nameof(ReaderSettings.SpeechRate):
                break;
            default:
                ScheduleRebuild();
                break;
        }
        ScheduleSave();
    }

    private void ApplyTheme() {
        ThemeManager.Apply(_settings, this);
        ApplyReaderTheme();
    }

    private void OnSystemThemeChanged() {
        Dispatcher.BeginInvoke(ApplyTheme);
    }

    private void LoadFonts() {
        _fonts = FontLibrary.Load();
        SetFont(FontLibrary.Find(_fonts, _settings.FontName, _settings.FontOrigin));
        ListCollectionView view = new(_fonts);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(FontChoice.Group)));
        _suppressEvents = true;
        FontCombo.ItemsSource = view;
        FontCombo.SelectedItem = _font;
        _suppressEvents = false;
    }

    private void OnFontSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_suppressEvents || (FontCombo.SelectedItem is not FontChoice choice)) {
            return;
        }
        SetFont(choice);
        _settings.FontName = choice.Name;
        _settings.FontOrigin = choice.Origin;
        ScheduleRebuild();
    }

    private void LoadLanguages() {
        List<Language> languages = [.. Localizer.Languages.Select(static language =>
            (language.Code.Length == 0) ? language with { Name = Localizer.Get("Settings_Language_System") } : language)];
        _suppressEvents = true;
        LanguageCombo.ItemsSource = languages;
        LanguageCombo.SelectedItem = languages.FirstOrDefault(language => language.Code == _settings.Language) ?? languages[0];
        _suppressEvents = false;
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_suppressEvents || (LanguageCombo.SelectedItem is not Language language)) {
            return;
        }
        _settings.Language = language.Code;
        // Text already on screen keeps its language; everything is read again at the next start.
        LanguageHint.Visibility = Visibility.Visible;
    }

    private void OnOpenFontsFolderClick(object sender, RoutedEventArgs e) {
        try {
            Directory.CreateDirectory(FontLibrary.UserFolder);
            Process.Start(new ProcessStartInfo(FontLibrary.UserFolder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception) {
            MessageBox.Show(this, Localizer.Format("Error_FontsFolder", FontLibrary.UserFolder), "OmniRead");
        }
    }

    private void OnReloadFontsClick(object sender, RoutedEventArgs e) {
        LoadFonts();
        ScheduleRebuild();
    }

    // ---------------------------------------------------------------- eye breaks

    private void RestartBreakTimer() {
        _breakTimer.Stop();
        if (_settings.BreakReminders) {
            _breakTimer.Interval = TimeSpan.FromMinutes(_settings.BreakIntervalMinutes);
            _breakTimer.Start();
        }
    }

    private void ShowBreakReminder() {
        if ((_book is null) || !IsActive || (BreakBanner.Visibility == Visibility.Visible)) {
            return;
        }
        _breakSecondsLeft = BreakSeconds;
        BreakProgress.Value = 0;
        BreakText.Text = Localizer.Format("Break_Start", BreakSeconds);
        BreakBanner.Visibility = Visibility.Visible;
        _breakCountdown.Start();
    }

    private void OnBreakCountdown() {
        _breakSecondsLeft--;
        BreakProgress.Value = BreakSeconds - _breakSecondsLeft;
        if (_breakSecondsLeft <= 0) {
            HideBreakReminder();
        }
        else {
            BreakText.Text = Localizer.Format("Break_Remaining", _breakSecondsLeft);
        }
    }

    private void HideBreakReminder() {
        _breakCountdown.Stop();
        BreakBanner.Visibility = Visibility.Collapsed;
    }

    private void OnBreakDismissClick(object sender, RoutedEventArgs e) {
        HideBreakReminder();
    }

    // ---------------------------------------------------------------- window chrome

    private void TogglePanel(FrameworkElement panel, ToggleButton toggle) {
        bool show = panel.Visibility != Visibility.Visible;
        panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        toggle.IsChecked = show;
    }

    private void OnPanelToggle(object sender, RoutedEventArgs e) {
        ToggleButton toggle = (ToggleButton)sender;
        if (ReferenceEquals(toggle, SettingsToggle)) {
            SettingsPanel.Visibility = (toggle.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        FrameworkElement panel = ReferenceEquals(toggle, TocToggle) ? TocPanel : ReferenceEquals(toggle, NotesToggle) ? NotesPanel : SearchPanel;
        ShowSidePanel(panel, toggle.IsChecked == true);
        if (ReferenceEquals(panel, SearchPanel) && (toggle.IsChecked == true)) {
            SearchBox.Focus();
        }
    }

    private void ToggleFullScreen() {
        _fullScreen = !_fullScreen;
        if (_fullScreen) {
            _restoreState = WindowState;
            // Leaving the maximized state first makes Windows re-measure the borderless window over the taskbar.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _restoreState;
            ApplyTheme();
        }
        Visibility chrome = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Toolbar.Visibility = chrome;
        StatusBar.Visibility = chrome;
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) {
        ToggleFullScreen();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) {
        OpenWithDialog();
    }

    private void OnPreviousPageClick(object sender, RoutedEventArgs e) {
        PreviousPage();
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e) {
        NextPage();
    }

    private void OnPreviousChapterClick(object sender, RoutedEventArgs e) {
        PreviousChapter(false);
    }

    private void OnNextChapterClick(object sender, RoutedEventArgs e) {
        NextChapter();
    }

    private void OnTocSelectionChanged(object sender, SelectionChangedEventArgs e) {
        if (_suppressEvents || (TocList.SelectedItem is not TocEntry entry)) {
            return;
        }
        ShowChapter(entry.ChapterIndex, 0, entry.BlockIndex, true);
    }

    private void OnDragOver(object sender, DragEventArgs e) {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e) {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) {
            _ = OpenAsync(files[0]);
        }
    }

    protected override void OnClosing(CancelEventArgs e) {
        base.OnClosing(e);
        StopReading();
        _narrator.Dispose();
        Piper.Shutdown();
        SaveProgress();
        if (!_fullScreen) {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) {
                _settings.WindowWidth = ActualWidth;
                _settings.WindowHeight = ActualHeight;
            }
        }
        ThemeManager.SystemThemeChanged -= OnSystemThemeChanged;
        SettingsStore.Save(_settings);
    }
}