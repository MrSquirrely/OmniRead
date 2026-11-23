using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

using Microsoft.Web.WebView2.Core;

using OmniRead.Localization;
using OmniRead.Models;
using OmniRead.Rendering;
using OmniRead.Services;

namespace OmniRead;

/// <summary>Text the reader has selected on the page, as block indexes and character offsets.</summary>
public sealed record ReaderSelection(int Block, int Start, int EndBlock, int End, string Text);

/// <summary>
/// The reading area: a WebView2 showing one chapter at a time. The app typesets the chapter as HTML and posts it
/// to the page script, which paginates it and reports back where the reader is.
/// </summary>
public partial class MainWindow {
    private const string ReaderOrigin = "https://reader.omniread";

    private bool _webReady;
    private string? _queuedShow;
    private int _showId;
    private int _imageToken;
    private int _fontToken;
    private IReadOnlyList<FontFace> _faces = [];

    /// <summary>True while a chapter is on screen.</summary>
    private bool _shown;

    /// <summary>True once the page has reported its position for the chapter being shown.</summary>
    private bool _located;
    private int _page;
    private int _pages;
    private double _fraction;
    private double _share;
    private int _topBlock;
    private int _topOffset;
    private double _seekFraction;
    private int? _seekBlock;
    private int _seekOffset;
    private ReaderSelection? _selection;

    private async Task InitializeReaderAsync() {
        // The browser keeps its profile beside the settings. Where that folder cannot be used (it is redirected
        // for some sandboxed launches, which the browser process does not follow), a temporary one stands in.
        string[] profiles = [
            Path.Combine(SettingsStore.DataFolder, "WebView2"),
            Path.Combine(Path.GetTempPath(), "OmniRead", "WebView2")
        ];
        Exception? failure = null;
        foreach (string profile in profiles) {
            try {
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, profile);
                await Web.EnsureCoreWebView2Async(environment);
                failure = null;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) {
                    failure = ex;
            }
        }
        if (failure is not null) {
            // The WebView2 runtime ships with Windows 11; without it there is nothing to draw the pages with.
            MessageBox.Show(this, Localizer.Format("Error_WebView", failure.Message), "OmniRead", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        CoreWebView2 core = Web.CoreWebView2;
        CoreWebView2Settings options = core.Settings;
        options.AreBrowserAcceleratorKeysEnabled = false;
        options.AreDefaultContextMenusEnabled = false;
        options.AreDevToolsEnabled = false;
        options.IsStatusBarEnabled = false;
        options.IsZoomControlEnabled = false;
        options.IsPinchZoomEnabled = false;
        options.IsSwipeNavigationEnabled = false;
        options.IsBuiltInErrorPageEnabled = false;
        options.IsGeneralAutofillEnabled = false;
        options.IsPasswordAutosaveEnabled = false;

        // Everything the page loads (itself, pictures, fonts) is answered by the app; nothing comes from the network.
        core.AddWebResourceRequestedFilter(ReaderOrigin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessage;
        core.NavigationStarting += static (_, e) => e.Cancel = !e.Uri.StartsWith(ReaderOrigin + "/", StringComparison.OrdinalIgnoreCase);
        core.NewWindowRequested += static (_, e) => e.Handled = true;
        core.ProcessFailed += (_, _) => {
            // The page's process died: load it again and put the chapter back.
            _webReady = false;
            Dispatcher.BeginInvoke(() => {
                Web.CoreWebView2.Navigate(ReaderOrigin + "/reader.html");
                Rebuild();
            });
        };
        ApplyReaderTheme();
        core.Navigate(ReaderOrigin + "/reader.html");
    }

    // ---------------------------------------------------------------- showing chapters

    private void SetFont(FontChoice font) {
        _font = font;
        _faces = FontLibrary.Faces(font);
        _fontToken++;
    }

    private void ShowChapter(int index, double fraction, int? blockIndex = null, bool takeFocus = false, int offset = 0) {
        if (_book is null) {
            return;
        }
        if (_reading && !_narratorTurningChapter && (Math.Clamp(index, 0, _book.Chapters.Count - 1) != _chapterIndex)) {
            // Leaving the chapter by hand ends the reading; the narrator's own chapter turns do not.
            StopReading();
        }
        CheckLayout();
        _chapterIndex = Math.Clamp(index, 0, _book.Chapters.Count - 1);
        _activeMode = _settings.ViewMode;
        if (_font is null) {
            SetFont(FontLibrary.Find(_fonts, _settings.FontName, _settings.FontOrigin));
        }
        Palette palette = ThemeManager.Current;
        ChapterPage typeset = new ChapterHtml(_settings, _font!.Name, _faces, _fontToken, palette).Build(_book, _chapterIndex, _imageToken);

        _shown = true;
        _located = false;
        _showId++;
        _seekFraction = Math.Clamp(fraction, 0, 1);
        _seekBlock = blockIndex;
        _seekOffset = offset;
        _fraction = (blockIndex is null) ? _seekFraction : 0;
        _share = _fraction;
        _page = 0;
        _pages = 0;
        _topBlock = blockIndex ?? 0;
        _topOffset = offset;
        _selection = null;

        int[]? found = null;
        if (_pendingMatch is SearchHit hit) {
            _pendingMatch = null;
            if (hit.ChapterIndex == _chapterIndex) {
                found = [hit.BlockIndex, hit.Offset, hit.Match.Length];
            }
        }
        string show = JsonSerializer.Serialize(new Dictionary<string, object?> {
            ["t"] = "show",
            ["id"] = _showId,
            ["html"] = typeset.Html,
            ["css"] = typeset.Css,
            ["paged"] = Paged,
            ["fixed"] = _book.FixedLayout,
            ["title"] = typeset.TitlePage,
            ["columns"] = (int)_settings.Columns,
            ["line"] = typeset.Line,
            ["measure"] = typeset.Measure,
            ["gap"] = typeset.Gap,
            ["lang"] = _book.Language ?? CultureInfo.CurrentUICulture.Name,
            ["f"] = _seekFraction,
            ["b"] = blockIndex ?? -1,
            ["o"] = offset,
            ["marked"] = MarkedPassages(),
            ["found"] = found,
            ["vars"] = ChapterHtml.Variables(palette)
        });
        WelcomePanel.Visibility = Visibility.Collapsed;
        Web.Visibility = Visibility.Visible;
        if (_webReady) {
            Web.CoreWebView2.PostWebMessageAsJson(show);
        }
        else {
            // The page is still loading; only the newest chapter matters once it is ready.
            _queuedShow = show;
        }
        if (takeFocus) {
            Web.Focus();
        }

        PreviousChapterButton.IsEnabled = _chapterIndex > 0;
        NextChapterButton.IsEnabled = _chapterIndex < (_book.Chapters.Count - 1);
        SelectTocEntry();
        UpdateStatus();
    }

    /// <summary>This chapter's highlights, each as start block, start offset, end block, end offset.</summary>
    private List<int[]> MarkedPassages() {
        return [.. _annotations
            .Where(annotation => (annotation.Kind == AnnotationKind.Highlight) && (annotation.ChapterIndex == _chapterIndex))
            .Select(static annotation => new[] { annotation.BlockIndex, annotation.Offset, annotation.EndBlockIndex, annotation.EndOffset })];
    }

    private void PostToReader(Dictionary<string, object?> message) {
        if (_webReady) {
            Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
        }
    }

    private void ApplyReaderTheme() {
        Color page = ThemeManager.Current.Page;
        Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(page.R, page.G, page.B);
        PostToReader(new Dictionary<string, object?> { ["t"] = "theme", ["vars"] = ChapterHtml.Variables(ThemeManager.Current) });
    }

    /// <summary>The text of a block as its character offsets count it, or null for pictures and other blocks without text.</summary>
    private static string? TextOf(ContentBlock block) {
        return block switch {
            ParagraphBlock paragraph => paragraph.Text,
            HeadingBlock heading => heading.Text,
            _ => null
        };
    }

    // ---------------------------------------------------------------- messages from the page

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e) {
        using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
        JsonElement message = document.RootElement;
        switch (message.GetProperty("t").GetString()) {
            case "ready":
                _webReady = true;
                ApplyReaderTheme();
                if (_queuedShow is string queued) {
                    _queuedShow = null;
                    Web.CoreWebView2.PostWebMessageAsJson(queued);
                }
                break;
            case "pos":
                if (message.GetProperty("id").GetInt32() != _showId) {
                    break;
                }
                _located = true;
                _page = message.GetProperty("page").GetInt32();
                _pages = message.GetProperty("pages").GetInt32();
                _fraction = message.GetProperty("f").GetDouble();
                _share = message.GetProperty("share").GetDouble();
                _topBlock = message.GetProperty("b").GetInt32();
                _topOffset = message.GetProperty("o").GetInt32();
                OnPositionChanged();
                break;
            case "sel":
                string text = message.GetProperty("text").GetString() ?? "";
                _selection = (text.Length == 0) ? null : new ReaderSelection(
                    message.GetProperty("b").GetInt32(),
                    message.GetProperty("s").GetInt32(),
                    message.GetProperty("eb").GetInt32(),
                    message.GetProperty("e").GetInt32(),
                    text);
                break;
            case "edge":
                if (message.GetProperty("d").GetInt32() > 0) {
                    NextChapter();
                }
                else {
                    PreviousChapter(true);
                }
                break;
            case "key":
                ModifierKeys modifiers = (message.GetProperty("ctrl").GetBoolean() ? ModifierKeys.Control : ModifierKeys.None)
                    | (message.GetProperty("shift").GetBoolean() ? ModifierKeys.Shift : ModifierKeys.None);
                HandleKey(KeyInterop.KeyFromVirtualKey(message.GetProperty("code").GetInt32()), modifiers);
                break;
            case "zoom":
                _settings.FontSize += message.GetProperty("d").GetInt32();
                break;
            case "link":
                OnLink(
                    message.GetProperty("href").GetString() ?? "",
                    message.GetProperty("text").GetString() ?? "",
                    message.GetProperty("sup").GetBoolean());
                break;
            case "menu":
                ContextMenu menu = (ContextMenu)FindResource("ReaderMenu");
                menu.PlacementTarget = Web;
                menu.Placement = PlacementMode.MousePoint;
                menu.IsOpen = true;
                break;
            case "drop":
                if (e.AdditionalObjects.OfType<CoreWebView2File>().FirstOrDefault() is CoreWebView2File file) {
                    _ = OpenAsync(file.Path);
                }
                break;
        }
    }

    private static void OpenExternal(string address) {
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) || (uri.Scheme is not ("http" or "https" or "mailto"))) {
            return;
        }
        try {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) {
            // No handler for the link; nothing useful to do.
        }
    }

    // ---------------------------------------------------------------- what the page asks for

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e) {
        CoreWebView2Deferral deferral = e.GetDeferral();
        CoreWebView2Environment environment = Web.CoreWebView2.Environment;
        try {
            (byte[]? content, string type) = await ReadResourceAsync(new Uri(e.Request.Uri).AbsolutePath);
            e.Response = (content is null)
                ? environment.CreateWebResourceResponse(null, 404, "Not Found", "")
                : environment.CreateWebResourceResponse(
                    new MemoryStream(content), 200, "OK", $"Content-Type: {type}\r\nCache-Control: no-store");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // A picture or font that cannot be read is simply missing from the page.
            e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
        finally {
            deferral.Complete();
        }
    }

    private async Task<(byte[]? Content, string Type)> ReadResourceAsync(string path) {
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        switch (parts) {
            case ["reader.html"]:
                using (Stream? page = Assembly.GetExecutingAssembly().GetManifestResourceStream("OmniRead.Rendering.Reader.html")) {
                    if (page is null) {
                        return (null, "");
                    }
                    using MemoryStream buffer = new();
                    await page.CopyToAsync(buffer);
                    return (buffer.ToArray(), "text/html; charset=utf-8");
                }
            case ["font", _, string face] when int.TryParse(face, out int faceIndex) && (faceIndex >= 0) && (faceIndex < _faces.Count):
                string file = _faces[faceIndex].File;
                return (await File.ReadAllBytesAsync(file), file.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? "font/otf" : "font/ttf");
            case ["img", string token, string chapter, string block] when (_book is Book book)
                && int.TryParse(token, out int imageToken) && (imageToken == _imageToken)
                && int.TryParse(chapter, out int chapterIndex) && (chapterIndex >= 0) && (chapterIndex < book.Chapters.Count)
                && int.TryParse(block, out int blockIndex) && (blockIndex >= 0) && (blockIndex < book.Chapters[chapterIndex].Blocks.Count)
                && (book.Chapters[chapterIndex].Blocks[blockIndex] is ImageBlock image):
                byte[]? picture = await image.Load();
                return (picture, ImageType(picture));
            default:
                return (null, "");
        }
    }

    /// <summary>Recognises the common picture formats from their first bytes.</summary>
    private static string ImageType(byte[]? content) {
        return content switch {
            [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => "image/png",
            [0xFF, 0xD8, ..] => "image/jpeg",
            [(byte)'G', (byte)'I', (byte)'F', ..] => "image/gif",
            [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "image/webp",
            [(byte)'B', (byte)'M', ..] => "image/bmp",
            [(byte)'<', ..] => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}