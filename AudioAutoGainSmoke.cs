using System;
using System.Collections.Generic;

namespace lifeviz;

/// <summary>
/// Auto gain checks with synthetic 124 BPM music: a signal 34 dB quieter than a loud
/// loopback-style reference must, with Auto gain on, reach comparable level/band
/// readings and the same tempo; a sudden loud signal must pull the gain down fast; and
/// silence must not pump the gain up.
/// </summary>
internal static class AudioAutoGainSmoke
{
    private const int SampleRate = 48000;
    private const int Chunk = 480;

    public static int Run()
    {
        var failures = new List<string>();
        void Expect(bool ok, string message)
        {
            if (!ok) failures.Add(message);
            Logger.Info($"Auto gain: {(ok ? "PASS" : "FAIL")} {message}");
        }

        float[] music = Render(20);
        double quietScale = Math.Pow(10, -34 / 20.0);

        var reference = Measure(music, scale: 1.0, autoGain: false);
        var boosted = Measure(music, scale: quietScale, autoGain: true);
        var unboosted = Measure(music, scale: quietScale, autoGain: false);
        Expect(Math.Abs(boosted.Level - reference.Level) < 0.12 && Math.Abs(boosted.Bass - reference.Bass) < 0.12,
            $"-34 dB input with auto gain matches the loud reference (level {boosted.Level:F2} vs {reference.Level:F2}, low {boosted.Bass:F2} vs {reference.Bass:F2}; gain {boosted.GainDb:+0.0;-0.0} dB)");
        Expect(reference.Level - unboosted.Level > 0.3,
            $"without auto gain the quiet input is clearly weaker (level {unboosted.Level:F2})");
        Expect(Math.Abs(boosted.Bpm - 124) < 0.7, $"tempo still tracked from the boosted mic signal ({boosted.Bpm:F2} BPM)");
        var loudAuto = Measure(music, scale: 1.0, autoGain: true);
        Expect(Math.Abs(loudAuto.GainDb) < 6 && Math.Abs(loudAuto.Level - reference.Level) < 0.1,
            $"a loud loopback-level input is left nearly alone ({loudAuto.GainDb:+0.0;-0.0} dB, level {loudAuto.Level:F2})");

        // Quiet for 12 s, then the PA right next to the mic: gain must fall fast.
        using (var detector = Start(autoGain: true))
        {
            Feed(detector, music, quietScale, 0, 12);
            double before = detector.AutoGainDb;
            Feed(detector, music, 1.0, 12, 12.5);
            double after = detector.AutoGainDb;
            Expect(before - after > 25, $"sudden loud input drops gain within 0.5 s ({before:+0.0;-0.0} -> {after:+0.0;-0.0} dB)");

            // Silence after a quiet passage must hold the gain, not pump it to maximum.
            Feed(detector, music, quietScale, 12.5, 20);
            double held = detector.AutoGainDb;
            Feed(detector, new float[SampleRate * 6], 1.0, 20, 26);
            Expect(Math.Abs(detector.AutoGainDb - held) < 0.5, $"silence holds the gain ({held:+0.0;-0.0} -> {detector.AutoGainDb:+0.0;-0.0} dB)");
        }

        if (failures.Count > 0)
        {
            Logger.Error($"Auto gain smoke failed: {string.Join("; ", failures)}");
            return 1;
        }

        Logger.Info("Auto gain smoke passed.");
        return 0;
    }

    private sealed record Reading(double Level, double Bass, double Bpm, double GainDb);

    private static Reading Measure(float[] music, double scale, bool autoGain)
    {
        using var detector = Start(autoGain);
        double level = 0;
        double bass = 0;
        int count = 0;
        for (int start = 0; start + Chunk <= music.Length; start += Chunk)
        {
            ProcessChunk(detector, music, start, scale);
            if (start >= SampleRate * 12)
            {
                level += detector.NormalizedEnergy;
                bass += detector.BassNormalizedLevel;
                count++;
            }
        }

        return new Reading(level / count, bass / count, detector.BeatEstimate.Bpm, detector.AutoGainDb);
    }

    private static AudioBeatDetector Start(bool autoGain)
    {
        var detector = new AudioBeatDetector();
        detector.BeginOfflineInput(SampleRate);
        detector.SetAnalysisRequirements(true, false);
        detector.AutoGainEnabled = autoGain;
        return detector;
    }

    private static void Feed(AudioBeatDetector detector, float[] source, double scale, double fromSeconds, double toSeconds)
    {
        for (int start = (int)(fromSeconds * SampleRate); start + Chunk <= toSeconds * SampleRate; start += Chunk)
        {
            ProcessChunk(detector, source, start % (source.Length - Chunk), scale, start);
        }
    }

    private static void ProcessChunk(AudioBeatDetector detector, float[] source, int start, double scale, int? timelineSample = null)
    {
        var chunk = new float[Chunk];
        for (int i = 0; i < Chunk; i++)
        {
            chunk[i] = (float)(source[start + i] * scale);
        }

        detector.ProcessOfflineSamples(chunk, (timelineSample ?? start) / (double)SampleRate);
    }

    /// <summary>Kick + offbeat hat + pad at 124 BPM, mastered loud (loopback-like levels).</summary>
    private static float[] Render(double seconds)
    {
        var audio = new float[(int)(seconds * SampleRate)];
        var random = new Random(9);
        double beat = 60.0 / 124;
        for (int i = 0; i < audio.Length; i++)
        {
            double t = i / (double)SampleRate;
            double sinceBeat = t % beat;
            double sinceHat = (t + beat / 2) % beat;
            double kick = 0.9 * Math.Exp(-sinceBeat * 14) * Math.Sin(2 * Math.PI * (52 + 110 * Math.Exp(-sinceBeat * 35)) * sinceBeat);
            double hat = 0.2 * Math.Exp(-sinceHat * 70) * (random.NextDouble() * 2 - 1);
            double pad = 0.12 * Math.Sin(2 * Math.PI * 220 * t) + 0.08 * Math.Sin(2 * Math.PI * 330 * t) + 0.05 * Math.Sin(2 * Math.PI * 1320 * t);
            audio[i] = (float)Math.Clamp(kick + hat + pad, -1, 1);
        }

        return audio;
    }
}
