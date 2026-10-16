using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// v1.1 beta.5 (Amine 2026-10-16): the header chip is gone. A header button shows the active preset's icon in its
/// color; clicking it opens a small list of the presets (Battery, Quiet, Balanced, Performance, Custom), each with its
/// icon and color, the active one marked. Both themes. Picking one runs the same path as the tray menu.
/// </summary>
public partial class MainWindow
{
    private static readonly TrayProfile[] PresetOrder =
        { TrayProfile.Battery, TrayProfile.Quiet, TrayProfile.Balanced, TrayProfile.Performance, TrayProfile.Custom };
    private DateTime _presetClosedUtc;

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False closes the popup on the mouse-down of this very click; don't reopen it right away.
        if ((DateTime.UtcNow - _presetClosedUtc).TotalMilliseconds < 250) return;
        CloseSettings(instant: true);
        BuildPresetList();
        PresetPopup.HorizontalOffset = PresetButton.ActualWidth - PresetPanel.Width;   // right edge under the button
        PresetPopup.VerticalOffset = 2;
        PresetPopup.IsOpen = true;
    }

    private void PresetPopup_Closed(object? sender, EventArgs e) => _presetClosedUtc = DateTime.UtcNow;

    private void BuildPresetList()
    {
        PresetList.Children.Clear();
        var shown = ShownProfile;
        var hover = TryFindResource("HoverFxBrush") as Brush ?? new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255));
        var textStyle = TryFindResource("FooterText") as Style;
        foreach (var p in PresetOrder)
        {
            var t = Profiles.Tint(p);
            var tint = new SolidColorBrush(Color.FromRgb(t.R, t.G, t.B)); tint.Freeze();
            bool active = p == shown;
            bool locked = p == TrayProfile.Battery && BatteryLocked;

            var row = new Grid { Margin = new Thickness(0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new ContentControl
            {
                Content = TryFindResource(Profiles.IconKey(p)), Width = 16, Height = 16, Foreground = tint,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false,
            };
            TextElement.SetForeground(icon, tint);
            row.Children.Add(icon);

            var label = Profiles.Label(p);
            var text = new TextBlock
            {
                Text = IsLegend ? label.ToUpperInvariant() : label, Style = textStyle, VerticalAlignment = VerticalAlignment.Center,
            };
            if (active) { text.Foreground = tint; text.FontWeight = FontWeights.SemiBold; }
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            if (active)
            {
                var dot = new Border
                {
                    Width = 6, Height = 6, CornerRadius = new CornerRadius(IsLegend ? 1 : 3), Background = tint,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 2, 0),
                };
                Grid.SetColumn(dot, 2);
                row.Children.Add(dot);
            }

            var item = new Border
            {
                Child = row, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(IsLegend ? 3 : 5),
                Background = active ? new SolidColorBrush(Color.FromArgb(0x1C, t.R, t.G, t.B)) : Brushes.Transparent,
                Cursor = locked ? Cursors.Arrow : Cursors.Hand, Opacity = locked ? 0.4 : 1,
                ToolTip = locked ? "Battery comes on by itself when you unplug (Auto preset)" : Profiles.Describe(p),
            };
            if (!locked)
            {
                var rest = item.Background;
                item.MouseEnter += (_, _) => { if (!active) item.Background = hover; };
                item.MouseLeave += (_, _) => item.Background = rest;
                item.MouseLeftButtonUp += async (_, _) =>
                {
                    PresetPopup.IsOpen = false;
                    if (p != shown || p != CurrentProfile) await ApplyProfileAsync(p);
                };
            }
            PresetList.Children.Add(item);
        }
    }
}
