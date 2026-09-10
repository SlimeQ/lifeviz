using System.Buffers;
using System.Diagnostics;
using System.IO;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunOfflineRenderSmokeTest()
    {
        Exception? failure = null;
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-export-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // App.xaml only sets StartupUri. Create the single fixture window below
        // without loading it, so a second scene cannot compete with the export.
        var app = new App();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 160, Height = 120, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Opacity = 0
            };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    foreach (bool cancel in new[] { false, true })
                    {
                        string outputFolder = Path.Combine(directory, cancel ? "cancelled" : "complete");
                        Directory.CreateDirectory(outputFolder);
                        string path = await window.RunFixedDurationForSmokeAsync(outputFolder, cancel);
                        ValidateOfflineRecording(path, 1280, 720, cancel ? 1 : 90, 30, requireUniqueFrames: false);
                        Logger.Info($"Fixed-duration render smoke: cancel={cancel}, decoded count/dimensions/timing passed.");
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); app.Shutdown(); }
            };
            window.Show();
        };
        try { app.Run(); }
        finally { Directory.Delete(directory, recursive: true); }
        if (failure != null) throw new InvalidOperationException("Fixed-duration render smoke failed.", failure);
        return 0;
    }

    private static int RunOfflineRecordingSmokeTest()
    {
        // Independent inverse-coordinate reference covers crop-sized frames,
        // arbitrary alpha, scale=1, and oversized pooled input/output buffers.
        var random = new Random(7821);
        foreach (var (width, height, scale) in new[] { (14, 10, 1), (14, 10, 2), (256, 144, 5), (640, 360, 3), (1920, 1080, 2) })
        {
            byte[] source = new byte[width * height * 4 + 64];
            random.NextBytes(source);
            int length = width * height * scale * scale * 4;
            byte[] output = Enumerable.Repeat((byte)0xcd, length + 64).ToArray();
            RecordingFrameScaler.Scale(source, output, width, height, scale);
            int outputWidth = width * scale;
            for (int y = 0; y < height * scale; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    int expectedOffset = ((y / scale) * width + x / scale) * 4;
                    int actualOffset = (y * outputWidth + x) * 4;
                    if (!source.AsSpan(expectedOffset, 4).SequenceEqual(output.AsSpan(actualOffset, 4)))
                        throw new InvalidOperationException($"Recording scale mismatch at {x},{y}, scale={scale}.");
                }
            }
            if (output.AsSpan(length).ContainsAnyExcept((byte)0xcd))
                throw new InvalidOperationException("Scaler wrote beyond the logical frame.");

            byte[] legacy = new byte[length];
            ScaleRecordingFrameLegacy(source, legacy, width, height, scale);
            if (!legacy.AsSpan().SequenceEqual(output.AsSpan(0, length)))
                throw new InvalidOperationException("Legacy scaling parity failed.");
            double legacyMs = MeasureFramePreparation(() => ScaleRecordingFrameLegacy(source, legacy, width, height, scale));
            double optimizedMs = MeasureFramePreparation(() => RecordingFrameScaler.Scale(source, output, width, height, scale));
            Logger.Info($"Recording scale {width}x{height} x{scale}: legacy={legacyMs:0.000} ms/frame, optimized={optimizedMs:0.000} ms/frame, speedup={legacyMs / optimizedMs:0.00}x; pixel parity passed.");
        }

        if (RecordingSession.GetOfflineQueueCapacity(3840 * 2160 * 4) != 2 ||
            RecordingSession.GetOfflineQueueCapacity(7680 * 4320 * 4) != 1 ||
            RecordingSession.GetOfflineQueueCapacity(256 * 144 * 4) != 8)
            throw new InvalidOperationException("Offline queue memory budget regressed.");

        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-offline-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Include finalization in throughput, and validate decoded frame count,
            // timestamps, dimensions, and changing content from the actual files.
            foreach (RecordingQuality quality in new[] { RecordingQuality.High, RecordingQuality.LosslessCompatible, RecordingQuality.Lossless })
            {
                foreach (bool offline in new[] { false, true })
                {
                    const int width = 640, height = 360, fps = 30, count = 90;
                    RecordingSettings settings = RecordingSettings.FromQuality(quality, width, height, fps);
                    string path = Path.Combine(directory, $"{quality}-{offline}.{settings.FileExtension}");
                    Stopwatch timer = Stopwatch.StartNew();
                    using (var session = new RecordingSession(path, width, height, fps, settings, offlineRender: offline))
                    {
                        for (int index = 0; index < count; index++)
                        {
                            byte[] frame = ArrayPool<byte>.Shared.Rent(width * height * 4);
                            for (int pixel = 0; pixel < width * height; pixel++)
                            {
                                frame[pixel * 4] = (byte)(index * 2);
                                frame[pixel * 4 + 1] = (byte)(pixel % width);
                                frame[pixel * 4 + 2] = (byte)(pixel / width);
                                frame[pixel * 4 + 3] = 255;
                            }
                            if (!session.TryEnqueue(frame, waitForCapacity: true, failOnSaturation: true))
                                throw new InvalidOperationException("Smoke encoder rejected a frame.");
                        }
                        session.Dispose();
                        if (session.TryGetError(out string? error))
                            throw new InvalidOperationException(error);
                    }
                    timer.Stop();
                    ValidateOfflineRecording(path, width, height, count, fps);
                    Logger.Info($"Recording encode {quality}, offline={offline}: {timer.Elapsed.TotalSeconds:0.000}s, {count / timer.Elapsed.TotalSeconds:0.0} fps; decoded count/timing/content passed.");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        Logger.Info("Offline recording smoke passed.");
        return 0;
    }

    private static void ValidateOfflineRecording(string path, int width, int height, int count, int fps, bool requireUniqueFrames = true)
    {
        var start = new ProcessStartInfo
        {
            FileName = "ffmpeg", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-v", "error", "-i", path, "-map", "0:v:0", "-f", "framemd5", "-" })
            start.ArgumentList.Add(argument);
        Process process = FfmpegProcessManager.Shared.Start(start);
        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
                throw new TimeoutException("Recording validation timed out.");
            string output = outputTask.GetAwaiter().GetResult();
            string error = errorTask.GetAwaiter().GetResult();
            string[] lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            string[][] frames = lines.Where(line => !line.StartsWith('#')).Select(line => line.Split(',', StringSplitOptions.TrimEntries)).ToArray();
            if (process.ExitCode != 0 || frames.Length != count ||
                !lines.Contains($"#dimensions 0: {width}x{height}") || !lines.Contains($"#tb 0: 1/{fps}") ||
                frames.Where((frame, index) => long.Parse(frame[2]) != index || long.Parse(frame[3]) != 1).Any() ||
                (requireUniqueFrames && frames.Select(frame => frame[^1]).Distinct().Count() != count))
                throw new InvalidOperationException($"Encoded frame validation failed: {path}\n{error}\n{output}");
        }
        finally
        {
            FfmpegProcessManager.Shared.TerminateAndDispose(process, TimeSpan.FromSeconds(2));
        }
    }

    private static double MeasureFramePreparation(Action action)
    {
        for (int i = 0; i < 10; i++) action();
        Stopwatch timer = Stopwatch.StartNew();
        int iterations = 0;
        do { action(); iterations++; } while (timer.ElapsedMilliseconds < 350);
        return timer.Elapsed.TotalMilliseconds / iterations;
    }

    private static void ScaleRecordingFrameLegacy(byte[] source, byte[] destination, int width, int height, int scale)
    {
        if (scale == 1)
        {
            Buffer.BlockCopy(source, 0, destination, 0, width * height * 4);
            return;
        }
        int outputWidth = width * scale;
        for (int y = 0; y < height; y++)
        {
            int sourceRow = y * width * 4;
            int outputRow = y * scale * outputWidth * 4;
            for (int x = 0; x < width; x++)
            {
                int src = sourceRow + x * 4;
                byte b = source[src], g = source[src + 1], r = source[src + 2], a = source[src + 3];
                int pixelBase = outputRow + x * scale * 4;
                for (int dy = 0; dy < scale; dy++)
                {
                    int row = pixelBase + dy * outputWidth * 4;
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int dest = row + dx * 4;
                        destination[dest] = b;
                        destination[dest + 1] = g;
                        destination[dest + 2] = r;
                        destination[dest + 3] = a;
                    }
                }
            }
        }
    }
}
