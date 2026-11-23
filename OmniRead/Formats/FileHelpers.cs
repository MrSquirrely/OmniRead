using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;

namespace OmniRead.Formats;

internal static class FileHelpers {
    /// <summary>Reads text honouring a BOM, then strict UTF-8, then falling back to Windows-1252.</summary>
    public static string ReadText(string path) {
        byte[] bytes = File.ReadAllBytes(path);
        try {
            using StreamReader reader = new(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException) {
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    public static XDocument LoadXml(Stream stream) {
        XmlReaderSettings settings = new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader);
    }

    public static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) {
        return zip.GetEntry(name)
            ?? zip.Entries.FirstOrDefault(entry => string.Equals(entry.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Resolves a relative href against a directory inside an archive, collapsing "." and "..".</summary>
    public static string ResolveZipPath(string baseDirectory, string href) {
        int cut = href.IndexOfAny(['#', '?']);
        if (cut >= 0) {
            href = href[..cut];
        }
        href = Uri.UnescapeDataString(href).Replace('\\', '/');
        List<string> parts = href.StartsWith('/')
            ? []
            : [.. baseDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string part in href.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (part == "..") {
                if (parts.Count > 0) {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".") {
                parts.Add(part);
            }
        }
        return string.Join('/', parts);
    }

    public static string ZipDirectory(string entryPath) {
        int slash = entryPath.LastIndexOf('/');
        return (slash < 0) ? "" : entryPath[..slash];
    }

    public static ImageSource? Decode(byte[] bytes) {
        try {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException) {
            return null;
        }
    }

    public static Func<Task<ImageSource?>> ZipImage(string zipPath, string entryName) {
        return () => Task.Run(() => {
            try {
                using ZipArchive zip = ZipFile.OpenRead(zipPath);
                ZipArchiveEntry? entry = FindEntry(zip, entryName);
                if (entry is null) {
                    return null;
                }
                using Stream stream = entry.Open();
                using MemoryStream buffer = new();
                stream.CopyTo(buffer);
                return Decode(buffer.ToArray());
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) {
                return null;
            }
        });
    }

    /// <summary>Reads a stylesheet that sits beside an HTML file, with @import rules resolved. Local files only.</summary>
    public static string? FileStylesheet(string baseDirectory, string href, int depth) {
        if (href.Contains("://") || href.StartsWith("//", StringComparison.Ordinal) || (depth > 3)) {
            return null;
        }
        int cut = href.IndexOfAny(['#', '?']);
        if (cut >= 0) {
            href = href[..cut];
        }
        try {
            string path = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(href)));
            if (!File.Exists(path)) {
                return null;
            }
            string directory = Path.GetDirectoryName(path) ?? baseDirectory;
            return StyledHtml.InlineImports(ReadText(path), import => FileStylesheet(directory, import, depth + 1));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Resolves an image reference from an HTML or Markdown file on disk. Remote images are never fetched.</summary>
    public static Func<Task<ImageSource?>>? FileImage(string baseDirectory, string source) {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) {
            int comma = source.IndexOf(',');
            if ((comma < 0) || !source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }
            string payload = source[(comma + 1)..];
            return () => Task.Run(() => {
                try {
                    return Decode(Convert.FromBase64String(payload));
                }
                catch (FormatException) {
                    return null;
                }
            });
        }
        if (source.Contains("://")) {
            return null;
        }
        int cut = source.IndexOfAny(['#', '?']);
        if (cut >= 0) {
            source = source[..cut];
        }
        string path;
        try {
            path = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(source)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
            return null;
        }
        if (!File.Exists(path)) {
            return null;
        }
        return () => Task.Run(() => {
            try {
                return Decode(File.ReadAllBytes(path));
            }
            catch (IOException) {
                return null;
            }
        });
    }
}