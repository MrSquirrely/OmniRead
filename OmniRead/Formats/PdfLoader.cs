using System.IO;

using OmniRead.Localization;
using OmniRead.Models;

using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OmniRead.Formats;

/// <summary>
/// Renders PDF pages with the Windows.Data.Pdf API from the Windows SDK. Pages are fixed images, one per
/// "chapter", rendered only when they are shown.
/// </summary>
internal static class PdfLoader {
    public static async Task<LoadedContent> LoadAsync(string path) {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);
        LoadedContent content = new() { IsNativeBook = true, FixedLayout = true };
        for (uint i = 0; i < pdf.PageCount; i++) {
            uint index = i;
            Chapter chapter = new(Localizer.Format("Pdf_Page", i + 1));
            chapter.Blocks.Add(new ImageBlock(() => RenderAsync(pdf, index), true));
            content.Chapters.Add(chapter);
        }
        return content;
    }

    private static async Task<byte[]?> RenderAsync(PdfDocument pdf, uint index) {
        try {
            using PdfPage page = pdf.GetPage(index);
            using InMemoryRandomAccessStream stream = new();
            PdfPageRenderOptions options = new() {
                DestinationWidth = (uint)Math.Clamp(page.Size.Width * 2.5, 800, 2600)
            };
            await page.RenderToStreamAsync(stream, options);
            using Stream managed = stream.AsStreamForRead();
            using MemoryStream buffer = new();
            await managed.CopyToAsync(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.COMException) {
            return null;
        }
    }
}