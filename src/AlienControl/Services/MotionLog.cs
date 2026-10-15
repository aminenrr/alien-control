using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace AlienControl.Services;

/// <summary>
/// TEMPORARY (v0.8.5): diagnostics for "animations don't run". Writes %AppData%\AlienControl\motion.log,
/// starting fresh each launch and capped at 50 lines, so it costs nothing once the cap is hit.
/// REMOVE this file and its calls once the animation bug is fixed.
/// </summary>
public static class MotionLog
{
    private const int MaxLines = 50;
    private static int _lines;
    private static readonly object Gate = new();
    private static readonly string PathFile =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "motion.log");

    public static void Start(bool motionEnabled)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
            File.WriteAllText(PathFile, "");
        }
        catch { }
        Write($"start v{typeof(MotionLog).Assembly.GetName().Version} | Motion.Enabled={motionEnabled} | RenderTier={RenderCapability.Tier >> 16} " +
              $"| ClientAreaAnimation={SystemParameters.ClientAreaAnimation} | MenuAnimation={SystemParameters.MenuAnimation}");
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            if (_lines >= MaxLines) return;
            _lines++;
            try { File.AppendAllText(PathFile, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch { }
            if (_lines == MaxLines) try { File.AppendAllText(PathFile, "(cap reached, logging stopped)" + Environment.NewLine); } catch { }
        }
    }
}
