using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 2.0.0-beta1.4 (ui review, MainWindow split): split out of MainWindow.xaml.cs, code moved unchanged. The window itself:
/// pinned to the bottom-right corner above the taskbar, short-screen scaling, the native open / hide / minimize slide,
/// idle memory trim and AllowClose (the exit cleanup for the window's services).
/// </summary>
public partial class MainWindow
{
    // ---------- window: G-Helper style, pinned to the bottom-right corner above the taskbar ----------

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    private const uint ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABS_AUTOHIDE = 1;

    /// <summary>
    /// Bottom-right corner, above the taskbar. WorkArea ignores an auto-hide taskbar (it reports the
    /// full screen), so the taskbar's real size is read from the shell and always kept clear.
    /// </summary>
    public void SnapToCorner()
    {
        if (_slide?.Active == true || _openParked || _minSlid) return;   // v0.8.8: the slide owns the position until it lands
        var (l, t) = CornerPosition();
        Left = l;
        Top = t;
    }

    /// <summary>Right and bottom edge the window keeps clear of (the taskbar, also when it auto-hides) and the work
    /// area's top, in DIP.</summary>
    private (double Right, double Bottom, double Top) WorkBounds()
    {
        var wa = SystemParameters.WorkArea;
        double right = wa.Right, bottom = wa.Bottom;
        try
        {
            var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            bool autoHide = ((uint)SHAppBarMessage(ABM_GETSTATE, ref abd).ToInt64() & ABS_AUTOHIDE) != 0;
            if (autoHide && SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            {
                var src = PresentationSource.FromVisual(this);
                double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
                double sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
                double tbH = (abd.rc.Bottom - abd.rc.Top) * sy, tbW = (abd.rc.Right - abd.rc.Left) * sx;
                switch (abd.uEdge)
                {
                    case 3: bottom = SystemParameters.PrimaryScreenHeight - tbH; break; // bottom
                    case 2: right = SystemParameters.PrimaryScreenWidth - tbW; break;   // right
                }
            }
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("MainWindow.WorkBounds", __e); /* fall back to WorkArea */ }
        return (right, bottom, wa.Top);
    }

    private (double Left, double Top) CornerPosition()
    {
        // 2.0.0-beta1.4 (ui review #16): fixed size, so no forced layout pass and no 700 DIP fallback (compact mode)
        var (right, bottom, top) = WorkBounds();
        return (right - Width - 4, Math.Max(top, bottom - Height - 4));
    }

    protected override void OnContentRendered(EventArgs e) { base.OnContentRendered(e); FitToWorkArea(); SnapToCorner(); }

    // ---------- 2.0.0-beta1.4 (Amine, build review #8): short screens ----------
    // The window is 460 x 840 DIP (the 440 x 820 card plus 10 DIP of shadow on each side). On a screen whose work area
    // is shorter (1080p at 125 % or 150 %, 1440p at 175 %), the bottom of the card (footer: Quit, gear, Run on startup)
    // ended up under the taskbar. Then the whole card is scaled down uniformly (LayoutTransform on RootCard, window size
    // scaled with it) so it fits with 4 DIP to spare above and below. Nothing changes when it fits (Amine's 1440p panel at
    // 150 % has 912 DIP). Re-checked on DPI, display and work-area changes.
    private const double BaseWidth = 460, BaseHeight = 840;
    private double _fitScale = 1;

    private void FitToWorkArea()
    {
        var (_, bottom, top) = WorkBounds();
        double avail = bottom - top - 8;
        double scale = avail >= BaseHeight ? 1 : Math.Clamp(avail / BaseHeight, 0.5, 1);
        if (Math.Abs(scale - _fitScale) < 0.001 && Math.Abs(Height - BaseHeight * scale) < 0.5) return;
        _fitScale = scale;
        RootCard.LayoutTransform = scale >= 1 ? Transform.Identity : Freeze(new ScaleTransform(scale, scale));
        RootCard.Margin = new Thickness(10 * scale);
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;
        Log.Info("Window", scale >= 1 ? "full size" : $"scaled to {scale:P0} to fit a {avail + 8:0} DIP work area");
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(new Action(() => { FitToWorkArea(); SnapToCorner(); }));
    }

    /// <summary>v0.8.9: minimize slides down into the screen edge like hide, then minimizes (taskbar button stays).
    /// The restore position is left below the screen, so a restore (taskbar, tray, Alt+Tab) slides it back up.</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (!ShowInTaskbar) { HideToTray(); return; }   // v1.1: no taskbar button to restore from
        if (!Motion.Enabled || _hiding) { WindowState = WindowState.Minimized; return; }
        int gen = ++_showGen;
        try
        {
            var h = Hwnd;
            var r = WindowSlide.Rect(h);
            Slide.Run(r.Left, WindowSlide.MonitorBottom(h) + 2, HideMs, CubicBezier.Accelerate, () =>
            {
                if (gen != _showGen) return;
                _minSlid = true;
                WindowState = WindowState.Minimized;
            });
        }
        catch { WindowState = WindowState.Minimized; }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => HideToTray();

    // ---------- v0.8.8 window open / hide: Windows 11 Quick Settings style slide, done natively ----------
    // v0.8.6/0.8.7 slid the card INSIDE the transparent window; the laptop recording showed not one in-between
    // frame (WPF never presented them for the layered window). Now the whole HWND moves (WindowSlide, SetWindowPos
    // per frame): the compositor moves the already-drawn window, rising out of the bottom screen edge on open and
    // dropping back past it on hide. The per-frame hook lives only for the 250 / 170 ms of a slide.

    private const int OpenMs = 280, HideMs = 150;   // 1.3.0-beta.1: open 250 -> 280 ms with the softer Open curve   // 1.2.0-beta.3: hide 170 -> 150 ms
    private int _showGen;     // bumps on every show/hide so a stale hide never fires after a re-show
    private bool _hiding;
    private bool _minSlid;    // v0.8.9: minimized by sliding down; the restore slides it back up
    private bool _openParked; // PrepareShow parked the hidden window below the screen; the slide starts once it's drawn
    private WindowSlide? _slide;
    private WindowSlide Slide => _slide ??= new WindowSlide(() => new System.Windows.Interop.WindowInteropHelper(this).Handle);
    private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    /// <summary>The snapped corner position in device pixels (what SetWindowPos takes).</summary>
    private (int X, int Y) RestDevice()
    {
        var (l, t) = CornerPosition();
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        return ((int)Math.Round(l * m.M11), (int)Math.Round(t * m.M22));
    }

    /// <summary>App.ShowMain calls this before Show(). Hidden: park the window just below the screen so it is drawn
    /// out of sight and then rises. Mid-hide: reverse from where it is.</summary>
    public void PrepareShow()
    {
        if (_hiding) { _hiding = false; _showGen++; SlideIn(); return; }
        if (IsVisible || !Motion.Enabled) return;
        FitToWorkArea();   // 2.0.0-beta1.4: the right size before it is drawn out of sight
        var (l, _) = CornerPosition();
        Left = l;
        Top = SystemParameters.PrimaryScreenHeight + 2;   // fully below the bottom edge of the screen
        _openParked = true;
    }

    /// <summary>Called when the window becomes visible.</summary>
    private void PlayOpen()
    {
        Motion.Enabled = !_settings.ReduceMotion && Motion.WindowsAnimations;   // 1.3.0-beta.1: follows Windows live
        _showGen++;
        _hiding = false;
        if (!Motion.Enabled || !_openParked) { _openParked = false; Slide.Stop(); SnapToCorner(); return; }
        // Wait two rendered frames, so layout (SizeToContent) and the first paint are done while it's out of sight.
        int gen = _showGen, frames = 0;
        EventHandler? onFrame = null;
        onFrame = (_, _) =>
        {
            if (++frames < 1) return;   // 1.2.0-beta.3: one frame (was two), the slide starts sooner
            CompositionTarget.Rendering -= onFrame;   // one-shot: never keeps a per-frame hook alive
            _openParked = false;
            if (gen == _showGen && !_hiding && IsVisible) SlideIn();
        };
        CompositionTarget.Rendering += onFrame;
    }

    private void SlideIn()
    {
        int gen = _showGen;
        var (x, y) = RestDevice();
        Slide.Run(x, y, OpenMs, CubicBezier.Open, () => { if (gen == _showGen && !_hiding) SnapToCorner(); });
    }

    /// <summary>Slide down past the screen edge over 170 ms, THEN Hide(). Instant with Reduce motion or minimized;
    /// a one-shot 400 ms safety net hides it if the slide never reports back.</summary>
    public void HideToTray()
    {
        if (!IsVisible) return;
        if (!Motion.Enabled || WindowState == WindowState.Minimized || _hiding) { HideNow(); return; }
        _hiding = true;
        _openParked = false;
        int gen = ++_showGen;
        try
        {
            var h = Hwnd;
            var r = WindowSlide.Rect(h);
            Slide.Run(r.Left, WindowSlide.MonitorBottom(h) + 2, HideMs, CubicBezier.Accelerate,
                () => { if (gen == _showGen && _hiding) HideNow(); });
        }
        catch { HideNow(); return; }
        _ = HideSafetyNetAsync(gen);
    }

    private async Task HideSafetyNetAsync(int gen)
    {
        await Task.Delay(400);
        if (gen == _showGen && _hiding) HideNow();
    }

    private void HideNow()
    {
        _hiding = false;
        _minSlid = false;
        _showGen++;
        Slide.Stop();
        Hide();
        _ = TrimSoonAsync(_showGen);
    }

    // ---------- v0.9.13 idle trim: give memory back while the app sits in the tray ----------
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    private async Task TrimSoonAsync(int gen, int delayMs = 60000)
    {
        // 1.2.0-beta.3: 60 s (was 3 s). Trimming right after a hide paged WPF out, so the next open had to fault it all
        // back in (sluggish F3/F4/tray opens). A quick reopen now stays warm; sitting in the tray still drops to a few MB.
        await Task.Delay(delayMs);
        if (gen != _showGen || IsVisible) return;   // reopened meanwhile
        TrimMemory();
    }

    private static void TrimMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, false);   // 1.2.0-beta.3: lighter, no forced compacting GC
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch (System.Exception __e) { AlienControl.Services.Log.Warn("MainWindow.Window.cs:250", __e); }
    }

    // 2.0.0-beta1.3 (ui review #3): no RestoreBoostOnExit here any more, PrepareExitAsync (awaited by Quit and Reset
    // settings) restores the boost
    // 2.0.0-beta1.4: ends with one last write of settings a locked settings.json held back (Settings.FlushPending), so
    // Quit, Reset settings and the session end (logoff, restart, shutdown) don't lose the newest change
    public void AllowClose() { ResetOcOnExit(); StopAnimation(); _allowClose = true; _stats.Dispose(); _igpuTemp.Dispose(); _power.Dispose(); InputThread.Invoke(() => { _gkey.Dispose(); _macro.Dispose(); }); InputThread.Shutdown(); _indicatorTimer.Stop(); _watch?.Dispose(); Settings.FlushPending(); }
}
