using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// 1.3.0-beta.1 debug toggles (Part 3), so the old and new versions are compared from one beta:
/// - New slide engine (default on) vs the 1.2 UI-thread slide.
/// - Normal window (default off): no AllowsTransparency / live DropShadowEffect (WPF redraws the whole layered window
///   on the GPU and copies it back to the CPU on every change); Windows 11 rounded corners, the native DWM shadow
///   (WindowChrome glass frame) and DWM transitions off so its own open animation can't fight the slide. Needs a restart.
/// - Frame-gap log for every slide.
/// </summary>
public partial class MainWindow
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    private bool _normalWindow;

    /// <summary>Called right after InitializeComponent, before the window handle exists.</summary>
    private void ApplyWindowMode()
    {
        WindowSlide.UseThread = _settings.NewSlideEngine;
        WindowSlide.LogFrames = _settings.FrameLog;
        _normalWindow = _settings.NormalWindow;
        if (!_normalWindow) return;
        AllowsTransparency = false;
        RootCard.Margin = new Thickness(0);
        CardShadow.Visibility = Visibility.Collapsed;
        Background = CardBody.Background is Brush b ? b : Brushes.Black;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false,
        });
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int round = DWMWCP_ROUND, off = 1;
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_TRANSITIONS_FORCEDISABLED, ref off, sizeof(int));
        };
    }

    private void InitDebugToggles()
    {
        SlideEngineToggle.IsChecked = _settings.NewSlideEngine;
        NormalWindowToggle.IsChecked = _settings.NormalWindow;
        FrameLogToggle.IsChecked = _settings.FrameLog;
    }

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        _settings.NewSlideEngine = SlideEngineToggle.IsChecked == true;
        _settings.FrameLog = FrameLogToggle.IsChecked == true;
        bool normal = NormalWindowToggle.IsChecked == true;
        bool restart = normal != _settings.NormalWindow;
        _settings.NormalWindow = normal;
        _settings.Save();
        WindowSlide.UseThread = _settings.NewSlideEngine;
        WindowSlide.LogFrames = _settings.FrameLog;
        if (restart) Status("Normal window: quit and start Alien Controls again to switch.");
    }
}
