using System.Runtime.InteropServices.WindowsRuntime;

using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace OmniRead.Services;

/// <summary>A voice installed in Windows. An empty id means the Windows default voice.</summary>
public sealed record NarratorVoice(string Id, string Name);

/// <summary>Speaks text with the Windows speech synthesiser (Windows.Media.SpeechSynthesis).</summary>
public sealed class Narrator : IDisposable {
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly MediaPlayer _player = new();
    private TaskCompletionSource<bool>? _playing;

    public Narrator() {
        // Both events arrive on a media thread; completing the task hands control back to the caller's thread.
        _player.MediaEnded += (_, _) => _playing?.TrySetResult(true);
        _player.MediaFailed += (_, _) => _playing?.TrySetResult(true);
    }

    public static IReadOnlyList<NarratorVoice> Voices() {
        return [.. SpeechSynthesizer.AllVoices
            .OrderBy(static voice => voice.Language, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static voice => voice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static voice => new NarratorVoice(voice.Id, $"{voice.DisplayName} ({voice.Language})")),
            .. Piper.InstalledVoices.Select(static voice => new NarratorVoice(Piper.VoicePrefix + voice.Key, $"Piper · {voice.DisplayName}"))];
    }

    /// <summary>Speaks the text. Returns true when it was spoken to the end, false when it was stopped.</summary>
    public async Task<bool> SpeakAsync(string text, string voiceId, double rate) {
        Stop();
        TaskCompletionSource<bool> playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _playing = playing;
        VoiceInformation? voice = SpeechSynthesizer.AllVoices.FirstOrDefault(candidate => candidate.Id == voiceId);
        if (voiceId.StartsWith(Piper.VoicePrefix, StringComparison.Ordinal)) {
            // Piper answers with a whole WAV file for the sentence.
            byte[] wave = await Piper.SynthesizeAsync(voiceId[Piper.VoicePrefix.Length..], text, rate);
            if (playing.Task.IsCompleted) {
                return false;
            }
            InMemoryRandomAccessStream audio = new();
            await audio.WriteAsync(wave.AsBuffer());
            audio.Seek(0);
            _player.Source = MediaSource.CreateFromStream(audio, "audio/wav");
            _player.Play();
            return await playing.Task;
        }
        _synthesizer.Voice = voice ?? SpeechSynthesizer.DefaultVoice;
        _synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 3);
        SpeechSynthesisStream stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
        if (playing.Task.IsCompleted) {
            return false;
        }
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
        return await playing.Task;
    }

    public void Stop() {
        _playing?.TrySetResult(false);
        _playing = null;
        _player.Pause();
        _player.Source = null;
    }

    public void Dispose() {
        Stop();
        _player.Dispose();
        _synthesizer.Dispose();
    }
}