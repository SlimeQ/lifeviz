using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace lifeviz;

/// <summary>
/// Tempo-synced video loop checks. Generates lossless loops whose frame index is
/// encoded in the pixel color (R = 4 * index), drives a fake beat clock, and asserts the
/// exact frame shown: streamed and cached, direct-scaled and CPU-scaled, across loop
/// wraps, a backwards downbeat resync, fractional frame rates, live following at a
/// different tempo, and AutoClip phase ends snapping to bar lines.
/// </summary>
internal static class TempoSyncSmoke
{
    private sealed class FakeClock : ITempoClock
    {
        public double BeatPosition { get; set; }
        public double Bpm { get; set; } = 120;
    }

    public static int Run()
    {
        var failures = new List<string>();
        void Expect(bool ok, string message)
        {
            if (!ok) failures.Add(message);
            Logger.Info($"Tempo sync: {(ok ? "PASS" : "FAIL")} {message}");
        }

        string directory = Path.Combine(Path.GetTempPath(), "lifeviz-tempo-sync-smoke");
        Directory.CreateDirectory(directory);
        string loop24 = Path.Combine(directory, "loop-48f-24fps.mkv");   // 2 s = 4 beats at 120 BPM
        string loop28 = Path.Combine(directory, "loop-56f-28fps.mkv");   // 2 s, fractional frame period
        GenerateLoop(loop24, 48, 24);
        GenerateLoop(loop28, 56, 28);

        var clock = new FakeClock();
        FileCaptureService.TempoClock = clock;
        long originalBudget = FileCaptureService.LoopFrameCache.BudgetBytes;
        try
        {
            Expect(TempoLoopMath.ResolveLoopBeats(768, 28, 140, 0) == 64 &&
                   TempoLoopMath.ResolveLoopBeats(192, 28, 140, 0) == 16 &&
                   TempoLoopMath.ResolveLoopBeats(144, 28, 140, 0) == 12 &&
                   TempoLoopMath.ResolveLoopBeats(144, 28, 140, 8) == 8,
                "loop beats derive from frames / fps at the tagged BPM (768/192/144 frames @ 28 fps, 140 BPM -> 64/16/12)");
            Expect(TempoLoopMath.TargetAbsoluteFrame(0, 4, 48) == 0 &&
                   TempoLoopMath.TargetAbsoluteFrame(1, 4, 48) == 12 &&
                   TempoLoopMath.TargetAbsoluteFrame(4, 4, 48) == 48 &&
                   TempoLoopMath.TargetAbsoluteFrame(-0.5, 4, 48) == -6,
                "beat position maps to absolute loop frames");
            double snap = TempoLoopMath.SecondsUntilBarAlignedEnd(beatPosition: 5, bpm: 120, durationSeconds: 1.3);
            Expect(Math.Abs(snap - 1.5) < 1e-9, $"phase end snaps to the nearest bar line (5 beats + 1.3 s -> beat 8 = {snap:F3}s)");

            FileCaptureService.LoopFrameCache.Clear();
            FileCaptureService.LoopFrameCache.SetBudgetBytes(0);
            Expect(RunOfflineSequence(loop24, clock, 48, 32, 18, out string streamDirect), $"streamed offline frames exact (direct scale): {streamDirect}");
            Expect(RunOfflineSequence(loop24, clock, 48, 128, 72, out string streamCpu), $"streamed offline frames exact (CPU scale): {streamCpu}");
            Expect(RunOfflineSequence(loop28, clock, 56, 32, 18, out string stream28), $"streamed offline frames exact at 28 fps: {stream28}");

            Expect(RunSharedFileLayers(loop24, clock, out string shared), $"two layers on one file keep independent tempo tags: {shared}");

            FileCaptureService.LoopFrameCache.SetBudgetBytes(64L * 1024 * 1024);
            Expect(RunOfflineSequence(loop24, clock, 48, 32, 18, out string cached), $"cached offline frames exact: {cached}");
            long expectedBytes = 48L * 32 * 18 * 4;
            var fillWait = Stopwatch.StartNew();
            while (FileCaptureService.LoopFrameCache.CompleteEntryCount < 1 && fillWait.Elapsed.TotalSeconds < 10)
            {
                Thread.Sleep(20);
            }

            Expect(FileCaptureService.LoopFrameCache.UsedBytes == expectedBytes && FileCaptureService.LoopFrameCache.CompleteEntryCount == 1,
                $"cache holds exactly one 48-frame 32x18 loop ({FileCaptureService.LoopFrameCache.UsedBytes} bytes, expected {expectedBytes})");
            FileCaptureService.LoopFrameCache.SetBudgetBytes(expectedBytes - 1);
            Expect(FileCaptureService.LoopFrameCache.UsedBytes == 0, "shrinking the budget evicts loops that no longer fit");

            FileCaptureService.LoopFrameCache.SetBudgetBytes(0);
            Expect(RunLiveFollow(loop24, clock, 48, bpm: 150, out string live), $"live streaming follows a faster clock: {live}");
            FileCaptureService.LoopFrameCache.SetBudgetBytes(64L * 1024 * 1024);
            Expect(RunLiveFollow(loop24, clock, 48, bpm: 95, out string liveCached), $"live cached playback follows a slower clock: {liveCached}");

            FileCaptureService.LoopFrameCache.SetBudgetBytes(0);
            Expect(RunOverloadStress(directory, clock, out string stress), $"an overloaded decoder backs off instead of restarting in a loop: {stress}");

            Expect(RunAutoClipBarSnap(loop24, loop28, clock, out string autoClip), $"AutoClip clip boundaries land on bar lines: {autoClip}");
            Expect(RunLayerConfigRoundTrip(loop24, out string persistence), $"tempo settings survive the layer config file: {persistence}");
        }
        finally
        {
            FileCaptureService.TempoClock = null;
            FileCaptureService.LoopFrameCache.Clear();
            FileCaptureService.LoopFrameCache.SetBudgetBytes(originalBudget);
        }

        if (failures.Count > 0)
        {
            Logger.Error($"Tempo sync smoke failed: {string.Join("; ", failures)}");
            return 1;
        }

        Logger.Info("Tempo sync smoke passed.");
        return 0;
    }

    private static bool RunOfflineSequence(string path, FakeClock clock, int frameCount, int width, int height, out string summary)
    {
        using var service = new FileCaptureService();
        Guid layer = Guid.NewGuid();
        var settings = new TempoSyncSettings(120);
        service.BeginOfflineRender(30, 0);
        var positions = new List<double>();
        for (int i = 0; i <= 60; i++) positions.Add(i * 0.37);        // > 5 loop wraps, uneven steps
        for (int i = 0; i <= 20; i++) positions.Add(3.1 + i * 0.29);  // backwards jump (downbeat resync)
        int mismatches = 0;
        int missing = 0;
        string firstMismatch = "";
        foreach (double position in positions)
        {
            clock.BeatPosition = position;
            var frame = service.CaptureTempoLayerFrame(layer, path, settings, width, height, FitMode.Fill);
            int expected = TempoLoopMath.LocalFrame(TempoLoopMath.TargetAbsoluteFrame(position, 4, frameCount), frameCount);
            if (!frame.HasValue)
            {
                missing++;
                continue;
            }

            int actual = DecodeIndex(frame.Value.OverlayDownscaled, width, height);
            if (actual != expected)
            {
                if (mismatches == 0) firstMismatch = $" first at beat {position:F2}: got {actual}, expected {expected}";
                mismatches++;
            }
        }

        service.EndOfflineRender();
        summary = $"{positions.Count} positions, {mismatches} wrong, {missing} missing{firstMismatch}";
        return mismatches == 0 && missing == 0;
    }

    /// <summary>
    /// Two synced layers on the same 2 s loop: one tagged 120 BPM (4 beats), one tagged
    /// 60 BPM (2 beats, so it plays twice as fast). Each must show its own exact frame,
    /// and releasing one must leave the other playing.
    /// </summary>
    /// <summary>
    /// Regression for the restart storm: a 1080p60 file with a single keyframe (so every
    /// seek decodes from the start) driven ~25x faster than it can decode. The player must
    /// back off (few decoder starts, a bounded number alive) and keep showing frames.
    /// </summary>
    private static bool RunOverloadStress(string directory, FakeClock clock, out string summary)
    {
        string heavy = Path.Combine(directory, "heavy-1080p60-onekey.mp4");
        if (!File.Exists(heavy))
        {
            var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=60:duration=15",
                         "-c:v", "libx264", "-preset", "ultrafast", "-g", "900", "-pix_fmt", "yuv420p", heavy })
            {
                psi.ArgumentList.Add(arg);
            }

            using Process process = FfmpegProcessManager.Shared.Start(psi);
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }

        using var service = new FileCaptureService();
        Guid layer = Guid.NewGuid();
        var settings = new TempoSyncSettings(120); // 15 s at 120 BPM = 30 beats
        clock.Bpm = 3000;                           // 25x: needs ~1500 decoded fps
        int startsBefore = FileCaptureService.LoopStreamDecoder.StartedCount;
        int peakLive = 0;
        int changes = 0;
        long lastToken = -1;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < 10)
        {
            clock.BeatPosition = stopwatch.Elapsed.TotalSeconds * clock.Bpm / 60.0;
            var frame = service.CaptureTempoLayerFrame(layer, heavy, settings, 1920, 1080, FitMode.Fill);
            if (frame.HasValue && frame.Value.FrameToken != lastToken)
            {
                lastToken = frame.Value.FrameToken;
                changes++;
            }

            peakLive = Math.Max(peakLive, FileCaptureService.LoopStreamDecoder.LiveCount);
            Thread.Sleep(16);
        }

        clock.Bpm = 120;
        int starts = FileCaptureService.LoopStreamDecoder.StartedCount - startsBefore;
        summary = $"{starts} decoder starts in 10 s, peak {peakLive} alive, {changes} frames shown";
        // Without backoff this restarts every 0.75 s (13+ starts) and piles up decoders.
        return starts <= 6 && peakLive <= 3 && changes > 10;
    }

    private static bool RunSharedFileLayers(string path, FakeClock clock, out string summary)
    {
        using var service = new FileCaptureService();
        Guid slow = Guid.NewGuid();
        Guid fast = Guid.NewGuid();
        service.BeginOfflineRender(30, 0);
        int mismatches = 0;
        int differing = 0;
        for (int i = 0; i <= 40; i++)
        {
            double position = i * 0.31;
            clock.BeatPosition = position;
            var slowFrame = service.CaptureTempoLayerFrame(slow, path, new TempoSyncSettings(120), 32, 18, FitMode.Fill);
            var fastFrame = service.CaptureTempoLayerFrame(fast, path, new TempoSyncSettings(60), 32, 18, FitMode.Fill);
            int slowExpected = TempoLoopMath.LocalFrame(TempoLoopMath.TargetAbsoluteFrame(position, 4, 48), 48);
            int fastExpected = TempoLoopMath.LocalFrame(TempoLoopMath.TargetAbsoluteFrame(position, 2, 48), 48);
            int slowActual = slowFrame.HasValue ? DecodeIndex(slowFrame.Value.OverlayDownscaled, 32, 18) : -1;
            int fastActual = fastFrame.HasValue ? DecodeIndex(fastFrame.Value.OverlayDownscaled, 32, 18) : -1;
            if (slowActual != slowExpected || fastActual != fastExpected) mismatches++;
            if (slowActual != fastActual) differing++;
        }

        service.ReleaseTempoLayer(fast);
        clock.BeatPosition = 13.37;
        var remaining = service.CaptureTempoLayerFrame(slow, path, new TempoSyncSettings(120), 32, 18, FitMode.Fill);
        int remainingExpected = TempoLoopMath.LocalFrame(TempoLoopMath.TargetAbsoluteFrame(13.37, 4, 48), 48);
        bool survived = remaining.HasValue && DecodeIndex(remaining.Value.OverlayDownscaled, 32, 18) == remainingExpected &&
                        service.IsTempoLayerActive(slow) && !service.IsTempoLayerActive(fast);
        service.EndOfflineRender();
        summary = $"41 positions, {mismatches} wrong, layers differed at {differing}; other layer {(survived ? "kept playing" : "broke")} after release";
        return mismatches == 0 && differing > 20 && survived;
    }

    private static bool RunLiveFollow(string path, FakeClock clock, int frameCount, double bpm, out string summary)
    {
        using var service = new FileCaptureService();
        Guid layer = Guid.NewGuid();
        var settings = new TempoSyncSettings(120);
        clock.Bpm = bpm;
        int decodersBefore = FileCaptureService.LoopStreamDecoder.StartedCount;
        var stopwatch = Stopwatch.StartNew();
        var lags = new List<int>();
        int lastShown = -1;
        double lastChange = 0;
        double maxStall = 0;
        while (stopwatch.Elapsed.TotalSeconds < 5)
        {
            double now = stopwatch.Elapsed.TotalSeconds;
            clock.BeatPosition = 10 + now * bpm / 60.0;
            var frame = service.CaptureTempoLayerFrame(layer, path, settings, 32, 18, FitMode.Fill);
            if (frame.HasValue && now > 1.0)
            {
                int expected = TempoLoopMath.LocalFrame(TempoLoopMath.TargetAbsoluteFrame(clock.BeatPosition, 4, frameCount), frameCount);
                int actual = DecodeIndex(frame.Value.OverlayDownscaled, 32, 18);
                lags.Add(((expected - actual) % frameCount + frameCount) % frameCount);
                if (actual != lastShown)
                {
                    if (lastShown >= 0) maxStall = Math.Max(maxStall, now - lastChange);
                    lastShown = actual;
                    lastChange = now;
                }
            }

            Thread.Sleep(16);
        }

        clock.Bpm = 120;
        int decoders = FileCaptureService.LoopStreamDecoder.StartedCount - decodersBefore;
        lags.Sort();
        int p95 = lags.Count > 0 ? lags[(int)Math.Min(lags.Count - 1, lags.Count * 0.95)] : int.MaxValue;
        summary = $"{bpm} BPM: {lags.Count} samples, lag p50 {(lags.Count > 0 ? lags[lags.Count / 2] : -1)} / p95 {p95} frames, longest hold {maxStall * 1000:F0} ms, {decoders} decoder start(s)";
        // At most two frames behind 95% of the time, never frozen for long, and no
        // restart churn (one forward-following decoder covers steady playback).
        return lags.Count > 100 && p95 <= 2 && maxStall < 0.25 && decoders <= 1;
    }

    private static bool RunAutoClipBarSnap(string first, string second, FakeClock clock, out string summary)
    {
        using var session = new FileCaptureService.AutoClipSession(new[] { first, second }, 1.3, 1.3, 0, 0);
        session.SetPlaybackOptions(startWithDelay: false, playInOrder: true, playWholeFile: false);
        session.SetTempoSync(_ => new TempoSyncSettings(120));
        session.SetOfflineRenderMode(true, 30);
        clock.Bpm = 120;
        var switches = new List<double>();
        string? lastPath = null;
        int frames = 0;
        for (int i = 0; i < 360; i++)
        {
            clock.BeatPosition = i / 30.0 * 2.0;
            var frame = session.CaptureFrame(32, 18, FitMode.Fill, includeSource: false);
            if (frame.HasValue) frames++;
            string? path = session.CurrentFramePath;
            if (path != null && !string.Equals(path, lastPath, StringComparison.OrdinalIgnoreCase))
            {
                if (lastPath != null) switches.Add(clock.BeatPosition);
                lastPath = path;
            }
        }

        session.SetOfflineRenderMode(false, 0);
        double worst = switches.Count == 0 ? double.PositiveInfinity : switches.Max(beat => Math.Abs(beat - Math.Round(beat / 4) * 4));
        summary = $"{switches.Count} switches at beats [{string.Join(", ", switches.Select(beat => beat.ToString("0.##")))}], worst bar offset {worst:F3} beats, {frames}/360 frames";
        // One render frame is 0.067 beats at 120 BPM / 30 fps.
        return switches.Count >= 3 && worst <= 0.07 && frames >= 350;
    }

    /// <summary>
    /// Diagnostic over real loops: a tempo-synced AutoClip of every video in
    /// <paramref name="directory"/>, captured live at 1280x720 while a fake clock runs
    /// at 150 BPM (loops tagged 140), first streaming only, then with the RAM cache.
    /// Logs how far behind the beat each frame was, stalls, cache fill and memory.
    /// </summary>
    public static int RunRealLoops(string? directory, double seconds = 30)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            Logger.Error($"tempo-sync-loops needs a folder of loops (got '{directory}').");
            return 1;
        }

        string[] paths = Directory.GetFiles(directory)
            .Where(FileCaptureService.SupportsTempoSyncPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            Logger.Error("tempo-sync-loops found no videos.");
            return 1;
        }

        var clock = new FakeClock { Bpm = 150 };
        FileCaptureService.TempoClock = clock;
        long originalBudget = FileCaptureService.LoopFrameCache.BudgetBytes;
        try
        {
            foreach (long budget in new[] { 0L, FileCaptureService.LoopFrameCache.DefaultBudgetBytes() })
            {
                FileCaptureService.LoopFrameCache.Clear();
                FileCaptureService.LoopFrameCache.SetBudgetBytes(budget);
                GC.Collect();
                long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
                using var session = new FileCaptureService.AutoClipSession(paths, 6, 6, 0, 0);
                session.SetPlaybackOptions(startWithDelay: false, playInOrder: true, playWholeFile: false);
                session.SetTempoSync(_ => new TempoSyncSettings(140));
                var stopwatch = Stopwatch.StartNew();
                long lastToken = -1;
                double lastChange = 0;
                double longestHold = 0;
                int frames = 0;
                int changes = 0;
                var clips = new List<string>();
                while (stopwatch.Elapsed.TotalSeconds < seconds)
                {
                    double now = stopwatch.Elapsed.TotalSeconds;
                    clock.BeatPosition = now * clock.Bpm / 60.0;
                    var frame = session.CaptureFrame(1280, 720, FitMode.Fill, includeSource: false);
                    if (frame.HasValue)
                    {
                        frames++;
                        if (frame.Value.FrameToken != lastToken)
                        {
                            if (lastToken >= 0 && now > 2) longestHold = Math.Max(longestHold, now - lastChange);
                            lastToken = frame.Value.FrameToken;
                            lastChange = now;
                            changes++;
                        }
                    }

                    string? current = session.CurrentFramePath;
                    if (current != null && (clips.Count == 0 || clips[^1] != current))
                    {
                        clips.Add(current);
                    }

                    Thread.Sleep(16);
                }

                // Loops play at 28 fps * 150/140 = 30 frame changes/s when keeping up.
                double changesPerSecond = changes / seconds;
                long managedAfter = GC.GetTotalMemory(forceFullCollection: false);
                Logger.Info(
                    $"Tempo loops ({(budget == 0 ? "streaming" : $"cache {budget / (1024.0 * 1024 * 1024):0.#} GiB")}): " +
                    $"{frames} captures, {changesPerSecond:0.0} new frames/s (30 expected), longest hold {longestHold * 1000:0} ms, " +
                    $"{clips.Count} clips [{string.Join(", ", clips.Select(Path.GetFileNameWithoutExtension))}], " +
                    $"cache {FileCaptureService.LoopFrameCache.UsedBytes / (1024.0 * 1024 * 1024):0.00} GiB in {FileCaptureService.LoopFrameCache.CompleteEntryCount} loops, " +
                    $"managed heap +{(managedAfter - managedBefore) / (1024.0 * 1024 * 1024):0.00} GiB, decoders started {FileCaptureService.LoopStreamDecoder.StartedCount}.");
            }
        }
        finally
        {
            FileCaptureService.TempoClock = null;
            FileCaptureService.LoopFrameCache.Clear();
            FileCaptureService.LoopFrameCache.SetBudgetBytes(originalBudget);
        }

        return 0;
    }

    /// <summary>
    /// Diagnostic for one synced File layer on any video (including long non-loop
    /// files): live capture at 1280x720 while a fake clock runs at 140 BPM. Logs frames,
    /// longest hold, decoder starts and live FFmpeg processes, so restart storms show up.
    /// </summary>
    public static int RunFileLayer(string? path, double seconds = 20)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Logger.Error($"tempo-sync-file needs a video path (got '{path}').");
            return 1;
        }

        var clock = new FakeClock { Bpm = double.TryParse(Environment.GetEnvironmentVariable("LIFEVIZ_TEMPO_BPM"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double bpm) ? bpm : 140, BeatPosition = 0 };
        FileCaptureService.TempoClock = clock;
        try
        {
            using var service = new FileCaptureService();
            Guid layer = Guid.NewGuid();
            int decodersBefore = FileCaptureService.LoopStreamDecoder.StartedCount;
            var stopwatch = Stopwatch.StartNew();
            long lastToken = -1;
            int changes = 0;
            double firstFrame = -1;
            double lastChange = 0;
            double longestHold = 0;
            int maxFfmpeg = 0;
            double nextSample = 0;
            while (stopwatch.Elapsed.TotalSeconds < seconds)
            {
                double now = stopwatch.Elapsed.TotalSeconds;
                clock.BeatPosition = now * clock.Bpm / 60.0;
                var frame = service.CaptureTempoLayerFrame(layer, path, new TempoSyncSettings(140), 1280, 720, FitMode.Fill);
                if (frame.HasValue && frame.Value.FrameToken != lastToken)
                {
                    if (firstFrame < 0) firstFrame = now;
                    else longestHold = Math.Max(longestHold, now - lastChange);
                    lastToken = frame.Value.FrameToken;
                    lastChange = now;
                    changes++;
                }

                if (now >= nextSample)
                {
                    maxFfmpeg = Math.Max(maxFfmpeg, Process.GetProcessesByName("ffmpeg").Length);
                    nextSample = now + 0.5;
                }

                Thread.Sleep(16);
            }

            int decoders = FileCaptureService.LoopStreamDecoder.StartedCount - decodersBefore;
            Logger.Info($"Tempo file layer {Path.GetFileName(path)}: first frame {firstFrame:0.00}s, {changes / Math.Max(0.001, seconds - Math.Max(0, firstFrame)):0.0} new frames/s, longest hold {longestHold * 1000:0} ms, {decoders} decoder start(s), peak {maxFfmpeg} ffmpeg processes; {service.DescribeTempoLayer(layer)}");
            return 0;
        }
        finally
        {
            FileCaptureService.TempoClock = null;
        }
    }

    private static bool RunLayerConfigRoundTrip(string loopPath, out string summary)
    {
        var autoClip = new LayerEditorSource
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSourceKind.AutoClip,
            DisplayName = "Tempo AutoClip",
            TempoSyncEnabled = true,
            TempoLoopBpm = 128
        };
        autoClip.FilePaths.Add(loopPath);
        autoClip.AutoClipVideoPaths.Add(loopPath);
        autoClip.AutoClipVideoOverrides.Add(new LayerEditorAutoClipVideoOverride { FilePath = loopPath, LoopBpm = 140 });
        var file = new LayerEditorSource
        {
            Id = Guid.NewGuid(),
            Kind = LayerEditorSourceKind.File,
            DisplayName = "Tempo File",
            FilePath = loopPath,
            TempoSyncEnabled = true
        };
        var config = LayerConfigFile.FromEditorSources(
            new[] { autoClip, file },
            Array.Empty<LayerEditorSimulationLayer>(),
            new LayerEditorProjectSettings());
        string json = System.Text.Json.JsonSerializer.Serialize(config);
        var restored = System.Text.Json.JsonSerializer.Deserialize<LayerConfigFile>(json)!.ToEditorSources();
        var restoredClip = restored.FirstOrDefault(source => source.Kind == LayerEditorSourceKind.AutoClip);
        var restoredFile = restored.FirstOrDefault(source => source.Kind == LayerEditorSourceKind.File);
        bool ok = restoredClip is { TempoSyncEnabled: true, SupportsTempoSync: true } &&
                  Math.Abs(restoredClip.TempoLoopBpm - 128) < 1e-9 &&
                  restoredClip.AutoClipVideoOverrides.Count == 1 &&
                  Math.Abs(restoredClip.AutoClipVideoOverrides[0].LoopBpm - 140) < 1e-9 &&
                  restoredFile is { TempoSyncEnabled: true, SupportsTempoSync: true, TempoLoopBpm: 0 };
        summary = ok
            ? "AutoClip sync + 128 BPM layer + 140 BPM file tag, File sync with scene default"
            : $"restored AutoClip sync={restoredClip?.TempoSyncEnabled} bpm={restoredClip?.TempoLoopBpm} tag={restoredClip?.AutoClipVideoOverrides.FirstOrDefault()?.LoopBpm}, File sync={restoredFile?.TempoSyncEnabled} supports={restoredFile?.SupportsTempoSync}";
        return ok;
    }

    private static int DecodeIndex(byte[] bgra, int width, int height)
    {
        int center = ((height / 2) * width + width / 2) * 4;
        return (int)Math.Round(bgra[center + 2] / 4.0);
    }

    internal static void GenerateLoop(string path, int frames, int fps)
    {
        if (File.Exists(path)) return;
        const int width = 64;
        const int height = 36;
        string raw = path + ".raw";
        using (var stream = File.Create(raw))
        {
            var frame = new byte[width * height * 4];
            for (int i = 0; i < frames; i++)
            {
                for (int p = 0; p < frame.Length; p += 4)
                {
                    frame[p] = 128;
                    frame[p + 1] = (byte)(255 - i * 4);
                    frame[p + 2] = (byte)(i * 4);
                    frame[p + 3] = 255;
                }

                stream.Write(frame);
            }
        }

        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-r", fps.ToString(), "-i", raw, "-c:v", "ffv1", path })
        {
            psi.ArgumentList.Add(arg);
        }

        using Process process = FfmpegProcessManager.Shared.Start(psi);
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        File.Delete(raw);
        if (process.ExitCode != 0 || !File.Exists(path))
        {
            throw new InvalidOperationException($"Could not generate tempo test loop: {errors}");
        }
    }
}
