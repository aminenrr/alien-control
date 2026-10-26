using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AlienControl.Services;

/// <summary>
/// diag (overlay latency experiment, Amine 2026-10-26): opt-in measurement of the overlay's telemetry and render path.
/// Off unless %AppData%\AlienControl\overlay-diag.json exists with "enabled": true; read once at app start. With it off
/// every hook below is a single bool check and the overlay behaves exactly like 2.0.2.
///
/// All timestamps are QPC ticks (Stopwatch.GetTimestamp, the same counter PresentMon prints with --qpc_time).
///   C_i = PresentMon CPUStartQPC of game frame i          P_i = PresentMon TimeInQPC (the game's Present() call)
///   A_i = arrival QPC of its CSV row (ReadLine returned)   B_k = first arrival of batch k, W_k = when the pipe first
///                                                          had bytes for batch k (probe thread, PeekNamedPipe)
///   For overlay frame j: S_j = graph sampled (FillGraph), e = the newest frame drawn at the right edge,
///   Q0_j / Q1_j = Present() called / returned, V_j = display QPC from IDXGISwapChain::GetFrameStatistics.
///   D_data = A_e - P_e   D_wait = S_j - A_e (ring + Playhead hold + loop wait)   D_present = V_j - S_j
///   Age_e2e = V_j - P_e, computed directly; it equals the sum of the three by construction (no term counted twice).
///   Playhead's own delay estimate is logged as context only, never added.
/// </summary>
public static class OverlayDiag
{
    public enum PlayMode { Adaptive, Fixed, Off }

    public sealed class Config
    {
        public bool Enabled { get; set; }
        public string Playhead { get; set; } = "ab";      // adaptive | fixed | off | ab (cycles adaptive, fixed, off)
        public int FixedMs { get; set; } = 50;
        public int AbSeconds { get; set; } = 20;
        public string ReaderPriority { get; set; } = "BelowNormal";
        public bool PipeProbe { get; set; } = true;
        public bool EtwStatus { get; set; } = true;
        public bool Raw { get; set; } = true;
    }

    public static readonly Config Cfg = LoadCfg();
    public static bool On => Cfg.Enabled;
    public static readonly double F = Stopwatch.Frequency;

    private static Config LoadCfg()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "overlay-diag.json");
            if (!File.Exists(path)) return new Config();
            var c = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Config();
            c.FixedMs = Math.Clamp(c.FixedMs, 0, 1000);
            c.AbSeconds = Math.Clamp(c.AbSeconds, 5, 600);
            return c;
        }
        catch (Exception e) { Log.Warn("Diag.Config", e); return new Config(); }
    }

    // ---------- Playhead mode (A/B) ----------

    private static long _abStart;
    public static PlayMode Mode(long now)
    {
        var p = Cfg.Playhead.ToLowerInvariant();
        if (p == "adaptive") return PlayMode.Adaptive;
        if (p == "fixed") return PlayMode.Fixed;
        if (p == "off") return PlayMode.Off;
        if (_abStart == 0) _abStart = now;
        long slot = (long)((now - _abStart) / (Cfg.AbSeconds * F)) % 3;
        return slot switch { 0 => PlayMode.Adaptive, 1 => PlayMode.Fixed, _ => PlayMode.Off };
    }

    public static string ModeName(PlayMode m) => m switch { PlayMode.Fixed => $"fixed{Cfg.FixedMs}", PlayMode.Off => "off", _ => "adaptive" };

    // ---------- reader side (PresentMon reader thread) ----------

    private static readonly object _l = new();
    private static long _lastLine, _batchStart, _batchAvail;
    private static int _batchRows, _batchBytes;
    private static long _batchId;
    public static long AvailSince;   // written by the probe thread
    private static int _probeMissed;

    public static ThreadPriority ReaderPriority() =>
        Enum.TryParse<ThreadPriority>(Cfg.ReaderPriority, true, out var p) ? p : ThreadPriority.BelowNormal;

    /// <summary>Every CSV row (all processes), right after ReadLine. A new batch starts after a 2 ms silence.</summary>
    public static void OnRow(long now, int bytes)
    {
        lock (_l)
        {
            if (_lastLine == 0 || now - _lastLine > F * 0.002)
            {
                CloseBatch();
                _batchId++;
                _batchStart = now;
                long a = Interlocked.Read(ref AvailSince);
                _batchAvail = a > 0 && a <= now ? a : 0;
                if (_batchAvail == 0) _probeMissed++;
            }
            _batchRows++; _batchBytes += bytes;
            _lastLine = now;
        }
    }

    private static long _prevBatchStart;
    private static void CloseBatch()
    {
        if (_batchRows == 0) return;
        var w = Win();
        if (_prevBatchStart > 0) w.Gap.Add((_batchStart - _prevBatchStart) / F * 1000);
        w.BatchRows.Add(_batchRows);
        w.BatchBytes.Add(_batchBytes);
        if (_batchAvail > 0) w.ReaderDelay.Add((_batchStart - _batchAvail) / F * 1000);
        Raw("batches", $"{_batchId},{_batchStart},{_batchAvail},{_batchRows},{_batchBytes}");
        _prevBatchStart = _batchStart;
        _batchRows = 0; _batchBytes = 0;
    }

    /// <summary>A target frame kept in the ring. cpuQpc / presentQpc 0 = PresentMon didn't give it.</summary>
    public static void OnFrame(long arrival, long cpuQpc, long presentQpc, float ms)
    {
        lock (_l)
        {
            var w = Win();
            w.Frames++;
            if (presentQpc > 0) w.DataP.Add((arrival - presentQpc) / F * 1000);
            if (cpuQpc > 0) w.DataC.Add((arrival - cpuQpc) / F * 1000);
            Raw("rows", $"{_batchId},{arrival},{cpuQpc},{presentQpc},{ms.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    public static void OnEtwStatus(string fill, string inUse, string total, string lost, string bufLost, string overflow)
    {
        lock (_l) { var w = Win(); w.Etw = $"fill {fill}%, buffers {inUse}/{total}, events lost {lost}, buffers lost {bufLost}, overflowed presents {overflow}"; }
    }

    // ---------- pipe probe ----------

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(SafeHandle h, IntPtr buf, uint size, IntPtr read, out uint avail, IntPtr left);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>Polls the stdout pipe every ~1 ms and stamps when bytes first became available, so the reader thread's
    /// own scheduling delay (W_k to B_k) is measured apart from PresentMon's / ETW's delivery.</summary>
    public static void StartProbe(Stream s, Func<bool> running)
    {
        if (!Cfg.PipeProbe) return;
        SafeHandle? h = s switch { FileStream fs => fs.SafeFileHandle, System.IO.Pipes.PipeStream ps => ps.SafePipeHandle, _ => null };
        if (h is null) { Log.Info("Diag", $"pipe probe off: stream is {s.GetType().Name}"); return; }
        new Thread(() =>
        {
            timeBeginPeriod(1);
            try
            {
                while (running())
                {
                    if (!PeekNamedPipe(h, IntPtr.Zero, 0, IntPtr.Zero, out uint avail, IntPtr.Zero)) break;
                    if (avail > 0) { if (Interlocked.Read(ref AvailSince) == 0) Interlocked.Exchange(ref AvailSince, Stopwatch.GetTimestamp()); }
                    else Interlocked.Exchange(ref AvailSince, 0);
                    Thread.Sleep(1);
                }
            }
            catch (Exception e) { Log.Warn("Diag.Probe", e); }
            finally { timeEndPeriod(1); }
        }) { IsBackground = true, Name = "diag pipe probe", Priority = ThreadPriority.Highest }.Start();
    }

    // ---------- render side ----------

    public struct Sample { public long S, EdgeA, EdgeC, EdgeP; public double Delay; public PlayMode Mode; public bool Has; }

    private readonly struct Pending
    {
        public readonly uint Id; public readonly Sample Smp; public readonly long Q0, Q1;
        public Pending(uint id, Sample s, long q0, long q1) { Id = id; Smp = s; Q0 = q0; Q1 = q1; }
    }
    private static readonly Queue<Pending> _pending = new();
    private static long _lastQ1;
    private static string _statsNote = "";

    /// <summary>After each D2D Present: s = what FillGraph drew, q0 / q1 = Present called / returned, id = the swapchain's
    /// present count after it, stats = its frame statistics (ok false = not available, hr says why), dcomp = composition
    /// timing context (last / next estimated frame, rate) or zeros.</summary>
    public static void OnPresent(Sample s, long q0, long q1, uint id, bool ok, uint statCount, long statQpc, int hr,
        long dcLast, long dcNext, double dcRate, long dcFreq, double hz, double gameMs)
    {
        lock (_l)
        {
            var w = Win();
            if (Settling(q1)) { s.Has = false; }
            w.Draws++;
            w.PresentBlock.Add((q1 - q0) / F * 1000);
            if (_lastQ1 > 0)
            {
                double gap = (q1 - _lastQ1) / F;
                double expect = Math.Max(1.0 / Math.Clamp(hz, 24, 500), gameMs > 0 && gameMs < 1000 ? gameMs / 1000 : 0);
                if (gap > 1.5 * expect) w.Missed++;
            }
            _lastQ1 = q1;
            if (dcRate > 0) w.DcRate = dcRate;
            if (dcFreq > 0 && dcNext > 0)
            {
                // context only: the first composition at or after Present returned (an estimate, kept apart)
                double periodQ = F / Math.Max(dcRate > 0 ? dcRate : hz, 1);
                double est = dcNext * (F / dcFreq);
                while (est - periodQ >= q1) est -= periodQ;
                while (est < q1) est += periodQ;
                if (s.Has && s.EdgeP > 0) w.E2eDcompEst.Add((est - s.EdgeP) / F * 1000);
            }
            if (s.Has)
            {
                if (s.EdgeP > 0) w.AgeAtSample.Add((s.S - s.EdgeP) / F * 1000);
                w.Wait.Add((s.S - s.EdgeA) / F * 1000);
                w.Delay.Add(s.Delay * 1000);
            }
            if (!ok) { w.StatsFail++; if (_statsNote.Length == 0) { _statsNote = $"0x{hr:X8}"; Log.Info("Diag", $"GetFrameStatistics failed {_statsNote}"); } }
            _pending.Enqueue(new Pending(id, s, q0, q1));
            while (_pending.Count > 32) { _pending.Dequeue(); w.Unmatched++; }
            if (ok)
            {
                while (_pending.Count > 0 && _pending.Peek().Id <= statCount)
                {
                    var pd = _pending.Dequeue();
                    if (pd.Id != statCount) { w.Unmatched++; continue; }
                    w.Matched++;
                    long v = statQpc;
                    w.PresentToDisp.Add((v - pd.Q1) / F * 1000);
                    if (pd.Smp.Has)
                    {
                        w.DPresent.Add((v - pd.Smp.S) / F * 1000);
                        if (pd.Smp.EdgeP > 0)
                        {
                            w.E2e.Add((v - pd.Smp.EdgeP) / F * 1000);
                            w.EdgeDataP.Add((pd.Smp.EdgeA - pd.Smp.EdgeP) / F * 1000);
                            w.EdgeWait.Add((pd.Smp.S - pd.Smp.EdgeA) / F * 1000);
                        }
                    }
                    Raw("frames", $"{ModeName(pd.Smp.Mode)},{pd.Smp.S},{pd.Smp.EdgeP},{pd.Smp.EdgeC},{pd.Smp.EdgeA},{pd.Q0},{pd.Q1},{pd.Id},{v},{(pd.Smp.Delay * 1000).ToString("0.0", CultureInfo.InvariantCulture)}");
                }
            }
            else Raw("frames", $"{ModeName(s.Mode)},{s.S},{s.EdgeP},{s.EdgeC},{s.EdgeA},{q0},{q1},{id},0,{(s.Delay * 1000).ToString("0.0", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>WPF renderer: only the sample side is measurable (no swapchain statistics).</summary>
    public static void OnWpfFrame(Sample s)
    {
        lock (_l)
        {
            var w = Win();
            w.Draws++;
            if (!s.Has || Settling(s.S)) return;
            if (s.EdgeP > 0) w.AgeAtSample.Add((s.S - s.EdgeP) / F * 1000);
            w.Wait.Add((s.S - s.EdgeA) / F * 1000);
            w.Delay.Add(s.Delay * 1000);
        }
    }

    // ---------- windows and the log ----------

    private sealed class Window
    {
        public PlayMode Mode; public long Start;
        public int Frames, Draws, Missed, Matched, Unmatched, StatsFail;
        public double DcRate; public string Etw = "";
        public readonly List<double> DataP = new(), DataC = new(), Gap = new(), BatchRows = new(), BatchBytes = new(), ReaderDelay = new(),
            PresentBlock = new(), AgeAtSample = new(), Wait = new(), Delay = new(), DPresent = new(), E2e = new(), EdgeDataP = new(),
            EdgeWait = new(), PresentToDisp = new(), E2eDcompEst = new();
    }

    private static Window? _w;
    private static long _settleUntil;
    /// <summary>The first 2 s after a Playhead mode switch (the playhead glides to its new target) stay out of the stats;
    /// the raw CSV keeps them.</summary>
    private static bool Settling(long now) => now < _settleUntil;
    private static TimeSpan _cpu0, _pmCpu0;
    public static Func<TimeSpan>? PresentMonCpu;   // set by FramesSource
    public static string Target = "";
    public static string Renderer = "";

    private static Window Win()
    {
        long now = Stopwatch.GetTimestamp();
        var m = Mode(now);
        if (_w is null || _w.Mode != m || now - _w.Start > F * 10)
        {
            if (_w is not null) Flush(_w, now);
            if (_w is null || _w.Mode != m) _settleUntil = now + (long)(F * 2);
            _w = new Window { Mode = m, Start = now };
            try { using var p = Process.GetCurrentProcess(); _cpu0 = p.TotalProcessorTime; } catch { }
            try { _pmCpu0 = PresentMonCpu?.Invoke() ?? TimeSpan.Zero; } catch { _pmCpu0 = TimeSpan.Zero; }
        }
        return _w;
    }

    /// <summary>Called by the number timer so a window is written even when no frames arrive.</summary>
    public static void Tick() { if (On) lock (_l) Win(); }

    private static string P(List<double> v, string unit = " ms")
    {
        if (v.Count == 0) return "n/a";
        v.Sort();
        double q(double p) => v[Math.Min(v.Count - 1, (int)Math.Floor(p * (v.Count - 1) + 0.5))];
        return string.Format(CultureInfo.InvariantCulture, "p50 {0:0.0} p95 {1:0.0} max {2:0.0}{3} (n {4})", q(0.5), q(0.95), v[^1], unit, v.Count);
    }

    private static void Flush(Window w, long now)
    {
        double sec = (now - w.Start) / F;
        if (sec < 1) return;
        double cpu = 0, pm = 0;
        try { using var p = Process.GetCurrentProcess(); cpu = (p.TotalProcessorTime - _cpu0).TotalSeconds / sec / Environment.ProcessorCount * 100; } catch { }
        try { pm = ((PresentMonCpu?.Invoke() ?? TimeSpan.Zero) - _pmCpu0).TotalSeconds / sec / Environment.ProcessorCount * 100; } catch { }
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Diag [{ModeName(w.Mode)}] {Renderer} {Target} {sec:0.0} s, {w.Frames} game frames, {w.Draws} draws, {w.Missed} missed, CPU app {cpu:0.0} % PresentMon {pm:0.0} %\n");
        sb.Append($"  e2e age V-P_edge: {P(w.E2e)} | at sample S-P_edge: {P(w.AgeAtSample)} | dcomp est: {P(w.E2eDcompEst)}\n");
        sb.Append($"  parts (matched frames): D_data A-P {P(w.EdgeDataP)} | D_wait S-A {P(w.EdgeWait)} | D_present V-S {P(w.DPresent)}\n");
        sb.Append($"  all rows: A-P {P(w.DataP)} | A-C {P(w.DataC)} | Playhead delay {P(w.Delay)}\n");
        sb.Append($"  batches: gap {P(w.Gap)} | rows {P(w.BatchRows, "")} | bytes {P(w.BatchBytes, "")} | reader delay W-B {P(w.ReaderDelay)}, probe missed {_probeMissed}\n");
        sb.Append($"  present: block {P(w.PresentBlock)} | Q1->V {P(w.PresentToDisp)} | stats matched {w.Matched}, unmatched {w.Unmatched}, failed {w.StatsFail} | composition {w.DcRate:0.#} Hz");
        if (w.Etw.Length > 0) sb.Append($"\n  ETW: {w.Etw}");
        _probeMissed = 0;
        Log.Info("Diag", sb.ToString());
    }

    // ---------- raw CSV ----------

    private static readonly ConcurrentQueue<(string, string)> _raw = new();
    private static Thread? _writer;
    private static string _dir = "";
    private static long _rawBytes;

    private static void Raw(string file, string line)
    {
        if (!Cfg.Raw || _rawBytes > 200L * 1024 * 1024) return;
        if (_writer is null) StartWriter();
        _raw.Enqueue((file, line));
    }

    private static void StartWriter()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlienControl", "diag", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "meta.txt"), $"qpc_frequency={Stopwatch.Frequency}\nplayhead={Cfg.Playhead}\nfixed_ms={Cfg.FixedMs}\nab_seconds={Cfg.AbSeconds}\nreader_priority={Cfg.ReaderPriority}\n");
        Log.Info("Diag", "raw CSV in " + _dir);
        _writer = new Thread(() =>
        {
            var writers = new Dictionary<string, StreamWriter>();
            var heads = new Dictionary<string, string>
            {
                ["rows"] = "batch,arrival_qpc,cpustart_qpc,present_qpc,ms",
                ["batches"] = "batch,start_qpc,avail_qpc,rows,bytes",
                ["frames"] = "mode,sample_qpc,edge_present_qpc,edge_cpustart_qpc,edge_arrival_qpc,present_call_qpc,present_ret_qpc,present_id,display_qpc,playhead_delay_ms",
            };
            while (true)
            {
                Thread.Sleep(1000);
                while (_raw.TryDequeue(out var r))
                {
                    if (!writers.TryGetValue(r.Item1, out var sw))
                    {
                        sw = new StreamWriter(Path.Combine(_dir, r.Item1 + ".csv"), true);
                        sw.WriteLine(heads[r.Item1]);
                        writers[r.Item1] = sw;
                    }
                    sw.WriteLine(r.Item2);
                    _rawBytes += r.Item2.Length + 2;
                }
                foreach (var sw in writers.Values) sw.Flush();
            }
        }) { IsBackground = true, Name = "diag writer", Priority = ThreadPriority.BelowNormal };
        _writer.Start();
    }
}
