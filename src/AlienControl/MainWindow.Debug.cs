using System.Windows;
using AlienControl.Services;

namespace AlienControl;

/// <summary>
/// Debug: frame-gap log for every slide (steps/s + biggest gap to crash.log).
/// 1.3.0: the beta.1 "New slide engine" and "Normal window" A/B toggles were removed. The user picked the new engine
/// and the transparent window (the normal window felt worse), so both are now fixed.
/// </summary>
public partial class MainWindow
{
    /// <summary>Called right after InitializeComponent.</summary>
    private void ApplyWindowMode() => WindowSlide.LogFrames = _settings.FrameLog;

    private void InitDebugToggles() => FrameLogToggle.IsChecked = _settings.FrameLog;

    private void Debug_Click(object sender, RoutedEventArgs e)
    {
        _settings.FrameLog = FrameLogToggle.IsChecked == true;
        _settings.Save();
        WindowSlide.LogFrames = _settings.FrameLog;
    }
}
