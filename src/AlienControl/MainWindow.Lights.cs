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
/// 1.3.1 Lighting > Off menu. The Off button opens a small popup with four tiles: Keyboard, Rear ring, Alienhead
/// and All off. Any mix can be off; a lit tile = that light is off, and each tap applies at once.
/// The keyboard goes off through its hardware brightness (colors kept, frames held, see KeyboardHid.SetOff), so the
/// firmware F7 brightness key can still bring it back. The chassis lights get black (AlienFx.Chassis); the power
/// button is never sent. The user's color is never overwritten with black any more.
/// </summary>
public partial class MainWindow
{
    private const double LightsOffPopupWidth = 368;
    private readonly SemaphoreSlim _zoneGate = new(1, 1);
    private bool _zoneUiBusy;

    private int LightsOffCount => (_settings.OffKeyboard ? 1 : 0) + (_settings.OffRing ? 1 : 0) + (_settings.OffAlienhead ? 1 : 0);

    private void LightsOff_Click(object sender, RoutedEventArgs e)
    {
        if (LightsOffPopup.IsOpen) { LightsOffPopup.IsOpen = false; return; }
        SyncZoneTiles();
        LightsOffPopup.HorizontalOffset = LightsOffButton.ActualWidth - LightsOffPopupWidth;   // right edges line up
        LightsOffPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => ZoneKeyboard.Focus()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void LightsOffPanel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        LightsOffPopup.IsOpen = false;
        LightsOffButton.Focus();
        e.Handled = true;
    }

    /// <summary>Tiles and the Off button follow the saved switches.</summary>
    private void SyncZoneTiles()
    {
        _zoneUiBusy = true;
        ZoneKeyboard.IsChecked = _settings.OffKeyboard;
        ZoneRing.IsChecked = _settings.OffRing;
        ZoneHead.IsChecked = _settings.OffAlienhead;
        ZoneAll.IsChecked = LightsOffCount == 3;
        _zoneUiBusy = false;
        UpdateLightsOffButton();
    }

    /// <summary>"Off" when everything is on, "2 off" (accent) when some lights are off.</summary>
    private void UpdateLightsOffButton()
    {
        int n = LightsOffCount;
        LightsOffText.Text = n == 0 ? "Off" : $"{n} off";
        if (n == 0) LightsOffButton.ClearValue(ForegroundProperty);
        else LightsOffButton.SetResourceReference(ForegroundProperty, "AccentBrush");
        LightsOffButton.ToolTip = n == 0 ? "Turn lights off" : "Lights that are off: " + string.Join(", ",
            new[] { _settings.OffKeyboard ? "keyboard" : null, _settings.OffRing ? "rear ring" : null, _settings.OffAlienhead ? "Alienhead" : null }
            .Where(s => s is not null));
    }

    private async void Zone_Click(object sender, RoutedEventArgs e)
    {
        if (_zoneUiBusy) return;
        if (sender == ZoneAll)
        {
            bool all = LightsOffCount < 3;   // All off: everything off, or everything back on when all three were off
            await SetZonesAsync(all, all, all);
        }
        else await SetZonesAsync(ZoneKeyboard.IsChecked == true, ZoneRing.IsChecked == true, ZoneHead.IsChecked == true);
    }

    private async void ZonesAllOn_Click(object sender, RoutedEventArgs e) => await SetZonesAsync(false, false, false);

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
            _f2Dirty = true;
            await ApplyLightingAsync(_lastColor);   // chassis black / color, keyboard frame (held while off), effect restart
            SelectSwatch(_lastColor);
        }
        finally { _zoneGate.Release(); }
    }
}
