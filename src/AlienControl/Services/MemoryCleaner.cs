using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AlienControl.Services;

/// <summary>
/// RAM clean using documented/native Windows memory-list calls (own implementation; idea credited to
/// WinMemoryCleaner by Igor Mundstein). Needs admin, which the app already has.
///   Quick: empty working sets, flush modified list, purge low-priority standby, combine pages.
///   Deep:  Quick + purge the whole standby list + trim the system file cache.
/// </summary>
public static class MemoryCleaner
{
    private const int SystemMemoryListInformation = 80;
    private const int SystemCombinePhysicalMemoryInformation = 130;
    private const int MemoryEmptyWorkingSets = 2, MemoryFlushModifiedList = 3,
                      MemoryPurgeStandbyList = 4, MemoryPurgeLowPriorityStandbyList = 5;

    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int infoClass, ref MEMORY_COMBINE_INFORMATION_EX info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetSystemFileCacheSize(UIntPtr min, UIntPtr max, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_COMBINE_INFORMATION_EX { public IntPtr Handle; public UIntPtr PagesCombined; public ulong Flags; }

    // ---- privileges ----
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attributes; }

    private static bool _privileged;

    private static void EnablePrivileges()
    {
        if (_privileged) return;
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token)) return;
        try
        {
            foreach (var name in new[] { "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege" })
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) continue;
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            _privileged = true;
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Free + zeroed page bytes (truly free RAM; standby cache counts as "available", not free).</summary>
    private static double FreeBytes()
    {
        try
        {
            using var c = new PerformanceCounter("Memory", "Free & Zero Page List Bytes", true);
            return c.NextValue();
        }
        catch { return 0; }
    }

    private static int MemList(int cmd) => NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));

    /// <summary>Runs the clean off the UI thread. Returns GB moved to free memory, and any error text.</summary>
    public static Task<(double FreedGb, string? Error)> CleanAsync(bool deep) => Task.Run(() =>
    {
        try
        {
            EnablePrivileges();
            double before = FreeBytes();
            int failed = 0;

            if (MemList(MemoryEmptyWorkingSets) != 0) failed++;
            if (deep)
            {
                // Trim the system file cache (documented: both sizes -1 flushes it).
                SetSystemFileCacheSize(new UIntPtr(ulong.MaxValue), new UIntPtr(ulong.MaxValue), 0);
            }
            if (MemList(MemoryFlushModifiedList) != 0) failed++;
            if (MemList(deep ? MemoryPurgeStandbyList : MemoryPurgeLowPriorityStandbyList) != 0) failed++;
            try
            {
                var combine = new MEMORY_COMBINE_INFORMATION_EX();
                NtSetSystemInformation(SystemCombinePhysicalMemoryInformation, ref combine, Marshal.SizeOf<MEMORY_COMBINE_INFORMATION_EX>());
            }
            catch (System.Exception __e) { AlienControl.Services.Log.Warn("MemoryCleaner.cs:89", __e); /* Windows 8+ only; ignore */ }

            double after = FreeBytes();
            double freed = Math.Max(0, (after - before) / (1024d * 1024 * 1024));
            return (freed, failed == 3 ? "RAM clean was refused by Windows (admin rights missing?)" : null);
        }
        catch (Exception ex) { return (0d, "RAM clean failed: " + ex.Message); }
    });
}
