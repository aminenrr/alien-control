using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AlienControl.Services;

/// <summary>
/// Direct writer for the APIv5 (Darfon 0d62) per-key keyboard, using the same HID feature
/// reports as T-Troll's AlienFX SDK (reset 0x94, colorSet 0x8c 0x02, loop 0x8c 0x13, update 0x8b).
///
/// Why: the firmware keeps a status key (F1, F12, mutes, Win-lock) lit only if it was written in the
/// most recent color frame. alienfx-cli makes one frame per setone, so only the last status key
/// survived (tested 2026-10-15). Here all 136 keys go out in ONE frame, each with its own color.
/// Brightness is done in software (colors scaled) by AlienFx.
/// </summary>
public sealed class KeyboardHid
{
    private const ushort Vid = 0x0d62;
    private const byte ReportId = 0xcc;
    private readonly ushort _pid;

    public KeyboardHid(ushort pid = 0xcabc) => _pid = pid;

    // 1.3.0-beta.1 (review #3): one cached handle for every instance and one lock for every frame, so an effect
    // thread and a status-key change can't interleave their 0x94 resets, and no SetupDi search runs per frame.
    private static readonly object Gate = new();
    private static SafeFileHandle? _dev;
    private static int _len;
    private static long _nextOpen;

    private bool WithDevice(Func<SafeFileHandle, int, bool> send)
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_dev is null || _dev.IsInvalid || _dev.IsClosed)
                {
                    // 2.0.0-beta1.4 (services review #21): after a failed search (resume before USB is back) wait 1.5 s
                    // before the next full SetupDi enumeration, instead of one per frame (20 a second with Spectrum)
                    if (Environment.TickCount64 < _nextOpen) return false;
                    _dev = Open(out _len);
                    if (_dev is null) { _nextOpen = Environment.TickCount64 + 1500; return false; }
                }
                if (send(_dev, _len)) return true;
                _dev.Dispose(); _dev = null;   // device dropped (sleep / reconnect): reopen once
            }
            return false;
        }
    }

    // 1.3.0-beta.1 keyboard backlight timeout: while Dark, frames are held (the newest one) instead of lighting the
    // keys back up; Wake() restores full hardware brightness and sends the held frame.
    private static bool _dark;
    private static IReadOnlyList<(byte id, byte r, byte g, byte b)>? _held;
    public static bool IsDark { get { lock (Gate) return _dark; } }

    // 1.3.1: the user turned the keyboard off (Lighting > Off > Keyboard). Same mechanism as the timeout: hardware
    // brightness 0 and frames held, so the colors stay in the firmware and F7 (the firmware brightness key) can bring
    // the keys back. 1.3.0 painted the keys black instead, so F7 had nothing to light.
    private static bool _off;
    public static bool IsOff { get { lock (Gate) return _off; } }

    /// <summary>1.3.1: keyboard off (brightness 0, frames held) or back on (full brightness + the newest held frame,
    /// unless the timeout has it dark). Off is re-sent every call, since sleep / resume can reset the firmware level.</summary>
    public bool SetOff(bool off)
    {
        // 2.0.0-beta1.3 (services review #20): brightness and the held frame go out inside the lock (WithDevice and
        // SetKeys re-enter it), so a newer frame from another thread can't be overwritten by the stale held one
        lock (Gate)
        {
            _off = off;
            if (off) return WithDevice((dev, len) => SetBrightness(dev, len, 0));
            if (_dark) return true;                              // the timeout wake restores it
            var held = _held; _held = null;
            bool ok = WithDevice((dev, len) => SetBrightness(dev, len, 255));
            if (held is not null) ok &= SetKeys(held);
            return ok;
        }
    }

    /// <summary>1.3.1-beta.3: brightness 0 again while the keyboard is off or dark. beta.2 test: Keyboard off only blanked the
    /// 5 status keys and the rest stayed lit. Most likely alienfx-cli (run right after, for the rear ring / Alienhead)
    /// sets the keyboard's hardware brightness back to full when it opens the devices, and its reset drops the status keys
    /// from the frame. The timeout (no CLI run after its fade) did go dark. So every CLI run is followed by this.</summary>
    public bool Reassert()
    {
        lock (Gate) if (!_off && !_dark) return true;
        return WithDevice((dev, len) => SetBrightness(dev, len, 0));
    }

    // 2.0.0-beta1.3 (services review #3): bumped by Wake. A fade checks it (and its token) under the lock before
    // each step and before it sets _dark, so a key press during the last step can't leave the keys lit but "dark"
    // (every later frame held). Replaces the 30 ms wait in MainWindow.KbdWake.
    private static int _fadeGen;

    /// <summary>Fades the keyboard's hardware brightness to 0 over ms (one-shot, ~10 steps). False = cancelled/failed.</summary>
    public bool FadeOut(int ms, System.Threading.CancellationToken token)
    {
        const int steps = 10;
        int gen;
        lock (Gate)
        {
            if (_off) { _dark = true; return true; }             // 1.3.1: already off, nothing to fade
            gen = _fadeGen;
        }
        for (int i = steps - 1; i >= 0; i--)
        {
            int level = 255 * i / steps;
            lock (Gate)
            {
                if (token.IsCancellationRequested || gen != _fadeGen) return false;
                if (!WithDevice((dev, len) => SetBrightness(dev, len, level))) return false;
            }
            System.Threading.Thread.Sleep(ms / steps);
        }
        lock (Gate)
        {
            if (token.IsCancellationRequested || gen != _fadeGen) return false;
            _dark = true;
        }
        return true;
    }

    /// <summary>Full brightness again, then the newest frame that came in while dark.</summary>
    public bool Wake()
    {
        lock (Gate)
        {
            _fadeGen++;                                          // a fade still running stops at its next check
            _dark = false;
            if (_off) return true;                               // 1.3.1: user turned the keyboard off, stay off
            var held = _held; _held = null;
            // 2.0.0-beta1.3 (services review #20): sent inside the lock, see SetOff
            bool ok = WithDevice((dev, len) => SetBrightness(dev, len, 255));
            if (held is not null) ok &= SetKeys(held);
            return ok;
        }
    }

    /// <summary>Sends every key's color in one frame.</summary>
    /// <summary>Sends every key's color in one frame. The list may be a buffer the caller reuses (the animation):
    /// a frame held while dark or off is copied.</summary>
    public bool SetKeys(IReadOnlyList<(byte id, byte r, byte g, byte b)> keys)
    {
        lock (Gate) if (_dark || _off) { _held = new List<(byte, byte, byte, byte)>(keys); return true; }
        return WithDevice((dev, len) => SetKeys(dev, len, keys));
    }

    // 2.0.0-beta1.4 (services review #22): the commands are static and the report buffer is reused (always under Gate:
    // WithDevice holds it), so a frame no longer allocates a new feature buffer per HID report.
    private static readonly byte[] CmdReset = { 0x94 }, CmdColorSet = { 0x8c, 0x02 }, CmdLoop = { 0x8c, 0x13 }, CmdUpdate = { 0x8b, 0x01, 0xff };
    private static byte[]? _report;

    private static byte[] Report(int len, byte[] cmd)
    {
        if (_report is null || _report.Length != len) _report = new byte[len];
        Array.Clear(_report);
        _report[0] = ReportId;
        Array.Copy(cmd, 0, _report, 1, cmd.Length);
        return _report;
    }

    private static bool SetKeys(SafeFileHandle dev, int len, IReadOnlyList<(byte id, byte r, byte g, byte b)> keys)
    {
        bool ok = Send(dev, len, CmdReset);                         // reset: start a frame
        var buf = Report(len, CmdColorSet);                          // colorSet
        int pos = 4;
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (pos + 4 > len)
            {
                ok &= HidD_SetFeature(dev, buf, len);
                buf = Report(len, CmdColorSet);
                pos = 4;
            }
            buf[pos] = (byte)(k.id + 1); buf[pos + 1] = k.r; buf[pos + 2] = k.g; buf[pos + 3] = k.b;
            pos += 4;
        }
        if (pos > 4) ok &= HidD_SetFeature(dev, buf, len);
        ok &= Send(dev, len, CmdLoop);                              // loop
        ok &= Send(dev, len, CmdUpdate);                            // update: commit the frame
        return ok;
    }

    private static readonly byte[] CmdTurnOn = { 0x83, 0x38, 0x9c };

    public bool SetBrightness(int brightness) => WithDevice((dev, len) => SetBrightness(dev, len, brightness));

    private static bool SetBrightness(SafeFileHandle dev, int len, int brightness)
    {
        bool ok = Send(dev, len, CmdReset);
        var buf = Report(len, CmdTurnOn);                           // turnOnSet
        buf[4] = (byte)Math.Clamp(brightness, 0, 255);
        return ok & HidD_SetFeature(dev, buf, len);
    }

    private static bool Send(SafeFileHandle dev, int len, byte[] cmd) => HidD_SetFeature(dev, Report(len, cmd), len);

    // ---------- device lookup ----------

    private SafeFileHandle? Open(out int featureLen)
    {
        featureLen = 0;
        HidD_GetHidGuid(out var guid);
        var set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
        if (set == new IntPtr(-1)) return null;
        try
        {
            var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, size, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h.IsInvalid) { h.Dispose(); continue; }
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (HidD_GetAttributes(h, ref attr) && attr.VendorID == Vid && attr.ProductID == _pid
                        && HidD_GetPreparsedData(h, out var prep))
                    {
                        HidP_GetCaps(prep, out var caps);
                        HidD_FreePreparsedData(prep);
                        // Same test as the SDK: the lighting collection has usage 0xcc and no output report.
                        if (caps.Usage == 0xcc && caps.OutputReportByteLength == 0 && caps.FeatureReportByteLength > 0)
                        {
                            featureLen = caps.FeatureReportByteLength;
                            return h;
                        }
                    }
                    h.Dispose();
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr p, out HIDP_CAPS c);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("setupapi.dll")] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
}
