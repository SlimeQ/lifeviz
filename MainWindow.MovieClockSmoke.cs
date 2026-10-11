using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace lifeviz;

public partial class MainWindow
{
    internal void RunMovieClockChecks(string directory)
    {
        static void Generate(string[] arguments)
        {
            var info = new ProcessStartInfo { FileName = "ffmpeg", CreateNoWindow = true, RedirectStandardError = true };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            using var process = FfmpegProcessManager.Shared.Start(info);
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Movie clock fixture generation timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }
        static void Require(bool value, string message) => SmokeTestRunner.RequireSceneCheck(value, message);

        // Audio sample values encode their container timestamp. The English
        // track starts late and contains a packet gap; the Italian track is a
        // different signal. Counting decoded samples alone loses both offsets.
        string shifted = Path.Combine(directory, "audio-timestamp-gaps.mkv");
        const string ramp = "(t+1.1+if(gte(t,2),1.25,0))/20";
        Generate(new[] { "-hide_banner", "-loglevel", "error", "-y", "-copyts", "-f", "lavfi", "-i", "color=c=black:s=160x90:r=10:d=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=8",
            "-f", "lavfi", "-i", $"aevalsrc='{ramp}|{ramp}':s=48000:d=6,asetpts='PTS+1.1/TB+if(gte(T,2),1.25/TB,0)'",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-c:v", "ffv1", "-c:a", "pcm_s16le", shifted });
        var entry = new MoviePlaylistEntry { FilePath = shifted, AudioTrack = 1 };
        var settings = new MoviePlaylistSettings();
        settings.Movies.Add(entry);
        using (var sequence = new FileCaptureService.VideoSequenceSession(new[] { shifted }, settings))
        {
            sequence.SetLiveAudioAnalysisEnabled(true);
            sequence.SetAudioMaster(true, 1);
            sequence.SetAudioEnabled(true);
            var samples = new float[2048];
            int silenceChecks = 0, timestampChecks = 0;
            double worst = 0;
            foreach (double start in new[] { 0.0, 4.55 })
            {
                sequence.JumpToMovie(entry.Id, start);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed.TotalSeconds < (start == 0 ? 8 : 2.5))
                {
                    sequence.CaptureFrame(160, 90, FitMode.Fit, true);
                    Array.Clear(samples);
                    int count = sequence.MixLiveAudioSamples(samples);
                    if (count > 0 && sequence.TryGetPlaybackState(out var state))
                    {
                        double actual = samples[count - 1] * 20;
                        if (state.PositionSeconds < 0.8 || state.PositionSeconds is > 3.35 and < 4.1)
                        {
                            Require(Math.Abs(actual) < 0.001, "A delayed/gapped audio track played before its container timestamp.");
                            silenceChecks++;
                        }
                        else if (actual > 0.001)
                        {
                            double drift = Math.Abs(actual - state.PositionSeconds);
                            worst = Math.Max(worst, drift);
                            Require(drift < 0.25, $"Audio sample timestamp drifted {drift:F3}s after a track start offset/gap/seek.");
                            timestampChecks++;
                        }
                    }
                    Thread.Sleep(10);
                }
            }
            Require(silenceChecks > 10 && timestampChecks > 80, "Audio timestamp regression did not exercise silence, content and a seek.");
            Console.WriteLine($"Movie audio timestamps: 1.1s delayed English track, 1.25s packet gap, seek after gap; {silenceChecks} silence checks, {timestampChecks} timestamp checks, drift <= {worst:F3}s.");
        }

        const double fps = 24000.0 / 1001;
        string movie = Path.Combine(directory, "sustained-video-overload.mkv");
        Generate(new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "nullsrc=s=160x90:r=24000/1001:d=60,format=gbrp,geq=r='mod(N,256)':g='floor(N/256)':b=0",
            "-f", "lavfi", "-i", "aevalsrc='t/150|t/150':s=48000:d=60",
            "-map", "0:v:0", "-map", "1:a:0", "-c:v", "ffv1", "-pix_fmt", "bgr0", "-c:a", "pcm_s16le", movie });
        var overloadedEntry = new MoviePlaylistEntry { FilePath = movie };
        var overloadedSettings = new MoviePlaylistSettings();
        overloadedSettings.Movies.Add(overloadedEntry);
        using var overloaded = new FileCaptureService.VideoSequenceSession(new[] { movie }, overloadedSettings);
        overloaded.SetLiveAudioAnalysisEnabled(true);
        overloaded.SetAudioMaster(true, 1);
        overloaded.SetAudioEnabled(true);
        var scratch = new float[2048];
        var warmup = Stopwatch.StartNew();
        int initialSamples = 0;
        while (initialSamples == 0 && warmup.Elapsed.TotalSeconds < 10)
        {
            overloaded.CaptureFrame(160, 90, FitMode.Fit, true);
            initialSamples += overloaded.MixLiveAudioSamples(scratch);
            Thread.Sleep(10);
        }
        Require(initialSamples > 0, "Sustained overload regression did not warm up audio/video.");
        var original = overloaded.GetSubtitlePipelineStateForSmoke();
        overloaded.SetLiveFrameReadDelayForSmoke(75); // At most 13.33 frames/s, continuously, not one brief stall.
        var timer = Stopwatch.StartNew();
        double recoveryAt = -1, worstVideo = 0, worstAudio = 0;
        int recoveredChecks = 0;
        while (timer.Elapsed.TotalSeconds < 32)
        {
            var frame = overloaded.CaptureFrame(160, 90, FitMode.Fit, true);
            Array.Clear(scratch);
            int count = overloaded.MixLiveAudioSamples(scratch);
            if (!string.IsNullOrEmpty(overloaded.PlaybackStatus) &&
                overloaded.GetSubtitlePipelineStateForSmoke().videoPid != original.videoPid && recoveryAt < 0)
                recoveryAt = timer.Elapsed.TotalSeconds;
            if (frame.HasValue && count > 0 && timer.Elapsed.TotalSeconds > Math.Max(24, recoveryAt + 4) &&
                overloaded.TryGetPlaybackState(out var state))
            {
                var pixels = frame.Value.OverlayDownscaled;
                double actualVideo = (pixels[2] + pixels[1] * 256) / fps;
                double videoDrift = Math.Abs(actualVideo - state.PositionSeconds);
                double audioDrift = Math.Abs(scratch[count - 1] * 150 - state.PositionSeconds);
                worstVideo = Math.Max(worstVideo, videoDrift);
                worstAudio = Math.Max(worstAudio, audioDrift);
                Require(videoDrift < 0.4 && audioDrift < 0.25,
                    $"Sustained overload did not recover actual frames/PCM: video={videoDrift:F3}s audio={audioDrift:F3}s.");
                recoveredChecks++;
            }
            Thread.Sleep(10);
        }
        Require(recoveryAt > 0 && recoveredChecks > 50 && overloaded.PlaybackStatus.Contains("12 fps"),
            "Sustained overload regression did not lower the output budget and recover.");
        overloaded.SetPlaybackPaused(true);
        double held = overloaded.GetBookmark().seconds;
        Thread.Sleep(100);
        Require(Math.Abs(overloaded.GetBookmark().seconds - held) < 0.001, "Adaptive playback did not hold pause.");
        overloaded.SetPlaybackPaused(false);
        var resume = Stopwatch.StartNew();
        int resumeSamples = 0;
        while (resume.Elapsed.TotalSeconds < 2)
        {
            overloaded.CaptureFrame(160, 90, FitMode.Fit, true);
            Array.Clear(scratch);
            resumeSamples += overloaded.MixLiveAudioSamples(scratch);
            Thread.Sleep(10);
        }
        Require(resumeSamples > 1000 && overloaded.PlaybackStatus.Contains("12 fps"), "Pause/play discarded adaptive recovery or audio.");
        Console.WriteLine($"Movie sustained overload: 75ms per decoded frame, recovered at {recoveryAt:F2}s with 12 fps; {recoveredChecks} actual frame/PCM checks, video <= {worstVideo:F3}s audio <= {worstAudio:F3}s; pause/play retained recovery.");
    }
}
