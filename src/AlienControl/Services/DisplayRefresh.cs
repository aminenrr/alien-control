using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// Refresh rate of the built-in display through EnumDisplaySettings / ChangeDisplaySettingsEx (2.0.0-beta1.3: the
/// internal panel, was the primary display).
/// The resolution and color depth are kept; only dmDisplayFrequency changes. Read on demand, never polled.
/// </summary>
public static class DisplayRefresh
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_DISPLAYFREQUENCY = 0x400000;
    private const uint CDS_UPDATEREGISTRY = 0x01;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

    /// <summary>GDI name of the primary display (\\.\DISPLAY1), which is the laptop panel unless an external monitor is primary.</summary>
    private static string? PrimaryDevice()
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if ((dd.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0 && (dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                return dd.DeviceName;
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return null;
    }

    /// <summary>2.0.0-beta1.3 (services review #6): the built-in panel (QueryDisplayConfig, internal output technology,
    /// the same path GpuMode uses), so an external monitor set as primary is never switched. The primary display only
    /// when no internal panel is active (lid closed) or it can't be read.</summary>
    private static string? TargetDevice() => GpuMode.InternalPanelGdiName() ?? PrimaryDevice();

    private static DEVMODE NewMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>(), dmDeviceName = "", dmFormName = "" };

    public sealed record State(int CurrentHz, int Width, int Height, IReadOnlyList<int> AvailableHz);

    /// <summary>Current rate plus every rate offered at the current resolution and color depth.</summary>
    public static State? Read()
    {
        try
        {
            var dev = TargetDevice();
            var cur = NewMode();
            if (!EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref cur)) return null;
            var rates = new SortedSet<int>();
            var m = NewMode();
            for (int i = 0; EnumDisplaySettings(dev, i, ref m); i++)
            {
                if (m.dmPelsWidth == cur.dmPelsWidth && m.dmPelsHeight == cur.dmPelsHeight && m.dmBitsPerPel == cur.dmBitsPerPel && m.dmDisplayFrequency > 1)
                    rates.Add(m.dmDisplayFrequency);
                m = NewMode();
            }
            return new State(cur.dmDisplayFrequency, cur.dmPelsWidth, cur.dmPelsHeight, rates.ToList());
        }
        catch { return null; }
    }

    /// <summary>Closest offered rate within ±5 Hz of the target (239/240, 59/60), or null.</summary>
    public static int? Match(State s, int targetHz) =>
        s.AvailableHz.Where(h => Math.Abs(h - targetHz) <= 5).OrderBy(h => Math.Abs(h - targetHz)).Cast<int?>().FirstOrDefault();

    public static bool IsActive(State s, int targetHz) => Math.Abs(s.CurrentHz - targetHz) <= 5;

    /// <summary>Switches the built-in display to targetHz. Returns null on success or an error message.</summary>
    public static string? Set(int targetHz)
    {
        try
        {
            var s = Read();
            if (s is null) return "Couldn't read the display mode.";
            if (Match(s, targetHz) is not { } hz) return $"{targetHz} Hz isn't offered at {s.Width}x{s.Height}.";
            if (s.CurrentHz == hz) return null;
            var dev = TargetDevice();
            var mode = NewMode();
            if (!EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref mode)) return "Couldn't read the display mode.";
            mode.dmDisplayFrequency = hz;
            mode.dmFields = DM_DISPLAYFREQUENCY;
            int r = ChangeDisplaySettingsEx(dev, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            return r == DISP_CHANGE_SUCCESSFUL ? null : $"Windows refused {hz} Hz (code {r}).";
        }
        catch (Exception ex) { return "Refresh rate error: " + ex.Message; }
    }
}
