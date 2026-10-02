using System;
using System.Collections.Generic;
using System.Linq;

namespace lifeviz;

/// <summary>
/// Synthetic-audio checks for <see cref="BeatTracker"/> + <see cref="BeatClock"/>:
/// tempo accuracy, octave folding, phase accuracy, jitter, tempo changes, dropouts,
/// and that the clock never runs backwards.
/// </summary>
internal static class BeatTrackingSmoke
{
    private const int SampleRate = 48000;
    private const double RenderFps = 60.0;

    private sealed record Hit(double Time, string Kind);

    public static int Run()
    {
        var failures = new List<string>();
        void Expect(bool ok, string message)
        {
            if (!ok) failures.Add(message);
            Logger.Info($"Beat tracking: {(ok ? "PASS" : "FAIL")} {message}");
        }

        foreach (double bpm in new[] { 100.0, 124.0, 128.0, 140.0, 174.0 })
        {
            var result = Simulate(FourOnFloor(bpm, 0, 20, jitterMs: 0, seed: 1), 20, settleSeconds: 8);
            Expect(Math.Abs(result.FinalBpm - bpm) < 0.5, $"{bpm} BPM four-on-floor -> {result.FinalBpm:F2} BPM");
            Expect(result.MaxPhaseErrorMs < 10, $"{bpm} BPM phase error {result.MaxPhaseErrorMs:F1} ms (p95 {result.P95PhaseErrorMs:F1}, mean signed {result.MeanSignedErrorMs:+0.0;-0.0} ms; + = clock early)");
            Expect(result.Monotonic, $"{bpm} BPM clock monotonic");
        }

        var halfTime = Simulate(HalfTime(70, 0, 20), 20, settleSeconds: 8);
        Expect(Math.Abs(halfTime.FinalBpm - 140) < 0.7, $"70 BPM half-time folds into range -> {halfTime.FinalBpm:F2} BPM");

        var breakbeat = Simulate(Breakbeat(174, 0, 20), 20, settleSeconds: 8);
        Expect(Math.Abs(breakbeat.FinalBpm - 174) < 0.7, $"174 BPM kick/snare breakbeat -> {breakbeat.FinalBpm:F2} BPM");
        Expect(breakbeat.P95PhaseErrorMs < 10, $"174 BPM breakbeat phase p95 {breakbeat.P95PhaseErrorMs:F1} ms (max {breakbeat.MaxPhaseErrorMs:F1}, mean signed {breakbeat.MeanSignedErrorMs:+0.0;-0.0})");

        var jitter = Simulate(FourOnFloor(140, 0, 20, jitterMs: 12, seed: 7), 20, settleSeconds: 8);
        Expect(Math.Abs(jitter.FinalBpm - 140) < 0.7, $"140 BPM with ±12 ms jitter -> {jitter.FinalBpm:F2} BPM");
        Expect(jitter.P95PhaseErrorMs < 10, $"140 BPM jitter phase p95 {jitter.P95PhaseErrorMs:F1} ms");
        Expect(jitter.MaxRateDeviation < 0.3, $"140 BPM jitter max rate deviation {jitter.MaxRateDeviation:P0}");

        var change = Simulate(FourOnFloor(124, 0, 15, 0, 3).Concat(FourOnFloor(140, 15, 35, 0, 4)), 35, settleSeconds: 26);
        Expect(Math.Abs(change.FinalBpm - 140) < 0.5, $"124 -> 140 BPM change -> {change.FinalBpm:F2} BPM");
        Expect(change.P95PhaseErrorMs < 10, $"124 -> 140 BPM change phase p95 after settle {change.P95PhaseErrorMs:F1} ms");
        Expect(change.Monotonic, "tempo change clock monotonic");

        var dropout = Simulate(FourOnFloor(128, 0, 12, 0, 5).Concat(FourOnFloor(128, 16, 26, 0, 6)), 26, settleSeconds: 8, silentFrom: 12, silentTo: 16);
        Expect(dropout.MaxPhaseErrorMs < 10, $"128 BPM 4 s dropout keeps phase, max error {dropout.MaxPhaseErrorMs:F1} ms");
        Expect(Math.Abs(dropout.FinalBpm - 128) < 0.5, $"128 BPM after dropout -> {dropout.FinalBpm:F2} BPM");

        // Manual clock: tempo change must not jump the position.
        var clock = new BeatClock();
        clock.Update(0, 120, null, 0);
        clock.Update(1, 120, null, 0);
        double before = clock.GetPosition(1);
        clock.Update(1 + 1 / RenderFps, 60, null, 0);
        double after = clock.GetPosition(1 + 1 / RenderFps);
        Expect(Math.Abs(before - 2.0) < 1e-9 && after > before && after - before < 0.02, $"manual clock BPM change is continuous ({before:F3} -> {after:F3})");

        if (failures.Count > 0)
        {
            Logger.Error($"Beat tracking smoke failed: {string.Join("; ", failures)}");
            return 1;
        }

        Logger.Info("Beat tracking smoke passed.");
        return 0;
    }

    /// <summary>
    /// Diagnostic for real music: decodes <paramref name="path"/> with FFmpeg and logs the
    /// tracker's tempo, confidence, lock and estimate jitter. No pass/fail: there is no
    /// ground truth, so compare against the track's known tempo.
    /// </summary>
    public static int RunFile(string? path, double maxSeconds = 150)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            Logger.Error($"beat-tracking-file needs an existing audio/video path (got '{path}').");
            return 1;
        }

        var start = new System.Diagnostics.ProcessStartInfo(FfmpegProcessManager.ResolveFfmpegExecutable())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-t", maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "-" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        using var memory = new System.IO.MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        float[] audio = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(memory.GetBuffer().AsSpan(0, (int)memory.Length)).ToArray();
        if (audio.Length == 0)
        {
            Logger.Error("beat-tracking-file decoded no audio.");
            return 1;
        }

        var tracker = new BeatTracker();
        var clock = new BeatClock();
        int chunk = SampleRate / 100;
        double nextRender = 0;
        double nextLog = 5;
        double firstLock = double.NaN;
        BeatEstimate? previous = null;
        var residuals = new List<double>();
        var clockErrors = new List<double>();
        int tempoJumps = 0;
        for (int offset = 0; offset < audio.Length; offset += chunk)
        {
            int count = Math.Min(chunk, audio.Length - offset);
            double endTime = (offset + count - 1) / (double)SampleRate;
            tracker.ProcessSamples(audio.AsSpan(offset, count), SampleRate, 1.0, endTime);
            BeatEstimate estimate = tracker.Estimate;
            if (estimate.Locked && double.IsNaN(firstLock)) firstLock = endTime;
            if (estimate.Locked && previous is { Locked: true } && !ReferenceEquals(previous, estimate))
            {
                double beat = 60.0 / estimate.Bpm;
                if (Math.Abs(estimate.Bpm / previous.Bpm - 1.0) > 0.03)
                {
                    tempoJumps++;
                    Logger.Info($"Beat file {endTime,6:F1}s: tempo jump {previous.Bpm:F2} -> {estimate.Bpm:F2} BPM (conf {estimate.Confidence:F2})");
                }
                else residuals.Add(Math.Abs(BeatClock.Fraction((estimate.BeatTimeSeconds - previous.BeatTimeSeconds) / beat + 0.5) - 0.5) * beat * 1000.0);
            }

            previous = estimate;
            while (nextRender <= endTime)
            {
                clock.Update(nextRender, 120, estimate, nextRender);
                if (estimate.Locked && nextRender > firstLock + 5)
                {
                    double target = (nextRender - estimate.BeatTimeSeconds) * estimate.Bpm / 60.0;
                    clockErrors.Add(Math.Abs(BeatClock.Fraction(clock.GetPosition(nextRender) - target + 0.5) - 0.5) * 60000.0 / estimate.Bpm);
                }

                nextRender += 1.0 / RenderFps;
            }

            if (endTime >= nextLog)
            {
                Logger.Info($"Beat file {endTime,6:F1}s: est {estimate.Bpm,6:F2} BPM conf {estimate.Confidence:F2} {(estimate.Locked ? "LOCKED" : "free  ")} | clock {clock.Bpm,6:F2} BPM");
                nextLog += double.TryParse(Environment.GetEnvironmentVariable("LIFEVIZ_BEAT_LOG_INTERVAL"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double interval) && interval > 0 ? interval : 5;
            }
        }

        residuals.Sort();
        clockErrors.Sort();
        double Percentile(List<double> values, double p) => values.Count == 0 ? double.NaN : values[(int)Math.Min(values.Count - 1, Math.Floor(values.Count * p))];
        Logger.Info($"Beat file summary '{System.IO.Path.GetFileName(path)}': {audio.Length / (double)SampleRate:F1}s, first lock {firstLock:F1}s, final {tracker.Estimate.Bpm:F2} BPM, tempo jumps {tempoJumps}, estimate jitter p50 {Percentile(residuals, 0.5):F1} ms / p95 {Percentile(residuals, 0.95):F1} ms, clock-vs-estimate p50 {Percentile(clockErrors, 0.5):F1} ms / p95 {Percentile(clockErrors, 0.95):F1} ms");
        return 0;
    }

    private sealed record SimulationResult(double FinalBpm, double MaxPhaseErrorMs, double P95PhaseErrorMs, bool Monotonic, double MaxRateDeviation, double MeanSignedErrorMs);

    private static SimulationResult Simulate(IEnumerable<Hit> hitSource, double durationSeconds, double settleSeconds, double silentFrom = -1, double silentTo = -1)
    {
        var hits = hitSource.ToList();
        float[] audio = Render(hits, durationSeconds);
        if (silentFrom >= 0)
        {
            Array.Clear(audio, (int)(silentFrom * SampleRate), (int)((silentTo - silentFrom) * SampleRate));
        }

        var tracker = new BeatTracker();
        var clock = new BeatClock();
        var beatTimes = hits.Where(h => h.Kind == "beat").Select(h => h.Time).ToList();
        var errors = new List<double>();
        double signedSum = 0;
        bool monotonic = true;
        double lastPosition = 0;
        double maxRateDeviation = 0;
        int chunk = SampleRate / 100;
        double nextRender = 0;
        int beatCursor = 0;
        double lastRenderTime = 0;
        for (int start = 0; start < audio.Length; start += chunk)
        {
            int count = Math.Min(chunk, audio.Length - start);
            double endTime = (start + count - 1) / (double)SampleRate;
            tracker.ProcessSamples(audio.AsSpan(start, count), SampleRate, 1.0, endTime);
            while (nextRender <= endTime)
            {
                double now = nextRender;
                clock.Update(now, 120, tracker.Estimate, now);
                double position = clock.GetPosition(now);
                if (position < lastPosition - 1e-9) monotonic = false;
                if (now > settleSeconds && lastRenderTime > 0)
                {
                    double rate = (position - lastPosition) / (now - lastRenderTime) * 60.0;
                    double expected = tracker.Estimate.Bpm;
                    if (expected > 0) maxRateDeviation = Math.Max(maxRateDeviation, Math.Abs(rate / expected - 1.0));
                }

                lastPosition = position;
                lastRenderTime = now;
                while (beatCursor < beatTimes.Count && beatTimes[beatCursor] <= now)
                {
                    double beatTime = beatTimes[beatCursor++];
                    bool silent = silentFrom >= 0 && beatTime >= silentFrom && beatTime < silentTo + 1;
                    if (beatTime > settleSeconds && !silent)
                    {
                        double positionAtBeat = clock.GetPosition(beatTime);
                        double phase = BeatClock.Fraction(positionAtBeat + 0.5) - 0.5;
                        errors.Add(Math.Abs(phase) * 60.0 / clock.Bpm * 1000.0);
                        signedSum += phase * 60.0 / clock.Bpm * 1000.0;
                    }
                }

                nextRender += 1.0 / RenderFps;
            }
        }

        errors.Sort();
        double max = errors.Count > 0 ? errors[^1] : double.PositiveInfinity;
        double p95 = errors.Count > 0 ? errors[(int)Math.Min(errors.Count - 1, Math.Floor(errors.Count * 0.95))] : double.PositiveInfinity;
        return new SimulationResult(tracker.Estimate.Bpm, max, p95, monotonic, maxRateDeviation, errors.Count > 0 ? signedSum / errors.Count : 0);
    }

    private static IEnumerable<Hit> FourOnFloor(double bpm, double from, double to, double jitterMs, int seed)
    {
        var random = new Random(seed);
        double beat = 60.0 / bpm;
        for (double t = from; t < to; t += beat)
        {
            double jitter = jitterMs > 0 ? (random.NextDouble() * 2 - 1) * jitterMs / 1000.0 : 0;
            yield return new Hit(t, "beat");
            yield return new Hit(t + jitter, "kick");
            yield return new Hit(t + beat / 2 + jitter, "hat");
            if (random.NextDouble() < 0.4) yield return new Hit(t + beat * 0.75, "hat");
        }
    }

    private static IEnumerable<Hit> HalfTime(double bpm, double from, double to)
    {
        // 70 BPM half-time: kick on 1, snare on 3, hats on eighths of the 140 grid.
        double beat = 60.0 / bpm;
        for (double t = from; t < to; t += beat)
        {
            yield return new Hit(t, "beat");
            yield return new Hit(t, "kick");
            yield return new Hit(t + beat / 2, "snare");
            for (int i = 0; i < 4; i++) yield return new Hit(t + i * beat / 4, "hat");
        }
    }

    private static IEnumerable<Hit> Breakbeat(double bpm, double from, double to)
    {
        double beat = 60.0 / bpm;
        int index = 0;
        for (double t = from; t < to; t += beat, index++)
        {
            yield return new Hit(t, "beat");
            switch (index % 4)
            {
                case 0: yield return new Hit(t, "kick"); break;
                case 1: yield return new Hit(t, "snare"); break;
                case 2: yield return new Hit(t + beat / 2, "kick"); break;
                case 3: yield return new Hit(t, "snare"); break;
            }

            yield return new Hit(t + beat / 2, "hat");
        }
    }

    private static float[] Render(List<Hit> hits, double durationSeconds)
    {
        var audio = new float[(int)(durationSeconds * SampleRate)];
        var random = new Random(42);
        // Constant bed (pad + noise) so the tracker has to find hits above a floor.
        for (int i = 0; i < audio.Length; i++)
        {
            double t = i / (double)SampleRate;
            audio[i] = (float)(0.03 * Math.Sin(2 * Math.PI * 220 * t) + 0.02 * Math.Sin(2 * Math.PI * 330 * t) + 0.01 * (random.NextDouble() * 2 - 1));
        }

        foreach (var hit in hits)
        {
            if (hit.Kind == "beat") continue;
            int start = (int)(hit.Time * SampleRate);
            int length = hit.Kind == "kick" ? SampleRate / 4 : hit.Kind == "snare" ? SampleRate / 6 : SampleRate / 25;
            for (int i = 0; i < length && start + i < audio.Length; i++)
            {
                if (start + i < 0) continue;
                double t = i / (double)SampleRate;
                double value = hit.Kind switch
                {
                    "kick" => 0.7 * Math.Exp(-t * 18) * Math.Sin(2 * Math.PI * (55 + 90 * Math.Exp(-t * 40)) * t),
                    "snare" => Math.Exp(-t * 25) * (0.25 * (random.NextDouble() * 2 - 1) + 0.2 * Math.Sin(2 * Math.PI * 190 * t)),
                    _ => 0.12 * Math.Exp(-t * 90) * (random.NextDouble() * 2 - 1)
                };
                audio[start + i] += (float)value;
            }
        }

        return audio;
    }
}
