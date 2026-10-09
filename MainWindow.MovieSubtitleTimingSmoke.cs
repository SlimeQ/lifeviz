using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace lifeviz;

public partial class MainWindow
{
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
    }
}
