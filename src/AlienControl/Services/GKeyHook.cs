using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AlienControl.Services;

/// <summary>
/// Global low-level keyboard hook for the G-key (F1 G-Mode key on the m15 R7).
/// AWCC used to catch this key; without it nothing answers, so the app does.
/// Always enabled (v0.8: no off switch). Install on the UI thread (it needs a message loop, which WPF provides).
/// </summary>
public sealed class GKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly HookProc _proc; // keep a reference so the GC doesn't collect it
    private IntPtr _hook;
    private DateTime _lastFire = DateTime.MinValue;

    public int Vk { get; set; }
    public int Scan { get; set; }   // 0 = any

    /// <summary>Raised when the G-key goes down (debounced).</summary>
    public event Action? Pressed;

    /// <summary>When set, the next key down is reported here (vk, scan) instead of being matched.</summary>
    public Action<int, int>? LearnNext { get; set; }

    public GKeyHook(int vk, int scan)
    {
        Vk = vk; Scan = scan;
        _proc = Callback;
    }

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        using var mod = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod?.ModuleName), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var learn = LearnNext;
            if (learn is not null)
            {
                LearnNext = null;
                learn((int)k.vkCode, (int)k.scanCode);
            }
            else if (k.vkCode == Vk && (Scan == 0 || k.scanCode == Scan))
            {
                // Key repeat fires many downs; one toggle per press.
                var now = DateTime.UtcNow;
                if ((now - _lastFire).TotalMilliseconds > 600)
                {
                    _lastFire = now;
                    Pressed?.Invoke();
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
}
