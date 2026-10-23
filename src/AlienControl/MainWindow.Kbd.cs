using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 1.3.0-beta.1 keyboard backlight timeout (2.0: its rows moved from Settings to the Lights tab). A slow timer (5 s, only while the keys are lit and a timeout is set)
/// compares GetLastInputInfo with the timeout. Then the keyboard's hardware brightness fades to 0 (~600 ms) and
/// frames are held. While dark, the existing G-key hook (keys) and mouse raw input on the macro window (mouse,
/// touchpad) wake it at once: full brightness, the held frame, status keys re-sent. No new hook; nothing when off.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _kbdTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource? _kbdFade;
    private bool _kbdDark, _kbdUiReady;

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize, dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO i);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private static uint IdleMs()
    {
        var i = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref i) ? unchecked((uint)Environment.TickCount - i.dwTime) : 0;
    }

    private static bool FullscreenApp() =>
        SHQueryUserNotificationState(out int st) == 0 && st is 2 or 3 or 4;   // BUSY, RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE

    private void InitKbdTimeout()
    {
        KbdButton(_settings.KbdTimeoutMin).IsChecked = true;   // 1.3.0-beta.2: segmented Off / 1 / 2 / 5 / 10 min
        KbdBatteryToggle.IsChecked = _settings.KbdTimeoutBatteryOnly;
        KbdFullscreenToggle.IsChecked = _settings.KbdTimeoutPauseFullscreen;
        _kbdTimer.Tick += (_, _) => KbdTick();
        _kbdUiReady = true;
        UpdateKbdTimer();
        UpdateKbdHint();
    }

    /// <summary>2.0: the BACKLIGHT TIMEOUT header on the Lights tab reads the setting ("2 MIN", "OFF").</summary>
    private void UpdateKbdHint() => KbdHint.Text = _settings.KbdTimeoutMin > 0 ? $"{_settings.KbdTimeoutMin} MIN" : "OFF";

    /// <summary>The timer runs only while it can matter: a timeout is set, the keys are lit (and plugged-in rules allow).</summary>
    private void UpdateKbdTimer()
    {
        bool on = _settings.KbdTimeoutMin > 0 && !_kbdDark && !_settings.OffKeyboard && (!_settings.KbdTimeoutBatteryOnly || !OnAc);
        if (on && !_kbdTimer.IsEnabled) _kbdTimer.Start();
        else if (!on && _kbdTimer.IsEnabled) _kbdTimer.Stop();
        if (!on && _kbdDark && _settings.KbdTimeoutMin == 0) KbdWake();
    }

    private void KbdTick()
    {
        if (_kbdDark || _settings.KbdTimeoutMin <= 0 || _settings.OffKeyboard) { UpdateKbdTimer(); return; }
        if (_settings.KbdTimeoutBatteryOnly && OnAc) { UpdateKbdTimer(); return; }
        if (_anim is not null) return;                                           // Rainbow / Spectrum running
        if (_settings.KbdTimeoutPauseFullscreen && FullscreenApp()) return;
        if (IdleMs() < (uint)_settings.KbdTimeoutMin * 60_000u) return;
        KbdFadeOut();
    }

    private void KbdFadeOut()
    {
        _kbdDark = true;
        _kbdTimer.Stop();
        var cts = new CancellationTokenSource();
        _kbdFade = cts;
        // wake sources first, so input during the fade cancels it
        _gkey.AnyKey = () => Dispatcher.BeginInvoke(new Action(KbdWake));
        InputThread.Invoke(() => _macro.WatchMouse(() => Dispatcher.BeginInvoke(new Action(KbdWake))));
        _ = Task.Run(() =>
        {
            if (!_fx.Hid.FadeOut(600, cts.Token) && !cts.IsCancellationRequested)
                Dispatcher.BeginInvoke(new Action(KbdWake));   // HID failed: don't stay half-dim
        });
    }

    private void KbdWake()
    {
        if (!_kbdDark) return;
        _kbdDark = false;
        _kbdFade?.Cancel();
        _kbdFade = null;
        _gkey.AnyKey = null;
        InputThread.Dispatcher.BeginInvoke(new Action(() => _macro.WatchMouse(null)));
        _ = Task.Run(async () =>
        {
            // 2.0.0-beta1.3 (services review #3): no 30 ms wait for a cancelled fade step any more. Wake bumps the fade
            // generation under the HID lock, so the fade can't set "dark" after it.
            _fx.Hid.Wake();
            await Dispatcher.InvokeAsync(async () => { _f2Dirty = true; await SyncIndicatorsAsync(); }).Task.Unwrap();
        });
        UpdateKbdTimer();
    }

    private RadioButton KbdButton(int min) => min switch { 1 => Kbd1, 2 => Kbd2, 5 => Kbd5, 10 => Kbd10, _ => Kbd0 };

    private void KbdTimeout_Click(object sender, RoutedEventArgs e)
    {
        if (!_kbdUiReady) return;
        int min = sender == Kbd1 ? 1 : sender == Kbd2 ? 2 : sender == Kbd5 ? 5 : sender == Kbd10 ? 10 : 0;
        if (min == _settings.KbdTimeoutMin) return;
        _settings.KbdTimeoutMin = min;
        _settings.Save();
        UpdateKbdTimer();
        UpdateSettingsDims();
        UpdateKbdHint();
    }

    private void KbdOption_Click(object sender, RoutedEventArgs e)
    {
        _settings.KbdTimeoutBatteryOnly = KbdBatteryToggle.IsChecked == true;
        _settings.KbdTimeoutPauseFullscreen = KbdFullscreenToggle.IsChecked == true;
        _settings.Save();
        UpdateKbdTimer();
    }
}
