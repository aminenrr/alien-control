using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AlienControl.Services;

/// <summary>
/// beta1.3-ov: frametimes of the foreground game from PresentMon (GameTechDev, MIT), the same ETW data RTSS-free tools
/// use. PresentMon runs as a separate process (tools\PresentMon\PresentMon.exe, no injection, anti cheat safe) and
/// writes one CSV line per Present() to stdout. Only the cheapest tracking is on (no display, GPU or input tracking),
/// since the overlay only needs MsBetweenPresents, the frametime MangoHud shows.
///
/// Lifecycle: Start when the overlay goes from off to a level, Stop when it goes off. Nothing runs while off.
/// The child is put in a kill-on-close job object, so it dies with the app even on a crash or a Task Manager kill,
/// and every start passes --stop_existing_session for our fixed session name, so a stale ETW session is cleaned up.
/// </summary>
public sealed class FramesSource : IDisposable
{
    public const string SessionName = "AlienControlsOverlay";

    /// <summary>One frame: when it arrived (Stopwatch ticks), PresentMon's own CPUStartTime in seconds (exact even when
    /// stdout arrives in bursts) and its frametime in ms.</summary>
    public readonly record struct Frame(long Ticks, double T, float Ms);

    private readonly object _lock = new();
    private readonly Frame[] _ring = new Frame[4096];
    private int _head, _count;
    private Process? _proc;
    private Thread? _reader;
    private volatile bool _running;
    private int _targetPid;
    private string _targetApp = "";
    private string _runtime = "";
    private long _lastFgCheck;
    private IntPtr _job;

    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon", "PresentMon.exe");
    public static bool Available => File.Exists(ExePath);

    public bool Running => _running;
    /// <summary>Last error (PresentMon missing, failed to start, exited). Null while healthy.</summary>
    public string? Error { get; private set; }

    /// <summary>Exe name of the process the frames belong to (the foreground window's), empty when none presents.</summary>
    public string TargetApp { get { lock (_lock) return _targetApp; } }
    public int TargetPid { get { lock (_lock) return _targetPid; } }
    /// <summary>PresentMon's PresentRuntime for the target (DXGI, D3D9, Other). The overlay maps it to an engine label.</summary>
    public string Runtime { get { lock (_lock) return _runtime; } }

    public bool Start()
    {
        if (_running) return true;
        Error = null;
        if (!Available) { Error = "PresentMon is missing (tools\\PresentMon)."; Log.Info("Frames", Error); return false; }
        try
        {
            var psi = new ProcessStartInfo(ExePath,
                $"--output_stdout --no_console_stats --session_name {SessionName} --stop_existing_session " +
                "--no_track_display --no_track_gpu --no_track_input --exclude dwm.exe --exclude AlienControl.exe")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            };
            var p = Process.Start(psi)!;
            AttachToJob(p);
            p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("PresentMon", e.Data!); };
            p.BeginErrorReadLine();
            _proc = p;
            _running = true;
            lock (_lock) { _head = 0; _count = 0; _targetPid = 0; _targetApp = ""; _runtime = ""; }
            _reader = new Thread(() => ReadLoop(p)) { IsBackground = true, Name = "PresentMon reader", Priority = ThreadPriority.BelowNormal };
            _reader.Start();
            Log.Info("Frames", "PresentMon started");
            return true;
        }
        catch (Exception ex) { Error = "PresentMon didn't start: " + ex.Message; Log.Warn("Frames.Start", ex); _running = false; return false; }
    }

    public void Stop()
    {
        if (!_running && _proc is null) return;
        _running = false;
        var p = _proc;
        _proc = null;
        try { if (p is { HasExited: false }) p.Kill(true); } catch (Exception e) { Log.Warn("Frames.Kill", e); }
        try { p?.Dispose(); } catch { /* gone */ }
        StopSessionQuietly();
        lock (_lock) { _count = 0; _targetPid = 0; _targetApp = ""; _runtime = ""; }
        Log.Info("Frames", "PresentMon stopped");
    }

    /// <summary>Ends our ETW session if one is still around (killed PresentMon). Bounded, fire and forget.</summary>
    private static void StopSessionQuietly()
    {
        if (!Available) return;
        try
        {
            ProcessRunner.Run(ExePath, $"--session_name {SessionName} --terminate_existing_session", 3000);
        }
        catch (Exception e) { Log.Warn("Frames.StopSession", e); }
    }

    // ---------- CSV ----------

    private int _colApp = -1, _colPid = -1, _colRuntime = -1, _colMs = -1, _colT = -1;

    /// <summary>Reads the header once, then one frame per line. Lines that don't belong to the foreground process are dropped.</summary>
    private void ReadLoop(Process p)
    {
        try
        {
            var reader = p.StandardOutput;
            string? line;
            bool header = false;
            while (_running && (line = reader.ReadLine()) is not null)
            {
                if (!header)
                {
                    if (!line.StartsWith("Application", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ParseHeader(line)) { Error = "PresentMon output not understood."; Log.Info("Frames", "header: " + line); return; }
                    header = true;
                    continue;
                }
                OnLine(line);
            }
            if (_running) { Error = "PresentMon stopped."; Log.Info("Frames", $"PresentMon exited ({SafeExit(p)})"); }
        }
        catch (Exception ex) { if (_running) { Error = "PresentMon read failed."; Log.Warn("Frames.Read", ex); } }
        finally { _running = false; }
    }

    private static string SafeExit(Process p) { try { return p.HasExited ? p.ExitCode.ToString() : "running"; } catch { return "?"; } }

    internal bool ParseHeader(string line)
    {
        var cols = line.Split(',');
        _colApp = _colPid = _colRuntime = _colMs = _colT = -1;
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i].Trim();
            if (c.Equals("Application", StringComparison.OrdinalIgnoreCase)) _colApp = i;
            else if (c.Equals("ProcessID", StringComparison.OrdinalIgnoreCase)) _colPid = i;
            else if (c.Equals("PresentRuntime", StringComparison.OrdinalIgnoreCase)) _colRuntime = i;
            else if (c.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)) _colMs = i;
            else if (_colMs < 0 && c.Equals("FrameTime", StringComparison.OrdinalIgnoreCase)) _colMs = i;
            else if (c.Equals("CPUStartTime", StringComparison.OrdinalIgnoreCase) || c.Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase)) _colT = i;
        }
        return _colPid >= 0 && _colMs >= 0;
    }

    /// <summary>Parses one CSV line (no allocation for skipped lines beyond the split). Returns true when a frame was kept.</summary>
    internal bool OnLine(string line)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _lastFgCheck > Stopwatch.Frequency / 4) { _lastFgCheck = now; RefreshForeground(); }
        var cols = line.Split(',');
        if (cols.Length <= Math.Max(_colPid, _colMs)) return false;
        if (!int.TryParse(cols[_colPid], out int pid)) return false;
        int fg;
        lock (_lock) fg = _fgPid;
        if (pid != fg) return false;
        if (!float.TryParse(cols[_colMs], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ms)) return false;
        if (ms <= 0 || ms > 5000) return false;
        double t = double.NaN;
        if (_colT >= 0 && _colT < cols.Length) double.TryParse(cols[_colT], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out t);
        lock (_lock)
        {
            if (_targetPid != pid)
            {
                _targetPid = pid;
                _targetApp = _colApp >= 0 && _colApp < cols.Length ? cols[_colApp] : "";
                _runtime = _colRuntime >= 0 && _colRuntime < cols.Length ? cols[_colRuntime] : "";
                _count = 0;
            }
            if (double.IsNaN(t)) t = (_count > 0 ? _ring[(_head - 1 + _ring.Length) % _ring.Length].T : 0) + ms / 1000.0;
            _ring[_head] = new Frame(now, t, ms);
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
        return true;
    }

    // ---------- foreground process ----------

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    private int _fgPid;

    private void RefreshForeground()
    {
        var h = GetForegroundWindow();
        int pid = 0;
        if (h != IntPtr.Zero) GetWindowThreadProcessId(h, out pid);
        lock (_lock) _fgPid = pid;
    }

    /// <summary>Test hook: pretend this pid owns the foreground window.</summary>
    internal void SetForegroundForTest(int pid) { lock (_lock) _fgPid = pid; _lastFgCheck = long.MaxValue / 2; }

    // ---------- stats for the overlay ----------

    /// <summary>Copies the newest frames (oldest first) into dst; returns how many. Used by the overlay thread.</summary>
    public int CopyRecent(Span<Frame> dst)
    {
        lock (_lock)
        {
            int n = Math.Min(dst.Length, _count);
            int start = (_head - n + _ring.Length) % _ring.Length;
            for (int i = 0; i < n; i++) dst[i] = _ring[(start + i) % _ring.Length];
            return n;
        }
    }

    /// <summary>MangoHud's fps: the frames of the last sampling period, measured on PresentMon's own clock (the newest
    /// frame's CPUStartTime back by the window), so bursty stdout doesn't skew it. Zero once nothing new has
    /// arrived for a second (game paused, alt tabbed).</summary>
    public static (double Fps, double AvgMs) FpsOver(ReadOnlySpan<Frame> frames, long nowTicks, long windowTicks)
    {
        if (frames.Length == 0) return (0, 0);
        if (nowTicks - frames[^1].Ticks > Stopwatch.Frequency) return (0, 0);
        double window = windowTicks / (double)Stopwatch.Frequency, last = frames[^1].T;
        double sum = 0; int n = 0;
        for (int i = frames.Length - 1; i >= 0; i--)
        {
            if (last - frames[i].T > window) break;
            sum += frames[i].Ms; n++;
        }
        if (n == 0) return (0, 0);
        double avg = sum / n;
        return (avg > 0 ? 1000.0 / avg : 0, avg);
    }

    // ---------- job object: PresentMon dies with us ----------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int len);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private void AttachToJob(Process p)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                SetInformationJobObject(_job, 9, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            AssignProcessToJobObject(_job, p.Handle);
        }
        catch (Exception e) { Log.Warn("Frames.Job", e); }
    }

    public void Dispose() => Stop();
}
