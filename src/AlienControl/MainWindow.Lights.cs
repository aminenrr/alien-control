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
/// 1.3.1-beta.2 Lights menu (replaces the beta.1 Off menu). 2.0: inline on the Lights tab (ZONES), no popup. Four tiles:
/// Keyboard, Rear ring, Alienhead (lit tile = that light is ON, a tap switches it) and an action tile that reads
/// "Turn off" (lamp with a line, accent) while any light is on and "Turn on" (lamp, plain) once all three are off.
/// The keyboard goes off through its hardware brightness (colors kept, frames held, see KeyboardHid.SetOff), so the
/// firmware F7 key can still bring it back. The chassis lights get black (AlienFx.Chassis); the power button is never
/// sent. Settings keep the "off" switches (OffKeyboard / OffRing / OffAlienhead), only the UI shows them as "on".
/// </summary>
public partial class MainWindow
{
    // 2.0.0-beta1.3 (ui review #1): the one lighting gate. Zone changes and every ApplyLightingAsync go through it.
    private readonly SemaphoreSlim _zoneGate = new(1, 1);
    private bool _zoneUiBusy;
    private long _zoneLastClick;

    /// <summary>Brightness the lights are painted at: the app's setting, or half of it in the Ctrl+Alt+F 50% step.</summary>
    private int LightBrightness => _settings.LightsHalf ? _settings.Brightness / 2 : _settings.Brightness;

    private int LightsOffCount => (_settings.OffKeyboard ? 1 : 0) + (_settings.OffRing ? 1 : 0) + (_settings.OffAlienhead ? 1 : 0);

    /// <summary>Tiles and the Turn off / Turn on tile follow the saved switches.</summary>
    private void SyncZoneTiles()
    {
        _zoneUiBusy = true;
        ZoneKeyboard.IsChecked = !_settings.OffKeyboard;
        ZoneRing.IsChecked = !_settings.OffRing;
        ZoneHead.IsChecked = !_settings.OffAlienhead;
        // 1.3.1-beta.3 (Amine): "Turn off" / "Turn on", accent while any light is on (like a lit tile), plain once all are off
        bool allOff = LightsOffCount == 3;
        ZoneAll.Content = allOff ? "Turn on" : "Turn off";
        ZoneAll.SetResourceReference(TagProperty, allOff ? "Icon.Bulb" : "Icon.BulbOff");
        if (allOff)
        {
            ZoneAll.ClearValue(ForegroundProperty); ZoneAll.ClearValue(BackgroundProperty); ZoneAll.ClearValue(BorderBrushProperty);
        }
        else
        {
            ZoneAll.SetResourceReference(ForegroundProperty, "AccentBrush");
            ZoneAll.SetResourceReference(BackgroundProperty, "AccentFaint");
            ZoneAll.SetResourceReference(BorderBrushProperty, "AccentBrush");
        }
        _zoneUiBusy = false;
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
        bool off = LightsOffCount < 3;   // Turn off while any light is on; Turn on once all three are off
        bool half = _settings.LightsHalf;
        if (!off) _settings.LightsHalf = false;   // Turn on = full brightness
        await SetZonesAsync(off, off, off, half != _settings.LightsHalf);
    }

    private async Task SetZonesAsync(bool keyboard, bool ring, bool head, bool halfChanged = false)
    {
        await _zoneGate.WaitAsync();   // quick taps apply one after the other, never interleaved
        try
        {
            bool kbChanged = keyboard != _settings.OffKeyboard;
            bool chassisChanged = ring != _settings.OffRing || head != _settings.OffAlienhead || halfChanged;
            _settings.OffKeyboard = keyboard;
            _settings.OffRing = ring;
            _settings.OffAlienhead = head;
            _settings.Save();
            SyncZoneTiles();
            if (!_fx.IsAvailable) return;
            Log.Info("Lights", $"keyboard {(keyboard ? "off" : "on")}, rear ring {(ring ? "off" : "on")}, Alienhead {(head ? "off" : "on")}{(_settings.LightsHalf ? ", 50%" : "")}");
            if (_settings.LastEffect != 0 || !_lightsManaged)
            {
                // effects: the old full apply (the CLI owns the keyboard effect)
                if (kbChanged)
                {
                    if (keyboard) StopAnimation();
                    bool ok = await Task.Run(() => { try { return _fx.Hid.SetOff(keyboard); } catch (Exception ex) { Log.Warn("Lights.SetOff", ex); return false; } });
                    Log.Info("Lights", $"keyboard hardware brightness {(keyboard ? 0 : 255)} {(ok ? "ok" : "FAILED")}");
                    UpdateKbdTimer();
                }
                _f2Dirty = true;
                await ApplyLightingCoreAsync(_lastColor);   // 2.0.0-beta1.3: this method already holds the lighting gate
                if (kbChanged && !keyboard) await RefreshStatusKeysAsync();
                SelectSwatch(_lastColor);
                return;
            }
            // 1.3.2 (Amine: every change flashed): static color = touch only what changed. The keyboard goes over HID
            // alone; the chassis gets only its own setone lines. The CLI run can relight a dark keyboard for a moment,
            // so it runs while the keyboard is still lit (turning off) or before it lights up (turning on).
            // 1.3.2 fix (Amine): a chassis CLI run turns off the 5 white status keys, so a lit keyboard gets its frame
            // (status keys included, same colors, no visible change) again after it
            bool kbRepaint = !keyboard && (kbChanged || halfChanged || chassisChanged);
            if (kbChanged && keyboard)   // turning off: chassis first, then brightness 0
            {
                if (chassisChanged) await _fx.SetChassisAsync(_lastColor, LightBrightness);
                bool ok = await Task.Run(() => { try { return _fx.Hid.SetOff(true); } catch (Exception ex) { Log.Warn("Lights.SetOff", ex); return false; } });
                Log.Info("Lights", $"keyboard off (hardware brightness 0) {(ok ? "ok" : "FAILED")}");
            }
            else
            {
                if (chassisChanged) await _fx.SetChassisAsync(_lastColor, LightBrightness);
                if (kbRepaint)
                {
                    // turning on: the new frame is held while off and goes out with brightness 255, so the old colors
                    // (or full brightness for the 50% step) never show
                    ReadIndicatorStates();
                    await _fx.SetKeyboardAsync(_lastColor, _indicators, LightBrightness);
                }
                if (kbRepaint && !kbChanged) await RefreshStatusKeysAsync();   // the firmware can miss the first frame after the CLI
                if (kbChanged)
                {
                    bool ok = await Task.Run(() => { try { return _fx.Hid.SetOff(false); } catch (Exception ex) { Log.Warn("Lights.SetOff", ex); return false; } });
                    Log.Info("Lights", $"keyboard on (hardware brightness 255) {(ok ? "ok" : "FAILED")}");
                    await RefreshStatusKeysAsync();
                }
            }
            if (kbChanged) UpdateKbdTimer();
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
        bool ok = await _fx.SetKeyboardAsync(_lastColor, _indicators, LightBrightness);
        Log.Info("Lights", $"keyboard on: status keys re-sent {(ok ? "ok" : "FAILED")}");
    }

    // ---------- 1.3.1: Ctrl + Alt + F ----------
    // Amine: one shortcut for all three lights (keyboard, rear ring, Alienhead; never the power button), in F7's order:
    // full -> off -> 50% -> full. 50% = half of the app's brightness. The menu tiles follow; the step is saved.
    private long _cycleLast;

    private async Task CycleLightsAsync()
    {
        long now = Environment.TickCount64;
        if (now - _cycleLast < 200) return;
        _cycleLast = now;
        string title, icon;
        bool wasHalf = _settings.LightsHalf;
        if (LightsOffCount == 3) { _settings.LightsHalf = true; title = "Lights 50%"; icon = "Icon.Bulb"; }
        else if (_settings.LightsHalf) { _settings.LightsHalf = false; title = "Lights on"; icon = "Icon.Bulb"; }
        else { _settings.LightsHalf = false; title = "Lights off"; icon = "Icon.BulbOff"; }
        bool off = title == "Lights off";
        Log.Info("Lights", $"Ctrl+Alt+F: {title}");
        await SetZonesAsync(off, off, off, wasHalf != _settings.LightsHalf);
        if (_settings.Toasts)
            Toast.Show(true, new Toast.Info(CurrentProfile, icon, title, "Keyboard · rear ring · Alienhead", Toast.Right.None, !off,
                Accent: (Application.Current.Resources["AccentBrush"] as System.Windows.Media.SolidColorBrush)?.Color), Motion.Enabled);   // 1.3.5: laptop light colour
    }
}
