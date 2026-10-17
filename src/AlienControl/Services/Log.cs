using System;
using System.IO;

namespace AlienControl.Services;

/// <summary>
/// 1.3.0-beta.1 (review #16): local log, %AppData%\AlienControl\crash.log. Never sent anywhere. Warn lines record
/// why something silently did nothing; Frame lines are the debug frame-gap log of the slide engine.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "crash.log");

    public static void Warn(string where, Exception? ex) => Write("WARN", $"{where}: {ex?.GetType().Name}: {ex?.Message}");
    public static void Error(string where, Exception? ex) => Write("ERROR", $"{where}: {ex}");
    public static void Info(string where, string text) => Write("INFO", $"{where}: {text}");

    private static void Write(string level, string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {text}\n");
            }
        }
        catch { /* logging must never throw */ }
    }
}
