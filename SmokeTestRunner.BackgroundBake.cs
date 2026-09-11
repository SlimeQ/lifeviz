using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunBackgroundBakeSmokeTest()
    {
        RunBakeStatusTransportChecks();
        Exception? failure = null;
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-bake-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string firstImage = Path.Combine(directory, "first.png");
        string secondImage = Path.Combine(directory, "second.png");
        WriteBakeSmokeImage(firstImage, 240, 20);
        WriteBakeSmokeImage(secondImage, 20, 240);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            var window = new MainWindow
            {
                Width = 240, Height = 180, ShowInTaskbar = false, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Opacity = 0
            };
            window.Loaded += async (_, _) =>
            {
                try
                {
                    BakeJob[] jobs = await window.RunBackgroundBakeQueueSmokeAsync(directory, firstImage, secondImage);
                    ValidateOfflineRecording(jobs[0].OutputPath, 1280, 720, 60, 30, false);
                    ValidateOfflineRecording(jobs[1].OutputPath, 1280, 720, 60, 60, false);
                    ValidateOfflineRecording(jobs[2].OutputPath, 1280, 720, (int)jobs[2].Status.CompletedFrames, 30, false);
                    ValidateOfflineRecording(jobs[3].OutputPath, 1280, 720, 30, 30, false);
                    ValidateOfflineRecording(jobs[4].OutputPath, 1280, 720, 30, 30, false);
                    ValidateBakeColor(jobs[0].OutputPath, red: true);
                    ValidateBakeColor(jobs[1].OutputPath, red: false);
                    ValidateBakeColor(jobs[4].OutputPath, red: false);
                    if (!jobs[0].OutputPath.StartsWith(Path.Combine(directory, "first")) ||
                        !jobs[1].OutputPath.StartsWith(Path.Combine(directory, "second")))
                        throw new InvalidOperationException("Queued output folder snapshot was not preserved.");
                    await window.VerifyBackgroundBakeShutdownAsync();
                }
                catch (Exception ex) { failure = ex; }
                finally { window.Close(); app.Shutdown(); }
            };
            window.Show();
        };
        app.Run();
        if (failure != null) throw new InvalidOperationException($"Background bake smoke failed; fixtures: {directory}", failure);
        Directory.Delete(directory, recursive: true);
        Logger.Info("Background bake smoke passed: actual worker videos have captured scene colors, dimensions, FPS, durations, and valid partial output.");
        return 0;
    }

    private static void RunBakeStatusTransportChecks()
    {
        foreach (string state in new[] { "Completed", "Cancelled", "Failed", "Rendering" })
            VerifyHeldOpenBakeStatusPipeAsync(state).GetAwaiter().GetResult();

        using var writer = new BlockedBakeStatusWriter();
        using (var publisher = new BakeStatusTransport.Publisher(writer))
        {
            publisher.Publish(new BakeStatus("Rendering", "first"));
            if (!writer.Entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Status writer did not start.");
            // Publishing thousands of updates cannot wait on I/O or accumulate
            // thousands of snapshots. Completion must replace stale telemetry.
            for (int i = 0; i < 10000; i++) publisher.Publish(new BakeStatus("Rendering", "progress", i, 10000));
            publisher.Publish(new BakeStatus("Completed", "done", 10000, 10000));
            writer.Release.TrySetResult();
        }
        var receiver = new BakeStatusTransport.Receiver();
        receiver.DrainAsync(new StringReader("ordinary worker log\n" + writer.Output)).GetAwaiter().GetResult();
        if (writer.WriteCount != 2 || receiver.Latest?.State != "Completed" || receiver.Latest.CompletedFrames != 10000)
            throw new InvalidOperationException("Bounded status transport lost its terminal state.");

        using var deniedWriter = new DeniedBakeStatusWriter();
        using var deniedPublisher = new BakeStatusTransport.Publisher(deniedWriter);
        deniedPublisher.Publish(new BakeStatus("Rendering", "access denied injection"));
        deniedPublisher.Dispose();
        if (deniedPublisher.WriteError is not UnauthorizedAccessException)
            throw new InvalidOperationException("Telemetry write failure did not stay isolated.");
        deniedPublisher.Publish(new BakeStatus("Completed", "rendering can finish even after telemetry fails"));
    }

    private static async Task VerifyHeldOpenBakeStatusPipeAsync(string state)
    {
        string name = "lifeviz-status-smoke-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task connected = server.WaitForConnectionAsync(cancellation.Token);
        await client.ConnectAsync(cancellation.Token);
        await connected;
        using var output = new StreamWriter(server) { AutoFlush = true };
        using var input = new StreamReader(client);
        var receiver = new BakeStatusTransport.Receiver();
        Task reading = receiver.DrainAsync(input, cancellation.Token);
        try
        {
            await output.WriteLineAsync("ordinary diagnostic output");
            await output.WriteLineAsync("LIFEVIZ_BAKE_STATUS_V1 invalid-json");
            await output.WriteLineAsync("LIFEVIZ_BAKE_STATUS_V1 " +
                JsonSerializer.Serialize(new BakeStatus(state, "pipe remains open", 144000, 144000)));
            if (state != "Rendering")
            {
                // Do not close the writer: terminal delivery must finish even
                // when another process could still hold the pipe open.
                await reading.WaitAsync(TimeSpan.FromSeconds(3));
                if (receiver.Latest?.State != state)
                    throw new InvalidOperationException("An open status pipe lost the terminal bake result.");
            }
            else
            {
                while (receiver.Latest == null) await Task.Delay(10, cancellation.Token);
                if (reading.IsCompleted)
                    throw new InvalidOperationException("A full frame count was mistaken for encoder completion.");
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private sealed class BlockedBakeStatusWriter : StringWriter
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WriteCount;
        public string Output => ToString();
        public override async Task WriteLineAsync(string? value)
        {
            if (Interlocked.Increment(ref WriteCount) == 1)
            {
                Entered.Set();
                await Release.Task.ConfigureAwait(false);
            }
            await base.WriteLineAsync(value).ConfigureAwait(false);
        }
    }

    private sealed class DeniedBakeStatusWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => throw new UnauthorizedAccessException("Injected status-pipe failure");
    }

    private static void WriteBakeSmokeImage(string path, byte red, byte blue)
    {
        byte[] pixels = new byte[256 * 144 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        { pixels[i] = blue; pixels[i + 1] = 30; pixels[i + 2] = red; pixels[i + 3] = 255; }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(256, 144, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4)));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void ValidateBakeColor(string path, bool red)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        { RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in new[] { "-v", "error", "-i", path, "-frames:v", "1", "-vf", "scale=1:1", "-pix_fmt", "rgb24", "-f", "rawvideo", "-" })
            start.ArgumentList.Add(arg);
        var process = FfmpegProcessManager.Shared.Start(start);
        try
        {
            var error = process.StandardError.ReadToEndAsync();
            byte[] pixel = new byte[3];
            process.StandardOutput.BaseStream.ReadExactly(pixel);
            if (!process.WaitForExit(30000) || process.ExitCode != 0 ||
                (red ? pixel[0] <= pixel[2] + 100 : pixel[2] <= pixel[0] + 100))
                throw new InvalidOperationException($"Bake did not preserve captured scene color: {string.Join(',', pixel)} {error.GetAwaiter().GetResult()}");
        }
        finally { FfmpegProcessManager.Shared.TerminateAndDispose(process); }
    }
}
