using System.Configuration;
using System.Data;
using System.Windows;

namespace lifeviz;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static bool SuppressErrorDialogs { get; set; }
    public static bool IsSmokeTestMode { get; set; }
    public static bool IsDiagnosticTestMode { get; set; }
    public static bool LoadUserConfigInSmokeTest { get; set; }
    public static bool CaptureGpuFallbackBuffersInSmokeTest { get; set; } = true;

    /// <summary>
    /// Smoke/diagnostic runs normally read every presented frame back from the GPU
    /// so validation smokes can compare pixels. That readback plus its per-call
    /// buffer copy is far more expensive than the frame work it sits next to, so
    /// timing-oriented runs (profile/pacing) turn it off. Leaving it on made
    /// profiling runs report roughly double the real memory footprint and added
    /// GPU stalls that do not exist in the shipping app.
    /// </summary>
    public static bool CapturePresentedFramesForValidation { get; set; } = true;

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception.", args.Exception);
            if (!SuppressErrorDialogs)
            {
                MessageBox.Show($"Unexpected error:\n{args.Exception.Message}", "LifeViz Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            args.Handled = true;
        };
    }
}
