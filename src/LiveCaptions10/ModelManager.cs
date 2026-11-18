using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LiveCaptions10;

internal sealed record WhisperDownload(string File, string Url, string Size, string Label);

/// <summary>Finds and downloads the speech models (Vosk for instant words, Whisper for refined sentences).</summary>
internal static class ModelManager
{
    public const string DefaultModelName = "vosk-model-en-us-0.22-lgraph";
    public const string DefaultModelUrl = "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip";
    public const string DefaultModelSize = "128 MB";
    public const string MoreModelsUrl = "https://alphacephei.com/vosk/models";

    public const string WhisperModelName = "ggml-small.en-q5_1.bin";
    public const string WhisperModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.en-q5_1.bin";
    public const string WhisperModelSize = "181 MB";

    static readonly (string Code, string Name)[] Languages =
    {
        ("en-us", "English (US)"), ("en-in", "English (India)"), ("en", "English"), ("de", "German"), ("fr", "French"),
        ("es", "Spanish"), ("pt", "Portuguese"), ("it", "Italian"), ("nl", "Dutch"), ("ru", "Russian"),
        ("uk", "Ukrainian"), ("pl", "Polish"), ("cs", "Czech"), ("sv", "Swedish"), ("tr", "Turkish"),
        ("cn", "Chinese"), ("ja", "Japanese"), ("ko", "Korean"), ("hi", "Hindi"), ("ar", "Arabic"),
        ("fa", "Persian"), ("vn", "Vietnamese"), ("el", "Greek"), ("ca", "Catalan"), ("eo", "Esperanto"),
        ("kz", "Kazakh"), ("uz", "Uzbek"), ("tl", "Filipino"), ("br", "Breton"), ("gu", "Gujarati"), ("te", "Telugu"),
    };

    public static string ModelsDir => Path.Combine(SettingsStore.AppDataDir, "models");

    public static string WhisperDir => Path.Combine(SettingsStore.AppDataDir, "whisper");

    // ---------- Vosk ----------

    public static bool IsModel(string? dir) =>
        !string.IsNullOrEmpty(dir) &&
        File.Exists(Path.Combine(dir, "am", "final.mdl")) &&
        Directory.Exists(Path.Combine(dir, "conf"));

    public static List<string> FindModels()
    {
        var found = new List<string>();
        foreach (var root in new[] { ModelsDir, Path.Combine(AppContext.BaseDirectory, "models") })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                if (IsModel(dir)) found.Add(dir);
                else found.AddRange(Directory.GetDirectories(dir).Where(IsModel)); // zip extracted into an extra folder
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var id = name.StartsWith("vosk-model-", StringComparison.OrdinalIgnoreCase) ? name["vosk-model-".Length..] : name;
        var lang = id.StartsWith("small-", StringComparison.OrdinalIgnoreCase) ? id["small-".Length..] : id;
        foreach (var (code, language) in Languages)
        {
            if (lang.Equals(code, StringComparison.OrdinalIgnoreCase) ||
                lang.StartsWith(code + "-", StringComparison.OrdinalIgnoreCase))
                return $"{language}  ({id})";
        }
        return id;
    }

    /// <summary>Downloads and unpacks the default English Vosk model.</summary>
    public static async Task<string> DownloadDefaultModelAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsDir);
        var zipPath = Path.Combine(ModelsDir, DefaultModelName + ".zip");
        await DownloadFileAsync(DefaultModelUrl, zipPath, progress, ct);

        var extractDir = Path.Combine(ModelsDir, ".extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir), ct);
            var modelDir = IsModel(extractDir) ? extractDir : Directory.GetDirectories(extractDir).FirstOrDefault(IsModel)
                ?? throw new InvalidDataException("The downloaded file doesn't contain a speech model.");
            var destination = Path.Combine(ModelsDir, DefaultModelName);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(modelDir, destination);
            File.Delete(zipPath);
            return destination;
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            }
            catch (IOException)
            {
                // leftover temp folder is harmless
            }
        }
    }

    // ---------- Whisper ----------

    public static List<string> ListWhisperModels() =>
        Directory.Exists(WhisperDir)
            ? Directory.GetFiles(WhisperDir, "ggml-*.bin").OrderBy(f => new FileInfo(f).Length).ToList()
            : new List<string>();

    /// <summary>
    /// The chosen model file if it exists; otherwise the biggest installed model when a GPU will run it,
    /// or the default (small.en) on CPU, where large models are too slow for live use.
    /// LC10_WHISPER_MODEL (a file name) overrides it, for benchmarking.
    /// </summary>
    public static string? FindWhisperModel(string? chosenFile = null, bool preferLarge = false)
    {
        var files = ListWhisperModels();
        if (files.Count == 0) return null;
        foreach (var name in new[] { Environment.GetEnvironmentVariable("LC10_WHISPER_MODEL"), chosenFile })
        {
            var match = files.FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        if (preferLarge) return files[^1];
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(WhisperModelName, StringComparison.OrdinalIgnoreCase)) ?? files[0];
    }

    public static string WhisperDisplayName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        name = name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
        long mb = new FileInfo(path).Length / (1024 * 1024);
        string hint = name.Contains("large", StringComparison.OrdinalIgnoreCase) ? "most accurate, best on GPU"
                    : name.Contains("medium", StringComparison.OrdinalIgnoreCase) ? "accurate, best on GPU"
                    : "fast, fine on CPU";
        return $"{name}  ({mb} MB, {hint})";
    }

    /// <summary>Whisper models offered for download, smallest first.</summary>
    public static readonly IReadOnlyList<WhisperDownload> WhisperDownloads = new[]
    {
        new WhisperDownload(WhisperModelName, WhisperModelUrl, WhisperModelSize, "small.en (fast, works on CPU)"),
        new WhisperDownload("ggml-large-v3-turbo-q5_0.bin",
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin", "547 MB",
            "large-v3-turbo (most accurate, needs a GPU)"),
    };

    public static async Task<string> DownloadWhisperModelAsync(WhisperDownload model, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WhisperDir);
        var destination = Path.Combine(WhisperDir, model.File);
        await DownloadFileAsync(model.Url, destination, progress, ct);
        return destination;
    }

    // ---------- shared ----------

    /// <summary>Downloads to "destination.part", resuming after dropped connections, then renames it.</summary>
    static async Task DownloadFileAsync(string url, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        var partPath = destination + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LiveCaptions10/1.0");

        const int maxAttempts = 15;
        int lastPercent = -1;
        for (int attempt = 1; ; attempt++)
        {
            long have = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) break; // already complete
                response.EnsureSuccessStatusCode();

                bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) have = 0;
                long total = response.Content.Headers.ContentRange?.Length
                             ?? (response.Content.Headers.ContentLength is long len ? len + have : -1);

                long done = have;
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                await using (var output = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    int n;
                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0)
                        {
                            int percent = (int)(done * 100 / total);
                            if (percent != lastPercent)
                            {
                                lastPercent = percent;
                                progress?.Report(done / (double)total);
                            }
                        }
                    }
                }
                if (total <= 0 || done >= total) break;
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is IOException) && !ct.IsCancellationRequested && attempt < maxAttempts)
            {
                Log.Error($"Download attempt {attempt} of {url} failed; retrying", ex);
            }

            if (attempt >= maxAttempts) throw new IOException("The download kept getting interrupted. Check your connection and try again.");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        progress?.Report(1.0);
        File.Move(partPath, destination, overwrite: true);
    }
}
