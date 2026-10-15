using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// v0.9.7 GPU overclock row (plan OK'd by Amine 2026-10-15). Offsets only run in G-Mode on AC:
/// Apply writes them and starts a 20 s keep-or-revert, Keep saves them, and from then on entering G-Mode on AC
/// applies them and leaving G-Mode, unplugging or quitting the app puts +0 back. No stress test, no voltage,
/// power limit or fan controls, and profiles never touch the GPU mode.
/// </summary>
public partial class MainWindow
{
    private const int OcTestSeconds = 20;
    private readonly DispatcherTimer _ocTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _ocLeft;
    private bool _ocTesting, _ocBusy, _ocInit;
    /// <summary>What the app last wrote to the driver; null = unknown (startup, after resume).</summary>
    private (int Core, int Mem)? _ocApplied;
    private string? _ocError;

    private static bool OnAc =>
        System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;

    private bool OcAllowed => _gmodeOn && OnAc;

    /// <summary>Called once from the constructor.</summary>
    private void InitOc()
    {
        OcCoreSlider.Value = Math.Clamp(_settings.OcCore, 0, GpuOc.MaxUi);
        OcMemSlider.Value = Math.Clamp(_settings.OcMem, 0, GpuOc.MaxUi);
        _ocInit = true;
        _ocTimer.Tick += (_, _) =>
        {
            _ocLeft--;
            if (_ocLeft <= 0) _ = EndOcTestAsync(keep: false);
            else ShowOcCountdown();
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            {
                _ocApplied = null;                 // a sleep can reset the driver: write again once it has settled
                await Task.Delay(3000);
            }
            if (e.Mode != Microsoft.Win32.PowerModes.Suspend) await SyncOcAsync();
        }));
        UpdateOcUi();
        Dispatcher.BeginInvoke(new Action(async () => await StartupOcAsync()));
    }

    private async Task StartupOcAsync()
    {
        if (_settings.OcPending)
        {
            // the app (or the driver) died during a test: never come back with offsets nobody kept
            _settings.OcPending = false;
            _settings.OcOn = false;
            _settings.Save();
            var err = await Task.Run(() => GpuOc.Set(0, 0));
            _ocApplied = err is null ? (0, 0) : null;
            Status(err ?? "GPU OC: the last test wasn't kept, clocks are back to stock.");
        }
        else if (!_settings.OcOn) _ocApplied = (0, 0);   // nothing was ever kept: the driver is at stock after boot
        _gmodeOn = await _fan.GetGModeAsync() == true;
        await SyncOcAsync();
    }

    /// <summary>Brings the driver in line with G-Mode + AC + the kept offsets. Cheap when nothing changes.</summary>
    private async Task SyncOcAsync()
    {
        if (!Dispatcher.CheckAccess()) { await Dispatcher.InvokeAsync(SyncOcAsync).Task.Unwrap(); return; }
        if (_ocTesting)
        {
            if (!OcAllowed) await EndOcTestAsync(keep: false);
            UpdateOcUi();
            return;
        }
        var want = _settings.OcOn && OcAllowed ? (Math.Clamp(_settings.OcCore, 0, GpuOc.MaxUi), Math.Clamp(_settings.OcMem, 0, GpuOc.MaxUi)) : (0, 0);
        if (_ocApplied != want && !_ocBusy)
        {
            _ocBusy = true;
            try
            {
                var err = await Task.Run(() => GpuOc.Set(want.Item1, want.Item2));
                _ocError = err;
                _ocApplied = err is null ? want : null;
                if (err is not null) Status(err);
            }
            finally { _ocBusy = false; }
        }
        UpdateOcUi();
    }

    /// <summary>Synchronous +0 for Quit (NVML takes a few ms).</summary>
    private void ResetOcOnExit()
    {
        _ocTimer.Stop();
        if (_ocTesting) { _settings.OcPending = false; _settings.Save(); }
        if (_ocApplied is (0, 0)) return;
        try { GpuOc.Set(0, 0); } catch { /* exiting anyway */ }
    }

    private void Oc_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OcCoreValue is null || OcMemValue is null) return;   // fires during InitializeComponent
        UpdateOcUi();
    }

    private async void OcApply_Click(object sender, RoutedEventArgs e)
    {
        if (_ocTesting) { await EndOcTestAsync(keep: true); return; }   // the button reads Keep during a test
        if (!OcAllowed || _ocBusy) return;
        int core = (int)OcCoreSlider.Value, mem = (int)OcMemSlider.Value;
        _ocBusy = true;
        try
        {
            // saved BEFORE the write: if anything dies from here on, the next launch goes back to +0
            _settings.OcPending = true;
            _settings.Save();
            var err = await Task.Run(() => GpuOc.Set(core, mem));
            if (err is not null)
            {
                _settings.OcPending = false;
                _settings.Save();
                _ocApplied = null;
                _ocError = err;
                Status(err);
                UpdateOcUi();
                return;
            }
            _ocError = null;
            _ocApplied = (core, mem);
            _ocTesting = true;
            _ocLeft = OcTestSeconds;
            _ocTimer.Start();
            UpdateOcUi();
        }
        finally { _ocBusy = false; }
    }

    private async void OcReset_Click(object sender, RoutedEventArgs e)
    {
        if (_ocTesting) { await EndOcTestAsync(keep: false); return; }   // the button reads Revert during a test
        _settings.OcOn = false;
        _settings.Save();
        await SyncOcAsync();
    }

    private async Task EndOcTestAsync(bool keep)
    {
        if (!_ocTesting) return;
        _ocTimer.Stop();
        _ocTesting = false;
        if (keep && _ocApplied is { } a && OcAllowed)
        {
            _settings.OcCore = a.Core;
            _settings.OcMem = a.Mem;
            _settings.OcOn = true;
        }
        else _settings.OcOn = false;
        _settings.OcPending = false;
        _settings.Save();
        await SyncOcAsync();   // Revert / timeout / left G-Mode: writes +0
    }

    private void ShowOcCountdown() =>
        OcCountdown.Text = IsLegend ? $"back to +0 in {_ocLeft} s" : $"Back to +0 in {_ocLeft} s";

    private void UpdateOcUi()
    {
        if (!_ocInit) return;
        int core = (int)OcCoreSlider.Value, mem = (int)OcMemSlider.Value;
        OcCoreValue.Text = $"+{core} MHz";
        OcMemValue.Text = $"+{mem} MHz";

        bool allowed = OcAllowed;
        OcBody.IsEnabled = allowed && !_ocTesting;
        OcBody.Opacity = allowed ? 1 : 0.45;
        OcApplyButton.IsEnabled = allowed;
        OcResetButton.IsEnabled = _ocTesting || _settings.OcOn || _ocApplied is not (0, 0);

        OcApplyButton.Content = _ocTesting ? "Keep" : "Apply";
        OcResetButton.Content = _ocTesting ? "Revert" : "Reset";
        OcConfirm.Visibility = _ocTesting ? Visibility.Visible : Visibility.Collapsed;
        OcActive.Visibility = _ocTesting ? Visibility.Collapsed : Visibility.Visible;
        if (_ocTesting) ShowOcCountdown();

        string active = _ocApplied is { } a && a != (0, 0) ? $"+{a.Core} / +{a.Mem}" : "+0 / +0";
        OcActive.Text = IsLegend ? $"ACTIVE {active}" : $"Active: {active}";

        if (!_gmodeOn) SetHint(OcHint, "G-Mode only");
        else if (!OnAc) SetHint(OcHint, "Plug in to use");
        else if (_ocError is not null) SetHint(OcHint, "Driver error");
        else if (_settings.OcOn) SetHint(OcHint, "On in G-Mode");
        else SetHint(OcHint, "Off");
    }
}
