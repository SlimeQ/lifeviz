using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows.Threading;

namespace lifeviz;

// Makes freezes and crashes visible after the fact. Before this, a killed or crashed session left
// nothing behind: lifeviz.log was truncated by the next launch and a UI freeze logged nothing.
internal static class SessionHealth
{
    private const int KeptUnexpectedExitLogs = 5;
    private static string? _markerPath;

    internal static string? LogDirectoryOverrideForSmoke { get; set; }

    internal static string LogDirectory => LogDirectoryOverrideForSmoke ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lifeviz", "logs");

    private sealed record Marker(int ProcessId, DateTime ProcessStartUtc, DateTime SessionStartUtc, string Version);

    /// <summary>
    /// Records this session and reports any earlier session that ended without a clean shutdown
    /// (crash, kill after a freeze, power loss). Returns a user-facing summary, or null.
    /// Call after Logger.Initialize(), which rotates the previous session's log to lifeviz.1.log.
    /// </summary>
    internal static string? BeginSession(string version)
    {
        string? report = null;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            foreach (string path in Directory.GetFiles(LogDirectory, "session-*.running"))
            {
                Marker? marker = null;
                try { marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path)); }
                catch (Exception ex) when (ex is IOException or JsonException) { }
                if (marker != null && IsAlive(marker)) continue; // Another live LifeViz window.
                if (marker != null) report ??= DescribeUnexpectedExit(marker);
                TryDelete(path);
            }

            using var self = Process.GetCurrentProcess();
            var current = new Marker(self.Id, self.StartTime.ToUniversalTime(), DateTime.UtcNow, version);
            _markerPath = Path.Combine(LogDirectory, $"session-{self.Id}.running");
            File.WriteAllText(_markerPath, JsonSerializer.Serialize(current));
        }
        catch (Exception ex)
        {
            Logger.Warn($"Session health tracking is unavailable: {ex.Message}");
        }
        return report;
    }

    /// <summary>Clean shutdown: the next launch will not report this session.</summary>
    internal static void EndSession()
    {
        if (_markerPath != null) TryDelete(_markerPath);
        _markerPath = null;
    }

    private static bool IsAlive(Marker marker)
    {
        try
        {
            using var process = Process.GetProcessById(marker.ProcessId);
            // Process IDs are reused; only the same process start time is the same session.
            return !process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - marker.ProcessStartUtc).TotalSeconds) < 2;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string DescribeUnexpectedExit(Marker marker)
    {
        string previousLog = Path.Combine(LogDirectory, "lifeviz.1.log");
        string? kept = null;
        string? lastPreset = null, lastStall = null;
        try
        {
            if (File.Exists(previousLog))
            {
                kept = Path.Combine(LogDirectory, $"unexpected-exit-{marker.SessionStartUtc:yyyyMMdd'T'HHmmss'Z'}.log");
                File.Copy(previousLog, kept, overwrite: true);
                foreach (string line in File.ReadLines(kept))
                {
                    if (line.Contains("projectM loading preset", StringComparison.Ordinal)) lastPreset = line;
                    if (line.Contains("UI thread has not processed input", StringComparison.Ordinal)) lastStall = line;
                }
                foreach (string old in Directory.GetFiles(LogDirectory, "unexpected-exit-*.log")
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(KeptUnexpectedExitLogs))
                    TryDelete(old);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { kept = null; }

        string summary = $"LifeViz {marker.Version} did not shut down normally last time (session started {marker.SessionStartUtc.ToLocalTime():g}). " +
            "This happens after a crash, or when a frozen window is closed or ended from Task Manager.";
        if (lastStall != null) summary += $"\n\nThe UI had stopped responding: {Tail(lastStall)}";
        if (lastPreset != null) summary += $"\n\nLast MilkDrop preset being loaded: {Tail(lastPreset)}";
        if (kept != null) summary += $"\n\nThat session's log was saved to:\n{kept}";
        return summary;
    }

    private static string Tail(string logLine)
    {
        int level = logLine.IndexOf("] ", StringComparison.Ordinal);
        return level >= 0 ? logLine[(level + 2)..] : logLine;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

// Logs when the UI thread stops processing input, and when it recovers. The probe runs at Input
// priority, so it measures exactly what a user feels as "Not Responding". The logger writes on its
// own thread, so the warning reaches disk even while the UI thread is stuck.
internal sealed class UiStallWatchdog : IDisposable
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(4);
    private readonly Dispatcher _dispatcher;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private long _lastResponseTicks = Stopwatch.GetTimestamp();
    private int _probePending;
    internal int StallsReported, Recoveries;

    public UiStallWatchdog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "LifeViz UI stall watchdog", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        bool reported = false;
        long stallStartTicks = 0;
        while (!_stop.Wait(500))
        {
            if (Interlocked.Exchange(ref _probePending, 1) == 0)
            {
                try
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                    {
                        Volatile.Write(ref _lastResponseTicks, Stopwatch.GetTimestamp());
                        Volatile.Write(ref _probePending, 0);
                    }));
                }
                catch (Exception) { return; } // Dispatcher shut down.
            }

            long lastResponse = Volatile.Read(ref _lastResponseTicks);
            TimeSpan silent = Stopwatch.GetElapsedTime(lastResponse);
            if (!reported && silent > StallThreshold)
            {
                reported = true;
                stallStartTicks = lastResponse;
                Interlocked.Increment(ref StallsReported);
                Logger.Warn($"UI thread has not processed input for {silent.TotalSeconds:F0} s (LifeViz appears frozen).");
            }
            else if (reported && silent < TimeSpan.FromSeconds(1))
            {
                reported = false;
                Interlocked.Increment(ref Recoveries);
                double stalled = (lastResponse - stallStartTicks) / (double)Stopwatch.Frequency;
                Logger.Info($"UI thread is responding again after a {stalled:F1} s stall.");
            }
        }
    }

    public void Dispose() => _stop.Set();
}
