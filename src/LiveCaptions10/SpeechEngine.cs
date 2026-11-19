using System;
using System.Collections.Generic;
using System.Text.Json;
using Vosk;

namespace LiveCaptions10;

/// <summary>A recognized word with its time on the recognizer's clock (seconds of audio fed).</summary>
internal readonly record struct TimedWord(string Word, double Start, double End);

/// <summary>The sentence in progress. Utterance numbers increase with every finished sentence.</summary>
internal sealed record PartialUpdate(long Utterance, string Text);

/// <summary>A finished sentence, its words and the audio it was recognized from.</summary>
internal sealed record FinalUpdate(long Utterance, string Text, TimedWord[] Words, short[] Audio);

/// <summary>Audio of the sentence in progress up to the end of its last settled word (of which there are CoveredWords).</summary>
internal sealed record LiveSnapshot(long Utterance, short[] Audio, double CoveredUntil, int CoveredWords);

/// <summary>Offline streaming speech recognition (Vosk / Kaldi). Thread-safe.</summary>
internal sealed class SpeechEngine : IRecognizer
{
    const short SilenceLevel = 40;                                  // ≈ -58 dBFS
    const int SilenceSkipAfter = AudioPipeline.SampleRate * 2;      // keep feeding 2 s so the last sentence finishes

    static readonly int PartialIntervalSamples = ReadPartialInterval();

    const int MaxUtteranceSamples = AudioPipeline.SampleRate * 30;

    readonly object gate = new();
    readonly Model model;
    readonly VoskRecognizer recognizer;
    string lastPartial = "";
    int samplesSincePartial, silentRun;
    bool disposed;

    // Audio of the utterance in progress, handed out with each final result (for the Whisper pass).
    // Vosk reports word times on its own clock (samples fed since it was created); utteranceStart maps
    // utterance[0] onto that clock so each sentence can be cut at its first and last word.
    const int ForceFinalSamples = AudioPipeline.SampleRate * 15;
    short[] utterance = new short[AudioPipeline.SampleRate * 8];
    int utteranceLength;
    long fedSamples, utteranceStart;
    long utteranceNumber = 1;
    bool warnedClock;

    public SpeechEngine(string modelPath)
    {
        if (!ModelManager.IsModel(modelPath)) throw new ArgumentException("Not a Vosk model folder: " + modelPath);
        global::Vosk.Vosk.SetLogLevel(-1);
        ModelPath = modelPath;
        model = new Model(modelPath);
        recognizer = new VoskRecognizer(model, AudioPipeline.SampleRate);
        recognizer.SetMaxAlternatives(0);
        recognizer.SetWords(true);        // word timings, used to cut each sentence's audio precisely
        // Partial-word timings make Vosk refresh its partial text ~3x less often, so they are only switched
        // on for the one call that takes a live snapshot (see SnapshotLive).
        recognizer.SetPartialWords(false);
    }

    public string ModelPath { get; }

    public string Name => "Vosk";

    /// <summary>The words of the utterance in progress (may be revised). Empty text clears it.</summary>
    public event Action<PartialUpdate>? Partial;

    /// <summary>A finished utterance.</summary>
    public event Action<FinalUpdate>? Final;

    public void Accept(short[] pcm, int count)
    {
        PartialUpdate? partial = null;
        FinalUpdate? final = null;
        lock (gate)
        {
            if (disposed) return;

            // Nothing playing: after a short tail of silence, stop running the recognizer at all.
            if (IsSilent(pcm, count))
            {
                if (silentRun > SilenceSkipAfter) return;
                silentRun += count;
            }
            else
            {
                silentRun = 0;
            }

            AppendUtterance(pcm, count);
            fedSamples += count;
            string? finalJson = null;
            if (recognizer.AcceptWaveform(pcm, count)) finalJson = recognizer.Result();
            else if (utteranceLength >= ForceFinalSamples && lastPartial.Length > 0)
                finalJson = recognizer.FinalResult(); // no pause for 15 s (music bed?): cut here so captions settle

            if (finalJson != null)
            {
                final = Finish(finalJson);
            }
            else
            {
                samplesSincePartial += count;
                if (samplesSincePartial >= PartialIntervalSamples)
                {
                    samplesSincePartial = 0;
                    var p = Extract(recognizer.PartialResult(), "partial");
                    if (p != lastPartial)
                    {
                        lastPartial = p;
                        partial = new PartialUpdate(utteranceNumber, p);
                    }
                }
            }
        }

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

    /// <summary>Finishes the current utterance and returns it.</summary>
    public FinalUpdate Flush()
    {
        lock (gate)
        {
            if (disposed) return new FinalUpdate(0, "", Array.Empty<TimedWord>(), Array.Empty<short>());
            return Finish(recognizer.FinalResult());
        }
    }

    /// <summary>
    /// For live Whisper: the sentence in progress, cut after its second-to-last word (the last one is still
    /// changing). Returns null unless it has grown by at least <paramref name="minGrowth"/> samples since
    /// the caller's previous snapshot of the same sentence, so polling stays cheap.
    /// </summary>
    public LiveSnapshot? SnapshotLive(long previousUtterance, int previousLength, int minGrowth)
    {
        lock (gate)
        {
            if (disposed || warnedClock || lastPartial.Length == 0 || utteranceLength < AudioPipeline.SampleRate / 2) return null;
            if (previousUtterance == utteranceNumber && utteranceLength < previousLength + minGrowth) return null; // cheap check first

            recognizer.SetPartialWords(true);
            var words = ParseWords(recognizer.PartialResult(), "partial_result");
            recognizer.SetPartialWords(false);
            if (words.Length < 2) return null;

            double coveredUntil = words[^2].End + 0.08;
            long cut = Math.Min(utteranceLength, (long)(coveredUntil * AudioPipeline.SampleRate) - utteranceStart);
            if (cut < AudioPipeline.SampleRate / 2) return null;
            if (previousUtterance == utteranceNumber && cut < previousLength + minGrowth) return null;
            return new LiveSnapshot(utteranceNumber, utterance[..(int)cut], coveredUntil, words.Length - 1);
        }
    }

    FinalUpdate Finish(string json)
    {
        var update = new FinalUpdate(utteranceNumber, Extract(json, "text"), ParseWords(json, "result"), TakeUtterance(json));
        utteranceNumber++;
        lastPartial = "";
        samplesSincePartial = 0;
        return update;
    }

    static TimedWord[] ParseWords(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(property, out var words) || words.ValueKind != JsonValueKind.Array)
                return Array.Empty<TimedWord>();
            var result = new TimedWord[words.GetArrayLength()];
            int i = 0;
            foreach (var w in words.EnumerateArray())
                result[i++] = new TimedWord(w.GetProperty("word").GetString() ?? "", w.GetProperty("start").GetDouble(), w.GetProperty("end").GetDouble());
            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Array.Empty<TimedWord>();
        }
    }

    /// <summary>
    /// Returns the finished sentence's audio, trimmed to its words (+150 ms), and keeps anything after the
    /// last word — often the start of the next sentence — at the front of the buffer for the next one.
    /// </summary>
    short[] TakeUtterance(string json)
    {
        const int margin = AudioPipeline.SampleRate * 15 / 100;
        int length = utteranceLength, start = 0, end = length, carryFrom = length;
        if (TryWordSpan(json, out double firstWord, out double lastWord))
        {
            long s = (long)(firstWord * AudioPipeline.SampleRate) - utteranceStart;
            long e = (long)(lastWord * AudioPipeline.SampleRate) - utteranceStart;
            if (s >= -margin && e <= length + margin && s < e)
            {
                start = (int)Math.Max(0, s - margin);
                end = (int)Math.Min(length, e + margin);
                carryFrom = (int)Math.Clamp(e + AudioPipeline.SampleRate / 20, 0, length);
            }
            else if (!warnedClock)
            {
                warnedClock = true;
                Log.Info($"Vosk word times don't line up with the audio buffer (word {firstWord:F2}-{lastWord:F2}s, buffer starts {utteranceStart / (double)AudioPipeline.SampleRate:F2}s); using whole utterances.");
            }
        }

        var audio = utterance[start..end];
        int carry = length - carryFrom;
        Array.Copy(utterance, carryFrom, utterance, 0, carry);
        utteranceLength = carry;
        utteranceStart += carryFrom;
        return audio;
    }

    static bool TryWordSpan(string json, out double first, out double last)
    {
        first = last = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var words) || words.ValueKind != JsonValueKind.Array) return false;
            int n = words.GetArrayLength();
            if (n == 0) return false;
            first = words[0].GetProperty("start").GetDouble();
            last = words[n - 1].GetProperty("end").GetDouble();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            if (disposed) return;
            recognizer.Reset();
            lastPartial = "";
            samplesSincePartial = 0;
            utteranceStart = fedSamples; // Vosk's clock keeps running across resets
            utteranceLength = 0;
            utteranceNumber++;
        }
    }

    void AppendUtterance(short[] pcm, int count)
    {
        if (utteranceLength + count > MaxUtteranceSamples)
        {
            int drop = utteranceLength + count - MaxUtteranceSamples; // keep the most recent 30 s
            Array.Copy(utterance, drop, utterance, 0, utteranceLength - drop);
            utteranceLength -= drop;
            utteranceStart += drop;
        }
        if (utteranceLength + count > utterance.Length)
            Array.Resize(ref utterance, Math.Min(MaxUtteranceSamples, Math.Max(utterance.Length * 2, utteranceLength + count)));
        Array.Copy(pcm, 0, utterance, utteranceLength, count);
        utteranceLength += count;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            recognizer.Dispose();
            model.Dispose();
        }
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

    static int ReadPartialInterval()
    {
        // LC10_PARTIAL_MS overrides how often the in-progress line is refreshed (for tuning).
        var ms = int.TryParse(Environment.GetEnvironmentVariable("LC10_PARTIAL_MS"), out var v) && v > 0 ? v : 200;
        return AudioPipeline.SampleRate * ms / 1000;
    }

    static string Extract(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value) ? (value.GetString() ?? "").Trim() : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
