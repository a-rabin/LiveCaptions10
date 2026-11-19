using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace LiveCaptions10;

internal enum TextReveal
{
    Instant, // whole blocks as the recognizer delivers them
    Quick,   // letter by letter, each block typed in about a third of the gap before the next
    Smooth,  // letter by letter at a steady pace that spans the gap: continuous flow
}

internal enum SecondPass
{
    Whisper,
    Parakeet,
}

internal enum CaptionPosition
{
    Top,
    Bottom,
    Floating,
}

internal sealed class AppSettings
{
    public CaptionPosition Position { get; set; } = CaptionPosition.Top;
    public bool IncludeMicrophone { get; set; }
    public bool FilterProfanity { get; set; } = true;
    public bool SaveTranscript { get; set; }
    public bool UseWhisper { get; set; } = true;
    public ComputeDevice WhisperDevice { get; set; } = ComputeDevice.Auto;
    public bool LiveWhisper { get; set; } = true; // live Whisper updates while speaking (GPU only, Vosk only)
    public RecognizerKind Recognizer { get; set; } = RecognizerKind.Kroko; // falls back to Vosk if not installed
    public SecondPass SecondPassModel { get; set; } = SecondPass.Whisper;
    public string? WhisperModel { get; set; } // file name in data\whisper; null = pick automatically
    public string Style { get; set; } = CaptionStyles.Default.Key;
    public double FontSize { get; set; } = 20;
    public int Lines { get; set; } = 2;
    public TextReveal Reveal { get; set; } = TextReveal.Smooth; // how new text appears
    public string? ModelPath { get; set; }
    public double? FloatLeft { get; set; }
    public double? FloatTop { get; set; }
    public double? FloatWidth { get; set; }

    public void Normalize()
    {
        Lines = Math.Clamp(Lines, 1, 5);
        FontSize = Math.Clamp(FontSize, 12, 48);
        if (!Enum.IsDefined(Position)) Position = CaptionPosition.Top;
        Style = CaptionStyles.Get(Style).Key;
    }
}

internal static class SettingsStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Where settings, logs, speech models and transcripts live. Portable by default: a "data" folder next
    /// to the exe. LIVECAPTIONS10_DATA overrides it; %LOCALAPPDATA% is the fallback if the exe's folder is read-only.
    /// </summary>
    public static string AppDataDir { get; } = ResolveDataDir();

    static string ResolveDataDir()
    {
        var overridden = Environment.GetEnvironmentVariable("LIVECAPTIONS10_DATA");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (IsWritable(portable)) return portable;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptions10");
    }

    static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static string FilePath => Path.Combine(AppDataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (settings != null) return settings;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Reading settings failed; using defaults", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}

internal sealed record CaptionStyle(string Key, string Name, Color Foreground, Color Background, string FontFamily);

internal static class CaptionStyles
{
    public static readonly CaptionStyle Default =
        new("WhiteOnBlack", "White on black", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x1C, 0x1C, 0x1C), "Segoe UI");

    public static readonly IReadOnlyList<CaptionStyle> All = new[]
    {
        Default,
        new CaptionStyle("WhiteOnTranslucent", "White on translucent", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0xB8, 0x00, 0x00, 0x00), "Segoe UI"),
        new CaptionStyle("YellowOnBlue", "Yellow on blue", Color.FromRgb(0xFF, 0xE6, 0x1A), Color.FromRgb(0x0B, 0x2C, 0x6B), "Segoe UI"),
        new CaptionStyle("BlackOnWhite", "Black on white", Color.FromRgb(0x10, 0x10, 0x10), Color.FromRgb(0xF4, 0xF4, 0xF4), "Segoe UI"),
        new CaptionStyle("GreenOnBlack", "Green on black (monospace)", Color.FromRgb(0x7C, 0xF0, 0x7C), Color.FromRgb(0x0A, 0x0A, 0x0A), "Consolas"),
    };

    public static CaptionStyle Get(string? key) => All.FirstOrDefault(s => s.Key == key) ?? Default;
}
