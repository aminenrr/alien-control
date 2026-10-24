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
/// changes. ov.13: picks are staged and APPLY writes them (Amine), the sub line names the NVIDIA profile and the exe,
/// and when the game starts again the profile is checked so a cap something else removed (NVIDIA app) is reported.
/// </summary>
public partial class MainWindow
{
    private bool _capBusy, _capSyncing;
    private int _capFps;             // what the driver has now for the shown scope (0 = off)
    private bool _capVsync;
    private int? _capPendFps;        // ov.13: staged by the chips / stepper, written by APPLY
    private bool? _capPendVsync;
    private bool _capCustomMode;
    private NvDrs.ProfileInfo _capProfile;
    private (string Exe, int Fps, bool Vsync)? _capApplied;   // last APPLY, checked when that game starts again

    /// <summary>Not games: never become "this game" (browsers, launchers, the shell).</summary>
    private static readonly string[] CapSkip =
    {
        "explorer.exe", "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "discord.exe", "steam.exe",
        "steamwebhelper.exe", "epicgameslauncher.exe", "applicationframehost.exe", "searchhost.exe", "shellexperiencehost.exe",
        "startmenuexperiencehost.exe", "textinputhost.exe", "nvidia app.exe", "nvcplui.exe", "code.exe", "devenv.exe",
    };

    private void InitFpsCap()
    {
        (_settings.FpsCapAll ? OvCapAll : OvCapGame).IsChecked = true;
        OvCapValue.Text = $"{_settings.FpsCapCustom} FPS";
        PaintCapSub();
    }

    private string? CapExe => _settings.FpsCapAll ? null : _settings.FpsCapGame;
    private string CapGameName => _settings.FpsCapTitle is { Length: > 0 } t ? t : Path.GetFileNameWithoutExtension(_settings.FpsCapGame ?? "");

    /// <summary>The overlay saw a foreground game: remember it (exe + product name) and refresh the card if shown.
    /// The same game again (restarted) checks that the last APPLY is still in its profile.</summary>
    private void NoteCapGame(string exe, int pid)
    {
        if (string.IsNullOrWhiteSpace(exe) || CapSkip.Contains(exe.ToLowerInvariant())) return;
        if (_capApplied is { } ap && string.Equals(ap.Exe, exe, StringComparison.OrdinalIgnoreCase)) _ = CheckCapKeptAsync(ap);
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

    /// <summary>ov.13: the game started again after an APPLY: is the cap still in its profile? If not, something rewrote
    /// the driver profiles (the NVIDIA app does that), say so instead of failing silently.</summary>
    private async Task CheckCapKeptAsync((string Exe, int Fps, bool Vsync) ap)
    {
        _capApplied = null;
        var (st, pi, err) = await Task.Run(() => NvDrs.ReadFull(ap.Exe));
        if (err is not null) return;
        bool kept = st.Fps == ap.Fps && st.Vsync == ap.Vsync;
        Log.Info("FpsCap", $"{ap.Exe} started: profile \"{pi.Name ?? "base"}\" has cap {st.Fps} vsync {(st.Vsync ? "on" : "default")}, applied {ap.Fps} / {(ap.Vsync ? "on" : "default")}: {(kept ? "kept" : "CHANGED")}");
        if (!kept) Status(NvidiaAppOpen() ? "The NVIDIA app changed the cap back. Set it there, or quit the NVIDIA app and apply again"
                                          : "Something changed the cap back in the NVIDIA profile. Apply again");
        if (OverlayPage.IsVisible) _ = RefreshFpsCapAsync();
    }

    private static bool NvidiaAppOpen()
    {
        try
        {
            var ps = Process.GetProcessesByName("NVIDIA app");
            bool open = ps.Length > 0;
            foreach (var p in ps) p.Dispose();
            return open;
        }
        catch (Exception) { return false; }
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
            var (st, pi, err) = await Task.Run(() => NvDrs.ReadFull(exe));
            if (exe != CapExe) return;   // scope or game changed meanwhile: the next refresh paints it
            _capProfile = err is null && st.Own ? pi : default;
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

    /// <summary>Chips, custom box, V-Sync toggle and the sub line from the driver's values (drops anything staged).</summary>
    private void PaintCap(int fps, bool vsync, string? err)
    {
        _capFps = fps;
        _capVsync = vsync;
        _capPendFps = null;
        _capPendVsync = null;
        _capCustomMode = false;
        bool noGame = !_settings.FpsCapAll && string.IsNullOrEmpty(_settings.FpsCapGame);
        bool ok = err is null && !noGame && NvDrs.Available;
        OvCapChips.IsEnabled = OvCapCustom.IsEnabled = OvCapVsync.IsEnabled = ok;
        OvCapCard.Opacity = ok ? 1 : 0.6;
        ShowCapPick(fps, vsync);
        PaintCapSub(err);
    }

    /// <summary>Lights the chip (or the custom box) and the toggle for a value, driver or staged.</summary>
    private void ShowCapPick(int fps, bool vsync)
    {
        _capSyncing = true;
        try
        {
            int gs = CapGsync();
            RadioButton? chip = fps switch { 0 => OvCapOff, 60 => OvCap60, 120 => OvCap120, 144 => OvCap144, _ when fps == gs => OvCapGsync, _ => null };
            bool custom = _capCustomMode || chip is null;
            if (chip is null) { _settings.FpsCapCustom = fps; OvCapValue.Text = $"{fps} FPS"; }
            foreach (var rb in new[] { OvCapOff, OvCap60, OvCap120, OvCap144, OvCapGsync, OvCapMore }) rb.IsChecked = !custom && rb == chip;
            ShowCapCustom(custom);
            OvCapVsync.IsChecked = vsync;
        }
        finally { _capSyncing = false; }
        PaintCapApply();
    }

    private void ShowCapCustom(bool on)
    {
        OvCapChips.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        OvCapCustom.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (OvCapCustom.Children[0] is Border { Child: System.Windows.Controls.Primitives.UniformGrid g })
            foreach (RadioButton rb in g.Children) rb.IsChecked = false;
    }

    private bool CapPending => (_capPendFps is int f && f != _capFps) || (_capPendVsync is bool v && v != _capVsync);

    private void PaintCapApply()
    {
        bool pend = CapPending && OvCapChips.IsEnabled;
        OvCapApply.IsEnabled = pend;
        OvCapApply.Content = pend ? "APPLY" : "SAVED";
    }

    /// <summary>ov.13 (Amine): the NVIDIA profile's name when the game has one, else the exe, and always the exe it targets.</summary>
    private void PaintCapSub(string? err = null)
    {
        if (!NvDrs.Available) { OvCapSub.Text = "Needs an NVIDIA GPU and driver"; return; }
        if (err is not null) { OvCapSub.Text = err; return; }
        if (_settings.FpsCapAll)
        {
            OvCapSub.Text = _capFps > 0 ? $"Global profile · {_capFps} FPS" : "Global profile · no cap";
            OvCapSub.ToolTip = "NVIDIA's global profile. A game with its own cap keeps it";
            return;
        }
        var exe = _settings.FpsCapGame;
        if (string.IsNullOrEmpty(exe)) { OvCapSub.Text = "Play a game with the overlay on once"; OvCapSub.ToolTip = null; return; }
        var pi = _capProfile;
        OvCapSub.Text = pi.Name is { } n ? $"{n} · {exe}" : exe;
        OvCapSub.ToolTip = (pi.Name is { } pn ? $"NVIDIA profile: {pn}{(pi.Predefined ? " (made by NVIDIA)" : "")}" : "No NVIDIA profile yet, APPLY makes one")
            + $"\nTargets: {exe}" + (pi.FileInFolder is { } ff ? $"\nNVIDIA only uses this profile when {ff} is in the game folder" : "")
            + "\nApplies the next time the game starts";
    }

    private async void OvCapScope_Click(object sender, RoutedEventArgs e)
    {
        _settings.FpsCapAll = OvCapAll.IsChecked == true;
        _settings.Save();
        _capCustomMode = false;
        await RefreshFpsCapAsync();
    }

    private void OvCapChip_Click(object sender, RoutedEventArgs e)
    {
        if (_capSyncing || sender is not RadioButton { Tag: var t }) return;
        switch (t?.ToString())
        {
            case "custom":
                _capCustomMode = true;
                _capPendFps = _settings.FpsCapCustom;
                break;
            case "gsync":
                // the G-SYNC preset is the Blur Busters setup: cap under the refresh + driver V-Sync on
                _capCustomMode = false;
                _capPendFps = CapGsync();
                _capPendVsync = true;
                break;
            default:
                if (!int.TryParse(t?.ToString(), out int fps)) return;
                _capCustomMode = false;
                _capPendFps = fps;
                break;
        }
        ShowCapPick(_capPendFps ?? _capFps, _capPendVsync ?? _capVsync);
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

    /// <summary>Custom value: staged, APPLY writes it.</summary>
    private void StepCap(int delta)
    {
        if (!OvCapCustom.IsEnabled) return;
        _settings.FpsCapCustom = Math.Clamp(_settings.FpsCapCustom + delta, NvDrs.MinFps, NvDrs.MaxFps);
        OvCapValue.Text = $"{_settings.FpsCapCustom} FPS";
        _capPendFps = _settings.FpsCapCustom;
        PaintCapApply();
    }

    private void OvCapVsync_Click(object sender, RoutedEventArgs e)
    {
        if (_capSyncing) return;
        _capPendVsync = OvCapVsync.IsChecked == true;
        PaintCapApply();
    }

    private async void OvCapApply_Click(object sender, RoutedEventArgs e)
    {
        if (!CapPending) return;
        OvCapApply.IsEnabled = false;
        int? fps = _capPendFps is int f && f != _capFps ? f : null;
        bool? vs = _capPendVsync is bool v && v != _capVsync ? v : null;
        await WriteCapAsync(fps, vs);
    }

    /// <summary>Writes to the driver off the UI thread (NvDrs reads it back), then re-reads so the card shows what the
    /// driver really has.</summary>
    private async Task WriteCapAsync(int? fps, bool? vsync)
    {
        var exe = CapExe;
        if (exe is null && !_settings.FpsCapAll) return;
        _settings.Save();
        var err = await Task.Run(() => NvDrs.Write(exe, fps, vsync));
        _capCustomMode = false;
        if (err is not null) { Status("FPS cap: " + err); await RefreshFpsCapAsync(); PaintCapSub(err); return; }
        await RefreshFpsCapAsync();
        if (exe is not null) _capApplied = (exe, _capFps, _capVsync);
        // the driver reads the profile at launch: say so when the game is running now
        bool running = false;
        if (exe is not null)
        {
            try { var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)); running = ps.Length > 0; foreach (var p in ps) p.Dispose(); }
            catch (Exception) { /* can't tell */ }
        }
        string what = _capFps > 0 ? $"Cap {_capFps} FPS" : "No cap";
        if (vsync is not null) what += _capVsync ? ", V-Sync on" : ", V-Sync default";
        string where = _settings.FpsCapAll ? "the global profile" : _capProfile.Name is { } n ? $"profile \"{n}\"" : exe!;
        string tail = running ? "Restart the game to apply" : "Applies next launch";
        if (NvidiaAppOpen()) tail += ". NVIDIA app is open and may undo it";
        Status($"{what} saved in {where}. {tail}");
    }
}
