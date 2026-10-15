# GPU OC feasibility probe (read-only + a no-change write). Run in an ADMIN PowerShell, plugged in.
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class N {
  [DllImport("nvml.dll")] public static extern int nvmlInit_v2();
  [DllImport("nvml.dll")] public static extern int nvmlShutdown();
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetHandleByIndex_v2(uint i, out IntPtr d);
  [DllImport("nvml.dll")] public static extern IntPtr nvmlErrorString(int r);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetGpcClkVfOffset(IntPtr d, out int o);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemClkVfOffset(IntPtr d, out int o);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetGpcClkMinMaxVfOffset(IntPtr d, out int mn, out int mx);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetMemClkMinMaxVfOffset(IntPtr d, out int mn, out int mx);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceSetGpcClkVfOffset(IntPtr d, int o);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceSetMemClkVfOffset(IntPtr d, int o);
  [StructLayout(LayoutKind.Sequential)] public struct Off { public uint ver; public int type; public int pstate; public int cur; public int min; public int max; }
  [DllImport("nvml.dll")] public static extern int nvmlDeviceGetClockOffsets(IntPtr d, ref Off o);
  [DllImport("nvml.dll")] public static extern int nvmlDeviceSetClockOffsets(IntPtr d, ref Off o);
  public static string E(int r) { return r == 0 ? "OK" : (r + " " + Marshal.PtrToStringAnsi(nvmlErrorString(r))); }
}
"@
function T($name, [scriptblock]$b) { try { & $b } catch { "{0}: missing ({1})" -f $name, $_.Exception.GetType().Name } }
"init: " + [N]::E([N]::nvmlInit_v2())
$d = [IntPtr]::Zero; "handle: " + [N]::E([N]::nvmlDeviceGetHandleByIndex_v2(0, [ref]$d))
T "legacy" {
  $g=0;$m=0;$a=0;$b=0;$c=0;$e=0
  "gpc offset: "  + [N]::E([N]::nvmlDeviceGetGpcClkVfOffset($d,[ref]$g)) + " = $g"
  "mem offset: "  + [N]::E([N]::nvmlDeviceGetMemClkVfOffset($d,[ref]$m)) + " = $m"
  "gpc range: "   + [N]::E([N]::nvmlDeviceGetGpcClkMinMaxVfOffset($d,[ref]$a,[ref]$b)) + " = $a..$b"
  "mem range: "   + [N]::E([N]::nvmlDeviceGetMemClkMinMaxVfOffset($d,[ref]$c,[ref]$e)) + " = $c..$e"
  "gpc set same: "+ [N]::E([N]::nvmlDeviceSetGpcClkVfOffset($d,$g))
  "mem set same: "+ [N]::E([N]::nvmlDeviceSetMemClkVfOffset($d,$m))
}
T "new" {
  foreach ($t in 0,2) {
    $o = New-Object N+Off; $o.ver = 24 -bor (1 -shl 24); $o.type = $t; $o.pstate = 0
    $r = [N]::nvmlDeviceGetClockOffsets($d,[ref]$o)
    "offsets type ${t}: " + [N]::E($r) + " cur=$($o.cur) range=$($o.min)..$($o.max)"
    if ($r -eq 0) { "  set same: " + [N]::E([N]::nvmlDeviceSetClockOffsets($d,[ref]$o)) }
  }
}
[void][N]::nvmlShutdown()
