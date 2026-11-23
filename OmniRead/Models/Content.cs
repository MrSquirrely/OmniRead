using System.Windows;

namespace OmniRead.Models;

[Flags]
public enum InlineStyle {
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Underline = 8,
    Strike = 16,
    Superscript = 32,
    Subscript = 64,
    SmallCaps = 128
}

/// <summary>A run of text with uniform styling. A line break is represented by '\n' inside the text.</summary>
public sealed record InlineRun(string Text, InlineStyle Style = InlineStyle.None, string? Href = null);

public enum ParagraphKind {
    Normal,
    Quote,
    Code,
    ListItem,
    Centered
}

public abstract record ContentBlock {
    /// <summary>Link targets that land on this block, each in the form "document#id" (or just "document").</summary>
    public string[]? Anchors { get; init; }

    public static string PlainText(IEnumerable<InlineRun> inlines) {
        return string.Concat(inlines.Select(static run => run.Text));
    }
}

public sealed record HeadingBlock(int Level, IReadOnlyList<InlineRun> Inlines) : ContentBlock {
    public string Text => PlainText(Inlines);
}

/// <summary>
/// Styling a stylesheet asked for on one block, in em of the base font size. <see cref="RawIndent"/> is the
/// author's text-indent before normalisation; afterwards only <see cref="NoIndent"/> is meaningful.
/// </summary>
public sealed record BlockStyle(
    TextAlignment? Align,
    double? RawIndent,
    bool NoIndent,
    double SpaceBefore,
    double InsetLeft,
    double InsetRight,
    double Scale) {
    public bool IsDefault => (Align is null) && !NoIndent && (SpaceBefore == 0) && (InsetLeft == 0) && (InsetRight == 0) && (Scale == 1);
}

public sealed record ParagraphBlock(
    IReadOnlyList<InlineRun> Inlines,
    ParagraphKind Kind = ParagraphKind.Normal,
    int Depth = 0,
    string? Marker = null,
    BlockStyle? Style = null) : ContentBlock {
    public string Text => PlainText(Inlines);
}

public sealed record SceneBreakBlock : ContentBlock;

/// <summary>An image whose encoded bytes are read on demand, so large books do not hold every picture in memory.</summary>
public sealed record ImageBlock(Func<Task<byte[]?>> Load, bool FullPage = false) : ContentBlock;

public sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<InlineRun>>> Rows, bool HasHeader) : ContentBlock;