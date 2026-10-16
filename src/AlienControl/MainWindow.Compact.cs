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
        SetCompact(!IsCompact);
    }

    /// <summary>Keeps the bottom-right corner in place when the height changes (view switch, status line).</summary>
    private void OnSizeChangedKeepCorner(object sender, SizeChangedEventArgs e)
    {
        if (e.HeightChanged && IsVisible && WindowState == WindowState.Normal && !_hiding) SnapToCorner();
    }
}
