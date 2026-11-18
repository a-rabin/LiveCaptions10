using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace LiveCaptions10;

/// <summary>
/// Second pass: when Vosk finishes a sentence, Whisper re-transcribes that sentence's audio. Whisper is far
/// more accurate on real-world audio (music beds, accents, fast speech) and adds punctuation and casing,
/// but it is not a streaming model, so Vosk keeps providing the instant words and Whisper replaces them
/// about a second later.
/// </summary>
internal sealed class WhisperRefiner : IDisposable
{
    const int SampleRate = AudioPipeline.SampleRate;
    const int MaxBacklog = 2;                                   // skip older sentences if Whisper falls behind
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(10);

    static readonly Regex Annotations = new(@"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*|[♪♫]+", RegexOptions.Compiled);
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex Words = new(@"[\p{L}\p{N}']+", RegexOptions.Compiled);

    // Whisper's well-known inventions on silence or music.
    static readonly HashSet<string> StockPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        "you", "thank you", "thank you.", "thanks.", "thanks for watching!", "thanks for watching.",
        "thank you for watching.", "thank you for watching!", "thank you so much for watching!", "please subscribe.",
        "bye.", "bye!", "so", "okay.", ".", "...",
    };

    const int SamplesPerEncoderFrame = SampleRate / 50; // Whisper's encoder sees 50 frames per second (1500 = 30 s)

    readonly WhisperFactory factory;
    readonly BlockingCollection<Job> jobs = new();
    readonly CancellationTokenSource stop = new();
    readonly Thread worker;
    readonly bool fullWindow = Environment.GetEnvironmentVariable("LC10_WHISPER_FULL_WINDOW") == "1";
    WhisperProcessor? processor;
    int processorContext;

    public WhisperRefiner(string modelPath)
    {
        ModelPath = modelPath;
        factory = WhisperFactory.FromPath(modelPath);
        worker = new Thread(Run) { IsBackground = true, Name = "Whisper refiner", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <summary>
    /// Whisper normally encodes a fixed 30 s window, which costs the same for a 3 s sentence. Shrinking the
    /// encoder context to the sentence's length (rounded up to 5 s steps so processors can be reused) makes
    /// short sentences several times faster.
    /// </summary>
    WhisperProcessor ProcessorFor(int sampleCount)
    {
        const int step = 250; // 5 s
        int needed = (sampleCount + SamplesPerEncoderFrame - 1) / SamplesPerEncoderFrame + 16;
        int context = fullWindow ? 1500 : Math.Min(1500, (needed + step - 1) / step * step);
        if (processor != null && processorContext == context) return processor;

        processor?.Dispose();
        processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 2, 8))
            .WithNoContext()                 // each sentence stands alone; avoids repetition loops
            .WithSingleSegment()             // one sentence in, one text out
            .WithAudioContextSize(context == 1500 ? 0 : context)
            .Build();
        processorContext = context;
        return processor;
    }

    public string ModelPath { get; }

    /// <summary>
    /// Raised on the worker thread. Text is the replacement; "" means the draft was noise (remove it);
    /// null means keep the draft as it is.
    /// </summary>
    public event Action<long, string?>? Refined;

    public void Enqueue(long id, string draft, short[] audio)
    {
        if (!jobs.IsAddingCompleted) jobs.Add(new Job(id, draft, audio, DateTime.UtcNow));
    }

    /// <summary>Synchronous transcription of one sentence (used by the worker and by --selftest).</summary>
    public string? Transcribe(short[] audio, out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        var samples = Prepare(audio);
        if (samples == null) return null;
        var clock = Stopwatch.StartNew();
        var text = new StringBuilder();
        var whisper = ProcessorFor(samples.Length);
        foreach (var segment in whisper.ProcessAsync(samples, stop.Token).ToBlockingEnumerable(stop.Token))
            text.Append(segment.Text);
        elapsed = clock.Elapsed;
        return text.ToString();
    }

    /// <summary>Decides whether Whisper's text should replace the draft (see <see cref="Refined"/>).</summary>
    public static string? Validate(string? whisper, string draft)
    {
        if (whisper == null) return null;
        int draftWords = Words.Matches(draft).Count;
        var cleaned = Spaces.Replace(Annotations.Replace(whisper, " "), " ").Trim();
        int words = Words.Matches(cleaned).Count;

        if (words == 0)
        {
            // "[Music]", "(applause)" or nothing at all: the short draft was most likely noise.
            bool annotated = Annotations.IsMatch(whisper);
            return annotated || draftWords <= 3 ? "" : null;
        }
        if (StockPhrases.Contains(cleaned) && !draft.Contains(Words.Match(cleaned).Value, StringComparison.OrdinalIgnoreCase))
            return draftWords <= 3 ? "" : null;
        if (words > draftWords * 2 + 8) return null; // Whisper invented text
        if (HasRepetitionLoop(cleaned)) return null;
        return cleaned;
    }

    public void Dispose()
    {
        jobs.CompleteAdding();
        stop.Cancel();
        worker.Join(5000);
        processor?.Dispose();
        factory.Dispose();
    }

    void Run()
    {
        try
        {
            foreach (var job in jobs.GetConsumingEnumerable(stop.Token))
            {
                string? result = null;
                if (jobs.Count < MaxBacklog && DateTime.UtcNow - job.Queued < MaxAge)
                {
                    try
                    {
                        var raw = Transcribe(job.Audio, out var took);
                        result = Validate(raw, job.Draft);
                        Log.Info($"Whisper {took.TotalMilliseconds:F0} ms: \"{raw?.Trim()}\" (draft \"{job.Draft}\")" + (result == null ? " → kept draft" : ""));
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Whisper failed", ex);
                    }
                }
                Refined?.Invoke(job.Id, result);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Trims leading/trailing silence and converts to float; null if there is too little speech.</summary>
    static float[]? Prepare(short[] audio)
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
        int start = Math.Max(0, Math.Max(first - pad, end - SampleRate * 29)); // Whisper's window is 30 s
        if (last - first < SampleRate * 3 / 10) return null;                   // under 0.3 s of sound: not worth it

        var samples = new float[Math.Max(end - start, SampleRate + SampleRate / 5)]; // Whisper wants >= 1 s
        for (int i = start; i < end; i++) samples[i - start] = audio[i] / 32768f;
        return samples;
    }

    static bool HasRepetitionLoop(string text)
    {
        var words = Words.Matches(text.ToLowerInvariant()).Select(m => m.Value).ToArray();
        if (words.Length < 12) return false;
        var counts = new Dictionary<string, int>();
        for (int i = 0; i + 3 <= words.Length; i++)
        {
            var key = string.Join(' ', words, i, 3);
            counts[key] = counts.TryGetValue(key, out var c) ? c + 1 : 1;
            if (counts[key] >= 4) return true;
        }
        return false;
    }

    sealed record Job(long Id, string Draft, short[] Audio, DateTime Queued);
}
