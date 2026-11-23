using System.Windows;

namespace OmniRead.Models;

public sealed class Chapter(string title) {
    public string Title { get; set; } = title;

    public List<ContentBlock> Blocks { get; } = [];
}

public sealed record TocEntry(string Title, int Level, int ChapterIndex, int BlockIndex) {
    public Thickness Indent => new(Level * 16, 0, 0, 0);

    public FontWeight Weight => (Level == 0) ? FontWeights.SemiBold : FontWeights.Normal;
}

/// <summary>What a format loader produces before the document is assembled into a <see cref="Book"/>.</summary>
public sealed class LoadedContent {
    public string? Title { get; set; }

    public string? Author { get; set; }

    /// <summary>The language the book says it is written in, as a code such as "en" or "pt-BR".</summary>
    public string? Language { get; set; }

    public List<Chapter> Chapters { get; } = [];

    /// <summary>True for formats that already carry book structure (EPUB, FB2, PDF).</summary>
    public bool IsNativeBook { get; init; }

    /// <summary>True when pages are pre-rendered images that cannot be reflowed (PDF).</summary>
    public bool FixedLayout { get; init; }
}

/// <summary>Where a link inside the book leads.</summary>
public readonly record struct BookAnchor(int ChapterIndex, int BlockIndex);

public sealed class Book {
    public required string Title { get; init; }

    public string? Author { get; init; }

    /// <summary>The book's language code when the file declares one; hyphenation follows it.</summary>
    public string? Language { get; init; }

    public required string FilePath { get; init; }

    public required IReadOnlyList<Chapter> Chapters { get; init; }

    public required IReadOnlyList<TocEntry> Toc { get; init; }

    /// <summary>Targets of the book's internal links (footnotes, cross-references), keyed as in <see cref="ContentBlock.Anchors"/>.</summary>
    public IReadOnlyDictionary<string, BookAnchor> Anchors { get; init; } = new Dictionary<string, BookAnchor>();

    /// <summary>Use book typography: justified text, first-line indents, centred chapter openers.</summary>
    public bool BookLayout { get; init; }

    public bool FixedLayout { get; init; }

    /// <summary>True when the "format like a book" option applies to this file type.</summary>
    public bool CanBookFormat { get; init; }
}