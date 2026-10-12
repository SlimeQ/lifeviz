using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace lifeviz;

// Stream Audio Relay end-to-end check on two virtual output devices (VB-Audio CABLE or
// Voicemeeter inputs, so nothing is audible):
// a child process plays 440 Hz on the "analyzed" device while this process plays 660 Hz there.
// The relay device must carry the child's tone (another app) but not this process's tone
// (LifeViz's own audio is never relayed, which also proves the relay cannot feed back).
internal static partial class SmokeTestRunner
{
    private const double RelaySmokeToneAmplitude = 0.1;

    private static int RunStreamAudioRelaySmokeTest()
    {
        if (!StreamAudioRelay.IsSupported)
        {
            Console.WriteLine("Stream audio relay smoke SKIPPED: requires Windows 10 build 20348+.");
            return 0;
        }

        RunStreamAudioRelayMenuSmoke();
        Require(StreamAudioRelay.IsExcludedProcessName("Discord") &&
                StreamAudioRelay.IsExcludedProcessName("DiscordPTB") &&
                StreamAudioRelay.IsExcludedProcessName("discordcanary") &&
                !StreamAudioRelay.IsExcludedProcessName("chrome") &&
                !StreamAudioRelay.IsExcludedProcessName("Spotify"),
            "Discord clients must be excluded from the relay and ordinary players kept.");

        using var enumerator = new MMDeviceEnumerator();
        var virtualDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ||
                        d.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FriendlyName.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(d => d.AudioClient.MixFormat.Channels == 2 ? 0 : 1)
            .ToList();
        if (virtualDevices.Count < 2)
        {
            Console.WriteLine("Stream audio relay smoke SKIPPED: needs two virtual output devices (VB-Audio CABLE / Voicemeeter) so the test stays silent.");
            return 0;
        }

        // A virtual device can be held exclusively by another app; use the first pair that opens.
        MMDevice? analyzed = null;
        WasapiOut? ownToneOutput = null;
        foreach (var candidate in virtualDevices)
        {
            try
            {
                ownToneOutput = PlayRelaySmokeTone(candidate, 660);
                analyzed = candidate;
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipping {candidate.FriendlyName} as analyzed device: {ex.Message}");
            }
        }

        Require(analyzed != null && ownToneOutput != null, "No virtual device accepted a test tone.");
        using var ownTone = ownToneOutput!;
        using var relay = new StreamAudioRelay();
        MMDevice? relayDevice = null;
        foreach (var candidate in virtualDevices.Where(d => d.ID != analyzed!.ID))
        {
            relay.Start(candidate.ID, analyzed!.ID);
            if (relay.IsActive)
            {
                relayDevice = candidate;
                break;
            }

            Console.WriteLine($"Skipping {candidate.FriendlyName} as relay device: {relay.Status}");
        }

        if (relayDevice == null)
        {
            Console.WriteLine("Stream audio relay smoke SKIPPED: no second virtual device could be opened for the relay (CABLE Input and CABLE In 16ch are the same cable).");
            return 0;
        }

        Console.WriteLine($"Analyzed device: {analyzed!.FriendlyName}; relay device: {relayDevice.FriendlyName}");

        using var helper = StartRelaySmokeToneProcess(analyzed.ID, 440, seconds: 8);
        try
        {
            // Wait for the helper's session to appear and a relay refresh to pick it up.
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline && relay.RelayedProcesses.All(p => p.ProcessId != helper.Id))
            {
                Thread.Sleep(100);
            }

            Require(relay.RelayedProcesses.Any(p => p.ProcessId == helper.Id),
                $"Relay never picked up the tone process. Status: {relay.Status}");
            Require(relay.RelayedProcesses.All(p => p.ProcessId != Environment.ProcessId),
                "Relay must never capture LifeViz's own process.");
            Thread.Sleep(300);

            // The relayed tone must be continuous: a 0.1-amplitude 440 Hz sine moves at most 0.006
            // per sample, so larger steps are dropouts or misplaced blocks in the relay mixer.
            var mixed = new List<float>();
            relay.OutputTap = (block, channels) =>
            {
                lock (mixed)
                {
                    for (int i = 0; i < block.Length; i += channels)
                    {
                        mixed.Add(block[i]);
                    }
                }
            };
            Thread.Sleep(2000);
            relay.OutputTap = null;
            int discontinuities;
            lock (mixed)
            {
                discontinuities = mixed.Zip(mixed.Skip(1), (a, b) => Math.Abs(b - a) > 0.02f).Count(jump => jump);
            }

            Console.WriteLine($"Relay mixer output: {discontinuities} discontinuities in 2 s.");
            Require(discontinuities <= 2, "The relay mixer output has dropouts.");

            var (relayed440, relayed660) = MeasureRelaySmokeTones(relayDevice, TimeSpan.FromSeconds(2));
            Console.WriteLine($"Relay device tone amplitude: other app 440 Hz = {relayed440:0.000}, LifeViz 660 Hz = {relayed660:0.000} (played at {RelaySmokeToneAmplitude:0.000}).");
            Require(relayed440 > RelaySmokeToneAmplitude * 0.5,
                "The other app's audio did not reach the relay device.");
            Require(relayed660 < RelaySmokeToneAmplitude * 0.05,
                "LifeViz's own audio leaked into the relay (feedback risk).");

            relay.Stop();
            Require(!relay.IsActive && relay.RelayedProcesses.Count == 0, "Stop must release the relay output and captures.");
        }
        finally
        {
            ownTone.Stop();
            try
            {
                if (!helper.HasExited)
                {
                    helper.Kill();
                }
            }
            catch
            {
                // Already gone.
            }
        }

        Console.WriteLine("Stream audio relay smoke passed: other apps relayed, LifeViz excluded, Discord names excluded, clean stop.");
        return 0;

        static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    private static void RunStreamAudioRelayMenuSmoke()
    {
        App.IsDiagnosticTestMode = true;
        var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        Exception? failure = null;
        app.Startup += (_, _) =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.RunStreamAudioRelayMenuChecks();
                Console.WriteLine("Stream audio relay menu checks passed: analyzed device disabled, selection checked and saved, bad device and non-output source handled.");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.ShutdownResources();
                app.Shutdown();
            }
        };
        app.Run();
        if (failure != null)
        {
            throw new InvalidOperationException("Stream audio relay menu checks failed.", failure);
        }
    }

    // Child-process mode for the smoke above: --smoke-test stream-audio-relay-tone <deviceId> <hz> <seconds>
    private static int RunStreamAudioRelayToneHelper(string[] args)
    {
        if (args.Length < 5)
        {
            return 2;
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(args[2]);
        using var output = PlayRelaySmokeTone(device, double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
        Thread.Sleep(TimeSpan.FromSeconds(double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)));
        output.Stop();
        return 0;
    }

    private static WasapiOut PlayRelaySmokeTone(MMDevice device, double frequency)
    {
        var mixFormat = device.AudioClient.MixFormat;
        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, 30);
        output.Init(new SignalGenerator(mixFormat.SampleRate, mixFormat.Channels)
        {
            Frequency = frequency,
            Gain = RelaySmokeToneAmplitude,
            Type = SignalGeneratorType.Sin
        });
        output.Play();
        return output;
    }

    private static Process StartRelaySmokeToneProcess(string deviceId, int frequency, int seconds)
    {
        string dll = typeof(SmokeTestRunner).Assembly.Location;
        string exe = Path.ChangeExtension(dll, ".exe");
        string toneArgs = $"--smoke-test stream-audio-relay-tone \"{deviceId}\" {frequency} {seconds}";
        var startInfo = File.Exists(exe)
            ? new ProcessStartInfo(exe, toneArgs)
            : new ProcessStartInfo(Environment.ProcessPath ?? "dotnet", $"\"{dll}\" {toneArgs}");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the tone helper process.");
    }

    private static (double tone440, double tone660) MeasureRelaySmokeTones(MMDevice relayDevice, TimeSpan duration)
    {
        using var capture = new WasapiLoopbackCapture(relayDevice);
        int channels = capture.WaveFormat.Channels;
        int sampleRate = capture.WaveFormat.SampleRate;
        var left = new List<float>();
        capture.DataAvailable += (_, e) =>
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded));
            lock (left)
            {
                for (int i = 0; i < samples.Length; i += channels)
                {
                    left.Add(samples[i]);
                }
            }
        };
        capture.StartRecording();
        Thread.Sleep(duration);
        capture.StopRecording();
        float[] recorded;
        lock (left)
        {
            recorded = left.ToArray();
        }

        return (ToneAmplitude(recorded, sampleRate, 440), ToneAmplitude(recorded, sampleRate, 660));

        static double ToneAmplitude(float[] samples, int rate, double frequency)
        {
            double coefficient = 2 * Math.Cos(2 * Math.PI * frequency / rate);
            double s1 = 0, s2 = 0;
            foreach (float sample in samples)
            {
                double s0 = sample + coefficient * s1 - s2;
                s2 = s1;
                s1 = s0;
            }

            double power = Math.Max(0, s1 * s1 + s2 * s2 - coefficient * s1 * s2);
            return samples.Length == 0 ? 0 : 2 * Math.Sqrt(power) / samples.Length;
        }
    }
}
