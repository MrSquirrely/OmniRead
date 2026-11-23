using System.IO;
using System.Text.RegularExpressions;

using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

using OmniRead.Models;

using Markdig;

namespace OmniRead.Formats;

internal static partial class MarkdownLoader {
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .Build();

    [GeneratedRegex(@"\A---\s*\n(.*?)\n(---|\.\.\.)\s*\n", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^(title|author)\s*:\s*[""']?(.+?)[""']?\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex FrontMatterField();

    public static LoadedContent Load(string path) {
        string text = FileHelpers.ReadText(path).Replace("\r\n", "\n");
        LoadedContent content = new();
        Match frontMatter = FrontMatter().Match(text);
        if (frontMatter.Success) {
            foreach (Match field in FrontMatterField().Matches(frontMatter.Groups[1].Value)) {
                if (field.Groups[1].Value.Equals("title", StringComparison.OrdinalIgnoreCase)) {
                    content.Title ??= field.Groups[2].Value;
                }
                else {
                    content.Author ??= field.Groups[2].Value;
                }
            }
        }
        string html = Markdown.ToHtml(text, Pipeline);
        (IDocument document, StyleResolver styles) = StyledHtml.Open(html);
        string directory = Path.GetDirectoryName(path) ?? "";
        HtmlConverter converter = new(styles, source => FileHelpers.FileImage(directory, source), "", static href => href.StartsWith('#') ? "book:" + href : null);
        Chapter chapter = new(content.Title ?? Path.GetFileNameWithoutExtension(path));
        chapter.Blocks.AddRange(converter.Convert(document.Body ?? (INode)document));
        CssNormalizer.Normalize([chapter.Blocks]);
        content.Chapters.Add(chapter);
        return content;
    }
}

internal static class HtmlLoader {
    public static LoadedContent Load(string path) {
        string directory = Path.GetDirectoryName(path) ?? "";
        IDocument plain;
        using (FileStream stream = File.OpenRead(path)) {
            // Parsing from the stream honours the page's declared character set.
            plain = new HtmlParser().ParseDocument(stream);
        }
        (IDocument document, StyleResolver styles) = StyledHtml.Open(plain, href => FileHelpers.FileStylesheet(directory, href, 0));
        HtmlConverter converter = new(styles, source => FileHelpers.FileImage(directory, source), "", static href => href.StartsWith('#') ? "book:" + href : null);
        LoadedContent content = new() {
            Title = string.IsNullOrWhiteSpace(document.Title) ? null : document.Title.Trim(),
            Author = document.QuerySelector("meta[name=author]")?.GetAttribute("content")
        };
        Chapter chapter = new(content.Title ?? Path.GetFileNameWithoutExtension(path));
        chapter.Blocks.AddRange(converter.Convert(document.Body ?? (INode)document));
        CssNormalizer.Normalize([chapter.Blocks]);
        content.Chapters.Add(chapter);
        return content;
    }
}