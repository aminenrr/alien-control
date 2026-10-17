using System;
using System.Windows;
using System.Windows.Media;

namespace AlienControl;

/// <summary>
/// v1.1 (beta.3, Amine 2026-10-16): compact view. Tray click and F3 always open the compact view (sensors, power,
/// performance mode, lighting colors + Off); F4 opens the full menu. The footer button flips between them both ways.
/// Same window: sections collapse, SizeToContent re-measures, and the window stays anchored to its bottom-right corner.
/// </summary>
public partial class MainWindow
{
    private static readonly Geometry ExpandIcon = Geometry.Parse("M0,4 L4,0 L8,4 M0,10 L4,14 L8,10");
    private static readonly Geometry CollapseIcon = Geometry.Parse("M0,0 L4,4 L8,0 M0,14 L4,10 L8,14");

    public bool IsCompact { get; private set; }

    public void SetCompact(bool compact)
    {
        IsCompact = compact;
        var v = compact ? Visibility.Collapsed : Visibility.Visible;
        GpuSection.Visibility = v;
        OcSection.Visibility = v;
        RefreshSection.Visibility = v;
        CpuSection.Visibility = v;
        FanSection.Visibility = v;
        LightFxRow.Visibility = v;
        ViewButtonText.Text = compact ? "Full menu" : "Compact";
        ViewButtonIcon.Data = compact ? ExpandIcon : CollapseIcon;
        if (compact) CustomPopup.IsOpen = false;
    }

    private void ViewButton_Click(object sender, RoutedEventArgs e)
    {
        CloseSettings(instant: true);
        SwitchView(!IsCompact);
    }

    // 1.2.0-beta.3 (Amine, option A): compact <-> full as a native "dip and rise". The window drops 40 px (110 ms),
    // the view switches while it's down, then it rises into its new size (200 ms). Same WindowSlide as open/hide,
    // so the compositor moves the already-drawn window (in-window animations show no frames on the layered window).
    private const int ViewDipPx = 40, ViewDownMs = 110, ViewUpMs = 200;
    private bool _viewSwitching;
    private int _viewGen;

    public void SwitchView(bool compact)
    {
        if (compact == IsCompact) return;
        if (!Motion.Enabled || !IsShownOpen || _openParked || _slide?.Active == true)
        {
            _viewSwitching = false;
            SetCompact(compact);
            return;
        }
        int gen = ++_viewGen, show = _showGen;
        _viewSwitching = true;
        var h = Hwnd;
        var r = Services.WindowSlide.Rect(h);
        Slide.Run(r.Left, r.Top + ViewDipPx, ViewDownMs, Services.CubicBezier.Accelerate, () =>
        {
            if (gen != _viewGen || show != _showGen || _hiding) { _viewSwitching = false; return; }
            SetCompact(compact);
            // one rendered frame so SizeToContent has applied the new height, then rise from 40 px below the corner
            int frames = 0;
            EventHandler? onFrame = null;
            onFrame = (_, _) =>
            {
                if (++frames < 1) return;
                CompositionTarget.Rendering -= onFrame;
                if (gen != _viewGen || show != _showGen || _hiding) { _viewSwitching = false; return; }
                _viewSwitching = false;
                var (x, y) = RestDevice();
                Services.WindowSlide.Move(h, x, y + ViewDipPx);
                Slide.Run(x, y, ViewUpMs, Services.CubicBezier.Decelerate, () => { if (gen == _viewGen && !_hiding) SnapToCorner(); });
            };
            CompositionTarget.Rendering += onFrame;
        });
    }

    /// <summary>Keeps the bottom-right corner in place when the height changes (view switch, status line).</summary>
    private void OnSizeChangedKeepCorner(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && IsVisible && WindowState == WindowState.Normal && !_hiding && !_viewSwitching) SnapToCorner();
    }
}
