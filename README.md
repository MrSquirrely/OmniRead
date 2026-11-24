# OmniRead

A native WPF book reader for Windows 11 built on .NET 10 (Windows SDK 10.0.22621).

    dotnet run --project OmniRead

OmniRead pairs a WPF shell—styled with light and dark themes from [HandyControls](https://github.com/ghost1372/HandyControls)—with Microsoft Edge WebView2 to render the page itself, relying on the WebView2 runtime already built into Windows 11.

The window sits on a Windows 11 Mica backdrop that switches between light and dark to match your reading theme, showing through the title bar, status bar, and margins around the page. Controls live right in the title bar. Clicking the gear icon opens **Reading settings**, a categorized property grid of every option alongside buttons at the bottom for one-off actions like opening your fonts folder, viewing reading history, managing Piper voices, exporting audiobooks, and backing up or restoring your data. Selecting a setting displays a short description underneath when one is available, and the reset arrow on the right of each row (dimmed while at its default) reverts that option with a single click.

## How a page is drawn

Every supported format is parsed into the same unified list of blocks (headings, paragraphs, images, and tables). `Rendering/ChapterHtml` typesets one chapter at a time as HTML using your chosen font, size, and spacing, then passes it to `Rendering/Reader.html` inside the WebView2 control. A lightweight script lays the chapter out into CSS columns for paged reading (or a single continuous column for scrolling), handles page turns, and reports the page count, current scroll position, and text selection back to the app. Images and font files are served locally by the app on demand—the page never touches the network, and book files cannot execute scripts because only OmniRead's own markup ever reaches the view.

WebView2 stores its browser profile in `%LocalAppData%\OmniRead\WebView2`.

Because WebView2 doesn't ship with built-in hyphenation dictionaries, OmniRead hyphenates text itself using the TeX hyphenation patterns in `OmniRead/Data/Hyphenation` (currently English and Spanish). The language is detected from the book's metadata (EPUB and FictionBook) or falls back to the interface language; books in other languages are justified without hyphenation.

## Supported formats

OmniRead opens EPUB, FictionBook (`.fb2`, `.fbz`), PDF, Markdown, plain text, HTML, Word (`.docx`), and RTF files. PDFs are rendered via `Windows.Data.Pdf` as fixed pages, while every other format reflows to fit your window and typography settings.

EPUB packages (metadata, spine order, table of contents, and cover art) are loaded using `VersOne.Epub`, automatically normalizing backslash archive paths written by certain Windows tools.

For EPUB and HTML books, stylesheets are parsed with `AngleSharp.Css` to preserve emphasis, small caps, alignment, indents, extra spacing, relative font sizes, and hidden elements. Each block keeps only the styling that differs from a standard body paragraph so your chosen font, size, spacing, and color theme always take priority—floats, absolute positioning, publisher fonts, and hardcoded colors are stripped out.

**Format documents like a book** (**Reading settings → Layout**) cleans up non-book formats like plain text, Markdown, and `.docx`. It detects chapter breaks, unwraps hard-wrapped lines, generates a title page, and applies curly quotes, proper dashes, scene breaks, first-line indents, and drop caps. Turn it off anytime to view the file exactly as written.

## Fonts

The font picker organizes typefaces into three groups:

- **Bundled fonts** shipped in `OmniRead/Fonts`
- **Custom fonts** from `%LocalAppData%\OmniRead\Fonts` (click **Open fonts folder**, drop in `.ttf` or `.otf` files, and click **Reload fonts**)
- **System fonts** installed in Windows

OmniRead includes five open-source fonts under the SIL Open Font License (each accompanied by its license file): Atkinson Hyperlegible, Crimson Text, Gentium Book Plus, OpenDyslexic, and Spectral.

## Eye comfort

Choose from eight color profiles—**Warm Paper**, **Sepia**, **Soft Green**, **Grey Mist**, **Dusk**, **Night Amber**, **Midnight**, and **High Contrast**—each with adjustable blue-light filtering and screen dimming, plus an optional 20-20-20 break reminder.

## Library

When no book is open, OmniRead displays your library as shelves of book covers:

- **Library folders:** Add one or more folders at once (or remove them later). Each folder—along with any nested subfolder—gets its own shelf named after the folder, containing every supported book inside it. Folders sharing the same name are disambiguated using their parent folder's name. If you haven't added any folders yet, OmniRead shows your recently opened books instead.
- **Sorting and filtering:** Sort shelves by last read, title, or author. Use the filter bar below the header (press `Ctrl+F` to jump straight to it) to search by title or author, filter by file format, or narrow down to unread, in-progress, or finished books (a book is marked finished once you reach its final page).
- **Book actions and caching:** Click any cover to start reading, or right-click for quick actions (**Open**, **Show in folder**, **Copy path**, and **Forget reading progress**). Titles, authors, and covers are extracted from EPUB and FB2 metadata, PDFs display a thumbnail of their first page, and other formats get a generated title card. Everything discovered during scans is cached in `%LocalAppData%\OmniRead` (`library.json` and `Covers`).
- **Collections:** Create custom shelves that appear above your folder shelves with a tag icon. Right-click a book and choose **Collections** to assign it to one or more collections (without moving the file on disk) or create a new one. Right-click any collection header to rename or delete it.
- **Edit details…:** Fix missing or incorrect titles, authors, and cover art via the book's context menu without altering the underlying file. Custom covers are saved in `%LocalAppData%\OmniRead\CustomCovers`, and you can revert your edits anytime with **Reset to the file's details**.

## Bookmarks and highlights

Press `Ctrl+D` to bookmark the top of the current page, or select text and press `Ctrl+H` to highlight it. Both live in the bookmarks panel (`Ctrl+B`), where you can attach notes to any entry. Annotations are saved per book in `annotations.json` using paragraph and character offsets, so they stay aligned across window resizes and font changes (though toggling **Format documents like a book** changes paragraph structure and invalidates existing offsets).

Click **Export…** in the bookmarks panel to save a book's bookmarks, full highlight passages, and notes as Markdown or plain text, organized by chapter in reading order.

## Read aloud

Press `Ctrl+R` (or click the speaker icon) to have OmniRead read aloud from the top of the page—or from your current text selection—using Windows voices (`Windows.Media.SpeechSynthesis`). The active sentence is highlighted as it's spoken, and pages turn automatically to keep up. Voice and playback speed are configured in **Reading settings**. (PDFs cannot be read aloud since their pages are rendered as images.)

While reading aloud, four extra controls appear next to the speaker button: **Previous sentence** (`Ctrl+↑`, which stops at the start of the current chapter), **Pause / Resume** (`Ctrl+P`), **Next sentence** (`Ctrl+↓`), and a **Sleep timer** that stops playback after 15, 30, 45, or 60 minutes (after finishing the current sentence) or at the end of the chapter.

**Export as audiobook…** (a button at the bottom of **Reading settings**) renders either the current chapter or the entire book to audio files using your selected voice and speed, outputting one numbered file per chapter in reading order as M4A (AAC encoded via Windows) or uncompressed WAV. Exports run in the background so you can keep reading; closing the progress window cancels it while keeping any chapters already finished. Longer books take time, and if you use a Piper voice, the same engine powers both live read-aloud and audiobook export one sentence at a time.

## Focus mode

Focus mode (`Ctrl+M`, or the switch under **Layout**) dims the rest of the page so a single paragraph stands out. In scrolling view, it tracks whichever paragraph crosses roughly two-fifths of the way down the window; in paged view, it starts on the first paragraph of the page and lets you step backward or forward with `↑` and `↓`, turning pages automatically as needed. Clicking any paragraph brings it into focus, and Read Aloud highlights each paragraph as it speaks.

## Search, dictionary, and footnotes

Press `Ctrl+F` to search across the entire book and browse matches grouped by chapter; selecting a result jumps straight to that page and highlights the match. To look up a word, select it and press `Ctrl+K` (or right-click it). The offline dictionary (`OmniRead/Data/dictionary.txt.gz`) packs roughly 147,000 English entries from WordNet 3.1 (Princeton University; license in `Data/WORDNET-LICENSE.txt`) and automatically resolves plurals and inflected forms back to their base headwords. You can rebuild the dictionary file from WordNet using `tools/BuildDictionary.cs`.

Clicking a footnote reference in an EPUB, HTML, or Markdown book opens the note in a compact popup card so you don't lose your place, while standard internal links jump directly to their destination across EPUB, FictionBook, HTML, and Markdown files.

## Page numbers and reading time

Pages are numbered continuously across the entire book and displayed in the status bar. Click the page box (or press `Ctrl+G`), type a page number, and press `Enter` to jump directly there.

Whenever you open a book—or change the window size, font, text size, or line spacing—`BookPaginator` builds a background page map. It measures every chapter line by line using your reading font's actual character widths and page margins, recording where each page starts by chapter, block ID (`reader-ch-{chapter}-blk-{block}`), and character offset. Because images use estimated heights and hyphenation isn't modeled during the background pass, paged view counts the actual pages rendered within each chapter starting from that chapter's mapped first page, ensuring one page turn always equals one page number. In scrolling view, one window height counts as a page and updates as you scroll past page boundaries. Resizing the window or changing typography keeps your place and recalculates page numbers around it.

The status bar also estimates the time remaining in the current chapter, starting at 250 words per minute and adapting to your real reading pace once you've read for at least two minutes. Hover over it to see the time left in the entire book, your reading time today, and your average pace.

## Reading goal and history

Set a daily reading target in minutes under **Reading settings → Reading goal** (`0` turns goals off). When a goal is active, the status bar tooltip tracks today's progress and your daily streak—carrying yesterday's streak forward until you hit today's target. Click **Reading history…** to view a 30-day bar chart with your goal line overlaid, alongside today's reading time, your current streak, and your 30-day and all-time totals. OmniRead retains up to 400 days of daily reading logs.

## Backup and restore

**Back up…** (a button at the bottom of **Reading settings**) bundles everything OmniRead tracks into a single `.omnibackup` zip archive: settings, library folders, collections, reading positions, statistics, bookmarks, highlights, notes, edited book details and custom covers, and any fonts in your personal fonts folder. It excludes the book files themselves, the generated cover cache (which rebuilds automatically on the next scan), and Piper voices.

**Restore…** loads a backup and restarts OmniRead, preserving the files it replaces as `settings.json.bak` and `annotations.json.bak`. Because books are matched by full file path, positions and annotations reconnect automatically if your books remain in the same locations. Paths inside your Windows user folder are automatically remapped to the current user's profile when restoring onto a different account or PC, while books stored elsewhere need to stay on the same drive and folder path.

## Piper voices

Alongside standard Windows voices, OmniRead supports [Piper](https://github.com/OHF-Voice/piper1-gpl), an offline neural text-to-speech engine that runs locally on your machine. Open **Reading settings → Piper voices…** to install it and download voice models:

- Piper is licensed under GPL-3.0-or-later and is not bundled with OmniRead. When you install it, OmniRead downloads the official `piper-tts` package along with its own standalone Python environment into `%LocalAppData%\OmniRead\Piper` (about 190 MB) and runs it as a separate process via a small GPL-licensed bridge script.
- All 183 Piper voices are available, grouped by language and ordered from lowest to highest quality. Each entry displays the license from its `MODEL_CARD`—as well as any stricter terms inherited from its training dataset. Only 64 voices are public domain or CC0; many others are non-commercial, research-only, or require attribution, and 30 only link to a license URL, so be sure to check the terms before using a voice outside personal reading.
- Every downloaded voice keeps its `MODEL_CARD` file saved right beside it.
- Click **Uninstall Piper** in the same window at any time to remove the engine, its Python runtime, and—optionally—your downloaded voices.
- Installing Piper and downloading voices are the *only* actions in OmniRead that ever use the internet, and only when you explicitly ask for them.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open file |
| `←` `→` / `PgUp` `PgDn` / `Space` | Turn page |
| `Ctrl+←` / `Ctrl+→` | Previous / next chapter |
| `Ctrl+T` / `Ctrl+,` | Table of contents / Reading settings |
| `Ctrl+E` | Toggle Eye comfort mode |
| `Ctrl+L` | Return to library |
| `Ctrl+D` / `Ctrl+H` | Bookmark current page / Highlight selection |
| `Ctrl+B` | Open bookmarks and highlights |
| `Ctrl+R` | Start or stop Read aloud |
| `Ctrl+P` / `Ctrl+↑` / `Ctrl+↓` | Pause or resume / Previous or next sentence (while reading aloud) |
| `Ctrl+F` | Search in book (or filter shelves in the library) |
| `Ctrl+M` | Toggle Focus mode (`↑` / `↓` moves focus in paged view) |
| `Ctrl+K` | Look up selected word |
| `Ctrl+G` | Go to page |
| `Ctrl` + `+` / `−` / mouse wheel | Adjust text size |
| `F11` | Toggle full screen |

## Languages

UI strings live in `OmniRead/Resources/Strings.resx` (English) alongside translations such as `Strings.es.resx` (Spanish). To add another language, copy `Strings.resx` to `Strings.<culture>.resx`, translate the string values, and register the locale in `Localizer.Languages`. Users can switch languages under **Reading settings → Appearance** (applied after a restart). Use `{l:Loc Some_Key}` in XAML and `Localizer.Get("Some_Key")` in C#.

## Building a release

    dotnet publish OmniRead -p:PublishProfile=SingleFile

This produces a self-contained single-file executable at `release\OmniRead.exe` for 64-bit Windows 11. Bundled fonts, the dictionary, and hyphenation patterns are embedded inside and unpack to `%TEMP%\.net` on first launch. The WebView2 runtime is not bundled, as Windows 11 includes it out of the box.

## Tests

    dotnet test OmniRead.Tests

Runs the unit test suite for the background page mapper.

## License

OmniRead is free software licensed under the GNU General Public License, version 3 or (at your option) any later version—see `LICENSE` for details. It is provided with no warranty.

Third-party components retain their respective licenses:

- **Bundled fonts** (`OmniRead/Fonts`): SIL Open Font License (license files included alongside each font)
- **Dictionary data**: WordNet 3.1 (`OmniRead/Data/WORDNET-LICENSE.txt`)
- **Hyphenation patterns** (`OmniRead/Data/Hyphenation`): Unmodified files from the TeX `hyph-utf8` project with original headers intact (American English under Gerard Kuiken's permissive notice, incorporating Knuth's `hyphen.tex` tables; Spanish under the MIT License)
- **NuGet packages**: AngleSharp and AngleSharp.Css (MIT), Markdig (BSD 2-Clause), VersOne.Epub (Unlicense), HandyControls (MIT), and the Microsoft WebView2 SDK (BSD-style)
- **Piper & voice models**: Not bundled with OmniRead; downloaded separately under their own respective licenses