using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
                _frameDirty = true;
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
            Toast.Show(new Toast.Info(CurrentProfile, icon, title, "Keyboard · rear ring · Alienhead", Toast.Right.None, !off,
                Accent: (Application.Current.Resources["AccentBrush"] as System.Windows.Media.SolidColorBrush)?.Color), Motion.Enabled);   // 1.3.5: laptop light colour
    }

    // ---------- 2.0.0-beta1.4 (ui review, MainWindow split): moved here unchanged from MainWindow.xaml.cs ----------

    // ---------- status keys ----------

    private async Task StartupLightingAsync()
    {
        if (!_fx.IsAvailable) return;
        _gmodeOn = await StartupGModeAsync() == true;
        await _fx.ResetHardwareDimAsync();
        if (_settings.OffKeyboard) await Task.Run(() => _fx.Hid.SetOff(true));   // 1.3.1: before any frame goes out
        ReadIndicatorStates();
        // Reapply the user's color after boot (firmware resets to its default); otherwise leave firmware lighting alone.
        if (_settings.LastColor is { Length: 3 } c)
        {
            await _zoneGate.WaitAsync();   // 2.0.0-beta1.3: the one lighting gate (ui review #1)
            try
            {
                if (_settings.LastEffect >= 100) await ApplyLightingCoreAsync(c);
                else if (_settings.LastEffect == 0) await _fx.SetAllAsync(c[0], c[1], c[2], _indicators, LightBrightness);
                else await _fx.SetEffectAsync(_settings.LastEffect, c, LightBrightness);
                _lightsManaged = true;
            }
            finally { _zoneGate.Release(); }
        }
        _indicatorTimer.Start();
        // started to tray: trim once things have settled. 1.3.0-beta.2: 10 s (was 60 s), so the first-start peak
        // (~100 MB: theme, XAML, CLI + WMI reads) drops to a few MB right away instead of after a minute or more.
        if (!IsVisible) _ = TrimSoonAsync(_showGen, 10000);
    }

    /// <summary>Reads current states into _indicators; returns the keys whose state changed.</summary>
    private System.Collections.Generic.List<int> ReadIndicatorStates()
    {
        var changed = new System.Collections.Generic.List<int>();
        void Set(int key, bool? value)
        {
            if (value is not { } v) return;
            if (!_indicators.TryGetValue(key, out var old) || old != v) changed.Add(key);
            _indicators[key] = v;
        }
        Set(_settings.GModeKey, _gmodeOn);
        Set(_settings.SpeakerMuteKey, _watch?.SpeakerMuted() ?? SystemState.SpeakerMuted());
        Set(_settings.MicMuteKey, _watch?.MicMuted() ?? SystemState.MicMuted());
        Set(_settings.TouchpadKey, SystemState.TouchpadOff());
        if (changed.Count > 0 || _indicatorSnap is null)
        {
            var snap = new bool[Math.Max(1, _settings.KeyboardLightCount)];
            foreach (var (k, on) in _indicators) if (on && k >= 0 && k < snap.Length) snap[k] = true;
            _indicatorSnap = snap;
        }
        return changed;
    }

    private bool _indicatorDirty;

    /// <summary>2.0.0-beta1.3 (ui review #4): a call during a frame write marks it dirty and the running call syncs
    /// once more when it's done (a mute change used to wait for the 30 s safety timer).</summary>
    private async Task SyncIndicatorsAsync()
    {
        if (_indicatorBusy) { _indicatorDirty = true; return; }
        _indicatorBusy = true;
        try
        {
            do { _indicatorDirty = false; await SyncIndicatorsCoreAsync(); }
            while (_indicatorDirty);
        }
        finally { _indicatorBusy = false; }
    }

    private async Task SyncIndicatorsCoreAsync()
    {
        var changed = ReadIndicatorStates();
        if (!_lightsManaged || _settings.LastEffect != 0) return; // a static frame would stop the effect
        // One full frame: writing a single status key would blank the others.
        if (changed.Count > 0 || _frameDirty) { _frameDirty = false; await _fx.SetKeyboardAsync(_lastColor, _indicators, LightBrightness); }
    }

    private async void Color_Click(object sender, RoutedEventArgs e)
    {
        _lastColor = ((string)((FrameworkElement)sender).Tag).Split(',').Select(byte.Parse).ToArray();
        SelectSwatch(_lastColor);   // 2.0: Home and Lights swatches stay in step
        await ApplyLightingAsync(_lastColor);
    }

    private async Task ApplyColorAsync(byte[] c)
    {
        ReadIndicatorStates();
        Report(await _fx.SetAllAsync(c[0], c[1], c[2], _indicators, LightBrightness));
        _lightsManaged = true;
        ApplyAccent(c);
        _settings.LastColor = c;
        _settings.Save();
    }

    /// <summary>Applies the saved effect (Static = per-key frame with status keys) in the given color.
    /// 2.0.0-beta1.3 (ui review #1): through the one lighting gate (_zoneGate, the Lights tab's), so two applies can't
    /// overlap and start two animation loops. Code that already holds the gate (SetZonesAsync) calls the core.</summary>
    private async Task ApplyLightingAsync(byte[] c)
    {
        await _zoneGate.WaitAsync();
        try { await ApplyLightingCoreAsync(c); }
        finally { _zoneGate.Release(); }
    }

    /// <summary>The apply itself, ungated: only call it while holding _zoneGate (SemaphoreSlim isn't reentrant).</summary>
    private async Task ApplyLightingCoreAsync(byte[] c)
    {
        StopAnimation();
        if (_settings.LastEffect >= 100 && !c.All(v => v == 0))
        {
            Report(await _fx.SetChassisCycleAsync(_settings.LastEffect == 101));
            StartAnimation(_settings.LastEffect == 101);
            _lightsManaged = true;
            ApplyAccent(c);
            _settings.LastColor = c;
            _settings.Save();
            return;
        }
        if (_settings.LastEffect == 0 || c.All(v => v == 0)) { await ApplyColorAsync(c); return; }
        Report(await _fx.SetEffectAsync(_settings.LastEffect, c, LightBrightness));
        _lightsManaged = true;
        ApplyAccent(c);
        _settings.LastColor = c;
        _settings.Save();
    }

    private bool _effectBoxReady;

    // ---------- software effects (APIv5 has no spectrum, so the app animates the keys itself) ----------

    private System.Threading.CancellationTokenSource? _anim;

    private void StopAnimation()
    {
        _anim?.Cancel();
        _anim = null;
    }

    /// <summary>
    /// Spectrum: all keys cycle through the hues together. Rainbow wave: hues spread across the keys
    /// and scroll. Key IDs roughly follow rows, so the wave runs by ID until columns are mapped.
    /// Status keys stay white when on, because every frame includes them.
    /// </summary>
    private void StartAnimation(bool wave)
    {
        StopAnimation();   // 2.0.0-beta1.3 (ui review #1): never two loops; the old one used to keep running, unstoppable
        if (_settings.OffKeyboard) return;   // 1.3.1: keyboard turned off, nothing to animate (frames would be held anyway)
        var cts = new System.Threading.CancellationTokenSource();
        _anim = cts;
        var token = cts.Token;
        Task.Run(async () =>
        {
            // 2.0.0-beta1.4 (ui review #12): no per-frame dictionary copy and no byte[] per key; the colour function is
            // built once and reads the hue from a local (one frame array is reused in AlienFx.SendFrame)
            double hueBase = 0;
            Func<int, (byte R, byte G, byte B)> colorOf = wave
                ? i => Hsv(hueBase + KeyColumn(i) * 15)
                : _ => Hsv(hueBase);
            while (!token.IsCancellationRequested)
            {
                _fx.SendFrame(colorOf, _indicatorSnap, LightBrightness);
                hueBase = (hueBase + (wave ? 10 : 6)) % 360; // v0.6.3: about 1.7x faster (user found it slow)
                try { await Task.Delay(50, token); } catch { break; }
            }
        }, token);
    }

    /// <summary>Approximate column of a key: IDs run left to right in rows of about 23.</summary>
    private static int KeyColumn(int id) => id % 23;

    private async void Effect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_effectBoxReady || EffectBox.SelectedItem is not ComboBoxItem item) return;
        _settings.LastEffect = int.Parse((string)item.Tag);
        if (_lastColor.All(v => v == 0)) _lastColor = new byte[] { 0, 229, 255 };
        await ApplyLightingAsync(_lastColor);
    }

    private void SelectEffectInBox()
    {
        foreach (ComboBoxItem it in EffectBox.Items)
            if ((string)it.Tag == _settings.LastEffect.ToString()) { EffectBox.SelectedItem = it; break; }
        if (EffectBox.SelectedItem is null) EffectBox.SelectedIndex = 0;
        _effectBoxReady = true;
    }

    // ---------- custom color (hue + saturation at full value; brightness is the slider) ----------

    private void CustomSwatch_Click(object sender, RoutedEventArgs e)
    {
        SelectSwatch(_lastColor);
        if (!_lastColor.All(v => v == 0)) _custom = (byte[])_lastColor.Clone();
        UpdateCustomPreview();
        CustomPopup.PlacementTarget = sender as UIElement ?? CustomSwatch;   // 2.0: Home or Lights rainbow swatch
        CustomPopup.IsOpen = true;
    }

    private bool _syncingRgb;
    private byte[] _custom = { 0, 229, 255 };

    // Sliders -> color and boxes.
    private void CustomColor_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingRgb || HueSlider is null || SatSlider is null) return;
        _custom = HsvToRgb(HueSlider.Value, SatSlider.Value / 100.0);
        UpdateCustomPreview();
    }

    // Typed R, G, B (0..255) -> color.
    private void Rgb_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingRgb || RBox is null || GBox is null || BBox is null) return;
        if (byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b))
        {
            _custom = new[] { r, g, b };
            if (CustomPreview is not null) CustomPreview.Background = Freeze(new SolidColorBrush(Color.FromRgb(r, g, b)));
        }
    }

    private void UpdateCustomPreview()
    {
        if (CustomPreview is null || RBox is null) return;
        var c = _custom;
        CustomPreview.Background = Freeze(new SolidColorBrush(Color.FromRgb(c[0], c[1], c[2])));
        _syncingRgb = true;
        RBox.Text = c[0].ToString(); GBox.Text = c[1].ToString(); BBox.Text = c[2].ToString();
        _syncingRgb = false;
    }

    private async void CustomApply_Click(object sender, RoutedEventArgs e)
    {
        CustomPopup.IsOpen = false;
        _lastColor = (byte[])_custom.Clone();
        SelectSwatch(_lastColor);
        await ApplyLightingAsync(_lastColor);
    }

    private static byte[] HsvToRgb(double h, double s)
    {
        var (r, g, b) = HsvToRgbT(h, s);
        return new[] { r, g, b };
    }

    /// <summary>Full saturation and value, as a struct (the animation's per-key colour).</summary>
    private static (byte R, byte G, byte B) Hsv(double h) => HsvToRgbT(h, 1);

    private static (byte R, byte G, byte B) HsvToRgbT(double h, double s)
    {
        h = (h % 360 + 360) % 360;
        double c = s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = 1 - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private bool _brightSync;

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 2.0: the Home and Lights sliders are one setting; moving one moves the other
        if (!_brightSync && BrightnessSlider is not null && HomeBrightnessSlider is not null)
        {
            _brightSync = true;
            if (sender == BrightnessSlider) HomeBrightnessSlider.Value = e.NewValue; else BrightnessSlider.Value = e.NewValue;
            _brightSync = false;
        }
        UpdateBrightnessText((int)e.NewValue);
    }

    private void UpdateBrightnessText(int v)
    {
        if (BrightnessValue is null || HomeBrightnessValue is null || HomeBrightnessHint is null) return;
        BrightnessValue.Text = HomeBrightnessValue.Text = HomeBrightnessHint.Text = $"{v}%";
    }

    private async void Brightness_Commit(object sender, MouseButtonEventArgs e) => await CommitBrightnessAsync(sender as Slider);

    /// <summary>2.0.0-beta1.4 (ui review #11): arrow keys / Page Up / Page Down on a focused slider commit too (they
    /// moved the slider but never saved or repainted).</summary>
    private async void Brightness_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
            await CommitBrightnessAsync(sender as Slider);
    }

    private async Task CommitBrightnessAsync(Slider? slider)
    {
        int level = (int)Math.Round((slider ?? BrightnessSlider).Value * 255 / 100);
        if (level == _settings.Brightness) return;   // 2.0.0-beta1.4: a click without a change no longer repaints everything
        _settings.Brightness = level;
        _settings.Save();
        // Software brightness: repaint with scaled colors (only once the app manages the lights).
        if (_lightsManaged) await ApplyLightingAsync(_lastColor);
    }
}
