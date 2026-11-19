using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using NAudio.Wave;

namespace LiveCaptions10;

/// <summary>
/// <c>LiveCaptions10.exe --selftest input.wav output.txt [voskModelDir]</c> feeds a WAV file through the
/// same packet converter and recognizer as live audio (in 10 ms device-style packets), then runs the
/// Whisper pass on each sentence if a Whisper model is installed, and writes results plus timings.
/// </summary>
internal static class SelfTest
{
    public static int Run(string wavPath, string outPath, string? modelPath)
    {
        var report = new StringBuilder();
        try
        {
            // LC10_ENGINE = vosk | nemo | kroko picks the streaming recognizer (default vosk).
            var kind = Environment.GetEnvironmentVariable("LC10_ENGINE")?.ToLowerInvariant() switch
            {
                "nemo" => RecognizerKind.NemoStreaming,
                "kroko" => RecognizerKind.Kroko,
                "krokol64" => RecognizerKind.KrokoLFast,
                "krokol128" => RecognizerKind.KrokoL,
                _ => RecognizerKind.Vosk,
            };
            var loadClock = Stopwatch.StartNew();
            IRecognizer engine;
            if (kind == RecognizerKind.Vosk)
            {
                var model = modelPath ?? ModelManager.FindModels().FirstOrDefault()
                    ?? throw new InvalidOperationException("No speech model installed in " + ModelManager.ModelsDir);
                engine = new SpeechEngine(model);
            }
            else
            {
                engine = new SherpaEngine(kind, SherpaModels.Streaming(kind) ?? throw new InvalidOperationException(kind + " not installed"),
                    SherpaModels.Punctuation);
            }
            using var engineScope = engine;
            report.AppendLine($"engine={engine.Name} ({Path.GetFileName(engine.ModelPath)}) load={loadClock.Elapsed.TotalSeconds:F1}s");

            var sentences = new List<(string Text, short[] Audio)>();
            int partials = 0;
            long total = 0, speechStart = -1;
            var updateTimes = new List<double>(); // audio position (s) each time the text changed
            engine.Partial += u =>
            {
                if (u.Text.Length == 0) return;
                partials++;
                updateTimes.Add(total / (double)AudioPipeline.SampleRate);
            };
            engine.Final += u => sentences.Add((u.Text, u.Audio));

            using var reader = new WaveFileReader(wavPath);
            var format = reader.WaveFormat;
            report.AppendLine($"input={format.SampleRate}Hz {format.Channels}ch {format.BitsPerSample}bit {format.Encoding} {reader.TotalTime.TotalSeconds:F1}s");

            var converter = new PacketConverter(format);
            var queue = new FloatRing(AudioPipeline.SampleRate * 2);
            var gate = new object();
            int packetBytes = Math.Max(format.BlockAlign, format.AverageBytesPerSecond / 100 / format.BlockAlign * format.BlockAlign);
            var packet = new byte[packetBytes];
            var floats = new float[AudioPipeline.SampleRate];
            var pcm = new short[AudioPipeline.SampleRate];
            var decodeClock = Stopwatch.StartNew();

            // Feed in 50 ms chunks, like the app's recognizer thread.
            int read;
            while ((read = reader.Read(packet, 0, packet.Length)) > 0)
            {
                converter.Push(packet, read, queue, gate);
                while (queue.Count >= AudioPipeline.SampleRate / 20)
                {
                    int n = queue.Read(floats, AudioPipeline.SampleRate / 20);
                    AudioConvert.ToPcm16(floats, pcm, n);
                    if (speechStart < 0)
                        for (int i = 0; i < n; i++)
                            if (Math.Abs((int)pcm[i]) > 1000) { speechStart = total + i; break; }
                    total += n;
                    engine.Accept(pcm, n);
                }
            }

            // Trailing silence, like the live mixer produces when playback stops.
            Array.Clear(pcm);
            for (int i = 0; i < 30; i++) engine.Accept(pcm, AudioPipeline.SampleRate / 10);
            var last = engine.Flush();
            if (last.Text.Length > 0) sentences.Add((last.Text, last.Audio));

            double audioSeconds = total / (double)AudioPipeline.SampleRate;
            double decodeSeconds = decodeClock.Elapsed.TotalSeconds;
            report.AppendLine($"converted={audioSeconds:F2}s (expected {reader.TotalTime.TotalSeconds:F2}s) decode={decodeSeconds:F2}s " +
                              $"realtime-factor={decodeSeconds / Math.Max(0.001, audioSeconds):F3} partial-updates={partials}");
            if (updateTimes.Count > 1 && speechStart >= 0)
            {
                var gaps = updateTimes.Zip(updateTimes.Skip(1), (a, b) => b - a).ToList();
                report.AppendLine($"delay: first-words={updateTimes[0] - speechStart / (double)AudioPipeline.SampleRate:F2}s " +
                                  $"update-every={gaps.Average():F2}s (max {gaps.Max():F2}s)");
            }

            // Second pass: LC10_SECOND = whisper (default, same automatic choices as the app;
            // LC10_WHISPER_DEVICE / LC10_WHISPER_MODEL override them) | parakeet | none.
            var second = Environment.GetEnvironmentVariable("LC10_SECOND")?.ToLowerInvariant() ?? "whisper";
            WhisperRefiner? refiner = null;
            if (second == "parakeet")
            {
                loadClock.Restart();
                refiner = WhisperRefiner.CreateParakeet(SherpaModels.Parakeet ?? throw new InvalidOperationException("Parakeet not installed"));
                report.AppendLine($"second={refiner.Description} on CPU load={loadClock.Elapsed.TotalSeconds:F1}s");
            }
            else if (second == "whisper" && ModelManager.ListWhisperModels().Count > 0)
            {
                WhisperRuntime.Configure(ComputeDevice.Auto);
                WhisperRuntime.EnsureLoaded();
                var finalModel = ModelManager.FindWhisperModel(null, preferLarge: WhisperRuntime.OnGpu)!;
                var liveModel = WhisperRuntime.OnGpu && kind == RecognizerKind.Vosk ? ModelManager.ListWhisperModels()[0] : null;
                loadClock.Restart();
                refiner = WhisperRefiner.CreateWhisper(finalModel, liveModel);
                report.AppendLine($"second={refiner.Description} on {WhisperRuntime.LoadedName} load={loadClock.Elapsed.TotalSeconds:F1}s");
            }
            if (refiner != null)
            {
                if (sentences.Count > 0)
                {
                    refiner.Transcribe(sentences[0].Audio, out var warmup); // first call compiles GPU shaders etc.
                    refiner.TranscribeLive(sentences[0].Audio, out var liveWarmup);
                    report.AppendLine($"warm-up final={warmup.TotalSeconds:F2}s live={liveWarmup.TotalSeconds:F2}s");
                }
            }

            using (refiner)
            {
                foreach (var (text, audio) in sentences)
                {
                    var draft = CaptionFormatter.FormatFinal(text, true);
                    report.AppendLine($"DRAFT   ({audio.Length / (double)AudioPipeline.SampleRate:F1}s audio): {draft}");
                    if (refiner == null) continue;
                    // Live passes as the app makes them: every 0.5 s of the sentence as it grows.
                    var times = new List<double>();
                    string? lastLive = null;
                    for (int end = AudioPipeline.SampleRate; end <= audio.Length; end += AudioPipeline.SampleRate / 2)
                    {
                        var liveText = refiner.TranscribeLive(audio[..end], out var liveTook);
                        if (liveText == null) continue;
                        times.Add(liveTook.TotalSeconds);
                        lastLive = liveText.Trim();
                        if (Environment.GetEnvironmentVariable("LC10_SELFTEST_DETAIL") == "1")
                            report.AppendLine($"  live pass {end / (double)AudioPipeline.SampleRate:F1}s ctx={refiner.LastLiveContext} {liveTook.TotalSeconds:F2}s");
                    }
                    if (times.Count > 0)
                        report.AppendLine($"LIVE    ({times.Count} passes, avg {times.Average():F2}s, max {times.Max():F2}s): last = {lastLive}");
                    var raw = refiner.Transcribe(audio, out var took);
                    var refined = WhisperRefiner.Validate(raw, draft);
                    var shown = refined == null ? "(kept draft)" : refined.Length == 0 ? "(removed as noise)" : CaptionFormatter.FormatRefined(refined, true);
                    report.AppendLine($"SECOND  ({took.TotalSeconds:F2}s): {shown}");
                    report.AppendLine($"  raw: {raw?.Trim()}");
                }
            }

            File.WriteAllText(outPath, report.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine("ERROR: " + ex);
            File.WriteAllText(outPath, report.ToString());
            return 1;
        }
    }
}
