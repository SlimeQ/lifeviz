using System;
using System.IO;
using System.Windows;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunWebcamDevicesSmokeTest(bool liveCamera = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-webcam-devices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        App.IsDiagnosticTestMode = true;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        app.Startup += (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.RunWebcamDeviceChecks(directory);
                if (liveCamera) Console.WriteLine("Live webcam check: " + window.RunLiveWebcamCheck());
                Console.WriteLine("Webcam devices smoke passed: waiting layers kept (restore, capture loop, failed open), name match adopts local ID, Any camera, unplug/replug, live and draft swaps, in-use refusal, export/restore, editor camera picker.");
            }
            catch (Exception ex) { failure = ex; Console.WriteLine(ex); }
            finally
            {
                window?.ShutdownResources();
                app.Shutdown(failure == null ? 0 : 1);
            }
        };
        int result = app.Run();
        if (failure == null) Directory.Delete(directory, recursive: true);
        return failure == null ? result : 1;
    }
}
