using System;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// 1.3.0-beta.1: the NVML entry points, declared once (review #15). nvml.dll is only loaded from System32, where the
/// NVIDIA driver installs it (review #6): the default search order checks the app folder first, so a planted
/// nvml.dll next to the exe would have run as admin.
/// </summary>
internal static class Nvml
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ClockOffset { public uint Version; public int Type; public int PState; public int Cur; public int Min; public int Max; }   // nvmlClockOffset_v1

    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlInit_v2();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlShutdown();
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetClockOffsets(IntPtr device, ref ClockOffset o);
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceSetClockOffsets(IntPtr device, ref ClockOffset o);
    // beta1.3-ov: overlay levels 3 and 4 show the GPU core and memory clock (NVML_CLOCK_GRAPHICS 0, NVML_CLOCK_MEM 2)
    [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] public static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
}
