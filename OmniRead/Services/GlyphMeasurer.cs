using System.Windows;
using System.Windows.Media;

using OmniRead.Models;

namespace OmniRead.Services;

/// <summary>
/// Character widths taken from the reading font itself, in its regular, bold and italic cuts. Made and used on
/// the thread that paginates; nothing here touches the window.
/// </summary>
public sealed class GlyphMeasurer(string fontName, IReadOnlyList<FontFace> faces) : ITextMeasurer {
    /// <summary>Cascadia Mono and Consolas, which code is set in, are a little under six tenths of an em wide.</summary>
    private const double MonoAdvance = 0.57;

    private readonly Dictionary<int, GlyphTypeface?> _cuts = [];
    private readonly Dictionary<(int Cut, char Character), double> _advances = [];

    public double Advance(char character, InlineStyle style) {
        if (style.HasFlag(InlineStyle.Code)) {
            return MonoAdvance;
        }
        int cut = (style.HasFlag(InlineStyle.Bold) ? 1 : 0) | (style.HasFlag(InlineStyle.Italic) ? 2 : 0);
        if (_advances.TryGetValue((cut, character), out double known)) {
            return known;
        }
        if (!_cuts.TryGetValue(cut, out GlyphTypeface? glyphs)) {
            glyphs = Open((cut & 1) != 0, (cut & 2) != 0);
            _cuts[cut] = glyphs;
        }
        double advance = ((glyphs is not null) && glyphs.CharacterToGlyphMap.TryGetValue(character, out ushort glyph))
            ? glyphs.AdvanceWidths[glyph]
            // Not in the font: ideographs and the like are a full em wide, most other things about half.
            : (character >= 0x2E80) ? 1 : 0.5;
        _advances[(cut, character)] = advance;
        return advance;
    }

    private GlyphTypeface? Open(bool bold, bool italic) {
        try {
            if (faces.Count > 0) {
                // A font served from its files: the cut nearest the one asked for.
                FontFace best = faces.OrderBy(face => (face.Italic == italic) ? 0 : 1)
                    .ThenBy(face => Math.Abs(face.Weight - (bold ? 700 : 400)))
                    .First();
                return new GlyphTypeface(new Uri(best.File));
            }
            foreach (string family in (string[])[fontName, "Georgia"]) {
                Typeface typeface = new(
                    new FontFamily(family), italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStretches.Normal);
                if (typeface.TryGetGlyphTypeface(out GlyphTypeface found)) {
                    return found;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) {
            // A font that cannot be opened is measured with the fallback widths.
        }
        return null;
    }
}