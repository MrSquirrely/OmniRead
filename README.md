# OmniRead

A WPF book reader for Windows 11 (.NET 10, Windows SDK 10.0.22621).

    dotnet run --project OmniRead

The window is WPF, with its control styles, window chrome and light and dark themes from
[HandyControls](https://github.com/ghost1372/HandyControls); the page itself is drawn by Microsoft Edge WebView2,
whose runtime is part of Windows 11.

The toolbar sits in the title bar. The cog opens the reading settings: a property grid of everything that can be
set, grouped under the same headings as before, with the things that are done rather than set (fonts folder,
reading history, Piper voices, audiobook export, backup and restore) as buttons beneath it. Selecting a setting
shows a line of help under the grid where there is one.

## How a page is drawn

Every format is loaded into the same list of blocks (headings, paragraphs, pictures, tables). `Rendering/ChapterHtml`
typesets one chapter of them as HTML with the reader's font, size and spacing, and posts it to
`Rendering/Reader.html`, the page shown in the web view. Its script lays the chapter out in CSS columns for paged
view (or one scrolling column), turns pages, and reports the page count, the place at the top of the screen and
the selection back to the app. Pictures and font files are handed to the page by the app on request; the page
loads nothing from the network, and book files never run scripts because only the app's own markup reaches it.

The browser's profile is kept in `%LocalAppData%\OmniRead\WebView2`.

WebView2 has no hyphenation dictionaries, so words are hyphenated by OmniRead itself with the TeX hyphenation
patterns in `OmniRead/Data/Hyphenation` (English and Spanish). The language comes from the book (EPUB and
FictionBook say which they are in) and otherwise from the interface language; books in other languages are
justified without hyphenation.

## Formats

EPUB, FictionBook (.fb2, .fbz), PDF, Markdown, plain text, HTML, Word (.docx) and RTF.
PDF pages are rendered with `Windows.Data.Pdf` and shown as fixed pages; everything else reflows.

EPUB packages (metadata, reading order, contents, cover) are read with VersOne.Epub; archives whose paths use
backslashes, which some Windows tools write, are corrected on the way in.

EPUB and HTML stylesheets are honoured through AngleSharp.Css: emphasis, small caps, alignment, insets, extra
spacing, relative sizes and hidden content. Each block keeps only what differs from the book's ordinary
paragraph, so your font, size, spacing and colours always win. Floats, positioning, colours and publisher
fonts are ignored.

**Format documents like a book** (Reading settings → Layout) applies to the non-book formats. It detects
chapters, rejoins hard-wrapped lines, adds a title page, and sets curly quotes, dashes, scene breaks,
first-line indents and drop caps. Turn it off to see the document as written.

## Fonts

The font list has three groups: fonts shipped in `OmniRead/Fonts`, fonts from your own folder
(`%LocalAppData%\OmniRead\Fonts` — use **Open fonts folder**, copy .ttf/.otf files in, then **Reload fonts**),
and fonts installed in Windows.

Included fonts (all SIL Open Font License, licence text beside each): Atkinson Hyperlegible, Crimson Text,
Gentium Book Plus, OpenDyslexic, Spectral.

## Eye comfort

Eight profiles (Warm Paper, Sepia, Soft Green, Grey Mist, Dusk, Night Amber, Midnight, High Contrast), each with
adjustable blue-light reduction and dimming, plus an optional 20-20-20 break reminder.

## Library

With no book open, OmniRead shows shelves of covers. **Library folders** adds one or more folders (you can pick
several at once) and removes them again; each folder becomes a shelf labelled with the folder's name, holding every
supported book in it and its subfolders. Two folders with the same name are told apart by their parent folder, and a
folder inside another listed folder gets its own shelf. With no folders it shows recently opened books. Sort by last
read, title or author. The row under the heading narrows the shelves: type part of a title or author (Ctrl+F puts
the cursor there), pick a format, or show only unread, in-progress or finished books. A book counts as finished
once its last page has been reached. Click a book to open it; right-click for a menu (open, show in folder, copy path, forget
reading progress). Titles, authors and covers are read from EPUB and FB2 files, PDFs show their first page, and other
formats show a title card. What is found is cached in `%LocalAppData%\OmniRead` (`library.json` and `Covers`).

**Collections** are shelves you make yourself; a book can be in any number of them and stays in its folder.
Right-click a book → **Collections** to tick the ones it belongs to or make a new one. Collections stand above the
folder shelves, marked with a tag; right-click a collection's heading to rename or delete it.

**Edit details…** in the same menu corrects a book's title and author and lets you choose a cover picture, for
books whose files carry none or get them wrong. The corrections are kept by OmniRead (the book file is not
changed) and can be undone with **Reset to the file's details**. Chosen covers are stored in
`%LocalAppData%\OmniRead\CustomCovers`.

## Bookmarks and highlights

Ctrl+D bookmarks the top of the page; select text and press Ctrl+H to highlight it. Both are listed in the
bookmarks panel (Ctrl+B), where each can carry a note, and are saved per book in `annotations.json`. Places are
stored as a paragraph plus a character offset, so they survive font and window changes, but not switching
**Format documents like a book** on or off.

**Export…** in the same panel saves the book's bookmarks, highlights and notes as Markdown or plain text, grouped
by chapter in reading order. Highlights are written out in full, read back from the book.

## Read aloud

Ctrl+R (or the speaker button) reads from the top of the page, or from selected text, using the Windows voices
(`Windows.Media.SpeechSynthesis`). The sentence being spoken is tinted and pages turn to follow it. Voice and
speed are under Reading settings. PDFs cannot be read aloud because their pages are images.

While it reads, four more buttons appear beside the speaker: previous sentence (Ctrl+↑), pause or resume (Ctrl+P),
next sentence (Ctrl+↓) and a sleep timer. The timer stops the reading after 15, 30, 45 or 60 minutes, letting the
sentence being spoken finish, or at the end of the chapter. "Previous sentence" does not go back past the start of
the chapter.

**Export as audiobook…** (Reading settings → Read aloud) reads the current chapter or the whole book into audio
files with the chosen voice and speed, one file per chapter, numbered in reading order. Files are M4A (AAC, encoded
by Windows) or uncompressed WAV. The export runs in the background, so you can keep reading; closing its window
cancels it and keeps the chapters already finished. A long book takes a while, and with a Piper voice the same
engine serves both the export and read aloud, one sentence at a time.

## Focus mode

Focus mode (Ctrl+M, or the tick box under Layout) dims everything except one paragraph. In scrolling view that is
the paragraph crossing a line two fifths of the way down the window; in pages view it is the first on the page, and
↑ and ↓ move it to the previous or next paragraph, turning the page when needed. Clicking a paragraph lights it, and
read aloud lights the paragraph it is speaking.

## Search, dictionary and footnotes

Ctrl+F searches the whole book and lists every match with its chapter; picking one goes to the page and tints
the match. Select a word and press Ctrl+K (or right-click it) to see its definition.
The dictionary is built in and works offline: `OmniRead/Data/dictionary.txt.gz` holds about 147,000 English words
from WordNet 3.1 (Princeton University; licence in `Data/WORDNET-LICENSE.txt`), and plurals and other inflected forms
are traced back to their dictionary form. `tools/BuildDictionary.cs` rebuilds the file from the WordNet database.
A footnote reference in an EPUB, HTML or Markdown file opens the note in a small card instead of leaving the page;
other links inside a book are followed. This works for EPUB, FictionBook, HTML and Markdown.

## Page numbers and reading time

Pages are numbered through the whole book. Chapters are laid out one at a time, so the total is exact only once
every chapter has been shown at the current size and type settings; until then the rest are estimated from their
length and the total is marked with `~`. The status bar also shows the time left in the chapter, based on your
own pace once two minutes of reading have been measured (250 words a minute before that). Hover over it for the
time left in the book, today's reading time and your pace.

## Reading goal and history

Reading settings → **Reading goal** sets a number of minutes to read each day (0 for none). With a goal set, the
status bar's tooltip shows today's reading against it and how many days in a row it has been met; the count keeps
running from yesterday until today's reading reaches the goal. **Reading history…** opens a chart of the time read
on each of the last thirty days with the goal drawn across it, beside today's time, the streak, and the totals for
the thirty days and for all time. Up to 400 days of daily times are kept.

## Backup and restore

Reading settings → **Backup** saves what OmniRead remembers as one `.omnibackup` file (a zip): settings, library
folders, collections, reading positions, statistics, bookmarks, highlights, notes, corrected book details with
their chosen covers, and the fonts in your own fonts folder. It does
not hold the books themselves, the cover cache (rebuilt on the next scan) or Piper and its voices.

**Restore…** replaces the current state with a backup and restarts OmniRead; the two files it replaces are kept
beside the new ones as `settings.json.bak` and `annotations.json.bak`. Books are recognised by their full path, so
positions and highlights follow a book only if it sits at the same path as before. Paths under your user folder are
moved to the new user folder when the backup comes from a different user name; books elsewhere must be on the same
drive and folder.

## Piper voices

Besides the Windows voices, read aloud can use [Piper](https://github.com/OHF-Voice/piper1-gpl), a neural speech engine
that runs on this computer. Reading settings → **Piper voices…** installs it and downloads voices.

- Piper is GPL-3.0-or-later and is not part of OmniRead. The reader downloads the official `piper-tts` package,
  with its own copy of Python, into `%LocalAppData%\OmniRead\Piper` (about 190 MB) and runs it as a separate
  program. The small bridge script it writes there is under Piper's licence.
- Every Piper voice is offered: 183 voices, in a section per language and from the lowest quality to the highest.
  Each shows the licence its `MODEL_CARD` gives and, where the card names one, the stricter terms of the voice it
  was trained from. Only 64 are public domain or CC0; many of the rest are non-commercial, research-only or require
  credit, and 30 only point to a URL for their licence. Check before using a voice for more than your own reading.
- Each voice's `MODEL_CARD` is downloaded and kept beside the voice.
- **Uninstall Piper** in the same window removes the engine and its Python, and the voices too if you choose.
- Installing Piper and downloading voices are the only things in OmniRead that use the network, and only when asked.

## Keys

| Key | Action |
|---|---|
| Ctrl+O | Open |
| ← → / PgUp PgDn / Space | Turn page |
| Ctrl+← Ctrl+→ | Previous / next chapter |
| Ctrl+T, Ctrl+, | Contents, reading settings |
| Ctrl+E | Eye comfort mode |
| Ctrl+L | Back to the library |
| Ctrl+D, Ctrl+H | Bookmark this page, highlight the selection |
| Ctrl+B | Bookmarks and highlights |
| Ctrl+R | Read aloud on / off |
| Ctrl+P, Ctrl+↑ Ctrl+↓ | While reading aloud: pause or resume, previous / next sentence |
| Ctrl+F | Search in book (in the library: search the shelves) |
| Ctrl+M | Focus mode on / off; ↑ ↓ move the lit paragraph in pages view |
| Ctrl+K | Look up the selected word |
| Ctrl + / − / wheel | Text size |
| F11 | Full screen |

## Languages

Interface text lives in `OmniRead/Resources/Strings.resx` (English) with translations beside it
(`Strings.es.resx` for Spanish). To add a language, copy `Strings.resx` to `Strings.<culture>.resx`, translate
the values and add the language to `Localizer.Languages`. The language is chosen under Reading settings →
Appearance and applies after a restart. In XAML use `{l:Loc Some_Key}`; in code `Localizer.Get("Some_Key")`.

## Building a release

    dotnet publish OmniRead -p:PublishProfile=SingleFile

writes a single self-contained `release\OmniRead.exe` for 64-bit Windows 11. Fonts, the dictionary and the
hyphenation patterns are inside it; on first run it unpacks itself to a folder under `%TEMP%\.net`. The WebView2
runtime is not inside it: Windows 11 already has it.

## Licence

OmniRead is free software under the GNU General Public License, version 3 or (at your option) any later version;
see `LICENSE`. It comes with no warranty.

Bundled material keeps its own licence: the fonts in `OmniRead/Fonts` are under the SIL Open Font License (text
beside each), the dictionary data is from WordNet 3.1 (`OmniRead/Data/WORDNET-LICENSE.txt`), and the hyphenation
patterns in `OmniRead/Data/Hyphenation` are the unmodified files of the TeX hyph-utf8 project, each with its
licence at the top (American English: Gerard Kuiken's permissive notice, including Knuth's `hyphen.tex` tables;
Spanish: MIT). The libraries AngleSharp, AngleSharp.Css (MIT), Markdig (BSD 2-Clause), VersOne.Epub (Unlicense),
HandyControls (MIT) and the Microsoft WebView2 SDK (BSD-style) are fetched from NuGet. Piper and its voices are not part of OmniRead and
are downloaded separately under their own terms.
