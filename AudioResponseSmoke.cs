using System;
using System.Collections.Generic;
using System.Linq;

namespace lifeviz;

internal static class AudioResponseSmoke
{
    public static int RunCapture()
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
        using var capture = new LowLatencyLoopbackCapture(device);
        Exception? failure = null; int packets = 0; int largestPacket = 0;
        capture.DataAvailable += (_, e) =>
        {
            System.Threading.Interlocked.Increment(ref packets);
            largestPacket = Math.Max(largestPacket,e.BytesRecorded);
        };
        capture.RecordingStopped += (_, e) => failure=e.Exception;
        capture.StartRecording(); System.Threading.Thread.Sleep(500); capture.StopRecording();
        Check(failure==null,"Low-latency output capture failed: "+failure);
        double milliseconds=largestPacket*1000d/capture.WaveFormat.AverageBytesPerSecond;
        Logger.Info($"Low-latency loopback opened/stopped successfully: packets={packets}, largest packet={milliseconds:F1} ms. Silence may produce no packets.");
        return 0;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    public static void RunAnalysis()
    {
        var amplitudes = new List<double>();
        foreach (int frequency in new[] { 100, 900, 4500 })
        {
            double? reference = null;
            foreach (int packetSize in new[] { 128, 480, 1024, 1600, 4096 })
            {
                using var detector = new AudioBeatDetector(); detector.BeginOfflineInput(); detector.SetAnalysisRequirements(true, false);
                var samples = new float[48000];
                for (int i = 0; i < samples.Length; i++) samples[i] = (float)(0.12 * Math.Sin(2*Math.PI*frequency*i/48000));
                for (int start = 0; start < samples.Length; start += packetSize)
                    detector.ProcessOfflineSamples(samples.AsSpan(start, Math.Min(packetSize,samples.Length-start)), start/48000d);
                double level = frequency == 100 ? detector.BassNormalizedLevel : frequency == 900 ? detector.MidNormalizedLevel : detector.HighNormalizedLevel;
                Check(level > 0.5 && level < 0.9, $"Band calibration failed: {frequency} Hz={level:F4}.");
                if (reference.HasValue) Check(Math.Abs(level-reference.Value) < 1e-10, "Band level depends on capture packet size.");
                reference = level;
                if (packetSize == 480) amplitudes.Add(level);
            }
            Logger.Info($"Audio response: {frequency} Hz normalized={reference:F4}; identical at 128/480/1024/1600/4096-sample packets.");
        }
        Check(amplitudes.Max()-amplitudes.Min() < 0.025, "Equal-amplitude low/mid/high tones do not have comparable strength.");
        using (var detector = new AudioBeatDetector())
        {
            detector.BeginOfflineInput(); detector.SetAnalysisRequirements(true,false);
            var envelope = new ReactiveEnvelope(); int onset = -1; double maxSilence = 0;
            for (int chunk = 0; chunk < 360; chunk++)
            {
                var samples = new float[80];
                if (chunk >= 120 && chunk < 180)
                    for (int i = 0; i < samples.Length; i++) samples[i] = (float)(0.2*Math.Sin(2*Math.PI*100*(chunk*80+i)/48000));
                detector.ProcessOfflineSamples(samples,chunk/600d);
                double value = envelope.Process(detector.BassNormalizedLevel,1/600d,5,80);
                if (chunk < 120) maxSilence = Math.Max(maxSilence,value);
                if (chunk >= 120 && onset < 0 && value > 0.4) onset = chunk;
                if (chunk == 359) Check(value < 0.025, "Release held stale audio too long.");
            }
            double latencyMs = (onset-120)*1000/600d;
            Check(maxSilence == 0 && onset >= 120 && latencyMs <= 25, $"Synthetic bass attack arrived late: {latencyMs:F1} ms.");
            Logger.Info($"Audio response: synthetic PCM-to-mapping bass attack {latencyMs:F1} ms (excludes device/display latency).");
        }
        var fast = new ReactiveEnvelope(); var slow = new ReactiveEnvelope();
        double at30 = 0, at144 = 0;
        for (int i = 0; i < 3; i++) at30 = fast.Process(1,1/30d,50,80);
        for (int i = 0; i < 14; i++) at144 = slow.Process(1,1/144d,50,80);
        at144 = slow.Process(1,0.1-14/144d,50,80);
        Check(Math.Abs(at30-at144)<1e-10, "Envelope depends on presentation rate.");
        var smooth = new ReactiveEnvelope(); smooth.Process(0.5,0,5,80);
        var values = new List<double>();
        for (int i=0;i<60;i++) values.Add(smooth.Process(i%2==0 ? 0.45 : 0.55,1/120d,20,80));
        Check(values.Max()-values.Min()<0.075, "Response failed to reduce frame-to-frame chatter.");
        Check(new ReactiveEnvelope().Process(0.7,1/60d,0,0)==0.7, "Zero attack did not provide a direct response.");
        using var live = new AudioBeatDetector(); live.BeginExternalInput(); live.ProcessExternalSamples(new float[480]);
        Check(live.HasFreshReactiveSamples,"New audio was considered stale.");
        System.Threading.Thread.Sleep(150);
        Check(!live.HasFreshReactiveSamples,"Stopped audio remained latched indefinitely.");
    }
}
