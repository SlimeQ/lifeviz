using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunBackgroundBakeSmokeTest()
    {
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
