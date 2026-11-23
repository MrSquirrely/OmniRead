using System.Buffers.Binary;
using System.IO;
using System.Text;

using OmniRead.Localization;
using OmniRead.Models;

using Windows.Media.MediaProperties;
using Windows.Media.SpeechSynthesis;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace OmniRead.Services;

/// <summary>Where an export has got to: which chapter of how many, and how far through it.</summary>
public readonly record struct AudiobookProgress(int Chapter, int Chapters, string Title, double Fraction);

/// <summary>
/// Reads chapters aloud into audio files, one file per chapter, with the same voices as read aloud. Each
/// paragraph is synthesised to WAV and the pieces are joined; the result is kept as WAV or encoded to M4A (AAC)
/// with the encoder that comes with Windows.
/// </summary>
public static class AudiobookExporter {
    private const double PauseAfterParagraph = 0.35;
    private const double PauseAfterHeading = 0.9;

    /// <summary>The PCM layout of a WAV file: the bytes of its "fmt " chunk, and what is needed to time a pause.</summary>
    private sealed record WaveFormat(byte[] Chunk, int BytesPerSecond, int BlockAlign);

    /// <summary>
    /// Writes one audio file per chapter into <paramref name="folder"/> and returns how many were written.
    /// Chapters without any text (cover pictures, say) are skipped.
    /// </summary>
    public static async Task<int> ExportAsync(
        Book book,
        IReadOnlyList<int> chapters,
        string folder,
        string voiceId,
        double rate,
        bool encode,
        IProgress<AudiobookProgress> progress,
        CancellationToken cancel) {
        Directory.CreateDirectory(folder);
        using SpeechSynthesizer synthesizer = new();
        VoiceInformation? voice = SpeechSynthesizer.AllVoices.FirstOrDefault(candidate => candidate.Id == voiceId);
        synthesizer.Voice = voice ?? SpeechSynthesizer.DefaultVoice;
        synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3);
        string? piperVoice = voiceId.StartsWith(Piper.VoicePrefix, StringComparison.Ordinal) ? voiceId[Piper.VoicePrefix.Length..] : null;

        int written = 0;
        for (int i = 0; i < chapters.Count; i++) {
            cancel.ThrowIfCancellationRequested();
            Chapter chapter = book.Chapters[chapters[i]];
            List<(string Text, bool Heading)> pieces = [.. chapter.Blocks
                .Select(static block => (Text: TextOf(block), Heading: block is HeadingBlock))
                .Where(static piece => piece.Text.Any(char.IsLetterOrDigit))];
            if (pieces.Count == 0) {
                continue;
            }
            // Numbered by position in the book, so the files sort in reading order.
            string name = $"{chapters[i] + 1:000} {Safe(chapter.Title)}";
            string wave = Path.Combine(folder, name + ".wav");
            try {
                await using (FileStream output = File.Create(wave)) {
                    WaveFormat? format = null;
                    long dataStart = 0;
                    for (int p = 0; p < pieces.Count; p++) {
                        cancel.ThrowIfCancellationRequested();
                        progress.Report(new AudiobookProgress(i + 1, chapters.Count, chapter.Title, p / (double)pieces.Count));
                        byte[] spoken = (piperVoice is not null)
                            ? await Piper.SynthesizeAsync(piperVoice, pieces[p].Text, rate)
                            : await SpeakAsync(synthesizer, pieces[p].Text);
                        (WaveFormat pieceFormat, ReadOnlyMemory<byte> samples) = Parse(spoken);
                        if (format is null) {
                            format = pieceFormat;
                            dataStart = WriteHeader(output, format);
                        }
                        else if (!format.Chunk.AsSpan().SequenceEqual(pieceFormat.Chunk)) {
                            throw new InvalidDataException(Localizer.Get("Audiobook_FormatChanged"));
                        }
                        await output.WriteAsync(samples, cancel);
                        // Silence between paragraphs, longer after a heading; whole sample frames only.
                        int pause = (int)(format.BytesPerSecond * (pieces[p].Heading ? PauseAfterHeading : PauseAfterParagraph));
                        await output.WriteAsync(new byte[pause - (pause % format.BlockAlign)], cancel);
                    }
                    FinishHeader(output, dataStart);
                }
                if (encode) {
                    progress.Report(new AudiobookProgress(i + 1, chapters.Count, chapter.Title, 1));
                    await EncodeAsync(wave, folder, name + ".m4a", cancel);
                    File.Delete(wave);
                }
            }
            catch {
                // A half-written file would only be mistaken for a finished chapter.
                TryDelete(wave);
                TryDelete(Path.Combine(folder, name + ".m4a"));
                throw;
            }
            written++;
        }
        return written;
    }

    /// <summary>The text of a block as it should be spoken; table cells are read one after another.</summary>
    private static string TextOf(ContentBlock block) {
        return block switch {
            ParagraphBlock paragraph => paragraph.Text,
            HeadingBlock heading => heading.Text,
            TableBlock table => string.Join(". ", table.Rows.SelectMany(static row => row).Select(ContentBlock.PlainText)),
            _ => ""
        };
    }

    private static async Task<byte[]> SpeakAsync(SpeechSynthesizer synthesizer, string text) {
        using SpeechSynthesisStream stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        using Stream managed = stream.AsStreamForRead();
        using MemoryStream buffer = new();
        await managed.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>Finds the format and the samples in a WAV file.</summary>
    private static (WaveFormat Format, ReadOnlyMemory<byte> Samples) Parse(byte[] wave) {
        if ((wave.Length < 12) || (Encoding.ASCII.GetString(wave, 0, 4) != "RIFF") || (Encoding.ASCII.GetString(wave, 8, 4) != "WAVE")) {
            throw new InvalidDataException(Localizer.Get("Audiobook_NotWave"));
        }
        WaveFormat? format = null;
        int at = 12;
        while ((at + 8) <= wave.Length) {
            string id = Encoding.ASCII.GetString(wave, at, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(at + 4));
            int start = at + 8;
            // A file written as a stream may not know its own length; the samples then run to the end.
            int length = (int)Math.Min(size, (uint)(wave.Length - start));
            if ((id == "fmt ") && (length >= 16)) {
                byte[] chunk = wave[start..(start + length)];
                format = new WaveFormat(
                    chunk,
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(8)),
                    Math.Max(1, (int)BinaryPrimitives.ReadUInt16LittleEndian(chunk.AsSpan(12))));
            }
            else if ((id == "data") && (format is not null)) {
                length -= length % format.BlockAlign;
                return (format, wave.AsMemory(start, length));
            }
            at = start + length + (length & 1);
        }
        throw new InvalidDataException(Localizer.Get("Audiobook_NotWave"));
    }

    /// <summary>Writes a WAV header with the sizes left open, and returns where the samples start.</summary>
    private static long WriteHeader(Stream output, WaveFormat format) {
        Span<byte> size = stackalloc byte[4];
        output.Write("RIFF"u8);
        output.Write(size);
        output.Write("WAVEfmt "u8);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)format.Chunk.Length);
        output.Write(size);
        output.Write(format.Chunk);
        output.Write("data"u8);
        size.Clear();
        output.Write(size);
        return output.Position;
    }

    /// <summary>Fills in the two sizes once all the samples have been written.</summary>
    private static void FinishHeader(Stream output, long dataStart) {
        Span<byte> size = stackalloc byte[4];
        long end = output.Position;
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, end - 8));
        output.Position = 4;
        output.Write(size);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)Math.Min(uint.MaxValue, end - dataStart));
        output.Position = dataStart - 4;
        output.Write(size);
        output.Position = end;
    }

    private static async Task EncodeAsync(string wave, string folder, string name, CancellationToken cancel) {
        StorageFile source = await StorageFile.GetFileFromPathAsync(wave);
        StorageFolder target = await StorageFolder.GetFolderFromPathAsync(folder);
        StorageFile destination = await target.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        MediaEncodingProfile profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Medium);
        PrepareTranscodeResult prepared = await new MediaTranscoder().PrepareFileTranscodeAsync(source, destination, profile);
        if (!prepared.CanTranscode) {
            throw new InvalidOperationException(Localizer.Format("Audiobook_CannotEncode", prepared.FailureReason));
        }
        await prepared.TranscodeAsync().AsTask(cancel);
    }

    private static string Safe(string name) {
        string cleaned = string.Concat(name.Select(static c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim().TrimEnd('.');
        return (cleaned.Length > 80) ? cleaned[..80].Trim() : cleaned;
    }

    private static void TryDelete(string file) {
        try {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left behind; nothing more can be done about it here.
        }
    }
}