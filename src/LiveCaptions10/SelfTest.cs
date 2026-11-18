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
            var model = modelPath ?? ModelManager.FindModels().FirstOrDefault()
                ?? throw new InvalidOperationException("No speech model installed in " + ModelManager.ModelsDir);
            var loadClock = Stopwatch.StartNew();
            using var engine = new SpeechEngine(model);
            report.AppendLine($"vosk={Path.GetFileName(model)} load={loadClock.Elapsed.TotalSeconds:F1}s");

            var sentences = new List<(string Text, short[] Audio)>();
            int partials = 0;
            engine.Partial += u => { if (u.Text.Length > 0) partials++; };
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
            long total = 0;
            var decodeClock = Stopwatch.StartNew();

            int read;
            while ((read = reader.Read(packet, 0, packet.Length)) > 0)
            {
                converter.Push(packet, read, queue, gate);
                int n = queue.Read(floats, floats.Length);
                if (n == 0) continue;
                AudioConvert.ToPcm16(floats, pcm, n);
                engine.Accept(pcm, n);
                total += n;
            }

            // Trailing silence, like the live mixer produces when playback stops.
            Array.Clear(pcm);
            for (int i = 0; i < 30; i++) engine.Accept(pcm, AudioPipeline.SampleRate / 10);
            var last = engine.Flush();
            if (last.Text.Length > 0) sentences.Add((last.Text, last.Audio));

            double audioSeconds = total / (double)AudioPipeline.SampleRate;
            double decodeSeconds = decodeClock.Elapsed.TotalSeconds;
            report.AppendLine($"converted={audioSeconds:F2}s (expected {reader.TotalTime.TotalSeconds:F2}s) vosk-decode={decodeSeconds:F2}s " +
                              $"realtime-factor={decodeSeconds / Math.Max(0.001, audioSeconds):F3} partial-updates={partials}");

            // Same automatic choices as the app; LC10_WHISPER_DEVICE / LC10_WHISPER_MODEL override them.
            WhisperRuntime.Configure(ComputeDevice.Auto);
            WhisperRefiner? refiner = null;
            if (ModelManager.ListWhisperModels().Count > 0)
            {
                WhisperRuntime.EnsureLoaded();
                var finalModel = ModelManager.FindWhisperModel(null, preferLarge: WhisperRuntime.OnGpu)!;
                var liveModel = WhisperRuntime.OnGpu ? ModelManager.ListWhisperModels()[0] : null;
                loadClock.Restart();
                refiner = new WhisperRefiner(finalModel, liveModel);
                report.AppendLine($"whisper={refiner.Description} on {WhisperRuntime.LoadedName} load={loadClock.Elapsed.TotalSeconds:F1}s");
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
                    report.AppendLine($"VOSK    ({audio.Length / (double)AudioPipeline.SampleRate:F1}s audio): {draft}");
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
                    report.AppendLine($"WHISPER ({took.TotalSeconds:F2}s, processor build {refiner.LastBuildTime.TotalMilliseconds:F0} ms): {shown}");
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
