using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace LiveCaptions10;

internal enum ComputeDevice
{
    Auto,
    Gpu,
    Cpu,
}

/// <summary>
/// Picks the native whisper.cpp build: Vulkan (any modern GPU, including AMD/Intel integrated graphics)
/// or CPU. The choice is fixed once the native library has loaded, so changing it needs a restart.
/// </summary>
internal static class WhisperRuntime
{
    static bool configured;

    public static ComputeDevice Requested { get; private set; }

    public static void Configure(ComputeDevice device)
    {
        if (configured) return;
        configured = true;
        if (Enum.TryParse(Environment.GetEnvironmentVariable("LC10_WHISPER_DEVICE"), true, out ComputeDevice forced)) device = forced;
        Requested = device;
        RuntimeOptions.RuntimeLibraryOrder = device switch
        {
            ComputeDevice.Cpu => new List<RuntimeLibrary> { RuntimeLibrary.Cpu },
            ComputeDevice.Gpu => new List<RuntimeLibrary> { RuntimeLibrary.Vulkan },
            _ => new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu },
        };
    }

    /// <summary>Loads the native library now (if not yet) so <see cref="OnGpu"/> is known.</summary>
    public static void EnsureLoaded() => WhisperFactory.GetRuntimeInfo();

    public static bool OnGpu => RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan;

    public static string LoadedName => RuntimeOptions.LoadedLibrary switch
    {
        RuntimeLibrary.Vulkan => "GPU (Vulkan)",
        null => "not loaded",
        var other => other.ToString()!.Replace("Cpu", "CPU"),
    };
}

/// <summary>One Whisper model plus a processor sized to the clip being transcribed.</summary>
internal sealed class WhisperRunner : IClipTranscriber
{
    const int SampleRate = AudioPipeline.SampleRate;
    const int SamplesPerEncoderFrame = SampleRate / 50; // Whisper's encoder sees 50 frames per second (1500 = 30 s)

    static readonly bool FullWindow = Environment.GetEnvironmentVariable("LC10_WHISPER_FULL_WINDOW") == "1";

    readonly WhisperFactory factory;
    readonly CancellationToken token;
    WhisperProcessor? processor;
    int processorContext;

    public WhisperRunner(string modelPath, CancellationToken token)
    {
        ModelPath = modelPath;
        this.token = token;
        factory = WhisperFactory.FromPath(modelPath);
    }

    public string ModelPath { get; }

    public string Name => Path.GetFileNameWithoutExtension(ModelPath).Replace("ggml-", "");

    /// <summary>
    /// CPU threads for whisper.cpp. Its worker threads spin while waiting, so leave real cores free for
    /// the instant recognizer and the rest of the PC (logical CPUs / 2 ≈ physical cores, minus two).
    /// </summary>
    static int Threads => Math.Clamp(Environment.ProcessorCount / 2 - 2, 2, 8);

    public string? Transcribe(short[] audio, out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        var samples = ClipAudio.Prepare(audio, SampleRate + SampleRate / 5); // Whisper wants >= 1 s
        if (samples == null) return null;
        var clock = Stopwatch.StartNew();
        var text = new StringBuilder();
        foreach (var segment in ProcessorFor(samples.Length).ProcessAsync(samples, token).ToBlockingEnumerable(token))
            text.Append(segment.Text);
        elapsed = clock.Elapsed;
        return text.ToString();
    }

    /// <summary>
    /// Whisper normally encodes a fixed 30 s window, which costs the same for a 3 s sentence. Shrinking the
    /// encoder context to the clip's length (rounded up to 5 s steps so processors can be reused) makes
    /// short sentences several times faster with the same output.
    /// </summary>
    // Context is rounded up to 0.5 s steps. Whisper hallucinates into any silence left in the window
    // (typically repeating the clip's opening sentence), so the window must fit the clip closely.
    static readonly int ContextStep = int.TryParse(Environment.GetEnvironmentVariable("LC10_CTX_STEP"), out var s) && s > 0 ? s : 25;

    public TimeSpan LastBuildTime { get; private set; }

    public int LastContext => processorContext;

    WhisperProcessor ProcessorFor(int sampleCount)
    {
        int step = ContextStep;
        int needed = (sampleCount + SamplesPerEncoderFrame - 1) / SamplesPerEncoderFrame + 8;
        int context = FullWindow ? 1500 : Math.Min(1500, (needed + step - 1) / step * step);
        LastBuildTime = TimeSpan.Zero;
        if (processor != null && processorContext == context) return processor;

        var build = Stopwatch.StartNew();
        processor?.Dispose();
        processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(Threads)
            .WithNoContext()                 // each clip stands alone; avoids repetition loops
            .WithSingleSegment()             // one clip in, one text out
            .WithAudioContextSize(context == 1500 ? 0 : context)
            // No temperature-fallback retries: on a garbled clip Whisper would otherwise re-decode up to
            // ~25 times (20-30 s stalls measured on short clips). The output filters catch bad results instead.
            .WithTemperatureInc(0f)
            // Fast speech is ~6-8 tokens/s; anything far beyond that is a runaway repetition.
            .WithMaxTokensPerSegment(context / 50 * 10 + 16)
            .Build();
        processorContext = context;
        LastBuildTime = build.Elapsed;
        return processor;
    }

    public void Dispose()
    {
        processor?.Dispose();
        factory.Dispose();
    }
}

/// <summary>
/// Whisper passes on top of the instant Vosk text. Vosk is a streaming model, so it shows words immediately
/// but is weak on real-world audio. Whisper is far more accurate (music beds, accents, fast speech) and adds
/// punctuation and casing, but works on whole clips, so it runs in two ways on one worker thread:
/// <list type="bullet">
/// <item>Live: while a sentence is being spoken, re-transcribe it so far with a fast model (GPU only).</item>
/// <item>Final: when the sentence ends, transcribe it once more with the most accurate model; this replaces the draft.</item>
/// </list>
/// </summary>
internal sealed class WhisperRefiner : IDisposable
{
    const int SampleRate = AudioPipeline.SampleRate;
    const int MaxBacklog = 2;                                    // skip older sentences if Whisper falls behind
    const int LiveStepSamples = SampleRate / 2;                  // re-run live once 0.5 s more is settled
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(12);

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

    readonly IClipTranscriber final;
    readonly IClipTranscriber? live;
    readonly BlockingCollection<Job> jobs = new();
    readonly CancellationTokenSource stop;
    readonly Thread worker;
    long lastLiveUtterance;
    int lastLiveLength;

    WhisperRefiner(IClipTranscriber final, IClipTranscriber? live, CancellationTokenSource stop)
    {
        this.final = final;
        this.live = live;
        this.stop = stop;
        worker = new Thread(Run) { IsBackground = true, Name = "Second-pass refiner", Priority = ThreadPriority.BelowNormal };
        worker.Start();
    }

    /// <param name="finalModel">Whisper model for finished sentences (the most accurate one that is fast enough).</param>
    /// <param name="liveModel">Whisper model for live passes while speaking, or null for none. May equal <paramref name="finalModel"/>.</param>
    public static WhisperRefiner CreateWhisper(string finalModel, string? liveModel)
    {
        var stop = new CancellationTokenSource();
        var final = new WhisperRunner(finalModel, stop.Token);
        IClipTranscriber? live = liveModel == null ? null
            : string.Equals(liveModel, finalModel, StringComparison.OrdinalIgnoreCase) ? final : new WhisperRunner(liveModel, stop.Token);
        return new WhisperRefiner(final, live, stop);
    }

    /// <summary>Parakeet TDT for finished sentences (CPU, no live layer).</summary>
    public static WhisperRefiner CreateParakeet(string modelDir) => new(new ParakeetRunner(modelDir), null, new CancellationTokenSource());

    public string FinalModel => final.ModelPath;

    public string Description => live == null ? final.Name : live == final ? $"{final.Name}, live" : $"{final.Name} + {live.Name} live";

    /// <summary>
    /// Supplies the sentence in progress for live passes: (previous utterance, previous length, min growth)
    /// → snapshot or null. Live passes run only while this is set and a live model was given.
    /// </summary>
    public Func<long, int, int, LiveSnapshot?>? LiveSource { get; set; }

    /// <summary>
    /// Raised on the worker thread for a finished sentence. Text is the replacement; "" means the draft was
    /// noise (remove it); null means keep the draft as it is.
    /// </summary>
    public event Action<long, string?>? Refined;

    /// <summary>Raised on the worker thread with Whisper's text for the settled part of the sentence in progress.</summary>
    public event Action<LiveSnapshot, string>? LiveRefined;

    public void Enqueue(long id, string draft, short[] audio)
    {
        if (!jobs.IsAddingCompleted) jobs.Add(new Job(id, draft, audio, DateTime.UtcNow));
    }

    /// <summary>Synchronous transcription with the final model (used by --selftest).</summary>
    public string? Transcribe(short[] audio, out TimeSpan elapsed) => final.Transcribe(audio, out elapsed);

    public TimeSpan LastBuildTime => (final as WhisperRunner)?.LastBuildTime ?? TimeSpan.Zero;

    public int LastLiveContext => (live as WhisperRunner)?.LastContext ?? 0;

    /// <summary>Synchronous transcription with the live model (used by --selftest).</summary>
    public string? TranscribeLive(short[] audio, out TimeSpan elapsed)
    {
        elapsed = TimeSpan.Zero;
        return live?.Transcribe(audio, out elapsed);
    }

    /// <summary>Decides whether Whisper's text should replace the draft (see <see cref="Refined"/>).</summary>
    public static string? Validate(string? whisper, string draft)
    {
        if (whisper == null) return null;
        int draftWords = Words.Matches(draft).Count;
        var cleaned = Clean(whisper);
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
        if (live != null && live != final) live.Dispose();
        final.Dispose();
    }

    void Run()
    {
        try
        {
            while (!jobs.IsCompleted)
            {
                bool liveOn = live != null && LiveSource != null;
                if (jobs.TryTake(out var job, liveOn ? 40 : 250, stop.Token))
                {
                    ProcessFinal(job);
                    continue;
                }
                if (liveOn) LivePass();
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    void ProcessFinal(Job job)
    {
        string? result = null;
        if (jobs.Count < MaxBacklog && DateTime.UtcNow - job.Queued < MaxAge)
        {
            try
            {
                var raw = final.Transcribe(job.Audio, out var took);
                result = Validate(raw, job.Draft);
                Log.Info($"Second pass {final.Name} {took.TotalMilliseconds:F0} ms: \"{raw?.Trim()}\" (draft \"{job.Draft}\")" + (result == null ? " → kept draft" : ""));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("Whisper failed", ex);
            }
        }
        else
        {
            Log.Info($"Whisper behind: skipped a sentence (backlog {jobs.Count}, waited {(DateTime.UtcNow - job.Queued).TotalSeconds:F1} s)");
        }
        Refined?.Invoke(job.Id, result);
    }

    void LivePass()
    {
        LiveSnapshot? snapshot;
        try
        {
            snapshot = LiveSource?.Invoke(lastLiveUtterance, lastLiveLength, LiveStepSamples);
        }
        catch (Exception ex)
        {
            Log.Error("Live snapshot failed", ex);
            return;
        }
        if (snapshot == null) return;
        lastLiveUtterance = snapshot.Utterance;
        lastLiveLength = snapshot.Audio.Length;

        try
        {
            var raw = live!.Transcribe(snapshot.Audio, out _);
            if (raw == null) return;
            var cleaned = Clean(raw);
            if (Words.Matches(cleaned).Count == 0 || StockPhrases.Contains(cleaned) || HasRepetitionLoop(cleaned)) return;
            LiveRefined?.Invoke(snapshot, cleaned);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error("Live Whisper pass failed", ex);
        }
    }

    static string Clean(string raw) => DropRepeatedSentences(Spaces.Replace(Annotations.Replace(raw, " "), " ").Trim());

    static readonly Regex SentenceBreak = new(@"(?<=[.?!])\s+", RegexOptions.Compiled);

    /// <summary>
    /// Whisper sometimes pads a clip by repeating a sentence it already wrote (often the first or last).
    /// A sentence of 4+ words appearing twice in one short clip is almost never real speech: keep the first.
    /// </summary>
    static string DropRepeatedSentences(string text)
    {
        var seen = new HashSet<string>();
        var kept = new List<string>();
        foreach (var sentence in SentenceBreak.Split(text))
        {
            var key = string.Join(' ', Words.Matches(sentence.ToLowerInvariant()).Select(m => m.Value));
            if (key.Split(' ').Length >= 4 && !seen.Add(key)) continue;
            kept.Add(sentence);
        }
        return string.Join(' ', kept);
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
