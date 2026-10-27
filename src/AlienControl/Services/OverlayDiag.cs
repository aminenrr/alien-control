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
///   A_i = arrival QPC of its CSV row (ReadLine returned)   B_k = first arrival of batch k, W_k = when the raw stdout
///                                                          chunk holding batch k's first row came out of ReadFile on the
///                                                          Highest-priority pump thread (diag pass 2; pass 1's PeekNamedPipe
///                                                          probe was serialized behind the reader's blocking ReadFile)
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
        public string Playhead { get; set; } = "ab";      // adaptive | fixed | fixedNNN | off | ab (cycles adaptive, fixed, off) | cycle (PlayCycle)
        /// <summary>diag step 1 (Amine 2026-10-27): with Playhead "cycle", the modes in order, AbSeconds each.
        /// Tokens: off, adaptive, fixed (FixedMs) or fixedNNN. Adaptive is followed by off so the next fixed slot never
        /// has to glide down from the adaptive hold.</summary>
        public string[] PlayCycle { get; set; } = { "fixed120", "off", "fixed120", "off", "adaptive", "off" };
        public int FixedMs { get; set; } = 50;
        public int AbSeconds { get; set; } = 20;
        public string ReaderPriority { get; set; } = "BelowNormal";
        public bool PipeProbe { get; set; } = true;   // pass 1 only, ignored now (see Pump)
        public bool EtwStatus { get; set; } = true;
        public bool Raw { get; set; } = true;
        // diag pass 2 (Amine 2026-10-26)
        /// <summary>Stamp raw stdout chunks on a Highest-priority pump thread (W_k), the reader parses lines behind it.</summary>
        public bool Pump { get; set; } = true;
        /// <summary>Explicit ControlTrace(EVENT_TRACE_CONTROL_FLUSH) on our PresentMon session: "0" (none, the baseline),
        /// a period in ms ("250", "100", "50"), or "ab" (cycles FlushCycle every FlushSeconds).</summary>
        public string Flush { get; set; } = "0";
        public int[] FlushCycle { get; set; } = { 0, 250, 100, 50 };
        public int FlushSeconds { get; set; } = 30;
        /// <summary>Query the session ourselves (ControlTrace QUERY) once a second: events / buffers lost, buffers, timer.</summary>
        public bool EtwQuery { get; set; } = true;
        // diag pass 3 (Amine 2026-10-27): PresentMon output-drain interval. "stock" runs the official
        // tools\PresentMon\PresentMon.exe; "patched" runs tools\PresentMon\PresentMon-drain.exe (2.6.0 built from source
        // with only OutputThread's Sleep(100) made settable through a shared section, dev/diag/presentmon-2.6.0-drain.patch).
        public string PresentMon { get; set; } = "stock";
        /// <summary>Drain interval for the patched build: a period in ms ("100" = stock Sleep(100)), or "ab"
        /// (cycles DrainCycle every DrainSeconds).</summary>
        public string Drain { get; set; } = "100";
        public int[] DrainCycle { get; set; } = { 100, 16, 10 };
        public int DrainSeconds { get; set; } = 30;
        public bool Patched => string.Equals((PresentMon ?? "").Trim(), "patched", StringComparison.OrdinalIgnoreCase);
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
            c.FlushSeconds = Math.Clamp(c.FlushSeconds, 5, 600);
            if (c.FlushCycle is null || c.FlushCycle.Length == 0) c.FlushCycle = new[] { 0, 250, 100, 50 };
            for (int i = 0; i < c.FlushCycle.Length; i++) c.FlushCycle[i] = c.FlushCycle[i] <= 0 ? 0 : Math.Clamp(c.FlushCycle[i], 10, 1000);
            c.DrainSeconds = Math.Clamp(c.DrainSeconds, 5, 600);
            if (c.DrainCycle is null || c.DrainCycle.Length == 0) c.DrainCycle = new[] { 100, 16, 10 };
            for (int i = 0; i < c.DrainCycle.Length; i++) c.DrainCycle[i] = Math.Clamp(c.DrainCycle[i], 1, 1000);
            return c;
        }
        catch (Exception e) { Log.Warn("Diag.Config", e); return new Config(); }
    }

    // ---------- Playhead mode (A/B) ----------

    private static long _abStart;
    public static PlayMode Mode(long now) => ModeAt(Cfg, now, ref _abStart).Mode;
    /// <summary>The fixed hold (ms) in force at now (only meaningful while the mode is Fixed).</summary>
    public static int FixedMsNow(long now) => ModeAt(Cfg, now, ref _abStart).FixedMs;

    internal static (PlayMode Mode, int FixedMs) Token(string t, int fixedDefault)
    {
        t = (t ?? "").Trim().ToLowerInvariant();
        if (t == "off") return (PlayMode.Off, fixedDefault);
        if (t == "fixed") return (PlayMode.Fixed, fixedDefault);
        if (t.StartsWith("fixed", StringComparison.Ordinal) && int.TryParse(t.AsSpan(5), out int ms)) return (PlayMode.Fixed, Math.Clamp(ms, 0, 1000));
        return (PlayMode.Adaptive, fixedDefault);
    }

    internal static (PlayMode Mode, int FixedMs) ModeAt(Config c, long now, ref long start)
    {
        var p = (c.Playhead ?? "ab").Trim().ToLowerInvariant();
        if (p == "ab")
        {
            if (start == 0) start = now;
            long slot3 = (long)((now - start) / (c.AbSeconds * F)) % 3;
            return (slot3 switch { 0 => PlayMode.Adaptive, 1 => PlayMode.Fixed, _ => PlayMode.Off }, c.FixedMs);
        }
        if (p == "cycle" && c.PlayCycle is { Length: > 0 })
        {
            if (start == 0) start = now;
            long slot = (long)((now - start) / (c.AbSeconds * F)) % c.PlayCycle.Length;
            return Token(c.PlayCycle[slot], c.FixedMs);
        }
        return Token(p, c.FixedMs);
    }

    private static long _flushStart;
    /// <summary>diag pass 2: the explicit flush period in force at now (ms, 0 = none).</summary>
    public static int FlushMs(long now) => FlushMsAt(Cfg, now, ref _flushStart);

    internal static int FlushMsAt(Config c, long now, ref long start)
    {
        var f = (c.Flush ?? "0").Trim().ToLowerInvariant();
        if (f != "ab") return int.TryParse(f, out int v) && v > 0 ? Math.Clamp(v, 10, 1000) : 0;
        if (start == 0) start = now;
        long slot = (long)((now - start) / (c.FlushSeconds * F)) % c.FlushCycle.Length;
        return c.FlushCycle[slot];
    }

    private static long _drainStart;
    /// <summary>diag pass 3: the PresentMon drain interval in force at now (ms). The stock build is always 100.</summary>
    public static int DrainMs(long now) => Cfg.Patched ? DrainMsAt(Cfg, now, ref _drainStart) : 100;

    internal static int DrainMsAt(Config c, long now, ref long start)
    {
        var d = (c.Drain ?? "100").Trim().ToLowerInvariant();
        if (d != "ab") return int.TryParse(d, out int v) && v > 0 ? Math.Clamp(v, 1, 1000) : 100;
        if (start == 0) start = now;
        long slot = (long)((now - start) / (c.DrainSeconds * F)) % c.DrainCycle.Length;
        return c.DrainCycle[slot];
    }

    public static string Label(PlayMode m, int flushMs, int drainMs = -1, int fixedMs = -1) => ModeName(m, fixedMs) + (flushMs > 0 ? $" flush{flushMs}" : " noflush")
        + (Cfg.Patched ? $" drain{(drainMs >= 0 ? drainMs : DrainMs(Stopwatch.GetTimestamp()))}" : "");

    // ---------- pass 3: shared section read by the patched PresentMon ----------

    private static System.IO.MemoryMappedFiles.MemoryMappedFile? _drainMap;
    private static System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _drainView;
    public const string DrainSection = @"Local\AlienControlsPmDrain";

    /// <summary>Creates the 16-byte section ([0] drain ms, [4] loop counter, [8] magic, both written by the patched
    /// PresentMon) before PresentMon starts, and keeps [0] at the current drain interval. Patched mode only.</summary>
    public static void StartDrain()
    {
        if (!Cfg.Patched || _drainMap is not null) return;
        try
        {
            _drainMap = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen(DrainSection, 16);
            _drainView = _drainMap.CreateViewAccessor(0, 16);
            _drainView.Write(0, DrainMs(Stopwatch.GetTimestamp()));
            Log.Info("Diag", $"drain section {DrainSection}: drain {Cfg.Drain}" + (Cfg.Drain.Trim().ToLowerInvariant() == "ab" ? $" cycle [{string.Join(",", Cfg.DrainCycle)}] every {Cfg.DrainSeconds} s" : ""));
            new Thread(() =>
            {
                while (true)
                {
                    try { _drainView!.Write(0, DrainMs(Stopwatch.GetTimestamp())); } catch { }
                    Thread.Sleep(5);
                }
            }) { IsBackground = true, Name = "diag drain", Priority = ThreadPriority.BelowNormal }.Start();
        }
        catch (Exception e) { Log.Warn("Diag.Drain", e); }
    }

    private static int DrainLoops() { try { return _drainView?.ReadInt32(4) ?? 0; } catch { return 0; } }
    private static bool DrainAck() { try { return _drainView?.ReadInt32(8) == 0x41435044; } catch { return false; } }

    public static string ModeName(PlayMode m, int fixedMs = -1) => m switch { PlayMode.Fixed => $"fixed{(fixedMs >= 0 ? fixedMs : FixedMsNow(Stopwatch.GetTimestamp()))}", PlayMode.Off => "off", _ => "adaptive" };

    // ---------- reader side (PresentMon reader thread) ----------

    private static readonly object _l = new();
    private static long _lastLine, _batchStart, _batchAvail, _batchChunk;
    private static int _batchRows, _batchBytes, _batchFlush, _batchDrain;
    private static long _batchId;
    private static int _probeMissed;   // batches whose first row had no pump stamp
    private static long _batchNewestP, _batchNewestA;   // the target row with the newest Present() in the batch

    public static ThreadPriority ReaderPriority() =>
        Enum.TryParse<ThreadPriority>(Cfg.ReaderPriority, true, out var p) ? p : ThreadPriority.BelowNormal;

    /// <summary>Every CSV row (all processes), right after ReadLine. A new batch starts after a 2 ms silence.
    /// chunkQpc / chunkSeq = the pump stamp of the raw chunk this row came from (0 = no pump).</summary>
    public static void OnRow(long now, int bytes, long chunkQpc = 0, long chunkSeq = 0)
    {
        lock (_l)
        {
            if (_lastLine == 0 || now - _lastLine > F * 0.002)
            {
                CloseBatch();
                _batchId++;
                _batchStart = now;
                _batchAvail = chunkQpc > 0 && chunkQpc <= now ? chunkQpc : 0;
                _batchChunk = chunkSeq;
                _batchFlush = FlushMs(now);
                _batchDrain = DrainMs(now);
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
        if (_batchNewestP > 0) w.NewestInBatch.Add((_batchNewestA - _batchNewestP) / F * 1000);
        _batchNewestP = 0;
        Raw("batches", $"{_batchId},{_batchStart},{_batchAvail},{_batchChunk},{_batchRows},{_batchBytes},{_batchFlush},{_batchDrain}");
        _prevBatchStart = _batchStart;
        _batchRows = 0; _batchBytes = 0;
    }

    /// <summary>A target frame kept in the ring. cpuQpc / presentQpc 0 = PresentMon didn't give it.</summary>
    public static void OnFrame(long arrival, long cpuQpc, long presentQpc, float ms)
    {
        if (On && ms > 0 && ms < 1000) lock (_l) { var gw = Win(); if (!Settling(arrival)) gw.GameMs.Add(ms); }
        lock (_l)
        {
            var w = Win();
            w.Frames++;
            if (presentQpc > 0) w.DataP.Add((arrival - presentQpc) / F * 1000);
            if (presentQpc > _batchNewestP) { _batchNewestP = presentQpc; _batchNewestA = arrival; }
            if (cpuQpc > 0) w.DataC.Add((arrival - cpuQpc) / F * 1000);
            Raw("rows", $"{_batchId},{arrival},{cpuQpc},{presentQpc},{ms.ToString(CultureInfo.InvariantCulture)},{FlushMs(arrival)},{DrainMs(arrival)}");
        }
    }

    public static void OnEtwStatus(string fill, string inUse, string total, string lost, string bufLost, string overflow)
    {
        lock (_l) { var w = Win(); w.Etw = $"fill {fill}%, buffers {inUse}/{total}, events lost {lost}, buffers lost {bufLost}, overflowed presents {overflow}"; }
    }

    /// <summary>diag pass 2: PresentMon 2.6.0 prints --track_etw_status on stdout as "[ETW Status] BufferFillPct=..%
    /// BuffersInUse=.. TotalBuffers=.. EventsLost=.. BuffersLost=.., OverflowedPresents=.." once a second (its CSV path
    /// for our columns never writes the Etw* columns). Returns false when the line isn't one.</summary>
    public static bool OnEtwStatusLine(long now, string line)
    {
        if (!TryParseEtwStatus(line, out var e)) return false;
        lock (_l)
        {
            var w = Win();
            w.PmEtw ??= e; w.PmEtwLast = e;
            Raw("etw_pm", string.Format(CultureInfo.InvariantCulture, "{0},{1:0.0},{2},{3},{4},{5},{6},{7}",
                now, e.FillPct, e.InUse, e.Total, e.EventsLost, e.BuffersLost, e.Overflowed, FlushMs(now)));
        }
        return true;
    }

    public sealed record EtwPm(double FillPct, long InUse, long Total, long EventsLost, long BuffersLost, long Overflowed);

    internal static bool TryParseEtwStatus(string line, out EtwPm e)
    {
        e = new EtwPm(0, 0, 0, 0, 0, 0);
        if (!line.StartsWith("[ETW Status]", StringComparison.Ordinal)) return false;
        double V(string key)
        {
            int i = line.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return double.NaN;
            i += key.Length + 1;
            int j = i;
            while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.' || line[j] == '-')) j++;
            return double.TryParse(line.AsSpan(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
        }
        double fill = V("BufferFillPct"), inUse = V("BuffersInUse"), total = V("TotalBuffers"), lost = V("EventsLost"),
            bufLost = V("BuffersLost"), ov = V("OverflowedPresents");
        if (double.IsNaN(lost) || double.IsNaN(bufLost) || double.IsNaN(ov)) return false;
        e = new EtwPm(double.IsNaN(fill) ? 0 : fill, (long)(double.IsNaN(inUse) ? 0 : inUse), (long)(double.IsNaN(total) ? 0 : total),
            (long)lost, (long)bufLost, (long)ov);
        return true;
    }

    // ---------- pump (diag pass 2) ----------

    /// <summary>Wraps PresentMon's stdout: a Highest-priority thread blocks in Read on the raw pipe and stamps each chunk
    /// the moment ReadFile returns it (W). The reader thread (its normal priority) parses lines from the queued chunks
    /// through a StreamReader on top of this, so A - W is the reader's own delay. LastQpc / LastSeq = the stamp of the
    /// chunk the reader is consuming.</summary>
    public sealed class Pump : Stream
    {
        private readonly BlockingCollection<(byte[] Buf, int Len, long Qpc, long Seq)> _q = new(new ConcurrentQueue<(byte[], int, long, long)>());
        private byte[]? _cur; private int _curLen, _curPos;
        public long LastQpc { get; private set; }
        public long LastSeq { get; private set; }
        private long _seq;

        /// <summary>Test seam: queue a chunk stamped qpc (the pump thread does this for real).</summary>
        internal void Push(byte[] b, int len, long qpc) => _q.Add((b, len, qpc, ++_seq));
        internal void Complete() => _q.CompleteAdding();

        public static Pump Start(Stream raw, string name)
        {
            var p = new Pump();
            new Thread(() =>
            {
                try
                {
                    while (true)
                    {
                        var b = new byte[65536];
                        int n = raw.Read(b, 0, b.Length);
                        long t = Stopwatch.GetTimestamp();
                        if (n <= 0) break;
                        p.Push(b, n, t);
                    }
                }
                catch (Exception e) { Log.Warn("Diag.Pump", e); }
                finally { p.Complete(); }
            }) { IsBackground = true, Name = name, Priority = ThreadPriority.Highest }.Start();
            return p;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_cur is null || _curPos >= _curLen)
            {
                if (!_q.TryTake(out var c, Timeout.Infinite)) return 0;
                _cur = c.Buf; _curLen = c.Len; _curPos = 0; LastQpc = c.Qpc; LastSeq = c.Seq;
            }
            int n = Math.Min(count, _curLen - _curPos);
            Buffer.BlockCopy(_cur, _curPos, buffer, offset, n);
            _curPos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------- ETW session control (diag pass 2) ----------

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    private static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);
    private const uint EVENT_TRACE_CONTROL_QUERY = 0, EVENT_TRACE_CONTROL_FLUSH = 3;
    internal const int PropsSize = 120;            // sizeof(EVENT_TRACE_PROPERTIES) on x64
    private const int PropsBuf = PropsSize + 2 * 1024 * 2;

    public sealed record EtwQuery(uint Rc, uint BufferKb, uint MinBuf, uint MaxBuf, uint LogFileMode, uint FlushTimer,
        uint NumberOfBuffers, uint FreeBuffers, uint EventsLost, uint BuffersWritten, uint LogBuffersLost, uint RealTimeBuffersLost);

    private static IntPtr NewProps()
    {
        var p = Marshal.AllocHGlobal(PropsBuf);
        for (int i = 0; i < PropsBuf; i += 4) Marshal.WriteInt32(p, i, 0);
        Marshal.WriteInt32(p, 0, PropsBuf);          // Wnode.BufferSize
        Marshal.WriteInt32(p, 116, PropsSize);        // LoggerNameOffset
        Marshal.WriteInt32(p, 112, PropsSize + 2048); // LogFileNameOffset
        return p;
    }

    internal static EtwQuery ReadProps(uint rc, IntPtr p)
    {
        uint U(int off) => (uint)Marshal.ReadInt32(p, off);
        return new EtwQuery(rc, U(48), U(52), U(56), U(64), U(68), U(80), U(84), U(88), U(92), U(96), U(100));
    }

    public static EtwQuery Query(string session)
    {
        var p = NewProps();
        try { uint rc = ControlTrace(0, session, p, EVENT_TRACE_CONTROL_QUERY); return ReadProps(rc, p); }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetThreadTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    private static double ThreadCpuMs() => GetThreadTimes(GetCurrentThread(), out _, out _, out long k, out long u) ? (k + u) / 10000.0 : 0;

    private static double _flushCpuMs;   // flush thread CPU, cumulative (written by that thread)

    /// <summary>Starts the explicit flush thread and the once-a-second session query. Both only touch our own
    /// PresentMon session (by name) and only while diag is on; with Flush "0" no flush is ever issued.</summary>
    public static void StartEtwControl(string session, Func<bool> running)
    {
        if (_etwStarted) return;
        _etwStarted = true;
        bool admin = false;
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); admin = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); } catch { }
        Log.Info("Diag", $"ETW control: session {session}, elevated {admin}, flush {Cfg.Flush}" + (Cfg.Flush.Trim().ToLowerInvariant() == "ab" ? $" cycle [{string.Join(",", Cfg.FlushCycle)}] every {Cfg.FlushSeconds} s" : "") + $", query {Cfg.EtwQuery}");
        if (Cfg.EtwQuery)
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (running())
                        {
                            long now = Stopwatch.GetTimestamp();
                            var q = Query(session);
                            lock (_l)
                            {
                                var w = Win();
                                if (q.Rc == 0) { w.Q0 ??= q; w.QLast = q; } else w.QFail++;
                                if (q.Rc != 0 && _qNote != q.Rc) { _qNote = q.Rc; Log.Info("Diag", $"ControlTrace QUERY {session} failed rc {q.Rc}"); }
                                Raw("etw_query", $"{now},{q.Rc},{q.BufferKb},{q.MinBuf},{q.MaxBuf},0x{q.LogFileMode:X},{q.FlushTimer},{q.NumberOfBuffers},{q.FreeBuffers},{q.EventsLost},{q.BuffersWritten},{q.LogBuffersLost},{q.RealTimeBuffersLost},{FlushMs(now)}");
                            }
                        }
                    }
                    catch (Exception e) { Log.Warn("Diag.Query", e); }
                    Thread.Sleep(1000);
                }
            }) { IsBackground = true, Name = "diag etw query", Priority = ThreadPriority.BelowNormal }.Start();
        new Thread(() =>
        {
            timeBeginPeriod(1);
            var props = NewProps();
            long next = 0, prevCall = 0;
            int prevMode = -1;
            try
            {
                while (true)
                {
                    long now = Stopwatch.GetTimestamp();
                    int ms = FlushMs(now);
                    if (ms != prevMode) { prevMode = ms; next = now; prevCall = 0; }
                    if (ms <= 0 || !running()) { Thread.Sleep(5); prevCall = 0; continue; }
                    if (now >= next)
                    {
                        Marshal.WriteInt32(props, 0, PropsBuf);
                        Marshal.WriteInt32(props, 116, PropsSize);
                        Marshal.WriteInt32(props, 112, PropsSize + 2048);
                        double c0 = ThreadCpuMs();
                        long t0 = Stopwatch.GetTimestamp();
                        uint rc = ControlTrace(0, session, props, EVENT_TRACE_CONTROL_FLUSH);
                        long t1 = Stopwatch.GetTimestamp();
                        _flushCpuMs += ThreadCpuMs() - c0;
                        double interval = prevCall > 0 ? (t0 - prevCall) / F * 1000 : double.NaN;
                        prevCall = t0;
                        lock (_l)
                        {
                            var w = Win();
                            if (rc == 0) w.FlushOk++; else { w.FlushFail++; w.FlushLastRc = rc; }
                            w.FlushCall.Add((t1 - t0) / F * 1000);
                            if (!double.IsNaN(interval)) w.FlushInterval.Add(interval);
                            if (rc != 0 && _fNote != rc) { _fNote = rc; Log.Info("Diag", $"ControlTrace FLUSH {session} failed rc {rc}"); }
                            if (rc == 0 && !_fOkLogged) { _fOkLogged = true; Log.Info("Diag", $"ControlTrace FLUSH {session} ok (first success, {ms} ms period)"); }
                            Raw("flushes", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:0.000},{5:0.00}", t0, t1, ms, rc, (t1 - t0) / F * 1000, interval));
                        }
                        next += (long)(ms / 1000.0 * F);
                        if (next < t1) next = t1 + (long)(ms / 1000.0 * F);   // fell behind: re-anchor, never burst
                    }
                    Thread.Sleep(1);
                }
            }
            catch (Exception e) { Log.Warn("Diag.Flush", e); }
            finally { Marshal.FreeHGlobal(props); timeEndPeriod(1); }
        }) { IsBackground = true, Name = "diag etw flush", Priority = ThreadPriority.AboveNormal }.Start();
    }
    private static bool _etwStarted, _fOkLogged;
    private static uint _qNote, _fNote;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    // ---------- render side ----------

    public struct Sample
    {
        public long S, EdgeA, EdgeC, EdgeP; public double Delay; public PlayMode Mode; public int Flush; public bool Has;
        /// <summary>step 1: the graph's right edge as drawn (GraphNow, QPC seconds) and whether the playhead was
        /// waiting at the newest frame (buffer ran dry).</summary>
        public double EdgeNow; public bool Starved;
    }
    private static double _prevEdge = double.NaN;
    private static long _prevEdgeS;
    private static int _stallRun;

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
            EdgeStats(w, s);
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
                    Raw("frames", $"{Label(pd.Smp.Mode, pd.Smp.Flush)},{pd.Smp.S},{pd.Smp.EdgeP},{pd.Smp.EdgeC},{pd.Smp.EdgeA},{pd.Q0},{pd.Q1},{pd.Id},{v},{(pd.Smp.Delay * 1000).ToString("0.0", CultureInfo.InvariantCulture)}");
                }
            }
            else Raw("frames", $"{Label(s.Mode, s.Flush)},{s.S},{s.EdgeP},{s.EdgeC},{s.EdgeA},{q0},{q1},{id},0,{(s.Delay * 1000).ToString("0.0", CultureInfo.InvariantCulture)}");
        }
    }

    /// <summary>WPF renderer: only the sample side is measurable (no swapchain statistics).</summary>
    /// <summary>step 1 (Amine 2026-10-27): per render, how far the drawn right edge moved. A stall is a render where
    /// it did not move (&lt; 0.05 ms); runs of stalls are counted as consecutive renders. A jump is a move bigger than
    /// 2x the wall time since the previous render (and over 25 ms), or over 50 ms.</summary>
    private static void EdgeStats(Window w, Sample s)
    {
        if (!s.Has || s.EdgeNow <= 0) { _prevEdge = double.NaN; EndRun(w); return; }
        w.EdgeAge.Add((s.S / F - s.EdgeNow) * 1000);
        if (s.Starved) w.Starved++;
        if (!double.IsNaN(_prevEdge) && s.S > _prevEdgeS)
        {
            double adv = (s.EdgeNow - _prevEdge) * 1000, wall = (s.S - _prevEdgeS) / F * 1000;
            w.Renders++;
            w.Adv.Add(adv);
            w.Wall.Add(wall);
            if (adv < 0.05) { w.Stalled++; _stallRun++; }
            else
            {
                EndRun(w);
                if (adv > 50) w.Jump50++;
                if (adv > Math.Max(2 * wall, 25)) w.JumpBig++;
            }
        }
        _prevEdge = s.EdgeNow; _prevEdgeS = s.S;
    }

    private static void EndRun(Window w)
    {
        if (_stallRun > 0) { w.Runs.Add(_stallRun); if (_stallRun >= 2) w.Run2++; if (_stallRun >= 5) w.Run5++; if (_stallRun >= 10) w.Run10++; }
        _stallRun = 0;
    }

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
        public PlayMode Mode; public int FlushMs, DrainMs, Loops0, FixedMs; public long Start;
        public int Renders, Stalled, Run2, Run5, Run10, Jump50, JumpBig, Starved;
        public readonly List<double> EdgeAge = new(), Adv = new(), Wall = new(), Runs = new(), GameMs = new();
        public EtwPm? PmEtw, PmEtwLast; public EtwQuery? Q0, QLast; public int QFail;
        public int FlushOk, FlushFail; public uint FlushLastRc; public double FlushCpu0;
        public readonly List<double> FlushCall = new(), FlushInterval = new(), NewestInBatch = new();
        public int Frames, Draws, Missed, Matched, Unmatched, StatsFail;
        public double DcRate; public string Etw = "";
        public readonly List<double> DataP = new(), DataC = new(), Gap = new(), BatchRows = new(), BatchBytes = new(), ReaderDelay = new(),
            PresentBlock = new(), AgeAtSample = new(), Wait = new(), Delay = new(), DPresent = new(), E2e = new(), EdgeDataP = new(),
            EdgeWait = new(), PresentToDisp = new(), E2eDcompEst = new();
    }

    private static Window? _w;
    private static long _settleUntil;
    /// <summary>The first 2 s after a Playhead mode or flush period switch stay out of the stats;
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
        int fm = FlushMs(now), dm = DrainMs(now), xm = m == PlayMode.Fixed ? FixedMsNow(now) : 0;
        if (_w is null || _w.Mode != m || _w.FlushMs != fm || _w.DrainMs != dm || _w.FixedMs != xm || now - _w.Start > F * 10)
        {
            if (_w is not null) Flush(_w, now);
            if (_w is null || _w.Mode != m || _w.FlushMs != fm || _w.DrainMs != dm || _w.FixedMs != xm) { _settleUntil = now + (long)(F * 2); _stallRun = 0; _prevEdge = double.NaN; }
            _w = new Window { Mode = m, FlushMs = fm, DrainMs = dm, FixedMs = xm, Loops0 = DrainLoops(), Start = now, FlushCpu0 = _flushCpuMs };
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

    private static string P99(List<double> v, string unit = " ms")
    {
        if (v.Count == 0) return "n/a";
        v.Sort();
        double q(double p) => v[Math.Min(v.Count - 1, (int)Math.Floor(p * (v.Count - 1) + 0.5))];
        return string.Format(CultureInfo.InvariantCulture, "p50 {0:0.0} p95 {1:0.0} p99 {2:0.0} max {3:0.0}{4} (n {5})", q(0.5), q(0.95), q(0.99), v[^1], unit, v.Count);
    }

    private static void Flush(Window w, long now)
    {
        double sec = (now - w.Start) / F;
        if (sec < 1) return;
        double cpu = 0, pm = 0;
        try { using var p = Process.GetCurrentProcess(); cpu = (p.TotalProcessorTime - _cpu0).TotalSeconds / sec / Environment.ProcessorCount * 100; } catch { }
        try { pm = ((PresentMonCpu?.Invoke() ?? TimeSpan.Zero) - _pmCpu0).TotalSeconds / sec / Environment.ProcessorCount * 100; } catch { }
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Diag [{Label(w.Mode, w.FlushMs, w.DrainMs, w.FixedMs)}] {Renderer} {Target} {sec:0.0} s, {w.Frames} game frames, {w.Draws} draws, {w.Missed} missed, CPU app {cpu:0.0} % PresentMon {pm:0.0} % ({pm * Environment.ProcessorCount:0.00} % of one core)\n");
        sb.Append($"  e2e age V-P_edge: {P(w.E2e)} | at sample S-P_edge: {P(w.AgeAtSample)} | dcomp est: {P(w.E2eDcompEst)}\n");
        sb.Append($"  parts (matched frames): D_data A-P {P(w.EdgeDataP)} | D_wait S-A {P(w.EdgeWait)} | D_present V-S {P(w.DPresent)}\n");
        sb.Append($"  all rows: A-P {P(w.DataP)} | A-C {P(w.DataC)} | Playhead delay {P(w.Delay)}\n");
        EndRun(w);
        double sp = w.Renders > 0 ? 100.0 * w.Stalled / w.Renders : 0;
        sb.Append(FormattableString.Invariant($"  edge: age {P99(w.EdgeAge)} | advance/render {P99(w.Adv)} | render interval {P(w.Wall)}\n"));
        sb.Append(FormattableString.Invariant($"  edge stalls: {w.Stalled}/{w.Renders} renders ({sp:0.0} %), runs {w.Runs.Count} (>=2: {w.Run2}, >=5: {w.Run5}, >=10: {w.Run10}), run length {P(w.Runs, "")} | jumps >50 ms {w.Jump50}, >2x interval {w.JumpBig} | playhead starved {w.Starved} renders\n"));
        sb.Append($"  game frametime: {P99(w.GameMs)}\n");
        sb.Append($"  batches: gap {P99(w.Gap)} | rows {P(w.BatchRows, "")} | bytes {P(w.BatchBytes, "")} | newest row A-P {P(w.NewestInBatch)} | reader delay B-W {P(w.ReaderDelay)}, no stamp {_probeMissed}\n");
        double fcpu = _flushCpuMs - w.FlushCpu0;
        string rcNote = w.FlushFail > 0 ? $" (last rc {w.FlushLastRc})" : "";
        sb.Append(FormattableString.Invariant($"  flush: period {w.FlushMs} ms, ok {w.FlushOk}, failed {w.FlushFail}{rcNote} | interval {P(w.FlushInterval)} | call {P(w.FlushCall)} | flush thread CPU {fcpu:0.0} ms ({fcpu / 10 / sec:0.00} % of one core)\n"));
        if (Cfg.Patched)
        {
            int loops = DrainLoops() - w.Loops0;
            sb.Append(FormattableString.Invariant($"  drain: period {w.DrainMs} ms, PresentMon loops {loops} ({loops / sec:0.0}/s, {(loops > 0 ? sec * 1000 / loops : 0):0.0} ms each), patched build {(DrainAck() ? "seen" : "NOT seen")}\n"));
        }
        if (w.PmEtw is { } e0 && w.PmEtwLast is { } e1)
            sb.Append(FormattableString.Invariant($"  ETW (PresentMon): fill {e1.FillPct:0.0} %, buffers {e1.InUse}/{e1.Total}, events lost {e1.EventsLost} (+{e1.EventsLost - e0.EventsLost}), buffers lost {e1.BuffersLost} (+{e1.BuffersLost - e0.BuffersLost}), overflowed presents {e1.Overflowed} (+{e1.Overflowed - e0.Overflowed})\n"));
        else sb.Append("  ETW (PresentMon): no status line this window\n");
        if (w.Q0 is { } q0 && w.QLast is { } q1)
        {
            string qf = w.QFail > 0 ? $", {w.QFail} failed" : "";
            sb.Append(FormattableString.Invariant($"  ETW (query): {q1.BufferKb} KB x {q1.NumberOfBuffers} (free {q1.FreeBuffers}, min {q1.MinBuf}, max {q1.MaxBuf}), FlushTimer {q1.FlushTimer} s, mode 0x{q1.LogFileMode:X}, events lost {q1.EventsLost} (+{(long)q1.EventsLost - q0.EventsLost}), RT buffers lost {q1.RealTimeBuffersLost} (+{(long)q1.RealTimeBuffersLost - q0.RealTimeBuffersLost}), log buffers lost {q1.LogBuffersLost}, buffers written +{(long)q1.BuffersWritten - q0.BuffersWritten}{qf}\n"));
        }
        else if (w.QFail > 0) sb.Append($"  ETW (query): {w.QFail} failed\n");
        sb.Append($"  present: block {P(w.PresentBlock)} | Q1->V {P(w.PresentToDisp)} | stats matched {w.Matched}, unmatched {w.Unmatched}, failed {w.StatsFail} | composition {w.DcRate:0.#} Hz");
        if (w.Etw.Length > 0) sb.Append($"\n  ETW (csv): {w.Etw}");
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
        File.WriteAllText(Path.Combine(_dir, "meta.txt"), $"qpc_frequency={Stopwatch.Frequency}\nplayhead={Cfg.Playhead}\nfixed_ms={Cfg.FixedMs}\nab_seconds={Cfg.AbSeconds}\nreader_priority={Cfg.ReaderPriority}\npump={Cfg.Pump}\nflush={Cfg.Flush}\nflush_cycle={string.Join(" ", Cfg.FlushCycle)}\nflush_seconds={Cfg.FlushSeconds}\npresentmon={Cfg.PresentMon}\ndrain={Cfg.Drain}\ndrain_cycle={string.Join(" ", Cfg.DrainCycle)}\ndrain_seconds={Cfg.DrainSeconds}\n");
        Log.Info("Diag", "raw CSV in " + _dir);
        _writer = new Thread(() =>
        {
            var writers = new Dictionary<string, StreamWriter>();
            var heads = new Dictionary<string, string>
            {
                ["rows"] = "batch,arrival_qpc,cpustart_qpc,present_qpc,ms,flush_ms,drain_ms",
                ["batches"] = "batch,start_qpc,chunk_qpc,chunk_seq,rows,bytes,flush_ms,drain_ms",
                ["flushes"] = "call_qpc,return_qpc,period_ms,rc,call_ms,interval_ms",
                ["etw_pm"] = "qpc,fill_pct,in_use,total,events_lost,buffers_lost,overflowed_presents,flush_ms",
                ["etw_query"] = "qpc,rc,buffer_kb,min_buffers,max_buffers,log_file_mode,flush_timer_s,number_of_buffers,free_buffers,events_lost,buffers_written,log_buffers_lost,realtime_buffers_lost,flush_ms",
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
