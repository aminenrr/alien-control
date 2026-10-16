using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace AlienControl;

/// <summary>
/// v1.1 (beta.2): Fluent theme only, native Windows 11 Acrylic backdrop (DWMSBT_TRANSIENTWINDOW, the Quick Settings
/// look). Needs a NON-layered window, so it is decided before the window is shown: AllowsTransparency off, glass frame
/// extended over the whole client area, DWM draws the rounded corners, border and shadow (our margin + shadow Border go).
/// Accent colors are untouched (they still follow the laptop lights). Windows 11 22H2+ only; DWM itself falls back
/// to solid when Transparency effects or Energy saver are on. Switching it on / off (theme or toggle) restarts the app.
/// </summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, int msg, IntPtr w, IntPtr l);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int WM_NCACTIVATE = 0x0086;

    private bool _acrylic;

    public static bool AcrylicSupported => Environment.OSVersion.Version.Build >= 22621;

    private bool WantsAcrylic => !IsLegend && _settings.FluentAcrylic && AcrylicSupported;

    /// <summary>Called from the constructor, after InitializeComponent and before the window is shown.</summary>
    private void InitAcrylic()
    {
        FluentAcrylicToggle.IsChecked = _settings.FluentAcrylic;
        FluentAcrylicToggle.IsEnabled = AcrylicSupported;
        if (!WantsAcrylic) return;
        _acrylic = true;

        AllowsTransparency = false;
        Width -= 20;                                   // the 10 DIP shadow margin is gone, the card stays 420 wide
        RootCard.Margin = new Thickness(0);
        CardShadow.Visibility = Visibility.Collapsed;
        CardBody.SetResourceReference(StyleProperty, "WindowCardAcrylic");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(-1), CaptionHeight = 0, ResizeBorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
        });
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            var src = HwndSource.FromHwnd(h);
            if (src?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Transparent;
            var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
            DwmExtendFrameIntoClientArea(h, ref m);
            int on = 1, round = 2, acrylic = 3;
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref acrylic, sizeof(int));
            // Keep the blur when another window takes focus (DWM shows the flat fallback for inactive windows).
            src?.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg != WM_NCACTIVATE || w != IntPtr.Zero) return IntPtr.Zero;
                handled = true;
                return DefWindowProc(hwnd, msg, (IntPtr)1, l);
            });
        };
    }

    private void FluentAcrylic_Click(object sender, RoutedEventArgs e)
    {
        _settings.FluentAcrylic = FluentAcrylicToggle.IsChecked == true;
        _settings.Save();
        if (WantsAcrylic != _acrylic) ((App)Application.Current).Restart();
    }
}
