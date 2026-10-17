using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace AlienControl.Services;

/// <summary>v0.9.11: notification sounds (embedded WAVs, played async from memory, no extra packages).
/// 1.3.0-beta.2: two packs with the same 7 slots, Assets/Sounds/{Soft,Classic}/snd-*.wav.
/// Soft (default): warm sine mallet + soft pad, 48 kHz mono, ~1.3 s (from Amine's beta.2 handoff).
/// Classic: the v1.2 bell chimes (dev/chimes.py), unchanged.</summary>
public static class Sounds
{
    public enum Kind { Quiet, Balanced, Performance, Unplug, Plug, GModeOn, GModeOff }
    public enum PackKind { Soft, Classic }

    [DllImport("winmm.dll")] private static extern bool PlaySound(IntPtr data, IntPtr mod, uint flags);
    private const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
    // Unmanaged copies (async playback reads them after the call returns). Only the active pack is cached
    // (~880 KB if all 7 Soft sounds were played); switching packs stops playback and frees them.
    private static readonly Dictionary<Kind, IntPtr> _buf = new();
    private static PackKind _pack = PackKind.Soft;

    public static PackKind Pack
    {
        get => _pack;
        set
        {
            if (value == _pack) return;
            _pack = value;
            FreeAll();
        }
    }

    private static void FreeAll()
    {
        try { PlaySound(IntPtr.Zero, IntPtr.Zero, 0); }   // stop: a buffer must not be freed while it is playing
        catch (Exception e) { Log.Warn("Sounds.Stop", e); }
        foreach (var p in _buf.Values) Marshal.FreeHGlobal(p);
        _buf.Clear();
    }

    private static string File(Kind k) => k switch
    {
        Kind.Quiet => "snd-quiet.wav", Kind.Balanced => "snd-balanced.wav", Kind.Performance => "snd-performance.wav",
        Kind.Unplug => "snd-unplug.wav", Kind.Plug => "snd-plug.wav",
        Kind.GModeOn => "snd-gmode-on.wav", _ => "snd-gmode-off.wav",
    };

    public static void Play(Kind k)
    {
        try
        {
            if (!_buf.TryGetValue(k, out var p))
            {
                var res = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/Sounds/{_pack}/{File(k)}"));
                if (res is null) return;
                using var ms = new MemoryStream();
                res.Stream.CopyTo(ms);
                var bytes = ms.ToArray();
                p = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                _buf[k] = p;
            }
            PlaySound(p, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception e) { Log.Warn("Sounds.Play", e); }
    }
}
