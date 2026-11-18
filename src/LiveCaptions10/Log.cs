using System;
using System.IO;

namespace LiveCaptions10;

/// <summary>Tiny diagnostic log at data\app.log (rotated at 1 MB).</summary>
internal static class Log
{
    static readonly object Gate = new();

    static string FilePath => Path.Combine(SettingsStore.AppDataDir, "app.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.AppDataDir);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
