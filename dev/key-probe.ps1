# Key probe (v0.9.10 prep): prints every key the low-level keyboard hook sees.
# Run:  powershell -ExecutionPolicy Bypass -File dev\key-probe.ps1
# Press F1 (G-key, should show VK 0x80) then F2 a few times in Fn-lock (media) mode. Close the window to stop.
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Windows.Forms;
public static class KeyProbe {
  delegate IntPtr Proc(int n, IntPtr w, IntPtr l);
  [StructLayout(LayoutKind.Sequential)] struct K { public uint vk, scan, flags, time; public IntPtr extra; }
  [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc p, IntPtr m, uint t);
  [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
  [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string n);
  static Proc _p; static IntPtr _h;
  public static void Run() {
    _p = (n, w, l) => {
      if (n >= 0 && ((int)w == 0x100 || (int)w == 0x104)) {
        var k = (K)Marshal.PtrToStructure(l, typeof(K));
        Console.WriteLine(string.Format("VK 0x{0:X2} ({1})  scan 0x{2:X2}  flags 0x{3:X2}", k.vk, (Keys)k.vk, k.scan, k.flags));
      }
      return CallNextHookEx(_h, n, w, l);
    };
    _h = SetWindowsHookEx(13, _p, GetModuleHandle(null), 0);
    Console.WriteLine("Listening. Press F1, then F2 (Fn-lock / media mode). Close this window to stop.");
    Application.Run();
  }
}
"@
[KeyProbe]::Run()
