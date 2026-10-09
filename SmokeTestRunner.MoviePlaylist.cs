using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace lifeviz;

internal static partial class SmokeTestRunner
{
    private static int RunMoviePlaylistSmokeTest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string srt = Path.Combine(directory, "captions 'with', [punctuation];.srt");
        string video = Path.Combine(directory, "movie 'one', [embedded];.mkv");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:01,000\nEARLY CAPTION\n\n2\n00:00:02,000 --> 00:00:03,800\nLATE CAPTION\n", new UTF8Encoding(false));
        var info = new ProcessStartInfo { FileName = "ffmpeg", CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=320x180:r=10:d=4",
                     "-f", "lavfi", "-i", "sine=frequency=440:duration=4", "-i", srt,
                     "-map", "0:v", "-map", "1:a", "-map", "2:s", "-map", "2:s", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-c:s", "srt",
                     "-metadata:s:s:0", "language=eng", "-metadata:s:s:0", "title=English SDH", "-disposition:s:0", "default",
                     "-metadata:s:s:1", "language=fra", "-metadata:s:s:1", "title=French signs", "-disposition:s:1", "forced", "-t", "4", video })
            info.ArgumentList.Add(arg);
        using (var process = FfmpegProcessManager.Shared.Start(info))
        {
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Playlist fixture generation timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }
        App.IsDiagnosticTestMode = true;
        var app = new App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Exception? failure = null;
        app.Startup += (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.RunMoviePlaylistChecks(video, srt, directory);
                Console.WriteLine("Movie playlist smoke passed: ordered loop, duplicate entries, text/SRT captions, punctuation paths, seek timing, pause, resume on/off, edits, missing inputs, project/autosave persistence, draft controls and empty playlist.");
            }
            catch (Exception ex) { failure = ex; Console.WriteLine(ex); }
            finally { window?.ShutdownResources(); app.Shutdown(failure == null ? 0 : 1); }
        };
        int result = app.Run();
        Console.WriteLine("Playlist smoke artifacts: " + directory);
        return failure == null ? result : 1;
    }
}
