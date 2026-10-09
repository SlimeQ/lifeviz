using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace lifeviz;

public partial class MainWindow
{
    [DllImport("ntdll.dll", EntryPoint = "NtSuspendProcess")]
    private static extern int SuspendSubtitleSmokeProcess(IntPtr handle);
    [DllImport("ntdll.dll", EntryPoint = "NtResumeProcess")]
    private static extern int ResumeSubtitleSmokeProcess(IntPtr handle);
    private void RunMovieSubtitleTimingChecks(string video, string srt, string directory)
    {
        static void Require(bool value, string message) => SmokeTestRunner.RequireSceneCheck(value, message);
        static bool HasCaption(FileCaptureService.FileCaptureFrame frame) =>
            Enumerable.Range(0, frame.OverlayDownscaled.Length / 4).Any(i => frame.OverlayDownscaled[i * 4] > 100);
        static FileCaptureService.FileCaptureFrame Frame(FileCaptureService.VideoSequenceSession sequence, long newerThan = -1)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 10)
            {
                var frame = sequence.CaptureFrame(320, 180, FitMode.Fit, true);
                if (frame.HasValue && frame.Value.FrameToken > newerThan) return frame.Value;
                Thread.Sleep(10);
            }
            throw new TimeoutException("Subtitle timing test did not publish a fresh movie frame.");
        }

        foreach (string mode in new[] { "Srt", "Embedded" })
        {
            var movie = new MoviePlaylistEntry { FilePath = video, SubtitleMode = mode, SubtitlePath = srt, SubtitleDelaySeconds = 1 };
            var settings = new MoviePlaylistSettings();
            settings.Movies.Add(movie);
            using var sequence = new FileCaptureService.VideoSequenceSession(new[] { video }, settings);
            sequence.SetOfflineRenderMode(true, 10);
            sequence.SetAudioMaster(false, 0);
            sequence.JumpToMovie(movie.Id, 0.25);
            Require(!HasCaption(Frame(sequence)), mode + " positive delay displayed subtitles too early.");
            sequence.JumpToMovie(movie.Id, 1.25);
            Require(HasCaption(Frame(sequence)), mode + " positive delay did not move the early caption later.");
            movie.SubtitleDelaySeconds = -1;
            sequence.UpdatePlaylist(settings);
            sequence.JumpToMovie(movie.Id, 1.25);
            Require(HasCaption(Frame(sequence)), mode + " negative delay did not move the late caption earlier.");
            sequence.JumpToMovie(movie.Id, 3.25);
            Require(!HasCaption(Frame(sequence)), mode + " negative delay failed to shift caption end time.");

            sequence.JumpToMovie(movie.Id, 1.25);
            var caption = Frame(sequence);
            sequence.SetPlaybackPaused(true);
            var held = sequence.GetBookmark();
            movie.SubtitleDelaySeconds = 0;
            sequence.UpdatePlaylist(settings);
            var refreshed = Frame(sequence, caption.FrameToken);
            Require(!HasCaption(refreshed) && Math.Abs(sequence.GetBookmark().seconds - held.seconds) < 0.001 &&
                sequence.TryGetPlaybackState(out var pausedState) && pausedState.IsPaused,
                mode + " paused timing edit did not refresh captions while holding its clock.");
        }

        // Keep the unshifted cue far beyond this fixture's duration so a changed
        // pixel state proves a live command, independently of natural cue edges.
        string farSrt = Path.Combine(directory, "live-timing.srt");
        File.WriteAllText(farSrt, "1\n00:01:40,000 --> 00:01:50,000\nLIVE TIMING CAPTION\n");
        string longMovie = Path.Combine(directory, "live-timing.mkv");
        var info = new ProcessStartInfo { FileName = "ffmpeg", CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-y", "-stream_loop", "2", "-i", video,
            "-map", "0:v:0", "-map", "0:a:1", "-t", "12", "-c", "copy", longMovie }) info.ArgumentList.Add(argument);
        using (var process = FfmpegProcessManager.Shared.Start(info))
        {
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Live timing fixture generation timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }
        var liveMovie = new MoviePlaylistEntry { FilePath = longMovie, SubtitleMode = "Srt", SubtitlePath = farSrt };
        var liveSettings = new MoviePlaylistSettings { ResumePlayback = true, BookmarkMovieId = liveMovie.Id, BookmarkSeconds = 1.25 };
        liveSettings.Movies.Add(liveMovie);
        using var live = new FileCaptureService.VideoSequenceSession(new[] { longMovie }, liveSettings);
        live.SetLiveAudioAnalysisEnabled(true);
        live.SetAudioMaster(true, 1);
        live.SetAudioEnabled(true);
        Require(!HasCaption(Frame(live)), "Unshifted live timing cue unexpectedly appeared.");
        int audioSamples = 0;
        var scratch = new float[2048];
        var warmup = Stopwatch.StartNew();
        while (audioSamples == 0 && warmup.Elapsed.TotalSeconds < 5)
        {
            live.CaptureFrame(320, 180, FitMode.Fit, true);
            audioSamples += live.MixLiveAudioSamples(scratch);
            Thread.Sleep(10);
        }
        var pipeline = live.GetSubtitlePipelineStateForSmoke();
        Require(pipeline.videoPid.HasValue && pipeline.audioPid.HasValue && audioSamples > 0,
            "Live subtitle timing test did not start both movie and audio decoders.");
        double positionBefore = live.GetBookmark().seconds;
        var adjustmentClock = Stopwatch.StartNew();
        long lastFrameToken = -1;
        double lastFrameTime = 0, lastAudioTime = 0, maxFrameGap = 0, maxAudioGap = 0;

        void WaitForCaption(bool expected)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 3)
            {
                var frame = live.CaptureFrame(320, 180, FitMode.Fit, true);
                int count = live.MixLiveAudioSamples(scratch);
                audioSamples += count;
                double elapsed = adjustmentClock.Elapsed.TotalSeconds;
                if (count > 0) { maxAudioGap = Math.Max(maxAudioGap, elapsed - lastAudioTime); lastAudioTime = elapsed; }
                if (frame.HasValue && frame.Value.FrameToken != lastFrameToken)
                {
                    maxFrameGap = Math.Max(maxFrameGap, elapsed - lastFrameTime);
                    lastFrameTime = elapsed;
                    lastFrameToken = frame.Value.FrameToken;
                }
                Require(live.GetSubtitlePipelineStateForSmoke() == pipeline,
                    "A playing subtitle nudge replaced a movie/audio decoder or its pipeline generation.");
                if (frame.HasValue && HasCaption(frame.Value) == expected) return;
                Thread.Sleep(10);
            }
            throw new InvalidOperationException("Live subtitle timing command did not change rendered caption pixels.");
        }
        liveMovie.SubtitleDelaySeconds = -100;
        live.UpdatePlaylist(liveSettings);
        WaitForCaption(true);
        // Rapid small edits coalesce to the latest value without process churn.
        for (int i = 0; i < 5; i++)
        {
            liveMovie.SubtitleDelaySeconds -= 0.1;
            live.UpdatePlaylist(liveSettings);
        }
        Require(liveMovie.SubtitleDelaySeconds == -100.5, "Repeated subtitle nudges accumulated floating-point drift.");
        liveMovie.SubtitleDelaySeconds = 0;
        live.UpdatePlaylist(liveSettings);
        WaitForCaption(false);
        liveMovie.SubtitleDelaySeconds = -100;
        live.UpdatePlaylist(liveSettings);
        WaitForCaption(true);
        double positionAfter = live.GetBookmark().seconds;
        double wallSeconds = adjustmentClock.Elapsed.TotalSeconds;
        Console.WriteLine($"Subtitle timing continuity: media={positionAfter - positionBefore:F3}s, wall={wallSeconds:F3}s, videoGap={maxFrameGap:F3}s, audioGap={maxAudioGap:F3}s, samples={audioSamples}.");
        Require(positionAfter > positionBefore && Math.Abs((positionAfter - positionBefore) - wallSeconds) < 0.15 &&
            audioSamples > 2048 && maxFrameGap < 0.4 && maxAudioGap < 0.4,
            "Subtitle nudges interrupted frame/audio delivery or reset the movie clock.");
        Console.WriteLine("Subtitle timing smoke passed: signed SRT/embedded delays, seek timing, paused still refresh, live caption commands, rapid nudges, unchanged movie/audio processes and advancing clock.");
        RunMovieSubtitleDriftChecks(directory);
    }

    private void RunMovieSubtitleDriftChecks(string directory)
    {
        // Encode source frame numbers in an untouched corner. A wall-clock
        // bookmark can advance normally even while actual movie frames fall behind.
        const double fps = 24000.0 / 1001;
        string video = Path.Combine(directory, "subtitle-drift.mkv");
        string srt = Path.Combine(directory, "subtitle-drift.srt");
        File.WriteAllText(srt, "1\n00:00:00,000 --> 00:00:02,000\nFIRST CUE\n\n2\n00:00:03,000 --> 00:00:05,000\nSECOND CUE\n\n3\n00:00:06,000 --> 00:00:10,000\nTHIRD CUE\n");
        var info = new ProcessStartInfo { FileName = "ffmpeg", CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            "nullsrc=s=160x90:r=24000/1001:d=110,format=gbrp,geq=r='mod(N,256)':g='floor(N/256)':b=0",
            "-f", "lavfi", "-i", "sine=frequency=880:duration=110", "-i", srt,
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:s:0", "-c:v", "ffv1", "-pix_fmt", "bgr0", "-c:a", "pcm_s16le", "-c:s", "srt", video })
            info.ArgumentList.Add(arg);
        using (var process = FfmpegProcessManager.Shared.Start(info))
        {
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); throw new TimeoutException("Subtitle drift fixture generation timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
        }

        foreach (string mode in new[] { "Srt", "Embedded" })
        {
            var movie = new MoviePlaylistEntry { FilePath = video, SubtitleMode = mode, SubtitlePath = srt, SubtitleDelaySeconds = 84 };
            var settings = new MoviePlaylistSettings { ResumePlayback = true, BookmarkMovieId = movie.Id, BookmarkSeconds = 84 };
            settings.Movies.Add(movie);
            using var sequence = new FileCaptureService.VideoSequenceSession(new[] { video }, settings);
            sequence.SetPerformanceSettings(false, 2, mode == "Srt" ? 30 : 0);
            sequence.SetLiveAudioAnalysisEnabled(true);
            sequence.SetAudioEnabled(true);
            sequence.SetAudioMaster(true, 1);
            var samples = new float[4096];
            var clock = Stopwatch.StartNew();
            int sampleCount = 0, cueChecks = 0, recoveries = 0;
            double worstDrift = 0, nextStall = 1, recoverAfter = 0.5;
            bool warm = false;
            var pipeline = default((long videoGeneration, long audioGeneration, int? videoPid, int? audioPid));
            while (clock.Elapsed.TotalSeconds < 8)
            {
                var frame = sequence.CaptureFrame(160, 90, FitMode.Fit, true);
                sampleCount += sequence.MixLiveAudioSamples(samples);
                if (frame.HasValue && sequence.TryGetPlaybackState(out var state))
                {
                    var pixels = frame.Value.OverlayDownscaled;
                    double movieSeconds = (pixels[2] + pixels[1] * 256) / fps;
                    if (!warm && sampleCount > 0)
                    {
                        warm = true;
                        clock.Restart();
                        pipeline = sequence.GetSubtitlePipelineStateForSmoke();
                    }
                    if (warm)
                    {
                        SmokeTestRunner.RequireSceneCheck(sequence.GetSubtitlePipelineStateForSmoke() == pipeline,
                            mode + " large timing edits replaced a running decoder.");
                        double drift = Math.Abs(state.PositionSeconds - movieSeconds);
                        // Give a blocked pipe a short opportunity to catch up.
                        if (clock.Elapsed.TotalSeconds < nextStall - 0.1 && clock.Elapsed.TotalSeconds > recoverAfter)
                        {
                            worstDrift = Math.Max(worstDrift, drift);
                            SmokeTestRunner.RequireSceneCheck(drift < 0.25,
                                $"{mode} subtitle video drifted {drift:F3}s from audio/movie clock after a brief stall.");
                            recoveries++;
                        }
                        double subtitleSeconds = movieSeconds - movie.SubtitleDelaySeconds;
                        bool nearEdge = new[] { 0.0, 2, 3, 5, 6, 10 }.Any(edge => Math.Abs(subtitleSeconds - edge) < 0.15);
                        if (!nearEdge)
                        {
                            bool expected = subtitleSeconds >= 0 && subtitleSeconds < 2 || subtitleSeconds >= 3 && subtitleSeconds < 5 || subtitleSeconds >= 6 && subtitleSeconds < 10;
                            bool caption = Enumerable.Range(0, pixels.Length / 4).Any(i => pixels[i * 4] > 100);
                            SmokeTestRunner.RequireSceneCheck(caption == expected, mode + " caption cue diverged from the actual movie frame with +84 s delay.");
                            cueChecks++;
                        }
                        if (clock.Elapsed.TotalSeconds >= nextStall && nextStall < 6)
                        {
                            // Stall only this test's video decoder. Sleeping just
                            // the consumer lets FFmpeg buffer through the stall,
                            // masking the old pacing filter's clock reset.
                            using (var decoder = Process.GetProcessById(pipeline.videoPid!.Value))
                            {
                                if (SuspendSubtitleSmokeProcess(decoder.Handle) < 0)
                                    throw new InvalidOperationException("Could not suspend the isolated subtitle test decoder.");
                                try { Thread.Sleep(300); }
                                finally
                                {
                                    if (ResumeSubtitleSmokeProcess(decoder.Handle) < 0)
                                        throw new InvalidOperationException("Could not resume the isolated subtitle test decoder.");
                                }
                            }
                            movie.SubtitleDelaySeconds = nextStall % 2 == 0 ? 84 : 84.1;
                            sequence.UpdatePlaylist(settings);
                            recoverAfter = clock.Elapsed.TotalSeconds + 0.4;
                            nextStall += 1;
                        }
                    }
                }
                Thread.Sleep(10);
            }
            SmokeTestRunner.RequireSceneCheck(warm && sampleCount > 48000 && cueChecks > 20 && recoveries > 20,
                mode + " subtitle drift test did not exercise movie frames, captions, PCM and recovery.");
            Console.WriteLine($"Subtitle drift smoke {mode}: +84 s, fractional {fps:F3} fps, actual-frame drift <= {worstDrift:F3}s after repeated 300 ms video-only stalls, {cueChecks} cue checks, {sampleCount} PCM samples, same decoders.");
        }
    }
}
