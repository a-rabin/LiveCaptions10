using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using SherpaOnnx;

namespace LiveCaptions10;

internal enum RecognizerKind
{
    Vosk,
    NemoStreaming,
    Kroko,          // sherpa-onnx release build of Kroko (smaller model, 1.28 s chunks)
    KrokoLFast,     // Kroko Community "64-L" package: larger model, 1.28 s chunks
    KrokoL,         // Kroko Community "128-L" package: larger model, 2.56 s chunks (too slow for live use)
}

/// <summary>Locates the sherpa-onnx models in data\sherpa.</summary>
internal static class SherpaModels
{
    public static string Dir => Path.Combine(SettingsStore.AppDataDir, "sherpa");

    public static string? Streaming(RecognizerKind kind) => kind switch
    {
        RecognizerKind.NemoStreaming => Find("sherpa-onnx-nemo-streaming-fast-conformer-transducer-en-"),
        RecognizerKind.Kroko => Find("sherpa-onnx-streaming-zipformer-en-kroko"),
        RecognizerKind.KrokoLFast => KrokoPackage.Ensure(Dir, "Kroko-EN-Community-64-L-Streaming-001"),
        RecognizerKind.KrokoL => KrokoPackage.Ensure(Dir, "Kroko-EN-Community-128-L-Streaming-001"),
        _ => null,
    };

    public static string DisplayName(RecognizerKind kind) => kind switch
    {
        RecognizerKind.NemoStreaming => "NeMo streaming FastConformer",
        RecognizerKind.Kroko => "Kroko streaming",
        RecognizerKind.KrokoLFast => "Kroko L",
        RecognizerKind.KrokoL => "Kroko L slow",
        _ => "Vosk",
    };

    public static string? Punctuation => Find("sherpa-onnx-online-punct-en");

    public static string? Parakeet => Find("sherpa-onnx-nemo-parakeet-tdt-0.6b-v2");

    static string? Find(string prefix) =>
        Directory.Exists(Dir)
            ? Directory.GetDirectories(Dir).Where(d => Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(d => d).FirstOrDefault()
            : null;

    /// <summary>encoder/decoder/joiner file in a model folder, preferring the int8 build.</summary>
    public static string Part(string dir, string part)
    {
        var files = Directory.GetFiles(dir, part + "*.onnx");
        return files.FirstOrDefault(f => f.Contains("int8", StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault()
               ?? throw new FileNotFoundException($"No {part}*.onnx in {dir}");
    }
}

/// <summary>
/// Kroko Community packages (huggingface.co/Banafo/Kroko-ASR) ship as one ".data" file: a little-endian
/// u32-length-prefixed JSON header, then four u32-length-prefixed blobs (encoder.onnx, decoder.onnx,
/// joiner.onnx, tokens.txt). Free packages are plain; commercial ones are encrypted and are refused.
/// </summary>
internal static class KrokoPackage
{
    static readonly string[] Parts = { "encoder.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt" };

    /// <summary>The unpacked folder for a package, unpacking "name.data" on first use; null if absent.</summary>
    public static string? Ensure(string root, string name)
    {
        var folder = Path.Combine(root, name);
        if (Parts.All(p => File.Exists(Path.Combine(folder, p)))) return folder;
        var package = Path.Combine(root, name + ".data");
        if (!File.Exists(package)) return null;
        Unpack(package, folder);
        return folder;
    }

    static void Unpack(string package, string folder)
    {
        var temp = folder + ".unpacking";
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        Directory.CreateDirectory(temp);
        using (var input = File.OpenRead(package))
        using (var reader = new BinaryReader(input))
        {
            var header = reader.ReadBytes(checked((int)reader.ReadUInt32()));
            using (var json = System.Text.Json.JsonDocument.Parse(header))
            {
                if (!json.RootElement.TryGetProperty("free", out var free) || free.ValueKind != System.Text.Json.JsonValueKind.True)
                    throw new InvalidDataException("This Kroko package is a commercial/encrypted one, which only Kroko's own runtime can load.");
            }
            File.WriteAllBytes(Path.Combine(temp, "header.json"), header);

            var buffer = new byte[1 << 20];
            foreach (var part in Parts)
            {
                long remaining = reader.ReadUInt32();
                if (remaining > input.Length - input.Position) throw new InvalidDataException("Truncated Kroko package: " + part);
                using var output = File.Create(Path.Combine(temp, part));
                while (remaining > 0)
                {
                    int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (n <= 0) throw new EndOfStreamException(part);
                    output.Write(buffer, 0, n);
                    remaining -= n;
                }
            }
            if (input.Position != input.Length) throw new InvalidDataException("Unexpected data after the Kroko package contents.");
        }
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.Move(temp, folder);
        Log.Info($"Unpacked {Path.GetFileName(package)} into {folder}");
    }
}

/// <summary>
/// Streaming transducer recognizer (sherpa-onnx): the same family of model Windows 11 Live Captions uses.
/// Each 100 ms of audio is processed once, with words emitted as they're heard; an endpoint detector ends
/// sentences at pauses, and a small streaming punctuation model formats each finished sentence.
/// </summary>
internal sealed class SherpaEngine : IRecognizer
{
    const int SampleRate = AudioPipeline.SampleRate;
    const short SilenceLevel = 40;
    const int SilenceSkipAfter = SampleRate * 2;
    const int MaxUtteranceSamples = SampleRate * 30;

    readonly object gate = new();
    readonly OnlineRecognizer recognizer;
    readonly OnlineStream stream;
    readonly OnlinePunctuation? punctuation;
    string lastPartial = "";
    long utteranceNumber = 1;
    int silentRun;
    bool disposed, soundSinceFinish;
    short[] utterance = new short[SampleRate * 8];
    int utteranceLength;

    public SherpaEngine(RecognizerKind kind, string modelDir, string? punctuationDir)
    {
        ModelPath = modelDir;
        Name = SherpaModels.DisplayName(kind);

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = SherpaModels.Part(modelDir, "encoder");
        config.ModelConfig.Transducer.Decoder = SherpaModels.Part(modelDir, "decoder");
        config.ModelConfig.Transducer.Joiner = SherpaModels.Part(modelDir, "joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;
        // Sentence ends: 0.8 s pause after speech, 2 s of silence alone, or 15 s without a pause.
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.0f;
        config.Rule2MinTrailingSilence = 0.8f;
        config.Rule3MinUtteranceLength = 15f;
        recognizer = new OnlineRecognizer(config);
        stream = recognizer.CreateStream();

        if (punctuationDir != null)
        {
            var punct = new OnlinePunctuationConfig();
            punct.Model.CnnBiLstm = Path.Combine(punctuationDir, "model.onnx");
            punct.Model.BpeVocab = Path.Combine(punctuationDir, "bpe.vocab");
            punct.Model.NumThreads = 1;
            punct.Model.Provider = "cpu";
            punctuation = new OnlinePunctuation(punct);
        }
    }

    public string ModelPath { get; }

    public string Name { get; }

    public event Action<PartialUpdate>? Partial;

    public event Action<FinalUpdate>? Final;

    /// <summary>Time spent in the recognizer (for benchmarks).</summary>
    public TimeSpan Busy { get; private set; }

    public void Accept(short[] pcm, int count)
    {
        PartialUpdate? partial = null;
        FinalUpdate? final = null;
        lock (gate)
        {
            if (disposed) return;
            if (IsSilent(pcm, count))
            {
                if (silentRun > SilenceSkipAfter) return;
                silentRun += count;
                if (silentRun > SilenceSkipAfter && soundSinceFinish)
                {
                    // Going idle: the model only decodes whole chunks (1.28 s for Kroko), so the end of the
                    // last sentence may still be waiting. Push enough silence through to finish it.
                    final = FinishPending();
                    goto Raise;
                }
            }
            else
            {
                silentRun = 0;
                soundSinceFinish = true;
            }

            var clock = Stopwatch.StartNew();
            AppendUtterance(pcm, count);
            var samples = new float[count];
            for (int i = 0; i < count; i++) samples[i] = pcm[i] / 32768f;
            stream.AcceptWaveform(SampleRate, samples);
            while (recognizer.IsReady(stream)) recognizer.Decode(stream);
            var text = Tidy(recognizer.GetResult(stream).Text);

            if (recognizer.IsEndpoint(stream))
            {
                final = Finish(text);
                recognizer.Reset(stream);
            }
            else if (text != lastPartial)
            {
                lastPartial = text;
                partial = new PartialUpdate(utteranceNumber, text);
            }
            Busy += clock.Elapsed;
        }

        Raise:
        if (final != null)
        {
            if (final.Text.Length > 0) Final?.Invoke(final);
            else Partial?.Invoke(new PartialUpdate(final.Utterance, ""));
        }
        else if (partial != null)
        {
            Partial?.Invoke(partial);
        }
    }

    /// <summary>Flushes the model's last partial chunk with silence and closes the open sentence (caller holds the lock).</summary>
    FinalUpdate FinishPending()
    {
        stream.AcceptWaveform(SampleRate, new float[SampleRate * 3 / 2]); // > one 1.28 s chunk
        while (recognizer.IsReady(stream)) recognizer.Decode(stream);
        var update = Finish(Tidy(recognizer.GetResult(stream).Text));
        recognizer.Reset(stream);
        return update;
    }

    public FinalUpdate Flush()
    {
        lock (gate)
        {
            if (disposed) return new FinalUpdate(0, "", Array.Empty<TimedWord>(), Array.Empty<short>());
            var update = Finish(Tidy(recognizer.GetResult(stream).Text));
            recognizer.Reset(stream);
            return update;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            if (disposed) return;
            recognizer.Reset(stream);
            lastPartial = "";
            utteranceLength = 0;
            utteranceNumber++;
        }
    }

    // The transducer emits words as they're heard; a live Whisper layer would add nothing.
    public LiveSnapshot? SnapshotLive(long previousUtterance, int previousLength, int minGrowth) => null;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stream.Dispose();
            recognizer.Dispose();
            punctuation?.Dispose();
        }
    }

    FinalUpdate Finish(string text)
    {
        // Some models (Kroko) already emit punctuation and casing; only punctuate the ones that don't (NeMo).
        bool punctuated = text.IndexOfAny(new[] { '.', ',', '?', '!' }) >= 0;
        if (text.Length > 0 && punctuation != null && !punctuated) text = punctuation.AddPunct(text).Trim();
        var update = new FinalUpdate(utteranceNumber, text, Array.Empty<TimedWord>(), utterance[..utteranceLength]);
        utteranceNumber++;
        utteranceLength = 0;
        lastPartial = "";
        soundSinceFinish = false;
        return update;
    }

    /// <summary>Some models emit upper case (LibriSpeech style); the rest of the app expects lower case drafts.</summary>
    static string Tidy(string text)
    {
        text = text.Trim();
        return text.Any(char.IsLower) ? text : text.ToLowerInvariant();
    }

    void AppendUtterance(short[] pcm, int count)
    {
        if (utteranceLength + count > MaxUtteranceSamples)
        {
            int drop = utteranceLength + count - MaxUtteranceSamples;
            Array.Copy(utterance, drop, utterance, 0, utteranceLength - drop);
            utteranceLength -= drop;
        }
        if (utteranceLength + count > utterance.Length)
            Array.Resize(ref utterance, Math.Min(MaxUtteranceSamples, Math.Max(utterance.Length * 2, utteranceLength + count)));
        Array.Copy(pcm, 0, utterance, utteranceLength, count);
        utteranceLength += count;
    }

    static bool IsSilent(short[] pcm, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int v = pcm[i];
            if (v > SilenceLevel || v < -SilenceLevel) return false;
        }
        return true;
    }
}

/// <summary>
/// NVIDIA Parakeet TDT 0.6B (via sherpa-onnx) as the second pass on finished sentences: very accurate,
/// punctuated and cased, and, unlike Whisper, it can't free-run into invented text.
/// </summary>
internal sealed class ParakeetRunner : IClipTranscriber
{
    readonly OfflineRecognizer recognizer;

    public ParakeetRunner(string modelDir)
    {
        ModelPath = modelDir;
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = AudioPipeline.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = SherpaModels.Part(modelDir, "encoder");
        config.ModelConfig.Transducer.Decoder = SherpaModels.Part(modelDir, "decoder");
        config.ModelConfig.Transducer.Joiner = SherpaModels.Part(modelDir, "joiner");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.ModelType = "nemo_transducer";
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2 - 2, 2, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";
        recognizer = new OfflineRecognizer(config);
    }

    public string Name => "parakeet-tdt-0.6b-v2";

    public string ModelPath { get; }

    public string? Transcribe(short[] audio, out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        var samples = ClipAudio.Prepare(audio, 0);
        if (samples == null) return null;
        var clock = Stopwatch.StartNew();
        using var clip = recognizer.CreateStream();
        clip.AcceptWaveform(AudioPipeline.SampleRate, samples);
        recognizer.Decode(clip);
        elapsed = clock.Elapsed;
        return clip.Result.Text;
    }

    public void Dispose() => recognizer.Dispose();
}
