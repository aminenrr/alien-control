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
/// 2.0.0-beta1.4 (ui review, MainWindow split): split out of MainWindow.xaml.cs, code moved unchanged. The GPU MODE row:
/// Advanced Optimus / Ultimate (Dell BIOS), iGPU only (RTX off with pnputil) and the in-window GPU dialog.
/// </summary>
public partial class MainWindow
{
    // ---------- GPU mode ----------
    // v0.9.2: two modes, Advanced Optimus and Ultimate (Dell BIOS HybridGraphics + restart). NVIDIA has no API for its
    // display mode (Procmon 2026-10-15), so Auto / Optimus / GPU only stay in NVIDIA's tray popup and the hint just reads them.
    // Read on show, after a change, on display changes and on focus; never polled.

    private GpuMode.State _gpu = new(GpuModeKind.Unknown, false, null);
    private bool _gpuBusy, _gpuDisplayHooked, _rtxSwitching;

    private Task RefreshGpuHintAsync() => RefreshGpuAsync(withBios: true);

    private DateTime _gpuReadUtc = DateTime.MinValue;
    private int _gpuReadGen;

    /// <summary>2.0.0-beta1.4 (ui review #7, services #13): focus and tab changes re-read the GPU state only while Tuning
    /// (the GPU MODE row) is on screen, and at most every 10 s. Changes made by the app re-read straight away.</summary>
    private void RefreshGpuIfShown()
    {
        if (!IsVisible || _gpuBusy || TuningPage.Visibility != Visibility.Visible || SettingsShown) return;
        if ((DateTime.UtcNow - _gpuReadUtc).TotalSeconds < 10) return;
        _ = RefreshGpuAsync(withBios: false);
    }

    private async Task RefreshGpuAsync(bool withBios)
    {
        var prev = _gpu;
        var setUtc = _settings.GpuSwitchSetUtc;
        var setBoot = _settings.GpuSwitchSetBootId;
        int gen = ++_gpuReadGen;
        _gpuReadUtc = DateTime.UtcNow;
        var st = await Task.Run(() => GpuMode.Read(withBios, setUtc, setBoot));
        if (gen != _gpuReadGen) return;   // 2.0.0-beta1.4 (ui review #7): a newer read started meanwhile, it wins
        prev = _gpu;
        if (!withBios)
        {
            // only the BIOS read can tell Ultimate apart: keep what the last full read said
            st = st with { BiosUltimate = prev.BiosUltimate };
            if (prev.Kind == GpuModeKind.Ultimate) st = st with { Kind = GpuModeKind.Ultimate, Auto = false };
        }
        _gpu = st;
        ShowGpuState();
        if (prev.Kind != st.Kind) UpdateOcUi();   // 2.0.0-beta1.4 (bug B): grey / restore the OC card with the RTX
        if (!_gpuDisplayHooked)
        {
            _gpuDisplayHooked = true;
            // NVIDIA's switch (and Auto) reconfigures the panel: re-read once it settles. Event only, no timer.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(1500);
                if (IsVisible) await RefreshGpuAsync(withBios: false);
            });
            // Auto <-> Optimus while Intel stays on the panel changes no display, only NVIDIA's flag: re-read on focus
            // (2.0.0-beta1.4: only with Tuning on screen, at most every 10 s)
            Activated += (_, _) => RefreshGpuIfShown();
        }
    }

    // 1.2.0-beta.2 (Amine): iGPU only is back as a third button. It only works in Advanced Optimus with NVIDIA set to
    // Optimus (Intel on the panel, RTX asleep). Auto = pressable but warns to switch to Optimus first; dGPU only and
    // Ultimate = locked with a note. While the RTX is off, Ultimate is locked too (never boot the MUX with it disabled).
    // Plugging in never turns the RTX back on: only Advanced Optimus does (pnputil disable survives reboots).
    private static readonly System.Windows.Media.Color NoteAmber = System.Windows.Media.Color.FromRgb(255, 176, 32);
    private static readonly System.Windows.Media.Color NoteGreen = System.Windows.Media.Color.FromRgb(60, 255, 122);

    private void ShowGpuState()
    {
        var k = _gpu.Kind;
        bool ult = k == GpuModeKind.Ultimate;
        bool off = k == GpuModeKind.IGpuOnly;
        GpuIgpu.IsChecked = off;
        GpuOptimus.IsChecked = !ult && !off && k != GpuModeKind.Unknown;
        GpuUltimate.IsChecked = ult;
        GpuOptimus.ToolTip = ult ? "Back to hybrid: turns the Intel GPU back on in the BIOS. Needs a restart."
            : off ? "Turns the RTX back on. No restart."
            : "Intel + RTX. Pick Auto / Optimus / GPU only from NVIDIA's tray icon.";

        // locks: iGPU only needs hybrid with Intel on the panel; Ultimate needs the RTX on
        bool igpuLocked = (k == GpuModeKind.DGpuOnly && !_gpu.Auto) || k is GpuModeKind.Ultimate or GpuModeKind.Unknown || _gpu.PendingRestart;
        GpuIgpu.IsEnabled = !igpuLocked;
        GpuUltimate.IsEnabled = !off;
        GpuIgpu.ToolTip = k switch
        {
            GpuModeKind.IGpuOnly => "The RTX is off. Click Advanced Optimus to turn it back on.",
            GpuModeKind.Ultimate => "Locked in Ultimate: the Intel GPU is off.",
            _ when _gpu.Auto => "NVIDIA is on Automatic Select. Switch it to Optimus first.",
            GpuModeKind.DGpuOnly => "Locked: the RTX drives the screen. Pick Optimus from NVIDIA's tray icon first.",
            _ => "Turns the RTX off (Intel only). Needs NVIDIA set to Optimus."
        };
        GpuUltimate.ToolTip = off ? "Locked while the RTX is off. Click Advanced Optimus first."
            : "Dell BIOS: Intel GPU off, the RTX drives everything. Needs a restart.";

        if (off && !_power.GpuOff) _power.GpuOff = true;   // started in iGPU only: keep NVML closed
        IgpuLock.Visibility = igpuLocked ? Visibility.Visible : Visibility.Collapsed;
        // 1.2.0-beta.4 (Amine): the amber "why it's locked" note only shows when the locked button is clicked;
        // the green "RTX is off" note stays while it's off.
        if (off) ShowGpuNote("RTX is off. HDMI / Mini DP stay dark. Ultimate is locked until the RTX is back: click Advanced Optimus.", NoteGreen, 0);
        else if (_gpuNoteSticky || !igpuLocked) HideGpuNote();

        // v0.9.2: Advanced Optimus = NVIDIA's mode + the GPU driving the screen. Ultimate = just the RTX.
        bool rtx = k == GpuModeKind.DGpuOnly;
        // v0.9.5 (Amine): full GPU names, only the mode word is in caps
        string gpu = rtx ? "RTX 3070 Ti" : "Intel Iris Xe";
        string hint = k switch
        {
            GpuModeKind.Ultimate => "RTX 3070 Ti",
            GpuModeKind.Unknown => "",
            GpuModeKind.IGpuOnly => "iGPU ONLY · RTX OFF",
            _ when _gpu.Auto => $"AUTO · {gpu}",
            GpuModeKind.DGpuOnly => $"dGPU ONLY · {gpu}",
            _ => $"OPTIMUS · {gpu}"
        };
        if (_gpu.PendingRestart)
            hint = _gpu.BiosUltimate == true ? "ULTIMATE AFTER RESTART" : "ADV. OPTIMUS AFTER RESTART";
        GpuHint.Text = hint;
    }

    private bool _gpuNoteSticky;   // the green RTX-off note
    private int _gpuNoteGen;

    /// <summary>Shows the note under the GPU row. ms = 0 keeps it; otherwise it hides itself after ms.</summary>
    private async void ShowGpuNote(string text, System.Windows.Media.Color color, int ms)
    {
        int gen = ++_gpuNoteGen;
        _gpuNoteSticky = ms == 0;
        // 1.3.0-beta.1: no resize mid-slide; the note waits until the slide lands (at most ~0.6 s)
        for (int i = 0; i < 40 && (_slide?.Active == true || _openParked); i++) await Task.Delay(16);
        if (gen != _gpuNoteGen) return;
        GpuNote.Text = text;
        GpuNote.Foreground = Freeze(new SolidColorBrush(color));   // 2.0.0-beta1.4 (ui review #19): frozen
        GpuNote.Visibility = Visibility.Visible;
        if (ms == 0) return;
        await Task.Delay(ms);
        if (gen == _gpuNoteGen) HideGpuNote();
    }

    private void HideGpuNote()
    {
        _gpuNoteGen++;
        _gpuNoteSticky = false;
        GpuNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>Click on the locked iGPU only button (a disabled button passes the click to its wrapper).</summary>
    private void IgpuWrap_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (GpuIgpu.IsEnabled) return;
        var k = _gpu.Kind;
        string? why = _gpu.PendingRestart ? "iGPU only is locked until the restart finishes the GPU switch."
            : k == GpuModeKind.DGpuOnly ? "iGPU only is locked: the RTX drives the screen. Pick Optimus from NVIDIA's tray icon first."
            : k == GpuModeKind.Ultimate ? "iGPU only isn't available in Ultimate: the Intel GPU is off. Go back to Advanced Optimus (restart), then pick Optimus."
            : k == GpuModeKind.Unknown ? "iGPU only is locked: couldn't read the GPU state." : null;
        if (why is not null) ShowGpuNote(why, NoteAmber, 6000);
    }

    private async void GpuMode_Click(object sender, RoutedEventArgs e)
    {
        ShowGpuState();   // the row only moves once the switch really happened
        if (_gpuBusy) return;
        _gpuBusy = true;
        try
        {
            if (sender == GpuIgpu) await RtxOffAsync();
            else if (sender == GpuOptimus && _gpu.Kind == GpuModeKind.IGpuOnly) await RtxOnAsync();
            else await SwitchGpuAsync(sender == GpuUltimate);
        }
        catch (Exception ex) { Status("GPU switch failed: " + ex.Message); }
        finally
        {
            _gpuBusy = false;
            await RefreshGpuAsync(withBios: true);
        }
    }

    /// <summary>iGPU only: lists the apps on the RTX, then disables it with pnputil.</summary>
    private async Task RtxOffAsync()
    {
        // re-read first: NVIDIA's mode may have changed in its own popup since the last read
        await RefreshGpuAsync(withBios: false);
        var k = _gpu.Kind;
        if (k == GpuModeKind.IGpuOnly) return;
        if (_gpu.Auto && k is GpuModeKind.Optimus or GpuModeKind.DGpuOnly && !_gpu.PendingRestart)
        {
            var w = await ShowGpuDialog("Switch to Optimus first",
                "NVIDIA is on Automatic Select, so it can hand the screen to the RTX at any time. Turning the RTX off now could black out the display.\n\n"
                + "Pick Optimus from NVIDIA's tray icon, then try again.",
                null, null, ("nv", "Open NVIDIA panel", false), ("ok", "OK", true));
            if (w == "nv") { var err0 = GpuMode.OpenNvidiaDisplayMode(); if (err0 is not null) Status(err0); }
            return;
        }
        if (k != GpuModeKind.Optimus || _gpu.PendingRestart) { Status("iGPU only needs Advanced Optimus with NVIDIA set to Optimus."); return; }

        while (true)
        {
            var apps = await Task.Run(GpuMode.AppsUsingDgpuDetailed);
            string? pick;
            if (apps.Count > 0)
                pick = await ShowGpuDialog("Turn off the RTX?",
                    "Save your work first. These apps lose the RTX and may close or crash.\n\nHDMI / Mini DP go dark until you turn it back on.",
                    apps.Select(a => $"{a.Name}  ·  {a.Mb} MB").ToList(), "USING THE RTX",
                    ("cancel", "Cancel", false), ("refresh", "Refresh", false), ("go", "Turn off anyway", true));
            else
                pick = await ShowGpuDialog("Turn off the RTX?",
                    "Nothing is using it right now. HDMI / Mini DP go dark until you turn it back on with Advanced Optimus.",
                    null, null, ("cancel", "Cancel", false), ("go", "Turn off", true));
            if (pick == "refresh") continue;
            if (pick != "go") return;
            break;
        }

        Status("Turning the RTX off...");
        // 1.2.0-beta.4 fix: the app closed when the RTX went off. The power meter kept an NVML handle to the RTX and
        // read it from a pool thread while pnputil removed the device (native crash, can't be caught). Stop the power
        // timer and close NVML first; GPU power and OC stay off while the RTX is disabled.
        // 2.0.0-beta1.3 (services review #1): and every other NVML user too. BeginDeviceRemoval waits for an OC write
        // already in flight, closes the shared session and refuses NVML calls until pnputil has exited.
        _rtxSwitching = true;
        UpdateOcUi();   // 2.0.0-beta1.4 (bug B): the OC card greys while the RTX switches
        _powerTimer.Stop();
        await Task.Run(() => { _power.GpuOff = true; NvmlSession.BeginDeviceRemoval(); });
        string? err;
        try { err = await GpuMode.SetDgpuEnabledAsync(false); }
        finally { _rtxSwitching = false; _ = EndDeviceRemovalWhenIdleAsync(); }
        if (err is not null) _power.GpuOff = false;
        else _gpu = _gpu with { Kind = GpuModeKind.IGpuOnly };   // 2.0.0-beta1.3: OC syncs skip NVML until the re-read lands
        UpdateOcUi();   // 2.0.0-beta1.4 (bug B): OC card greyed, Home OC line hidden
        UpdatePolling();   // 2.0.0-beta1.3 (ui review #14): only when a sensor page is on screen (was IsVisible)
        _ocApplied = null;   // the driver forgets the offsets with the device
        Status(err ?? "RTX is off. Click Advanced Optimus to turn it back on.");
    }

    /// <summary>Advanced Optimus from iGPU only: re-enables the RTX (no restart) and brings NVIDIA's tray icon back.</summary>
    private async Task RtxOnAsync()
    {
        Status("Turning the RTX back on...");
        _rtxSwitching = true;
        UpdateOcUi();   // 2.0.0-beta1.4 (bug B)
        await Task.Run(NvmlSession.BeginDeviceRemoval);   // 2.0.0-beta1.3: no NVML call while the device comes back
        string? err;
        try { err = await GpuMode.SetDgpuEnabledAsync(true); }
        finally { _rtxSwitching = false; _ = EndDeviceRemovalWhenIdleAsync(); }
        if (err is not null)
        {
            Status(err);
            // 2.0.0-beta1.4: a pnputil that timed out may still bring the RTX back: then do the tray restart too
            _ = Task.Run(async () =>
            {
                try { await GpuMode.PnpIdle; } catch (Exception ex) { Services.Log.Warn("PnpIdle", ex); }
                if (GpuMode.Adapters().FirstOrDefault(a => a.IsNvidia)?.Ok == true) await AfterRtxOnAsync();
            });
            return;
        }
        _ocApplied = null;
        Status("RTX is back on.");
        _ = Task.Run(AfterRtxOnAsync);
    }

    /// <summary>2.0.0-beta1.4 (Amine, bug A): runs on a pool thread, outside the command gate (the service restart can
    /// take a minute). NVML again once the driver is up, then NVIDIA's Display Container is always restarted (its tray
    /// icon and display mode popup), then the kept OC offsets are re-applied if G-Mode is on AC.</summary>
    private async Task AfterRtxOnAsync()
    {
        try
        {
            await Task.Delay(4000);
            _power.GpuOff = false;
            await Task.Delay(2000);
            var r = await GpuMode.EnsureNvidiaTrayAsync();
            Services.Log.Info("RtxOn", "NVIDIA tray: " + r);
            // the OC card comes back once the RTX is up (bug B): re-read the mode first so OC syncs aren't skipped
            await Dispatcher.InvokeAsync(() => RefreshGpuAsync(withBios: false)).Task.Unwrap();
            await Dispatcher.InvokeAsync(() => UpdateOcUi());
            await SyncOcAsync();   // re-apply the kept OC offsets if G-Mode is on AC
        }
        catch (Exception ex) { Services.Log.Warn("AfterRtxOn", ex); }
    }

    /// <summary>Advanced Optimus <-> Ultimate through Dell BIOS HybridGraphics. Clicking the running mode does nothing,
    /// except it cancels a switch queued for the next restart.</summary>
    private async Task SwitchGpuAsync(bool toUlt)
    {
        var cur = _gpu.Kind;
        if (cur == GpuModeKind.Unknown) { Status("Couldn't read the GPU state. Nothing was changed."); return; }
        if (toUlt && cur == GpuModeKind.IGpuOnly) { Status("Ultimate is locked while the RTX is off. Click Advanced Optimus first."); return; }
        bool isUlt = cur == GpuModeKind.Ultimate;

        if (toUlt == isUlt)
        {
            if (!_gpu.PendingRestart) return;
            var undo = await Task.Run(() => GpuMode.SetUltimate(isUlt));
            if (undo is null) { _settings.GpuSwitchSetUtc = null; _settings.GpuSwitchSetBootId = null; _settings.Save(); }
            Status(undo ?? "Switch cancelled. No restart needed.");
            return;
        }
        if (_gpu.PendingRestart) { Status("Already set. It takes effect after your next restart."); return; }

        var pick = await ShowGpuDialog(
            toUlt ? "Switch to Ultimate?" : "Back to Advanced Optimus?",
            toUlt ? "The BIOS turns the Intel GPU off and the RTX drives everything: best FPS, shortest battery. "
                    + "NVIDIA's display modes come back when you switch back. Needs a restart."
                  : "The BIOS turns the Intel GPU back on, so NVIDIA's Auto / Optimus / GPU only modes come back. Needs a restart.",
            null, null,
            ("cancel", "Cancel", false), ("later", "Restart later", false), ("now", "Restart now", true));
        if (pick is null or "cancel") return;
        var err = await Task.Run(() => GpuMode.SetUltimate(toUlt));
        if (err is not null) { Status(err); return; }
        _settings.GpuSwitchSetUtc = DateTime.UtcNow;
        _settings.GpuSwitchSetBootId = GpuMode.BootId();
        _settings.Save();
        if (pick == "now") { GpuMode.RestartNow(); return; }
        Status(toUlt ? "Ultimate is set. It takes effect after your next restart." : "Advanced Optimus is set. It takes effect after your next restart.");
    }

    // ----- in-window dialog -----

    private TaskCompletionSource<string?>? _gpuDlg;

    private Task<string?> ShowGpuDialog(string title, string body, System.Collections.Generic.IList<string>? list, string? listTitle,
        params (string Id, string Text, bool Primary)[] buttons)
    {
        _gpuDlg?.TrySetResult(null);
        var tcs = new TaskCompletionSource<string?>();
        _gpuDlg = tcs;
        GpuDlgTitle.Text = title.ToUpperInvariant();
        GpuDlgBody.Text = body;
        GpuDlgListTitle.Text = listTitle ?? "";
        GpuDlgListTitle.Visibility = string.IsNullOrEmpty(listTitle) ? Visibility.Collapsed : Visibility.Visible;
        GpuDlgList.ItemsSource = list;
        GpuDlgList.Visibility = list is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        GpuDlgButtons.Children.Clear();
        foreach (var b in buttons)
        {
            var btn = new Button { Content = b.Text, Margin = new Thickness(8, 4, 0, 0) };
            btn.SetResourceReference(StyleProperty, b.Primary ? "ButtonPrimary2" : "Button2");
            var id = b.Id;
            btn.Click += (_, _) => CloseGpuDialog(id);
            GpuDlgButtons.Children.Add(btn);
        }
        GpuDialogLayer.Opacity = 1;
        GpuDialogLayer.Visibility = Visibility.Visible;
        if (Motion.Enabled)
            GpuDialogLayer.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        return tcs.Task;
    }

    private void CloseGpuDialog(string? id)
    {
        GpuDialogLayer.BeginAnimation(OpacityProperty, null);
        GpuDialogLayer.Visibility = Visibility.Collapsed;
        var t = _gpuDlg;
        _gpuDlg = null;
        t?.TrySetResult(id);
    }
}
