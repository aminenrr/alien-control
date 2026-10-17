using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace AlienControl.Services;

/// <summary>v0.9.11: notification sounds (embedded WAVs, no extra packages).
/// 1.3.0-beta.2: two packs with the same 7 slots, Assets/Sounds/{Soft,Classic}/snd-*.wav.
/// Soft (default): warm sine mallet + soft pad, 48 kHz mono, ~1.3 s (from Amine's beta.2 handoff).
/// Classic: the v1.2 bell chimes (dev/chimes.py), unchanged.
/// 1.3.0 (Amine: the Soft chimes crackled, the Classic ones a bit): PlaySound fed the audio device in small chunks
/// from its own worker, so a busy moment (the CLI / ACPI calls of a mode switch start right after the chime) starved
/// it. Now the whole sound goes to waveOut as ONE prepared buffer, so nothing in the app has to refill it while it
/// plays. The device is opened per sound and closed once it is done: nothing stays open or running between sounds.</summary>
public static class Sounds
{
    public enum Kind { Quiet, Balanced, Performance, Unplug, Plug, GModeOn, GModeOff }
    public enum PackKind { Soft, Classic }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);
    private const int WAVE_MAPPER = -1, WHDR_DONE = 1;

    /// <summary>One decoded sound: PCM in unmanaged memory (read by the device while it plays).</summary>
    private sealed record Clip(WAVEFORMATEX Fmt, IntPtr Pcm, int Bytes, int Ms);

    // Only the active pack is cached (~880 KB if all 7 Soft sounds were played); switching packs stops and frees them.
    private static readonly Dictionary<Kind, Clip> _cache = new();
    private static PackKind _pack = PackKind.Soft;
    private static IntPtr _dev, _hdr;   // the sound playing now (UI thread only)
    private static int _gen;

    public static PackKind Pack
    {
        get => _pack;
        set
        {
            if (value == _pack) return;
            _pack = value;
            Stop();   // a buffer must never be freed while the device reads it
            foreach (var c in _cache.Values) Marshal.FreeHGlobal(c.Pcm);
            _cache.Clear();
        }
    }

    private static string FileName(Kind k) => k switch
    {
        Kind.Quiet => "snd-quiet.wav", Kind.Balanced => "snd-balanced.wav", Kind.Performance => "snd-performance.wav",
        Kind.Unplug => "snd-unplug.wav", Kind.Plug => "snd-plug.wav",
        Kind.GModeOn => "snd-gmode-on.wav", _ => "snd-gmode-off.wav",
    };

    public static void Play(Kind k)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.BeginInvoke(new Action(() => Play(k))); return; }
        try
        {
            if (!_cache.TryGetValue(k, out var clip))
            {
                var res = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Sounds/{_pack}/{FileName(k)}"));
                if (res is null) return;
                using var ms = new MemoryStream();
                res.Stream.CopyTo(ms);
                clip = Parse(ms.ToArray());
                if (clip is null) { Log.Warn("Sounds: unsupported WAV " + FileName(k), null); return; }
                _cache[k] = clip;
            }
            Stop();   // a new sound replaces the old one at once
            var fmt = clip.Fmt;
            if (waveOutOpen(out var dev, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;
            int size = Marshal.SizeOf<WAVEHDR>();
            var hdr = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(new WAVEHDR { lpData = clip.Pcm, dwBufferLength = (uint)clip.Bytes }, hdr, false);
            if (waveOutPrepareHeader(dev, hdr, size) != 0 || waveOutWrite(dev, hdr, size) != 0)
            {
                waveOutReset(dev); waveOutUnprepareHeader(dev, hdr, size); waveOutClose(dev); Marshal.FreeHGlobal(hdr);
                return;
            }
            _dev = dev; _hdr = hdr;
            _ = CloseWhenDoneAsync(++_gen, clip.Ms);
        }
        catch (Exception e) { Log.Warn("Sounds.Play", e); }
    }

    /// <summary>One-shot: closes the device shortly after the sound ends (checks the done flag, retries a few times).</summary>
    private static async Task CloseWhenDoneAsync(int gen, int ms)
    {
        await Task.Delay(ms + 150);
        for (int i = 0; i < 10 && gen == _gen && _hdr != IntPtr.Zero; i++)
        {
            if ((Marshal.PtrToStructure<WAVEHDR>(_hdr).dwFlags & WHDR_DONE) != 0) break;
            await Task.Delay(100);
        }
        if (gen == _gen) Stop();
    }

    private static void Stop()
    {
        if (_dev == IntPtr.Zero) return;
        try
        {
            waveOutReset(_dev);
            waveOutUnprepareHeader(_dev, _hdr, Marshal.SizeOf<WAVEHDR>());
            waveOutClose(_dev);
        }
        catch (Exception e) { Log.Warn("Sounds.Stop", e); }
        Marshal.FreeHGlobal(_hdr);
        _dev = _hdr = IntPtr.Zero;
        _gen++;
    }

    /// <summary>Defensive RIFF/WAVE reader: 16-bit PCM, 1-2 channels, 8-96 kHz, at most 4 MB of data.</summary>
    internal static (ushort ch, uint rate, int dataOffset, int dataBytes)? ReadWav(byte[] b)
    {
        if (b.Length < 44 || BitConverter.ToUInt32(b, 0) != 0x46464952 || BitConverter.ToUInt32(b, 8) != 0x45564157) return null;   // RIFF, WAVE
        ushort ch = 0, bits = 0, tag = 0; uint rate = 0; int off = 12;
        while (off + 8 <= b.Length)
        {
            uint id = BitConverter.ToUInt32(b, off); int len = BitConverter.ToInt32(b, off + 4);
            if (len < 0 || off + 8 + (long)len > b.Length) return null;
            if (id == 0x20746D66 && len >= 16)   // "fmt "
            {
                tag = BitConverter.ToUInt16(b, off + 8); ch = BitConverter.ToUInt16(b, off + 10);
                rate = BitConverter.ToUInt32(b, off + 12); bits = BitConverter.ToUInt16(b, off + 22);
            }
            else if (id == 0x61746164)   // "data"
            {
                if (tag != 1 || bits != 16 || ch is < 1 or > 2 || rate is < 8000 or > 96000 || len > 4 << 20) return null;
                return (ch, rate, off + 8, len - len % (ch * 2));
            }
            off += 8 + len + (len & 1);
        }
        return null;
    }

    private static Clip? Parse(byte[] b)
    {
        if (ReadWav(b) is not { } w || w.dataBytes <= 0) return null;
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = w.ch, nSamplesPerSec = w.rate, wBitsPerSample = 16,
            nBlockAlign = (ushort)(w.ch * 2), nAvgBytesPerSec = w.rate * w.ch * 2u,
        };
        var p = Marshal.AllocHGlobal(w.dataBytes);
        Marshal.Copy(b, w.dataOffset, p, w.dataBytes);
        return new Clip(fmt, p, w.dataBytes, (int)(w.dataBytes * 1000L / fmt.nAvgBytesPerSec));
    }
}
