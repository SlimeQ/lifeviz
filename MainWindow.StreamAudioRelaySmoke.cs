using System;
using System.Linq;
using System.Windows.Controls;

namespace lifeviz;

public partial class MainWindow
{
    // Menu and persistence checks for Audio Source -> Stream Audio Relay, using fake device IDs.
    // The only real device call is starting the relay on a bogus ID, which must fail cleanly.
    internal void RunStreamAudioRelayMenuChecks()
    {
        if (!App.IsSmokeTestMode) throw new InvalidOperationException("Stream audio relay menu checks require smoke mode.");
        var outputs = new[]
        {
            new AudioBeatDetector.AudioDeviceInfo("render:default", "System Output (Default)", AudioBeatDetector.AudioDeviceInfo.AudioDeviceKind.Render, isSystemDefault: true),
            new AudioBeatDetector.AudioDeviceInfo("render:smoke-dev-a", "Output: Smoke A", AudioBeatDetector.AudioDeviceInfo.AudioDeviceKind.Render),
            new AudioBeatDetector.AudioDeviceInfo("render:smoke-dev-b", "Output: Smoke B", AudioBeatDetector.AudioDeviceInfo.AudioDeviceKind.Render)
        };

        _selectedAudioDeviceId = "render:smoke-dev-a";
        _streamAudioRelayDeviceId = null;
        var relayMenu = BuildRelayMenu();
        var items = relayMenu.Items.OfType<MenuItem>().ToList();
        Require(StreamAudioRelay.IsSupported ? relayMenu.IsEnabled : !relayMenu.IsEnabled, "Relay menu availability must follow OS support.");
        Require(items.Single(i => (string)i.Header == "Off").IsChecked, "Off must be checked when no relay device is chosen.");
        Require(items.All(i => !((string)i.Header).StartsWith("System Output", StringComparison.Ordinal)), "The default pseudo-device is not a relay target.");
        var analyzed = items.Single(i => ((string)i.Header).StartsWith("Output: Smoke A", StringComparison.Ordinal));
        var other = items.Single(i => (string)i.Header == "Output: Smoke B");
        Require(!analyzed.IsEnabled && ((string)analyzed.Header).Contains("being analyzed"), "The analyzed device must be disabled as a relay target.");
        Require(other.IsEnabled && !other.IsChecked, "Other output devices must be selectable.");

        _streamAudioRelayDeviceId = "smoke-dev-b";
        relayMenu = BuildRelayMenu();
        Require(relayMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Output: Smoke B").IsChecked &&
                !relayMenu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Off").IsChecked,
            "The chosen relay device must be checked.");
        using (var saved = System.Text.Json.JsonDocument.Parse(SerializeCurrentConfig()))
        {
            Require(saved.RootElement.TryGetProperty(nameof(AppConfig.StreamAudioRelayDeviceId), out var relayId) && relayId.GetString() == "smoke-dev-b",
                "The relay device must be saved in config.json.");
        }

        // A bogus device fails to start without throwing; a non-output source parks the relay.
        UpdateStreamAudioRelay();
        Require(_streamAudioRelayUpdate.Wait(TimeSpan.FromSeconds(10)) && !_streamAudioRelay.IsActive, "A missing relay device must not start.");
        if (StreamAudioRelay.IsSupported)
        {
            Require(_streamAudioRelay.Status.StartsWith("Could not start", StringComparison.Ordinal), $"Unexpected relay status: {_streamAudioRelay.Status}");
        }

        _selectedAudioDeviceId = VideoStackAudioSourceId;
        UpdateStreamAudioRelay();
        Require(_streamAudioRelayUpdate.Wait(TimeSpan.FromSeconds(10)) && _streamAudioRelay.Status.StartsWith("Waiting", StringComparison.Ordinal),
            "The relay must wait while the Audio Source is not an output device.");

        _streamAudioRelayDeviceId = null;
        _selectedAudioDeviceId = null;
        UpdateStreamAudioRelay();
        _streamAudioRelayUpdate.Wait(TimeSpan.FromSeconds(10));

        MenuItem BuildRelayMenu()
        {
            AudioSourceMenu.Items.Clear();
            AddStreamAudioRelayMenu(outputs);
            return AudioSourceMenu.Items.OfType<MenuItem>().Single();
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
