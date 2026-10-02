using System;
using System.IO;
using System.Windows;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunTempoSyncAppSmokeTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-tempo-sync-app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string loop = Path.Combine(directory, "loop-48f-24fps.mkv");
        TempoSyncSmoke.GenerateLoop(loop, 48, 24);
        App.IsDiagnosticTestMode = true;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        app.Startup += (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.RunTempoSyncIntegrationChecks(directory, loop, 48);
                Console.WriteLine("Tempo sync app smoke passed.");
            }
            catch (Exception ex) { failure = ex; Console.WriteLine(ex); Logger.Error("Tempo sync app smoke failed.", ex); }
            finally
            {
                window?.ShutdownResources();
                app.Shutdown(failure == null ? 0 : 1);
            }
        };
        int result = app.Run();
        if (failure == null)
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }

        return failure == null ? result : 1;
    }
}
