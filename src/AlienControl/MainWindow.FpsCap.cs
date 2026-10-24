using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// beta1.3-ov.11: the FPS CAP card on the Overlay tab (Amine picked preset chips + "···" custom, mockup v14).
/// The cap and V-Sync are the NVIDIA driver's own (NvDrs, per game profile, nothing injected, nothing left running).
/// Which game: the exe the overlay last saw presenting in the foreground (FramesSource.TargetChanged), remembered in
/// settings so the card still knows it with the overlay off. Driver values are read when the tab opens or the game
/// changes, written on a click; the driver applies them at the game's next launch.
/// </summary>
public partial class MainWindow
{
    private bool _capBusy, _capSyncing;
    private int _capFps;             // what the driver has now for the shown scope (0 = off)
    private bool _capCustomMode;
    private readonly DispatcherTimer _capCustomSave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>Not games: never become "this game" (browsers, launchers, the shell).</summary>
    private static readonly string[] CapSkip =
    {
        "explorer.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "discord.exe", "steam.exe",
        "steamwebhelper.exe", "epicgameslauncher.exe", "applicationframehost.exe", "searchhost.exe", "shellexperiencehost.exe",
        "startmenuexperiencehost.exe", "textinputhost.exe", "nvidia app.exe", "nvcplui.exe", "code.exe", "devenv.exe",
    };

    private void InitFpsCap()
    {
        _capCustomSave.Tick += async (_, _) => { _capCustomSave.Stop(); await WriteCapAsync(_settings.FpsCapCustom, null); };
        (_settings.FpsCapAll ? OvCapAll : OvCapGame).IsChecked = true;
        OvCapValue.Text = $"{_settings.FpsCapCustom} FPS";
        PaintCapSub();
    }

    private string? CapExe => _settings.FpsCapAll ? null : _settings.FpsCapGame;
    private string CapGameName => _settings.FpsCapTitle is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(_settings.FpsCapGame ?? "");

    /// <summary>The overlay saw a new foreground game: remember it (exe + product name) and refresh the card if shown.</summary>
    private void NoteCapGame(string exe, int pid)
    {
        if (string.IsNullOrWhiteSpace(exe) || CapSkip.Contains(exe.ToLowerInvariant())) return;
        if (string.Equals(exe, _settings.FpsCapGame, StringComparison.OrdinalIgnoreCase)) return;
        string title = Path.GetFileNameWithoutExtension(exe);
        try
        {
            using var p = Process.GetProcessById(pid);
            var vi = p.MainModule?.FileVersionInfo;
            var name = vi?.ProductName?.Trim();
            if (!string.IsNullOrEmpty(name) && name.Length <= 40) title = name;
        }
        catch (Exception) { /* protected by an anti cheat: the exe name will do */ }
        _settings.FpsCapGame = exe;
        _settings.FpsCapTitle = title;
        _settings.Save();
        Log.Info("FpsCap", $"game: {exe} ({title})");
        if (OverlayPage.IsVisible) _ = RefreshFpsCapAsync();
        else PaintCapSub();
    }

    /// <summary>Reads the driver's cap and V-Sync for the shown scope and paints the chips.</summary>
    private async Task RefreshFpsCapAsync()
    {
        if (_capBusy) return;
        if (CapExe is null && !_settings.FpsCapAll) { PaintCap(0, false, null); return; }
        _capBusy = true;
        try
        {
            var exe = CapExe;
            var (st, err) = await Task.Run(() => NvDrs.Read(exe));
            if (exe != CapExe) return;   // scope or game changed meanwhile: the next refresh paints it
            PaintCap(err is null ? st.Fps : 0, err is null && st.Vsync, err);
        }
        finally { _capBusy = false; }
    }

    private int CapGsync()
    {
        var s = DisplayRefresh.Read();
        int hz = s is null ? 240 : s.AvailableHz.Count > 0 ? s.AvailableHz.Max() : s.CurrentHz;
        return NvDrs.GsyncCap(hz);
    }

    /// <summary>Chips, custom box, V-Sync toggle and the sub line from the driver's values.</summary>
    private void PaintCap(int fps, bool vsync, string? err)
    {
        _capSyncing = true;
        try
        {
            _capFps = fps;
            int gs = CapGsync();
            bool noGame = !_settings.FpsCapAll && string.IsNullOrEmpty(_settings.FpsCapGame);
            bool ok = err is null && !noGame && NvDrs.Available;
            OvCapChips.IsEnabled = OvCapCustom.IsEnabled = OvCapVsync.IsEnabled = ok;
            OvCapCard.Opacity = ok ? 1 : 0.6;
            RadioButton? chip = fps switch { 0 => OvCapOff, 60 => OvCap60, 120 => OvCap120, 144 => OvCap144, _ when fps == gs => OvCapGsync, _ => null };
            _capCustomMode = ok && chip is null;
            if (_capCustomMode) { _settings.FpsCapCustom = fps; OvCapValue.Text = $"{fps} FPS"; }
            foreach (var rb in new[] { OvCapOff, OvCap60, OvCap120, OvCap144, OvCapGsync, OvCapMore }) rb.IsChecked = rb == chip;
            ShowCapCustom(_capCustomMode);
            OvCapVsync.IsChecked = vsync;
            PaintCapSub(err);
        }
        finally { _capSyncing = false; }
    }

    private void ShowCapCustom(bool on)
    {
        OvCapChips.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        OvCapCustom.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (OvCapCustom.Children[0] is Border { Child: System.Windows.Controls.Primitives.UniformGrid g })
            foreach (RadioButton rb in g.Children) rb.IsChecked = false;
    }

    private void PaintCapSub(string? err = null)
    {
        if (!NvDrs.Available) { OvCapSub.Text = "Needs an NVIDIA GPU and driver"; return; }
        if (err is not null) { OvCapSub.Text = err; return; }
        string who = _settings.FpsCapAll ? "All games" : CapGameName;
        if (!_settings.FpsCapAll && string.IsNullOrEmpty(_settings.FpsCapGame)) { OvCapSub.Text = "Play a game with the overlay on once"; return; }
        OvCapSub.Text = _capFps > 0 ? $"{who} · {_capFps} FPS" : $"Off: {who} runs uncapped";
        OvCapSub.ToolTip = _settings.FpsCapAll ? "NVIDIA's global profile. A game with its own cap keeps it"
            : $"{_settings.FpsCapGame}: its NVIDIA profile. Applies the next time the game starts";
    }

    private async void OvCapScope_Click(object sender, RoutedEventArgs e)
    {
        _settings.FpsCapAll = OvCapAll.IsChecked == true;
        _settings.Save();
        await RefreshFpsCapAsync();
    }

    private async void OvCapChip_Click(object sender, RoutedEventArgs e)
    {
        if (_capSyncing || sender is not RadioButton { Tag: var t }) return;
        switch (t?.ToString())
        {
            case "custom":
                _capCustomMode = true;
                ShowCapCustom(true);
                await WriteCapAsync(_settings.FpsCapCustom, null);
                break;
            case "gsync":
                // the G-SYNC preset is the Blur Busters setup: cap under the refresh + driver V-Sync on
                await WriteCapAsync(CapGsync(), OvCapVsync.IsChecked == true ? null : true);
                break;
            default:
                if (int.TryParse(t?.ToString(), out int fps)) await WriteCapAsync(fps, null);
                break;
        }
    }

    private void OvCapStep_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: var t } || !int.TryParse(t?.ToString(), out int dir)) return;
        StepCap(dir * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1));
        e.Handled = true;
    }

    private void OvCapWheel(object sender, MouseWheelEventArgs e)
    {
        StepCap(Math.Sign(e.Delta) * (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1));
        e.Handled = true;
    }

    /// <summary>Custom value: shown at once, written 0.7 s after the last step (one driver write per burst).</summary>
    private void StepCap(int delta)
    {
        if (!OvCapCustom.IsEnabled) return;
        _settings.FpsCapCustom = Math.Clamp(_settings.FpsCapCustom + delta, NvDrs.MinFps, NvDrs.MaxFps);
        OvCapValue.Text = $"{_settings.FpsCapCustom} FPS";
        _capCustomSave.Stop();
        _capCustomSave.Start();
    }

    private async void OvCapVsync_Click(object sender, RoutedEventArgs e)
    {
        if (_capSyncing) return;
        await WriteCapAsync(null, OvCapVsync.IsChecked == true);
    }

    /// <summary>Writes to the driver off the UI thread, then re-reads so the card shows what the driver really has.</summary>
    private async Task WriteCapAsync(int? fps, bool? vsync)
    {
        var exe = CapExe;
        if (exe is null && !_settings.FpsCapAll) return;
        _settings.Save();
        var err = await Task.Run(() => NvDrs.Write(exe, fps, vsync));
        if (err is not null) { Status("FPS cap: " + err); PaintCapSub(err); return; }
        await RefreshFpsCapAsync();
        // the driver reads the profile at launch: say so when the game is running now
        bool running = false;
        if (exe is not null)
        {
            try { var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); running = ps.Length > 0; foreach (var p in ps) p.Dispose(); }
            catch (Exception) { /* can't tell */ }
        }
        string what = fps is int f ? (f <= 0 ? "Cap off" : $"Cap {f} FPS") : vsync == true ? "Driver V-Sync on" : "Driver V-Sync back to default";
        string who = _settings.FpsCapAll ? "all games" : CapGameName;
        Status(running ? $"{what} for {who}. Restart the game to apply" : $"{what} for {who}. Applies next launch");
    }
}
