using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AlienControl.Services;

/// <summary>
/// v0.9.10: the m15 R7 macro keys (F2..F6 in media mode = Macro A..E). They never reach the keyboard hook;
/// the keyboard sends them on its consumer HID collection (0D62:CABC, page 0x0C usage 0x01) as report
/// 02-51-00 (F2) .. 02-55-00 (F6), release 02-00-00. Read with Raw Input on a message-only window:
/// no polling, no admin, and nothing is swallowed (Windows ignores these usages anyway).
/// </summary>
public sealed class MacroKeys : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct RID { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] private struct INFO { public uint cb, type, vid, pid, ver; public ushort page, usage; public uint pad1, pad2; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RID[] r, uint n, uint cb);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, ref INFO d, ref uint cb);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr h, uint cmd, byte[]? d, ref uint cb, uint hcb);
    [StructLayout(LayoutKind.Sequential)] private struct RIDL { public IntPtr Device; public uint Type; }
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RIDL[]? list, ref uint n, uint cb);

    private const int WM_INPUT = 0x00FF, WM_HOTKEY = 0x0312;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
    private const int LightsHotkeyId = 0xA1F;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000, VK_F = 0x46;

    /// <summary>1.3.1: Ctrl + Alt + F (all lights: full, off, 50%). A system hotkey on this message window: no hook,
    /// no polling, held keys don't repeat. Raised on the input thread.</summary>
    public event Action? LightsHotkey;
    private const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1, RID_INPUT = 0x10000003, RIDI_DEVICEINFO = 0x2000000b;
    private const ushort Vid = 0x0D62;
    private const ushort VidElc = 0x187C;   // Alienware AW-ELC lighting controller (chassis lights)

    private HwndSource? _src;
    private IntPtr _dev;          // cached handle of the Alienware keyboard collection
    private byte _down;           // code currently held (repeat guard)

    /// <summary>Macro key pressed: 0 = F2 (Macro A), 1 = F3, ... 4 = F6.</summary>
    public event Action<int>? Pressed;

    /// <summary>1.3.0-beta.1: set while the keyboard backlight is timed out; mouse / touchpad raw input wakes it.
    /// Mouse raw input is only registered during that time, so normal use costs nothing.</summary>
    public Action? AnyMouse { get; private set; }
    private const uint RID_HEADER = 0x10000005;

    public void WatchMouse(Action? onInput)
    {
        if (_src is null) return;
        AnyMouse = onInput;
        var r = new[] { new RID { Page = 0x01, Usage = 0x02, Flags = onInput is null ? RIDEV_REMOVE : RIDEV_INPUTSINK, Target = onInput is null ? IntPtr.Zero : _src.Handle } };
        if (!RegisterRawInputDevices(r, 1, (uint)Marshal.SizeOf<RID>())) Log.Info("MacroKeys", "mouse raw input register failed");
    }

    public bool Install()
    {
        if (_src is not null) return true;
        var p = new HwndSourceParameters("AlienControlMacroKeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };   // HWND_MESSAGE
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
        if (!RegisterHotKey(_src.Handle, LightsHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F))
            Log.Info("MacroKeys", $"Ctrl+Alt+F not registered (error {Marshal.GetLastWin32Error()}, another app may own it)");
        var r = new[] { new RID { Page = 0x0C, Usage = 0x01, Flags = RIDEV_INPUTSINK, Target = _src.Handle } };
        return RegisterRawInputDevices(r, 1, (uint)Marshal.SizeOf<RID>());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == LightsHotkeyId) { LightsHotkey?.Invoke(); handled = true; }
            return IntPtr.Zero;
        }
        if (msg != WM_INPUT) return IntPtr.Zero;
        try
        {
            if (AnyMouse is { } wake)
            {
                uint hc = (uint)(8 + 2 * IntPtr.Size), size = hc;
                var hdr = new byte[hc];
                if (GetRawInputData(lParam, RID_HEADER, hdr, ref size, hc) == hc && BitConverter.ToUInt32(hdr, 0) == 0) { wake(); return IntPtr.Zero; }   // RIM_TYPEMOUSE
            }
            uint hcb = (uint)(8 + 2 * IntPtr.Size), cb = 0;
            GetRawInputData(lParam, RID_INPUT, null, ref cb, hcb);
            if (cb == 0 || cb > 256) return IntPtr.Zero;
            var b = new byte[cb];
            if (GetRawInputData(lParam, RID_INPUT, b, ref cb, hcb) != cb) return IntPtr.Zero;
            var dev = IntPtr.Size == 8 ? new IntPtr(BitConverter.ToInt64(b, 8)) : new IntPtr(BitConverter.ToInt32(b, 8));
            if (ProbeAll && _extra.Count > 0 && ProbeOther(dev, b, (int)hcb, (int)cb)) return IntPtr.Zero;
            if (!IsAlienware(dev)) return IntPtr.Zero;
            int off = (int)hcb + 8;                     // RAWHID: dwSizeHid, dwCount, then the report
            if (cb < off + 2) return IntPtr.Zero;
            if (b[off] != 0x02) { Probe(b, off, (int)cb); return IntPtr.Zero; }
            byte code = b[off + 1];
            if (code == 0) { _down = 0; return IntPtr.Zero; }
            if (code == _down) return IntPtr.Zero;      // held
            _down = code;
            if (code is >= 0x51 and <= 0x55) Pressed?.Invoke(code - 0x51);
            else Probe(b, off, (int)cb);
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("MacroKeys.cs:63", __e); }
        return IntPtr.Zero;
    }

    // 1.3.1 probe: any other report from the Alienware keyboard collection (for example the F7 brightness key, if the
    // firmware reports it at all) is written to crash.log once per distinct report, at most 32. Event-driven: no cost
    // until such a key is pressed. Only the report bytes are logged, never normal typing (that is a different collection).
    private readonly System.Collections.Generic.HashSet<string> _probed = new();

    /// <summary>1.3.1-beta.2 F7 probe: while true (20 s, from Settings), every Alienware report is logged.</summary>
    public static volatile bool ProbeAll;

    private void Probe(byte[] b, int off, int cb)
    {
        int n = Math.Min(4, cb - off);
        if (ProbeAll && n > 0) { Log.Info("F7 probe", $"Alienware report {BitConverter.ToString(b, off, n)}"); return; }
        if (n <= 0 || _probed.Count >= 32) return;
        var sig = BitConverter.ToString(b, off, n);
        if (_probed.Add(sig)) Log.Info("MacroKeys", $"unknown Alienware report {sig}");
    }

    // ---------- 1.3.1-beta.4: F7 probe on every Alienware HID collection ----------
    // beta.2's test only watched the consumer collection (0x0C / 0x01) that F2..F6 use, and the low-level hook; F7 showed
    // in neither. Amine: F7 is a hotkey of the Fn-lock row like F2..F5, so it may go out on another collection of the
    // same keyboard (a vendor page) or on the AW-ELC. During the 20 s test every HID collection of 0D62 and 187C is
    // registered too (input sink, nothing swallowed) and each report is logged with its collection. Removed afterwards.
    private readonly System.Collections.Generic.List<(ushort page, ushort usage)> _extra = new();
    private readonly System.Collections.Generic.Dictionary<IntPtr, (uint vid, uint pid, ushort page, ushort usage)> _info = new();

    private (uint vid, uint pid, ushort page, ushort usage) Info(IntPtr dev)
    {
        if (_info.TryGetValue(dev, out var v)) return v;
        var i = new INFO { cb = (uint)Marshal.SizeOf<INFO>() };
        uint cb = i.cb;
        v = GetRawInputDeviceInfo(dev, RIDI_DEVICEINFO, ref i, ref cb) == uint.MaxValue || i.type != 2 ? (0, 0, 0, 0) : (i.vid, i.pid, i.page, i.usage);
        return _info[dev] = v;
    }

    /// <summary>Probe on: registers every other HID collection of the Alienware keyboard and AW-ELC. Off: removes them.</summary>
    public void ProbeAllCollections(bool on)
    {
        if (_src is null) return;
        try
        {
            if (!on)
            {
                if (_extra.Count == 0) return;
                var rm = _extra.ConvertAll(x => new RID { Page = x.page, Usage = x.usage, Flags = RIDEV_REMOVE });
                RegisterRawInputDevices(rm.ToArray(), (uint)rm.Count, (uint)Marshal.SizeOf<RID>());
                _extra.Clear();
                return;
            }
            uint n = 0, sz = (uint)Marshal.SizeOf<RIDL>();
            GetRawInputDeviceList(null, ref n, sz);
            var list = new RIDL[n];
            if (n == 0 || GetRawInputDeviceList(list, ref n, sz) == uint.MaxValue) return;
            var seen = new System.Collections.Generic.List<string>();
            foreach (var d in list)
            {
                if (d.Type != 2) continue;                                   // HID only (keyboard typing is the hook's)
                var v = Info(d.Device);
                if (v.vid != Vid && v.vid != VidElc) continue;
                seen.Add($"{v.vid:X4}:{v.pid:X4} {v.page:X2}/{v.usage:X2}");
                if ((v.page, v.usage) is (0x0C, 0x01) or (0x01, 0x02) or (0x01, 0x06) or (0x01, 0x01)) continue;   // already ours / typing / mouse
                if (!_extra.Contains((v.page, v.usage))) _extra.Add((v.page, v.usage));
            }
            var add = _extra.ConvertAll(x => new RID { Page = x.page, Usage = x.usage, Flags = RIDEV_INPUTSINK, Target = _src.Handle });
            bool ok = add.Count == 0 || RegisterRawInputDevices(add.ToArray(), (uint)add.Count, (uint)Marshal.SizeOf<RID>());
            Log.Info("F7 probe", $"collections: {string.Join(", ", seen)}; extra watched: {(add.Count == 0 ? "none" : string.Join(", ", _extra.ConvertAll(x => $"{x.page:X2}/{x.usage:X2}")))} {(ok ? "ok" : "register FAILED")}");
        }
        catch (Exception e) { Log.Warn("MacroKeys.ProbeAllCollections", e); }
    }

    /// <summary>True when the report came from one of the extra probe collections (logged, nothing else done).</summary>
    private bool ProbeOther(IntPtr dev, byte[] b, int hcb, int cb)
    {
        var v = Info(dev);
        if (v.vid != Vid && v.vid != VidElc) return false;
        if ((v.page, v.usage) == (0x0C, 0x01)) return false;                  // the macro collection: normal path
        int off = hcb + 8, n = Math.Min(8, cb - off);
        if (n > 0) Log.Info("F7 probe", $"report {v.vid:X4}:{v.pid:X4} {v.page:X2}/{v.usage:X2}: {BitConverter.ToString(b, off, n)}");
        return true;
    }

    private bool IsAlienware(IntPtr dev)
    {
        if (dev == IntPtr.Zero) return false;
        if (dev == _dev) return true;
        var i = new INFO { cb = (uint)Marshal.SizeOf<INFO>() };
        uint cb = i.cb;
        if (GetRawInputDeviceInfo(dev, RIDI_DEVICEINFO, ref i, ref cb) == uint.MaxValue || i.vid != Vid) return false;
        _dev = dev;
        return true;
    }

    public void Dispose()
    {
        if (_src is null) return;
        try { RegisterRawInputDevices(new[] { new RID { Page = 0x0C, Usage = 0x01, Flags = RIDEV_REMOVE } }, 1, (uint)Marshal.SizeOf<RID>()); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("MacroKeys.cs:81", __e); }
        try { UnregisterHotKey(_src.Handle, LightsHotkeyId); } catch (System.Exception __e) { AlienControl.Services.Log.Warn("MacroKeys.Hotkey", __e); }
        _src.Dispose(); _src = null;
    }
}
