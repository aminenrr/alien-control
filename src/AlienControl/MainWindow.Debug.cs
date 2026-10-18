using System;
using System.Threading.Tasks;
using System.Windows;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// Debug: frame-gap log for every slide (steps/s + biggest gap to crash.log).
/// 1.3.0: the beta.1 "New slide engine" and "Normal window" A/B toggles were removed. The user picked the new engine
/// and the transparent window (the normal window felt worse), so both are now fixed.
/// </summary>
public partial class MainWindow
{
    /// <summary>Called right after InitializeComponent.</summary>
    private void ApplyWindowMode() => WindowSlide.LogFrames = _settings.FrameLog;

    private void InitDebugToggles() => FrameLogToggle.IsChecked = _settings.FrameLog;

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        _settings.FrameLog = FrameLogToggle.IsChecked == true;
        _settings.Save();
        WindowSlide.LogFrames = _settings.FrameLog;
    }

    // ---------- 1.3.1-beta.2: F7 probe ----------
    // F7 sends no consumer report (crash.log, 2026-10-18), so the app can't see it yet. For 20 s this logs every key
    // down the low-level hook sees (vk / scan / flags), every Alienware HID report, and the keyboard's lighting state
    // read back over HID whenever it changes (4 reads a second, only during these 20 s). One click, then press F7.
    // 1.3.1-beta.4: also every other HID collection of the Alienware keyboard and AW-ELC (see MacroKeys.ProbeAllCollections).
    private bool _f7Probe;

    private async void F7Probe_Click(object sender, RoutedEventArgs e)
    {
        if (_f7Probe) return;
        _f7Probe = true;
        F7ProbeButton.IsEnabled = false;
        try
        {
            Log.Info("F7 probe", $"start (keyboard {(_settings.OffKeyboard ? "off" : "on")} in the app, brightness {_settings.Brightness})");
            _gkey.Probe = (vk, scan, flags) => Task.Run(() => Log.Info("F7 probe", $"key vk 0x{vk:X2} scan 0x{scan:X2} flags 0x{flags:X2}"));
            MacroKeys.ProbeAll = true;
            InputThread.Invoke(() => _macro.ProbeAllCollections(true));   // 1.3.1-beta.4: every Alienware HID collection
            string? last = null;
            var end = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < end)
            {
                int left = (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds);
                F7ProbeButton.Content = $"Press F7 · {left}s";
                string state = await Task.Run(() => { try { return _fx.Hid.ReadState(); } catch (Exception ex) { return "error " + ex.Message; } });
                if (state != last) { Log.Info("F7 probe", "state " + state); last = state; }
                await Task.Delay(250);
            }
        }
        finally
        {
            _gkey.Probe = null;
            MacroKeys.ProbeAll = false;
            InputThread.Invoke(() => _macro.ProbeAllCollections(false));
            Log.Info("F7 probe", "end");
            F7ProbeButton.Content = "Watch F7";
            F7ProbeButton.IsEnabled = true;
            _f7Probe = false;
            Status("F7 test done. The results are in crash.log.");
        }
    }
}
