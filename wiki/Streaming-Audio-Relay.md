# Streaming LifeViz with its audio (Stream Audio Relay)

When you stream LifeViz as an **application** in Discord (or capture it with OBS **Application Audio Capture**), the stream only carries audio that the LifeViz process itself plays. With an output device such as **System Output (Default)** as the Audio Source, LifeViz only *listens*: Spotify, a browser, or a DJ app plays the music, so viewers see the visuals react to music they cannot hear.

**Audio Source → Stream Audio Relay** fixes this. LifeViz replays the audio it is analyzing from its own process to a separate **relay device**. The stream then receives exactly what LifeViz reacts to.

## Set it up

1. Choose an output device under **Audio Source** (usually **System Output (Default)**). The relay only runs while an output device is the Audio Source.
2. Open **Audio Source → Stream Audio Relay** and pick a relay device that you **don't listen to**. A virtual device is ideal, for example **CABLE Input (VB-Audio Virtual Cable)**, or an unused HDMI monitor output. The device being analyzed is disabled in this list, because relaying to it would make you hear everything twice.
3. Start the Discord application stream of the LifeViz window as usual.

The choice is saved as `StreamAudioRelayDeviceId` in `%AppData%\lifeviz\config.json` and restored at startup. Pick **Off** to stop the relay. The submenu shows a status line and the apps currently being relayed.

You will not hear the relay: it plays on a device you are not listening to. Windows per-application capture follows the process, not the output device, so Discord still receives it. Anything that records from that virtual device, however, such as a program using **CABLE Output** as its microphone, will also receive the relay. Pick a device nothing else reads from.

## What gets relayed

The relay captures every app with an audio session on the analyzed device and mixes them. It deliberately skips:

- **Discord** (and Vesktop): relaying the call would echo everyone's voices back to them through your stream. Process names starting with `Discord` are excluded, including Discord's helper processes.
- **LifeViz itself**: movie/video audio played by LifeViz already reaches the stream directly. Excluding it prevents a doubled copy, and the relay output can never feed back into itself.

Windows system sounds are not relayed. An app that starts playing appears in the relay within about one second. If its parent process is already being relayed (e.g. a browser and its audio service), it is captured once through the parent.

## Requirements and limits

- Windows 11 or Windows 10 build 20348+ (the per-process loopback API). On older builds the submenu is disabled.
- Microphone/line inputs and **Video Stack (Silent)** are not relayed. The status reads *Waiting: the relay follows an output-device audio source*.
- Latency: about 30–50 ms on top of capture, buffered per app (20 ms priming, trimmed to at most 120 ms so delay cannot build up).
- If the relay device disappears or fails, the status reports the error; pick the device again.
- If the system default output changes to the relay device, the relay pauses (it would otherwise double what you hear) and resumes when the default moves away.

## How it works

`StreamAudioRelay` opens the relay device in shared mode at its mix format and renders a mix pulled on the device clock. Once a second, a background refresh enumerates the analyzed device's audio sessions, filters out LifeViz/Discord and children of already-relayed processes, and starts or stops one `ProcessLoopbackCapture` per app. That class wraps `ActivateAudioInterfaceAsync` with `VAD\Process_Loopback` (include-process-tree mode), which captures the app on any endpoint after its session volume. Each capture feeds a small ring queue. A queue joins the mix once it holds 20 ms and is trimmed to 120 ms. The sum is clamped to ±1.

Validate with `dotnet bin/Release/net9.0-windows/lifeviz.dll --smoke-test stream-audio-relay` (see [Build & Install](Build-and-Install.md)).
