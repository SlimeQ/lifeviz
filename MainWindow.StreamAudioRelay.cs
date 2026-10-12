using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace lifeviz;

// Audio Source -> Stream Audio Relay. When an output device is the audio source, LifeViz can
// replay the apps playing there from its own process onto a relay device, so a Discord
// application stream (which only carries the streamed app's audio) hears what LifeViz reacts to.
public partial class MainWindow
{
    private readonly StreamAudioRelay _streamAudioRelay = new();
    private string? _streamAudioRelayDeviceId;
    private Task _streamAudioRelayUpdate = Task.CompletedTask;

    private void UpdateStreamAudioRelay()
    {
        if (BackgroundBakeWorker.IsWorker || _isShuttingDown)
        {
            return;
        }

        string? relayDeviceId = _streamAudioRelayDeviceId;
        string? audioSelection = _selectedAudioDeviceId;
        _streamAudioRelayUpdate = _streamAudioRelayUpdate.ContinueWith(_ =>
        {
            if (string.IsNullOrWhiteSpace(relayDeviceId))
            {
                _streamAudioRelay.Stop();
            }
            else if (AudioBeatDetector.TryParseRenderSelectionId(audioSelection ?? string.Empty, out string? renderDeviceId, out bool useSystemDefault))
            {
                _streamAudioRelay.Start(relayDeviceId, useSystemDefault ? null : renderDeviceId);
            }
            else
            {
                _streamAudioRelay.Stop("Waiting: the relay follows an output-device audio source");
            }
        }, TaskScheduler.Default);
    }

    private void AddStreamAudioRelayMenu(IReadOnlyList<AudioBeatDetector.AudioDeviceInfo> outputDevices)
    {
        if (AudioSourceMenu == null)
        {
            return;
        }

        var relayMenu = new MenuItem
        {
            Header = "Stream Audio Relay",
            ToolTip = "Replays the analyzed output device's apps (minus Discord and LifeViz) from LifeViz on a device you don't listen to,\n" +
                      "so a Discord application stream or OBS Application Audio Capture of LifeViz carries that audio."
        };

        var offItem = new MenuItem
        {
            Header = "Off",
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(_streamAudioRelayDeviceId)
        };
        offItem.Click += (_, _) => SetStreamAudioRelayDevice(null);
        relayMenu.Items.Add(offItem);
        relayMenu.Items.Add(new Separator());
        relayMenu.Items.Add(new MenuItem { Header = "Relay Device (pick one you don't listen to)", IsEnabled = false });

        string? analyzedDeviceId = null;
        if (AudioBeatDetector.TryParseRenderSelectionId(_selectedAudioDeviceId ?? string.Empty, out string? renderDeviceId, out bool useSystemDefault))
        {
            analyzedDeviceId = useSystemDefault ? AudioBeatDetector.TryGetDefaultRenderDeviceId() : renderDeviceId;
        }

        foreach (var device in outputDevices.Where(d => !d.IsSystemDefault))
        {
            string deviceId = device.Id["render:".Length..];
            bool isAnalyzed = string.Equals(deviceId, analyzedDeviceId, StringComparison.OrdinalIgnoreCase);
            var item = new MenuItem
            {
                Header = isAnalyzed ? $"{device.Name} (being analyzed)" : device.Name,
                IsCheckable = true,
                IsChecked = string.Equals(_streamAudioRelayDeviceId, deviceId, StringComparison.OrdinalIgnoreCase),
                IsEnabled = !isAnalyzed
            };
            item.Click += (_, _) => SetStreamAudioRelayDevice(deviceId);
            relayMenu.Items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(_streamAudioRelayDeviceId))
        {
            relayMenu.Items.Add(new Separator());
            relayMenu.Items.Add(new MenuItem { Header = "Status: " + _streamAudioRelay.Status, IsEnabled = false });
            var relayed = _streamAudioRelay.RelayedProcesses;
            if (relayed.Count > 0)
            {
                relayMenu.Items.Add(new MenuItem
                {
                    Header = "Apps: " + string.Join(", ", relayed.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase)),
                    IsEnabled = false
                });
            }
        }

        if (!StreamAudioRelay.IsSupported)
        {
            relayMenu.IsEnabled = false;
            relayMenu.Header = "Stream Audio Relay (requires Windows 11)";
        }

        AudioSourceMenu.Items.Add(new Separator());
        AudioSourceMenu.Items.Add(relayMenu);
    }

    private void SetStreamAudioRelayDevice(string? deviceId)
    {
        _streamAudioRelayDeviceId = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        UpdateStreamAudioRelay();
        SaveConfig();
        PopulateAudioMenu();
    }

    private void ShutdownStreamAudioRelay()
    {
        try
        {
            _streamAudioRelayUpdate.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // A failed start was already logged; disposal below still releases the device.
        }

        _streamAudioRelay.Dispose();
    }
}
