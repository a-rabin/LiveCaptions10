using System;

namespace LiveCaptions10;

/// <summary>A streaming recognizer: audio in, words out as they are spoken (Vosk or sherpa-onnx).</summary>
internal interface IRecognizer : IDisposable
{
    string ModelPath { get; }

    string Name { get; }

    /// <summary>The sentence in progress (may be revised). Empty text clears it.</summary>
    event Action<PartialUpdate>? Partial;

    /// <summary>A finished sentence with its audio.</summary>
    event Action<FinalUpdate>? Final;

    void Accept(short[] pcm, int count);

    /// <summary>Finishes the current sentence and returns it.</summary>
    FinalUpdate Flush();

    void Reset();

    /// <summary>Audio for a live Whisper pass, or null when not supported / not due.</summary>
    LiveSnapshot? SnapshotLive(long previousUtterance, int previousLength, int minGrowth);
}

/// <summary>A whole-clip transcriber used for the second pass on finished sentences (Whisper or Parakeet).</summary>
internal interface IClipTranscriber : IDisposable
{
    string Name { get; }

    string ModelPath { get; }

    /// <summary>Transcribes one clip; null if it holds too little sound to bother.</summary>
    string? Transcribe(short[] audio, out TimeSpan elapsed);
}

internal static class ClipAudio
{
    const int SampleRate = AudioPipeline.SampleRate;

    /// <summary>
    /// Trims leading/trailing silence (keeping 250 ms either side), caps the clip at 29 s and converts it to
    /// float, padding to <paramref name="minSamples"/>. Null when there is under 0.3 s of sound.
    /// </summary>
    public static float[]? Prepare(short[] audio, int minSamples)
    {
        const int frame = SampleRate / 50; // 20 ms
        const double speechRms = 150;      // ≈ -47 dBFS
        int first = -1, last = -1;
        for (int f = 0; f + frame <= audio.Length; f += frame)
        {
            double sum = 0;
            for (int i = f; i < f + frame; i++) sum += (double)audio[i] * audio[i];
            if (Math.Sqrt(sum / frame) > speechRms)
            {
                if (first < 0) first = f;
                last = f + frame;
            }
        }
        if (first < 0) return null;

        int pad = SampleRate / 4;
        int end = Math.Min(audio.Length, last + pad);
        int start = Math.Max(0, Math.Max(first - pad, end - SampleRate * 29));
        if (last - first < SampleRate * 3 / 10) return null;

        var samples = new float[Math.Max(end - start, minSamples)];
        for (int i = start; i < end; i++) samples[i - start] = audio[i] / 32768f;
        return samples;
    }
}
