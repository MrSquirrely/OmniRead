using System.Text;
using System.Windows.Documents;

namespace OmniRead.Rendering;

/// <summary>
/// Converts between places in a typeset block and plain character offsets. Offsets count the block's visible
/// characters, with a line break counting as one, so they stay valid when the chapter is typeset again.
/// </summary>
public static class TextPositions {
    /// <summary>The block's text as it is counted by the other methods; a line break is '\n'.</summary>
    public static string TextOf(TextElement block) {
        StringBuilder text = new();
        Walk(block, block.ContentEnd, int.MaxValue, text);
        return text.ToString();
    }

    public static int OffsetOf(TextElement block, TextPointer position) {
        return Walk(block, position, int.MaxValue, null).Count;
    }

    public static TextPointer PointerAt(TextElement block, int offset) {
        return Walk(block, block.ContentEnd, Math.Max(0, offset), null).Position;
    }

    /// <summary>
    /// Walks forward from the start of the block until either <paramref name="stop"/> or
    /// <paramref name="limit"/> characters are reached, whichever comes first.
    /// </summary>
    private static (int Count, TextPointer Position) Walk(TextElement block, TextPointer stop, int limit, StringBuilder? text) {
        int count = 0;
        TextPointer? pointer = block.ContentStart;
        while ((pointer is not null) && (pointer.CompareTo(stop) < 0)) {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text) {
                int run = pointer.GetTextRunLength(LogicalDirection.Forward);
                TextPointer next = pointer.GetPositionAtOffset(run);
                if (next.CompareTo(stop) > 0) {
                    run = pointer.GetOffsetToPosition(stop);
                }
                if ((count + run) >= limit) {
                    return (limit, pointer.GetPositionAtOffset(limit - count));
                }
                text?.Append(pointer.GetTextInRun(LogicalDirection.Forward).AsSpan(0, run));
                count += run;
                pointer = pointer.GetPositionAtOffset(run);
                continue;
            }
            if ((pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart)
                && (pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)) {
                if (count >= limit) {
                    return (count, pointer);
                }
                text?.Append('\n');
                count++;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return (count, (pointer is null) || (pointer.CompareTo(stop) > 0) ? stop : pointer);
    }
}