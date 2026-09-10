using System;
using System.IO;
using System.Windows;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunFileReplacementSmokeTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-file-replacement-" + Guid.NewGuid().ToString("N"));
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
                window.RunFileReplacementChecks(directory);
                Console.WriteLine("File replacement smoke passed: live replacement, nested identity/settings, new pixels/dimensions, failed loads, draft apply, and persistence.");
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
