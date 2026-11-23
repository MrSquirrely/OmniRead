using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

using OmniRead.Models;

namespace OmniRead.Formats;

/// <summary>
/// The part of an element's computed style the reader can express. Lengths are in em of the book's base size.
/// Text properties inherit from the parent; display and margins belong to the element alone.
/// </summary>
internal readonly record struct ElementStyle(
    InlineStyle Inline,
    bool Uppercase,
    double FontScale,
    TextAlignment? Align,
    double? TextIndent,
    bool Hidden,
    bool? IsBlock,
    double MarginTop,
    double MarginBottom,
    double MarginLeft,
    double MarginRight) {
    public static ElementStyle Root { get; } = new(InlineStyle.None, false, 1, null, null, false, null, 0, 0, 0, 0);

    /// <summary>The style a child starts from before its own rules are applied.</summary>
    public ElementStyle Inherit() {
        return this with { Hidden = false, IsBlock = null, MarginTop = 0, MarginBottom = 0, MarginLeft = 0, MarginRight = 0 };
    }
}

/// <summary>
/// Resolves styles with AngleSharp.Css: it parses the stylesheets, matches selectors and orders the cascade
/// (including the browser default sheet and inline style attributes); inheritance is tracked here while the
/// converter walks the tree, which keeps the cost to one cascade per element.
/// </summary>
internal sealed partial class StyleResolver(IStyleCollection? styles) {
    /// <summary>Assumed text measure, used to turn percentage margins into em.</summary>
    private const double MeasureEm = 32;

    [GeneratedRegex(@"^(-?\d*\.?\d+)(em|rem|ex|ch|px|pt|pc|in|cm|mm|q|%|vw|vh)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Length();

    public ElementStyle Resolve(IElement element, ElementStyle parent) {
        ElementStyle style = parent.Inherit();
        if (styles is null) {
            return style with { Inline = style.Inline | TagStyle(element) };
        }
        ICssStyleDeclaration declarations;
        try {
            declarations = styles.ComputeCascadedStyle(element, null!);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException) {
            return style with { Inline = style.Inline | TagStyle(element) };
        }

        InlineStyle inline = style.Inline | TagStyle(element);
        bool uppercase = style.Uppercase;
        double scale = style.FontScale;
        TextAlignment? align = style.Align;
        double? textIndent = style.TextIndent;
        bool hidden = false;
        bool? isBlock = null;
        double paddingLeft = 0;
        double paddingRight = 0;
        double top = 0;
        double bottom = 0;
        double left = 0;
        double right = 0;

        // Font size first: em lengths on this element are relative to it.
        string fontSize = Value(declarations, "font-size");
        if (fontSize.Length > 0) {
            scale = FontSize(fontSize, style.FontScale);
        }

        foreach (ICssProperty property in declarations) {
            string value = property.Value.Trim().ToLowerInvariant();
            if (value.Length == 0) {
                continue;
            }
            switch (property.Name) {
                case "display":
                    hidden = value == "none";
                    isBlock = value is not ("inline" or "inline-block" or "contents" or "none");
                    break;
                case "visibility":
                    hidden |= value is "hidden" or "collapse";
                    break;
                case "font-style":
                    inline = Set(inline, InlineStyle.Italic, value is "italic" || value.StartsWith("oblique", StringComparison.Ordinal));
                    break;
                case "font-weight":
                    bool bold = value switch {
                        "bold" or "bolder" => true,
                        "normal" or "lighter" => false,
                        _ => int.TryParse(value, out int weight) ? weight >= 600 : inline.HasFlag(InlineStyle.Bold)
                    };
                    inline = Set(inline, InlineStyle.Bold, bold);
                    break;
                case "font-variant-caps" or "font-variant":
                    inline = Set(inline, InlineStyle.SmallCaps, value.Contains("small-caps"));
                    break;
                case "text-transform":
                    uppercase = value == "uppercase";
                    break;
                case "text-decoration-line" or "text-decoration":
                    if (value.Contains("none")) {
                        inline &= ~(InlineStyle.Underline | InlineStyle.Strike);
                    }
                    if (value.Contains("underline")) {
                        inline |= InlineStyle.Underline;
                    }
                    if (value.Contains("line-through")) {
                        inline |= InlineStyle.Strike;
                    }
                    break;
                case "vertical-align":
                    inline = Set(inline, InlineStyle.Superscript, value == "super");
                    inline = Set(inline, InlineStyle.Subscript, value == "sub");
                    break;
                case "text-align":
                    align = value switch {
                        "center" or "-webkit-center" => TextAlignment.Center,
                        "right" or "end" => TextAlignment.Right,
                        "justify" => TextAlignment.Justify,
                        _ => TextAlignment.Left
                    };
                    break;
                case "text-indent":
                    textIndent = ToEm(value, scale);
                    break;
                case "margin-top":
                    top = Math.Max(0, ToEm(value, scale));
                    break;
                case "margin-bottom":
                    bottom = Math.Max(0, ToEm(value, scale));
                    break;
                case "margin-left":
                    left = Math.Max(0, ToEm(value, scale));
                    break;
                case "margin-right":
                    right = Math.Max(0, ToEm(value, scale));
                    break;
                case "padding-left":
                    paddingLeft = Math.Max(0, ToEm(value, scale));
                    break;
                case "padding-right":
                    paddingRight = Math.Max(0, ToEm(value, scale));
                    break;
            }
        }
        return new ElementStyle(
            inline, uppercase, scale, align, textIndent, hidden, isBlock, top, bottom, left + paddingLeft, right + paddingRight);
    }

    /// <summary>The meaning carried by the tag itself; the cascade may then override it.</summary>
    private static InlineStyle TagStyle(IElement element) {
        return element.LocalName.ToLowerInvariant() switch {
            "b" or "strong" => InlineStyle.Bold,
            "i" or "em" or "cite" or "dfn" or "var" => InlineStyle.Italic,
            "code" or "kbd" or "samp" or "tt" => InlineStyle.Code,
            "u" or "ins" => InlineStyle.Underline,
            "s" or "del" or "strike" => InlineStyle.Strike,
            "sup" => InlineStyle.Superscript,
            "sub" => InlineStyle.Subscript,
            _ => InlineStyle.None
        };
    }

    private static InlineStyle Set(InlineStyle style, InlineStyle flag, bool on) {
        return on ? (style | flag) : (style & ~flag);
    }

    private static string Value(ICssStyleDeclaration declarations, string name) {
        return (declarations.GetPropertyValue(name) ?? "").Trim().ToLowerInvariant();
    }

    private static double FontSize(string value, double parentScale) {
        double? keyword = value switch {
            "xx-small" => 0.6,
            "x-small" => 0.75,
            "small" => 0.89,
            "medium" => 1,
            "large" => 1.2,
            "x-large" => 1.5,
            "xx-large" => 2,
            "smaller" => parentScale * 0.83,
            "larger" => parentScale * 1.2,
            _ => null
        };
        if (keyword is double known) {
            return known;
        }
        Match match = Length().Match(value);
        if (!match.Success) {
            return parentScale;
        }
        double number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        double result = match.Groups[2].Value.ToLowerInvariant() switch {
            "em" => parentScale * number,
            "%" => parentScale * number / 100,
            "ex" or "ch" => parentScale * number * 0.5,
            "" => parentScale,
            _ => ToEm(value, 1)
        };
        return (result > 0.05) ? result : parentScale;
    }

    private static double ToEm(string value, double fontScale) {
        Match match = Length().Match(value);
        if (!match.Success) {
            return 0;
        }
        double number = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value.ToLowerInvariant() switch {
            "em" => number * fontScale,
            "ex" or "ch" => number * fontScale * 0.5,
            "rem" => number,
            "px" => number / 16,
            "pt" => number / 12,
            "pc" => number,
            "in" => number * 6,
            "cm" => number * 6 / 2.54,
            "mm" => number * 6 / 25.4,
            "q" => number * 6 / 101.6,
            "%" or "vw" or "vh" => number / 100 * MeasureEm,
            _ => number / 16
        };
    }
}

/// <summary>Opens HTML with the CSS engine attached. Nothing is ever fetched over the network.</summary>
internal static partial class StyledHtml {
    private static readonly IConfiguration Configuration = AngleSharp.Configuration.Default.WithCss();

    private static readonly IRenderDevice Screen = new DefaultRenderDevice {
        DeviceWidth = 1280,
        DeviceHeight = 900,
        ViewPortWidth = 1280,
        ViewPortHeight = 900
    };

    [GeneratedRegex(@"@import\s+(?:url\(\s*)?[""']?([^""')\s;]+)[""']?\s*\)?[^;]*;", RegexOptions.IgnoreCase)]
    private static partial Regex Import();

    /// <summary>
    /// Parses the markup, pulls every linked stylesheet in through <paramref name="loadStylesheet"/> (which is
    /// given the href and returns CSS text, or null), and returns the document with a resolver for its styles.
    /// </summary>
    public static (IDocument Document, StyleResolver Styles) Open(IDocument plain, Func<string, string?> loadStylesheet) {
        foreach (IElement link in plain.QuerySelectorAll("link[href]").ToList()) {
            string relation = link.GetAttribute("rel") ?? "";
            if (!relation.Contains("stylesheet", StringComparison.OrdinalIgnoreCase)
                || relation.Contains("alternate", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            string? css = loadStylesheet(link.GetAttribute("href")!);
            if (css is null) {
                continue;
            }
            IElement style = plain.CreateElement("style");
            style.TextContent = css;
            link.Replace(style);
        }
        return Open(plain.DocumentElement.OuterHtml);
    }

    /// <summary>Opens markup whose styles are all inline or in style elements.</summary>
    public static (IDocument Document, StyleResolver Styles) Open(string html) {
        try {
            IBrowsingContext context = BrowsingContext.New(Configuration);
            IDocument document = context.OpenAsync(request => request.Content(html)).GetAwaiter().GetResult();
            IStyleCollection? styles = document.DefaultView?.GetStyleCollection(Screen);
            return (document, new StyleResolver(styles));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException) {
            // A stylesheet the engine cannot digest should cost the styling, not the book.
            return (new HtmlParser().ParseDocument(html), new StyleResolver(null));
        }
    }

    /// <summary>Replaces @import rules with the text they refer to, a few levels deep.</summary>
    public static string InlineImports(string css, Func<string, string?> load, int depth = 0) {
        if ((depth > 3) || !css.Contains("@import", StringComparison.OrdinalIgnoreCase)) {
            return css;
        }
        return Import().Replace(css, match => {
            string? imported = load(match.Groups[1].Value);
            return (imported is null) ? "" : InlineImports(imported, load, depth + 1);
        });
    }
}

/// <summary>
/// Publisher stylesheets describe a whole look; the reader only wants what is exceptional about a paragraph.
/// This pass finds the book's ordinary paragraph and keeps each block's differences from it, so that the
/// user's font size, spacing and justification still apply everywhere.
/// </summary>
internal static class CssNormalizer {
    public static void Normalize(IEnumerable<List<ContentBlock>> documents) {
        List<List<ContentBlock>> all = [.. documents];
        List<ParagraphBlock> body = [.. all.SelectMany(static blocks => blocks)
            .OfType<ParagraphBlock>()
            .Where(static paragraph => (paragraph.Kind == ParagraphKind.Normal)
                && (paragraph.Style is { Align: not (TextAlignment.Center or TextAlignment.Right) }))];
        bool enough = body.Sum(static paragraph => paragraph.Text.Length) >= 400;

        double scale = enough ? Dominant(body, static style => Math.Round(style.Scale, 2)) : 1;
        double space = enough ? Dominant(body, static style => Math.Round(style.SpaceBefore, 1)) : 0;
        double left = enough ? Dominant(body, static style => Math.Round(style.InsetLeft, 1)) : 0;
        double right = enough ? Dominant(body, static style => Math.Round(style.InsetRight, 1)) : 0;
        // A book counts as indented when a fair share of its text is; unindented openers are then the exception.
        double indentedText = body.Where(static paragraph => paragraph.Style!.RawIndent > 0.05).Sum(static paragraph => paragraph.Text.Length);
        bool indented = enough && (indentedText >= (0.3 * body.Sum(static paragraph => paragraph.Text.Length)));
        if (scale < 0.05) {
            scale = 1;
        }

        foreach (List<ContentBlock> blocks in all) {
            for (int i = 0; i < blocks.Count; i++) {
                if (blocks[i] is not ParagraphBlock { Style: BlockStyle raw } paragraph) {
                    continue;
                }
                bool plainKind = paragraph.Kind == ParagraphKind.Normal;
                double relativeScale = Math.Clamp(raw.Scale / scale, 0.6, 2.2);
                BlockStyle style = new(
                    (raw.Align is TextAlignment.Center or TextAlignment.Right) ? raw.Align : null,
                    null,
                    plainKind && indented && (raw.RawIndent is <= 0.05),
                    Excess(raw.SpaceBefore - space, 5),
                    plainKind ? Excess(raw.InsetLeft - left, 8) : 0,
                    plainKind ? Excess(raw.InsetRight - right, 8) : 0,
                    (Math.Abs(relativeScale - 1) < 0.06) ? 1 : relativeScale);
                blocks[i] = paragraph with { Style = style.IsDefault ? null : style };
            }
        }
    }

    private static double Excess(double value, double limit) {
        return (value < 0.4) ? 0 : Math.Min(value, limit);
    }

    /// <summary>The value carried by the most text.</summary>
    private static double Dominant(List<ParagraphBlock> paragraphs, Func<BlockStyle, double> selector) {
        return paragraphs
            .GroupBy(paragraph => selector(paragraph.Style!))
            .OrderByDescending(static group => group.Sum(static paragraph => paragraph.Text.Length + 1))
            .Select(static group => group.Key)
            .FirstOrDefault();
    }
}