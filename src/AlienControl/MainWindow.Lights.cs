using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 1.3.1-beta.2 Lights menu (replaces the beta.1 Off menu). The laptop button opens a popup above it with four tiles:
/// Keyboard, Rear ring, Alienhead (lit tile = that light is ON, a tap switches it) and an action tile that reads
/// "All off" (lamp with a line) while any light is on and "All on" (lamp) once all three are off.
/// The keyboard goes off through its hardware brightness (colors kept, frames held, see KeyboardHid.SetOff), so the
/// firmware F7 key can still bring it back. The chassis lights get black (AlienFx.Chassis); the power button is never
/// sent. Settings keep the "off" switches (OffKeyboard / OffRing / OffAlienhead), only the UI shows them as "on".
/// </summary>
public partial class MainWindow
{
    private const double LightsPopupWidth = 368;
    private readonly SemaphoreSlim _zoneGate = new(1, 1);
    private bool _zoneUiBusy;
    private long _lightsClosedAt;
    private long _zoneLastClick;

    private int LightsOffCount => (_settings.OffKeyboard ? 1 : 0) + (_settings.OffRing ? 1 : 0) + (_settings.OffAlienhead ? 1 : 0);

    private void Lights_Click(object sender, RoutedEventArgs e)
    {
        if (LightsPopup.IsOpen) { LightsPopup.IsOpen = false; return; }
        // StaysOpen=False closes the popup on the mouse-down that lands on this button, before Click runs: without
        // this check the same click would open it straight back up.
        if (Environment.TickCount64 - _lightsClosedAt < 250 && LightsButton.IsMouseOver) return;
        SyncZoneTiles();
        LightsPopup.HorizontalOffset = LightsButton.ActualWidth - LightsPopupWidth;   // right edges line up
        LightsPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => ZoneKeyboard.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void LightsPopup_Closed(object? sender, EventArgs e) => _lightsClosedAt = Environment.TickCount64;

    private void LightsPanel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        LightsPopup.IsOpen = false;
        LightsButton.Focus();
        e.Handled = true;
    }

    /// <summary>Tiles, the All tile and the laptop button follow the saved switches.</summary>
    private void SyncZoneTiles()
    {
        _zoneUiBusy = true;
        ZoneKeyboard.IsChecked = !_settings.OffKeyboard;
        ZoneRing.IsChecked = !_settings.OffRing;
        ZoneHead.IsChecked = !_settings.OffAlienhead;
        bool allOff = LightsOffCount == 3;
        ZoneAll.Content = allOff ? "All on" : "All off";
        ZoneAll.SetResourceReference(TagProperty, allOff ? "Icon.Bulb" : "Icon.BulbOff");
        if (allOff) ZoneAll.SetResourceReference(ForegroundProperty, "AccentBrush"); else ZoneAll.ClearValue(ForegroundProperty);
        _zoneUiBusy = false;
        UpdateLightsButton();
    }

    /// <summary>Accent laptop when lights are on, an amber dot when some are off, muted when all three are off.</summary>
    private void UpdateLightsButton()
    {
        int n = LightsOffCount;
        if (n == 3) LightsIcon.SetResourceReference(ForegroundProperty, "TextMuted");
        else LightsIcon.SetResourceReference(ForegroundProperty, "AccentBrush");
        LightsDot.Visibility = n is > 0 and < 3 ? Visibility.Visible : Visibility.Collapsed;
        LightsButton.ToolTip = n == 0 ? "Lights: all on" : n == 3 ? "Lights: all off" : "Lights off: " + string.Join(", ",
            new[] { _settings.OffKeyboard ? "keyboard" : null, _settings.OffRing ? "rear ring" : null, _settings.OffAlienhead ? "Alienhead" : null }
            .Where(s => s is not null));
    }

    /// <summary>One action per click: a second click within 350 ms (a double-click) is ignored, so a tile can't
    /// switch a light on and straight back off (beta.1 bug, crash.log 2026-10-18 21:55:15).</summary>
    private bool ZoneDebounced()
    {
        long now = Environment.TickCount64;
        if (now - _zoneLastClick < 350) return true;
        _zoneLastClick = now;
        return false;
    }

    private async void Zone_Click(object sender, RoutedEventArgs e)
    {
        if (_zoneUiBusy) return;
        if (ZoneDebounced()) { SyncZoneTiles(); return; }   // undo the CheckBox's own flip
        bool kb = _settings.OffKeyboard, ring = _settings.OffRing, head = _settings.OffAlienhead;
        if (sender == ZoneKeyboard) kb = !kb;
        else if (sender == ZoneRing) ring = !ring;
        else if (sender == ZoneHead) head = !head;
        await SetZonesAsync(kb, ring, head);
    }

    private async void ZoneAll_Click(object sender, RoutedEventArgs e)
    {
        if (ZoneDebounced()) return;
        bool off = LightsOffCount < 3;   // All off while any light is on; All on once all three are off
        await SetZonesAsync(off, off, off);
    }

    private async Task SetZonesAsync(bool keyboard, bool ring, bool head)
    {
        await _zoneGate.WaitAsync();   // quick taps apply one after the other, never interleaved
        try
        {
            bool kbChanged = keyboard != _settings.OffKeyboard;
            _settings.OffKeyboard = keyboard;
            _settings.OffRing = ring;
            _settings.OffAlienhead = head;
            _settings.Save();
            SyncZoneTiles();
            if (!_fx.IsAvailable) return;
            if (kbChanged)
            {
                if (keyboard) StopAnimation();
                bool ok = await Task.Run(() => { try { return _fx.Hid.SetOff(keyboard); } catch (Exception ex) { Log.Warn("Lights.SetOff", ex); return false; } });
                Log.Info("Lights", $"keyboard {(keyboard ? "off" : "on")} (hardware brightness {(keyboard ? 0 : 255)}) {(ok ? "ok" : "FAILED")}");
                UpdateKbdTimer();
            }
            Log.Info("Lights", $"keyboard {(keyboard ? "off" : "on")}, rear ring {(ring ? "off" : "on")}, Alienhead {(head ? "off" : "on")}");
            _f2Dirty = true;
            await ApplyLightingAsync(_lastColor);   // chassis black / color, keyboard frame (held while off), effect restart
            if (kbChanged && !keyboard) await RefreshStatusKeysAsync();
            SelectSwatch(_lastColor);
        }
        finally { _zoneGate.Release(); }
    }

    /// <summary>
    /// beta.1 bug: after Keyboard off then on, the 5 white status keys (F1, F12, mute, mic, right Win) stayed dark until
    /// a color swatch was clicked. Right after the brightness command the firmware can miss the first frame, so once
    /// the keyboard is back on, wait a moment and send the full frame (status keys included) again, the same frame a
    /// swatch click sends. Static only: an effect owns the keys and has no status-key frame.
    /// </summary>
    private async Task RefreshStatusKeysAsync()
    {
        if (_settings.LastEffect != 0 || _settings.OffKeyboard) return;
        await Task.Delay(150);
        ReadIndicatorStates();
        bool ok = await _fx.SetKeyboardAsync(_lastColor, _indicators, _settings.Brightness);
        Log.Info("Lights", $"keyboard on: status keys re-sent {(ok ? "ok" : "FAILED")}");
    }
}
