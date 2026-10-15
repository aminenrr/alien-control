# HID probe (v0.9.10 prep): prints raw HID input from every non-keyboard/mouse/touchpad collection.
# Goal: see whether the macro keys (F2..F6 = Macro A..E in media mode) send anything Windows can read.
# Run: powershell -ExecutionPolicy Bypass -File dev\hid-probe.ps1 ; press F2, F3, F4 (media mode), then close.
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @"
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Windows.Forms;
public class HidProbe : Form {
  [StructLayout(LayoutKind.Sequential)] struct RID { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
  [StructLayout(LayoutKind.Sequential)] struct RIDL { public IntPtr h; public uint type; }
  [StructLayout(LayoutKind.Sequential)] struct HDR { public uint type, size; public IntPtr dev, w; }
  [StructLayout(LayoutKind.Sequential)] struct INFO { public uint cb, type; public uint vid, pid, ver; public ushort page, usage; public uint pad1, pad2; }
  [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RID[] r, uint n, uint cb);
  [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] RIDL[] l, ref uint n, uint cb);
  [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr h, uint cmd, ref INFO d, ref uint cb);
  [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, byte[] d, ref uint cb, uint hcb);
  Dictionary<IntPtr, string> names = new Dictionary<IntPtr, string>();
  protected override void OnLoad(EventArgs e) {
    base.OnLoad(e);
    uint n = 0; GetRawInputDeviceList(null, ref n, (uint)Marshal.SizeOf(typeof(RIDL)));
    var l = new RIDL[n]; GetRawInputDeviceList(l, ref n, (uint)Marshal.SizeOf(typeof(RIDL)));
    var pairs = new HashSet<uint>(); var regs = new List<RID>();
    foreach (var d in l) {
      if (d.type != 2) continue;   // HID only
      var i = new INFO(); i.cb = (uint)Marshal.SizeOf(typeof(INFO)); uint cb = i.cb;
      GetRawInputDeviceInfo(d.h, 0x2000000b, ref i, ref cb);
      string s = string.Format("{0:X4}:{1:X4} page 0x{2:X4} usage 0x{3:X2}", i.vid, i.pid, i.page, i.usage);
      names[d.h] = s; Console.WriteLine("found  " + s);
      if (i.page == 0x0D || (i.page == 1 && i.usage == 2)) continue;   // touchpad / mouse spam
      if (pairs.Add(((uint)i.page << 16) | i.usage)) regs.Add(new RID { Page = i.page, Usage = i.usage, Flags = 0x100, Target = Handle });
    }
    Console.WriteLine(RegisterRawInputDevices(regs.ToArray(), (uint)regs.Count, (uint)Marshal.SizeOf(typeof(RID)))
      ? "Listening. Press F2, F3, F4 (media mode, no Fn). Close the PowerShell window to stop." : "Register failed.");
  }
  protected override void WndProc(ref Message m) {
    if (m.Msg == 0x00FF) {
      uint cb = 0, hcb = (uint)Marshal.SizeOf(typeof(HDR));
      GetRawInputData(m.LParam, 0x10000003, null, ref cb, hcb);
      var b = new byte[cb]; GetRawInputData(m.LParam, 0x10000003, b, ref cb, hcb);
      IntPtr dev = Marshal.ReadIntPtr(b, 8);
      int off = (int)hcb + 8;
      string name; names.TryGetValue(dev, out name);
      Console.WriteLine(string.Format("{0}  {1}  {2}", DateTime.Now.ToString("HH:mm:ss.fff"), name ?? "?", BitConverter.ToString(b, off, Math.Max(0, (int)cb - off))));
    }
    base.WndProc(ref m);
  }
  public static void Run() { var f = new HidProbe(); f.ShowInTaskbar = false; f.WindowState = FormWindowState.Minimized; Application.Run(f); }
}
"@
[HidProbe]::Run()
