# MilkDrop / projectM layers

LifeViz can render MilkDrop presets as ordinary source layers using projectM.
Choose **MilkDrop** in the Scene Editor's root or group add controls, or
**Add MilkDrop / projectM** in the Sources context menu. New layers start with
three bundled spectrum presets, shuffle playback, a 30-second duration and
3-second transitions.

The layer has the usual enable, opacity, scale, blend, keying and animation
controls. It can sit above or below media and feed a simulation group.
It fills the scene canvas and does not select a new scene aspect ratio.

## Preset library and playlists

Select the layer and open **Presets & Playback...**. The library includes the
9,795 Cream of the Crop presets from projectM's Windows 2.0.0-pre1 distributable,
plus its associated texture collection. Search matches category folders,
author names and preset names; multiple search words must all match.
Ctrl/Shift selects multiple entries. **Add Selected** adds them to this layer's
playlist; **Remove**, **Move Up** and **Move Down** edit that list. These controls
do not delete files. **Import .milk Files...** references additional local files;
keep their textures beside them or in a sibling `textures` directory.

Select a preset in either list to see its animated **Preset preview**. With a
multi-selection, the most recently selected entry is auditioned. The preview
works in live and draft editing and has its own renderer; browsing never changes
the playing layer, adds a preset, or saves settings. **Use demo audio** supplies
a silent synthetic beat by default, so audio-reactive presets can be auditioned
without music playing. Uncheck it to use LifeViz's current Audio Source (silence
when no input is available). **Pause preview** freezes it; **Restart preview**
reloads the selected preset and can retry a failure. Missing or unsupported
presets show an inline error and clear the previous image. Closing the picker
releases its preview renderer.

**Save Playlist Settings** applies the dialog's edits. Cancel discards them.
In Scene Editor Live Mode this updates playback immediately; in draft mode it
updates the draft, which takes effect when the scene is applied. Live transport
buttons operate the existing playing playlist even while a settings dialog is
open; they are unavailable for drafts.

Each layer owns an independent playlist:

| Control | Behavior |
| --- | --- |
| Ordered loop | Follow list order and wrap to the beginning. |
| Shuffle without repeats | Shuffle a complete cycle, exhaust it, then reshuffle; avoid an immediate repeat at the cycle boundary. |
| After duration | Change when the elapsed preset duration is reached. |
| After duration, on next beat | Wait for the duration and minimum time, then change on the next newly detected beat. Silence holds the preset. |
| Every N detected beats | Count new detected beats since the last change, subject to the minimum time. These are detected audio beats, not inferred musical bars. |
| Hold / manual only | Keep the current preset until Previous or Next is used. |
| Duration | 0.1–86,400 seconds. |
| Transition | 0–30 seconds; zero is an immediate cut, otherwise projectM blends presets. Automatic changes wait for a transition to finish. |
| Beats per preset | 1–4,096 detected beats. |
| Minimum seconds | 0–86,400 seconds; protects beat-driven changes against rapid switching. |

**Previous** follows recent playback history, including shuffle history.
**Next** moves forward through that history before selecting a new entry.
**Retry / Restart** starts a fresh renderer and playlist sequence and retries
presets that failed earlier. Playback-option and playlist edits preserve the
current preset when it remains in the list. An empty playlist produces no layer
image. Missing or invalid presets are logged and skipped with bounded retries;
the settings window displays current playback or the failure reason.

## Audio and baking

The current preset continuously receives PCM samples from LifeViz's selected
**Audio Source**, with LifeViz's input gain applied. This includes microphone,
system output and **Video Stack (Silent)**. projectM does its own waveform and
spectrum analysis for rendering. Preset-change events use LifeViz's existing
beat counter, so changing audio inputs does not count a counter reset as a beat.
If a live audio endpoint stops delivering samples, the visualizer receives
silence after a short grace period instead of repeatedly reacting to stale data.

Background bakes use the worker's fixed frame clock and decoded video-stack
audio. They start a fresh projectM timeline and playlist; changing the editor
after queuing does not change the captured playlist. Baked output retains
LifeViz's existing silent-video behavior. A bake with a nonempty playlist and
no playable presets, or a failed native renderer, fails visibly rather than
silently exporting a missing layer. The saved shuffle seed reproduces selection
order, but projectM's preset randomness and GPU differences do not promise
bit-identical pixels across runs.

Disabled layers and layers inside disabled groups do not render. Enabled layers
hidden by opacity or a visibility-group takeover continue their timeline.

## Engine, assets and distribution

`Native/projectm` is a Git submodule pinned to
`1e7ef7803b69024d1e0656705670adda2ffac817`, with its own pinned projectm-eval
submodule. This development revision identifies as 4.2.0. The older engine in
the upstream Windows distributable lacks the frame-time API needed by bakes,
so LifeViz builds its own unmodified engine DLL while using that distributable's
preset and texture content.

`Prepare-ProjectM.ps1` verifies the pinned upstream archive, builds the engine
with Visual Studio C++/CMake, and assembles `artifacts/projectm/bundle`.
The installed `projectm` directory contains the DLL, `assets.zip`, complete
corresponding engine source in `projectM-source.zip`, license texts and notices.
The engine DLL can be replaced with an API-compatible build. No runtime integrity
check prevents replacement. See [Build & Install](Build-and-Install.md).

On first use the preset assets are extracted offline to
`%LOCALAPPDATA%/lifeviz/projectm/sdl-2.0.0-pre1-7129cae0-v1/`.
Live extraction runs in the background, with a preparing status. A bake waits
for extraction before producing its first frame. The versioned cache is shared
by the editor and bake workers, with a mutex and completion marker protecting
concurrent first use and interrupted extraction. Bundled playlist entries use
paths relative to the preset collection so moving or upgrading the installation
does not break the scene. Imported entries reference absolute file paths.

The source engine permits LGPL 2.1 or later. This bundle selects LGPL 3.0 to
accommodate its Apache-2.0 glad portions, and includes the required license and
source material. The preset collection has separate, less explicit permissions:
upstream assumes public-domain status for most presets based on their history
of free distribution, rather than collecting individual license grants. LifeViz
follows upstream packaging practice and preserves author names; it does not
claim to relicense these assets. See [full notices](../ProjectM-NOTICES.md).

## Rendering limits and verification

Each active layer owns a private OpenGL 3.3 context on its own render thread and
renders to an offscreen framebuffer at the scene's working resolution. BGRA pixels
are read back, flipped vertically, made opaque, and handed to LifeViz's existing
source compositor. Normal opacity, blending and keying then control transparency.
This implementation does not share OpenGL textures directly with Direct3D.

Live playback never blocks the UI thread on projectM. Preset loads (shader
compilation typically takes 0.5–2 s), renderer start-up and each frame's GPU
wait and readback all happen on the layer's render thread; the frame loop posts
the newest time/size/audio and shows the newest finished frame, one frame behind.
While the next preset compiles, the current one keeps playing. Before this, a
1080p layer used roughly two thirds of the UI thread (about 16 ms per frame) and
each preset change froze the window for up to two seconds, which with busy scenes
or the preset preview open left too little time for input and Windows reported
LifeViz as not responding. The preset preview in **Presets & Playback** uses its
own render thread the same way, so auditioning presets never stalls the dialog or
the output behind it. Offline bakes still wait for each preset load and frame, so
exported frames stay tied to the fixed frame clock.

Each projectM instance holds several scene-sized float render targets; at 1080p
expect roughly 300 MB of memory per layer. Windows x64 and an OpenGL 3.3 graphics
driver are required.

Preset loads are logged (`projectM loading preset '...'`) so a freeze or crash
report can name the preset that was loading; see [Logs and Crash Reports](Logs-and-Crash-Reports.md).

After a Release build, run `dotnet bin/Release/net9.0-windows/lifeviz.dll --smoke-test projectm`
for playlist, persistence, native rendering, audio, transition, resize, group
compositing and failure/recovery checks. It also runs live playback through a
renderer start and a preset switch and fails if any frame-loop tick blocks for
250 ms or more (measured worst tick is about 2 ms). PNG inspection artifacts are written
under `bin/Release/net9.0-windows/projectm-smoke/`.
Run `./tests/Test-ProjectM.ps1` to create a short audio fixture, launch a real
background bake worker and verify all 60 exported frames decode and animate.
It leaves the output and diagnostics in a unique `artifacts/projectm-bake-*` folder.
