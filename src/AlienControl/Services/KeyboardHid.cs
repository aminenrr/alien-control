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

    private bool WithDevice(Func<SafeFileHandle, int, bool> send)
    {
        lock (Gate)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_dev is null || _dev.IsInvalid || _dev.IsClosed) { _dev = Open(out _len); if (_dev is null) return false; }
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
        IReadOnlyList<(byte id, byte r, byte g, byte b)>? held = null;
        bool dark;
        lock (Gate)
        {
            _off = off;
            dark = _dark;
            if (!off && !dark) { held = _held; _held = null; }
        }
        if (off) return WithDevice((dev, len) => SetBrightness(dev, len, 0));
        if (dark) return true;                                   // the timeout wake restores it
        bool ok = WithDevice((dev, len) => SetBrightness(dev, len, 255));
        if (held is not null) ok &= SetKeys(held);
        return ok;
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

    /// <summary>Fades the keyboard's hardware brightness to 0 over ms (one-shot, ~10 steps). False = cancelled/failed.</summary>
    public bool FadeOut(int ms, System.Threading.CancellationToken token)
    {
        const int steps = 10;
        lock (Gate) if (_off) { _dark = true; return true; }     // 1.3.1: already off, nothing to fade
        for (int i = steps - 1; i >= 0; i--)
        {
            if (token.IsCancellationRequested) return false;
            int level = 255 * i / steps;
            if (!WithDevice((dev, len) => SetBrightness(dev, len, level))) return false;
            System.Threading.Thread.Sleep(ms / steps);
        }
        lock (Gate) _dark = true;
        return true;
    }

    /// <summary>Full brightness again, then the newest frame that came in while dark.</summary>
    public bool Wake()
    {
        IReadOnlyList<(byte id, byte r, byte g, byte b)>? held;
        lock (Gate)
        {
            _dark = false;
            if (_off) return true;                               // 1.3.1: user turned the keyboard off, stay off
            held = _held; _held = null;
        }
        bool ok = WithDevice((dev, len) => SetBrightness(dev, len, 255));
        if (held is not null) ok &= SetKeys(held);
        return ok;
    }

    /// <summary>Sends every key's color in one frame.</summary>
    public bool SetKeys(IReadOnlyList<(byte id, byte r, byte g, byte b)> keys)
    {
        lock (Gate) if (_dark || _off) { _held = keys; return true; }
        return WithDevice((dev, len) => SetKeys(dev, len, keys));
    }

    private static bool SetKeys(SafeFileHandle dev, int len, IReadOnlyList<(byte id, byte r, byte g, byte b)> keys)
    {
        bool ok = Send(dev, len, new byte[] { 0x94 });            // reset: start a frame
        var buf = NewBuffer(len, new byte[] { 0x8c, 0x02 });       // colorSet
        int pos = 4;
        foreach (var k in keys)
        {
            if (pos + 4 > len)
            {
                ok &= HidD_SetFeature(dev, buf, len);
                buf = NewBuffer(len, new byte[] { 0x8c, 0x02 });
                pos = 4;
            }
            buf[pos] = (byte)(k.id + 1); buf[pos + 1] = k.r; buf[pos + 2] = k.g; buf[pos + 3] = k.b;
            pos += 4;
        }
        if (pos > 4) ok &= HidD_SetFeature(dev, buf, len);
        ok &= Send(dev, len, new byte[] { 0x8c, 0x13 });           // loop
        ok &= Send(dev, len, new byte[] { 0x8b, 0x01, 0xff });     // update: commit the frame
        return ok;
    }

    public bool SetBrightness(int brightness) => WithDevice((dev, len) => SetBrightness(dev, len, brightness));

    private static bool SetBrightness(SafeFileHandle dev, int len, int brightness)
    {
        bool ok = Send(dev, len, new byte[] { 0x94 });
        var buf = NewBuffer(len, new byte[] { 0x83, 0x38, 0x9c });  // turnOnSet
        buf[4] = (byte)Math.Clamp(brightness, 0, 255);
        return ok & HidD_SetFeature(dev, buf, len);
    }

    /// <summary>
    /// 1.3.1-beta.2 F7 probe (Settings > Advanced > Watch F7): reads what the lighting collection hands back, without
    /// writing anything. Returns the feature report and the input report (report id 0xcc) as hex, or "fail" for one the
    /// device refuses. If a byte follows F7, the app can show F7's level; if nothing changes, the firmware keeps it.
    /// </summary>
    public string ReadState()
    {
        string feat = "fail", input = "fail";
        WithDevice((dev, len) =>
        {
            var b = new byte[len]; b[0] = ReportId;
            if (HidD_GetFeature(dev, b, len)) feat = BitConverter.ToString(b, 0, Math.Min(len, 34));
            var c = new byte[len]; c[0] = ReportId;
            if (HidD_GetInputReport(dev, c, len)) input = BitConverter.ToString(c, 0, Math.Min(len, 34));
            return true;   // a refused read is a result, not a dropped device
        });
        return $"feature {feat} | input {input}";
    }

    private static byte[] NewBuffer(int len, byte[] cmd)
    {
        var b = new byte[len];
        b[0] = ReportId;
        Array.Copy(cmd, 0, b, 1, cmd.Length);
        return b;
    }

    private static bool Send(SafeFileHandle dev, int len, byte[] cmd) => HidD_SetFeature(dev, NewBuffer(len, cmd), len);

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
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] b, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] b, int len);
    [DllImport("setupapi.dll")] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr w, int f);
    [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr info);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
}
