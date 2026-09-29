using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    // Unexpected-exit detection, previous-log retention and the UI freeze watchdog. Runs in an
    // isolated temp directory; the user's real logs and session markers are never touched.
    private static int RunSessionHealthSmokeTest()
    {
        static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-session-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SessionHealth.LogDirectoryOverrideForSmoke = directory;
        try
        {
            // A session whose process is gone (crash, or killed while frozen) must be reported once.
            using var dead = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
            DateTime deadStart = dead.StartTime.ToUniversalTime();
            dead.WaitForExit();
            WriteMarker(directory, dead.Id, deadStart, "9.9.9");
            File.WriteAllLines(Path.Combine(directory, "lifeviz.1.log"), new[]
            {
                "2026-09-28T01:00:00Z [INFO] projectM loading preset 'Old/First.milk'.",
                "2026-09-28T01:00:30Z [INFO] projectM loading preset 'Custom/Heavy.milk'.",
                "2026-09-28T01:00:35Z [WARN] UI thread has not processed input for 5 s (LifeViz appears frozen)."
            });

            // A marker from a live process (this one) belongs to another open window: keep it.
            using var self = Process.GetCurrentProcess();
            string liveMarker = WriteMarker(directory, self.Id, self.StartTime.ToUniversalTime(), "live", "session-live.running");
            // A reused PID with a different start time is not the same session.
            WriteMarker(directory, self.Id, self.StartTime.ToUniversalTime().AddHours(-3), "reused", "session-reused.running");

            string? report = SessionHealth.BeginSession("smoke");
            Check(report != null && report.Contains("9.9.9") && report.Contains("Custom/Heavy.milk") && report.Contains("not processed input"),
                $"Unexpected exit was not reported with its last preset and freeze: {report}");
            Check(Directory.GetFiles(directory, "unexpected-exit-*.log").Length == 1, "The crashed session's log was not preserved.");
            Check(File.Exists(liveMarker), "A live session's marker was removed.");
            Check(!File.Exists(Path.Combine(directory, "session-reused.running")), "A reused-PID marker was treated as live.");
            Check(File.Exists(Path.Combine(directory, $"session-{self.Id}.running")), "This session did not record itself.");
            SessionHealth.EndSession();
            Check(!File.Exists(Path.Combine(directory, $"session-{self.Id}.running")), "A clean shutdown left its marker behind.");
            File.Delete(liveMarker);
            Check(SessionHealth.BeginSession("smoke") == null, "A clean shutdown was reported as unexpected.");
            SessionHealth.EndSession();

            // Previous session logs rotate instead of being overwritten.
            foreach (string name in new[] { "lifeviz.log", "lifeviz.1.log", "lifeviz.2.log", "lifeviz.3.log" }) File.Delete(Path.Combine(directory, name));
            for (int session = 1; session <= 5; session++)
            {
                File.WriteAllText(Path.Combine(directory, "lifeviz.log"), $"session {session}");
                Logger.RotateSessionLogs(directory);
            }
            Check(!File.Exists(Path.Combine(directory, "lifeviz.log")) &&
                File.ReadAllText(Path.Combine(directory, "lifeviz.1.log")) == "session 5" &&
                File.ReadAllText(Path.Combine(directory, "lifeviz.3.log")) == "session 3" &&
                !File.Exists(Path.Combine(directory, "lifeviz.4.log")),
                "Session logs did not rotate through lifeviz.1-3.log.");

            // The watchdog reports a real UI-thread freeze and its recovery.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Exception? failure = null;
            app.Startup += (_, _) =>
            {
                var watchdog = new UiStallWatchdog(app.Dispatcher);
                try
                {
                    Thread.Sleep(5500); // Freeze the UI thread past the 4 s threshold.
                    var frame = new DispatcherFrame();
                    var started = DateTime.UtcNow;
                    var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
                    poll.Tick += (_, _) =>
                    {
                        if (Volatile.Read(ref watchdog.Recoveries) == 0 && DateTime.UtcNow - started < TimeSpan.FromSeconds(5)) return;
                        poll.Stop(); frame.Continue = false;
                    };
                    poll.Start();
                    Dispatcher.PushFrame(frame);
                    Check(Volatile.Read(ref watchdog.StallsReported) == 1 && Volatile.Read(ref watchdog.Recoveries) == 1,
                        $"Watchdog saw {watchdog.StallsReported} freezes and {watchdog.Recoveries} recoveries for one 5.5 s freeze.");
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    watchdog.Dispose();
                    app.Shutdown();
                }
            };
            app.Run();
            if (failure != null) throw failure;

            Console.WriteLine("Session health smoke passed: unexpected-exit report with last preset and freeze, live/reused-PID markers, clean shutdown, log rotation, UI freeze watchdog.");
            Directory.Delete(directory, recursive: true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
        finally
        {
            SessionHealth.LogDirectoryOverrideForSmoke = null;
        }
    }

    private static string WriteMarker(string directory, int processId, DateTime processStartUtc, string version, string? name = null)
    {
        string path = Path.Combine(directory, name ?? $"session-{processId}.running");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            ProcessId = processId,
            ProcessStartUtc = processStartUtc,
            SessionStartUtc = DateTime.UtcNow.AddMinutes(-30),
            Version = version
        }));
        return path;
    }
}
