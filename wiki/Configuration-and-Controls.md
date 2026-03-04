# Configuration & Controls

The UI stays invisible until you right-click anywhere on the canvas, revealing the context menu.

## Menu Actions

- **Pause Simulation / Resume Simulation** - toggles the dispatcher tick loop.
- **Randomize** - re-seeds every frame in the depth stack to a fresh random state.
- **Start Recording / Stop Recording** - toggles capture with a pixel-perfect integer upscale to the nearest HD height (720/1080/1440/2160 when divisible). Recordings are saved to `%UserProfile%\\Videos\\LifeViz`; the encoder favors quality-based VBR with bitrate caps to keep pixel edges crisp without runaway file sizes, and the taskbar icon shows a red overlay while active.
- **Recording Quality** - choose Lossless (FFV1 in MKV, compressed), Crisp (H.264 in MP4, Windows Media Player compatible), Uncompressed (AVI, huge files), or H.264 tiers (High/Balanced/Compact). Lossless and crisp options use `ffmpeg` and keep pixel-perfect output with smaller files; uncompressed records raw RGB; H.264 options trade size for quality.
- **Height** - quick presets for common screen heights (144p, 240p, 480p, 720p, 1080p, 1440p/2K, 2160p/4K) plus a *Custom...* dialog to pick any value between 72 and 2160. Height maps to the simulation's row count; width is derived from the current aspect ratio.
- **Depth** - quick presets (12-48) plus a *Custom...* dialog for 3-96 layers.
- **Lock Aspect Ratio (16:9)** - freezes the simulation grid at a 16:9 ratio; disable to let the primary source drive the aspect ratio.
- **Sources** - stack multiple live feeds. Use *Add Window Source* to pull in any visible, non-minimized top-level window, *Add Webcam Source* to attach a camera, *Add File Source* to load a static image (PNG/JPG/BMP/WEBP), GIF, or video (animated files loop), *Add Video Sequence* to build a looping playlist of videos, or *Add Layer Group* to create a nested stack. Layer groups expose their own submenu for adding sources or more groups; they composite their children internally and blend as a single layer, with the first child setting the group's aspect ratio. Each entry exposes *Make Primary* (adopts aspect ratio), *Move Up/Down*, *Remove*, *Restart Video* (video sources + sequences), *Rename Group...* (layer groups only), an *Animations* stack (Zoom In, Translate, Rotate, Beat Shake, Audio Granular, Fade, DVD Bounce with loop + speed controls; Beat Shake responds to detected audio beats when a device is selected, uses an intensity slider for tuning, and ignores speed/cycle settings for consistent amplitude, Audio Granular uses a gated/compressed response curve to keep normal spoken/ambient content from overdriving motion, adds a per-animation *3-Band EQ* (Low/Mid/High, 0-300%), and expands intensity up to 1000%, and DVD Bounce includes a size slider and a tighter cycle length range; all animations have a cycle length in beats for long fades), a per-source blend mode (Additive default; Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive), optional keying when in Normal mode (default key color black, adjustable range), and a per-source fit mode (Fill default, plus Fit/Stretch/Center/Tile/Span). Normal respects per-pixel transparency in sources like PNGs. The top-most source drives the aspect ratio; clearing all sources restores the default 16:9 ratio. Webcam capture retries initialization once and waits longer for first frames before a source is removed; capture releases automatically when you close the app or clear sources.
- **Scene Editor...** - opens a dedicated source-management window with a draggable nested tree on the left and selected-layer settings on the right. It exposes the same source operations as the *Sources* context menu (add/remove/reorder/nest sources, set primary, rename groups, restart video layers, tune blend/fit/opacity/keying, and manage animations). It also includes **App Controls...**, which opens the full main context menu at the Scene Editor location so global controls (simulation/audio/render/system toggles) are available without switching screens. Toggle *Live Mode* to apply changes immediately, or turn it off and use *Apply* to commit a batch of edits at once. Use *Save...* / *Load...* to export or import layer stacks as `.lifevizlayers.json`; live mode applies loads immediately, otherwise load into the draft and press *Apply*.
- **Audio Source** - choose the device used for beat detection/reactivity, or pick *None* to disable audio input. The menu now lists both *Output Devices* (including `System Output (Default)` for speaker loopback-style capture where supported) and *Input Devices* (microphones).
- Menu responsiveness note: device enumeration for **Audio Source** and capture-source discovery for **Sources** are performed when those specific submenus open, keeping the root context menu/snapped Scene Editor controls responsive.
- **Audio Reactivity** - configurable audio-driven simulation control (uses the selected **Audio Source** device). The menu includes:
  - *Enable Audio Reactivity* master toggle.
  - *Input Gain* (0.25x-64x) to amplify weak incoming signal before beat/energy analysis; the app remembers separate gain values for input-device mode vs output-device mode.
  - *Level -> Framerate* with `Energy Gain`, `Framerate Boost`, and `Framerate Minimum` sliders (maps smoothed audio level from the configured minimum floor to 100% of the selected target FPS; boost controls how quickly loudness reaches full-speed).
  - *Level -> Life Opacity* with `Opacity Min Scalar` (maps audio level to a scalar floor..1.0, applied against the base **Life Opacity** slider value).
  - *Level -> Seeder* with `Max Level Seeds` (continuously injects patterns as loudness rises, making reactivity obvious even without strong beat detection).
  - *Beat -> Seeder* with `Seeds Per Beat`, `Seed Cooldown`, and `Seed Pattern` (Glider, R-pentomino, Random Burst) to inject patterns on detected beat edges.
  - Reactivity controls are disabled when no audio device is selected.
- **Passthrough Underlay** - when sources are active, enable a live underlay of the composited stack behind the Game of Life output.
- **Show FPS** - toggles an overlay with measured FPS, target simulation FPS, and audio/reactivity diagnostics (input gain, multiplier, beat count, and last-step seed burst counts). Audio stats include a signal state (`Signal` vs `Low/No Signal`) to make input troubleshooting clearer.
  - Reactive diagnostics also include the current effective life opacity.
  - The `Audio: %` value is transient-weighted, so it can drop near zero between hits instead of staying pinned like a long-window loudness average.
- **Composite Blend** - choose how the underlay mixes with the simulation (Additive default; Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive).
- **Framerate** - set the base simulation target to 15 / 30 / 60 / 144 fps; the simulation can run multiple steps per render when target speed is higher than display refresh.
- **Life Modes** - switch between *Naive Grayscale* (single simulation thresholded from luminance) and *RGB Channel Bins* (independent simulations in R/G/B bins, with per-channel capture injection).
- **Binning Mode** - choose *Fill* (default; channel intensity = fraction of alive cells within the bin) or *Binary* (original bit-packed depth encoding).
- **Capture Threshold Window** - two sliders (min/max) plus *Invert Window*. Pixels inside the window set cells alive when injecting captures; invert selects the wrapped outside range instead.
- **Injection Noise** - slider (0-1) that randomly skips injection per pixel to introduce noise.
- **Fullscreen** - toggles a topmost, borderless view sized to the active monitor (covers the taskbar); state is remembered between runs.
- **Update to Latest Release...** - downloads the newest GitHub release installer and launches it to upgrade the current installation in place (LifeViz closes while the installer runs).
- **Animation BPM** - global tempo for layer animations (Zoom/Translate/Rotate/Fade/DVD Bounce). Each animation can run at expanded speeds (1/8x–8x), loop forward or reverse, and set a cycle length in beats for long durations (e.g., 10-minute fades). Use **Sync to Audio BPM** to align all animations to detected audio tempo when a device is selected.

## Custom Inputs

- Both *Custom...* entries open a lightweight modal dialog (no standard window chrome) that clamps input to supported ranges.
- Dialogs default to the current value and report validation errors inline.

## Applying Changes

- When you change height (rows), depth, or the source stack (including changing the primary), the simulation pauses, reconfigures the engine, rebuilds the `WriteableBitmap`, then resumes if it was running.
- Loading a layer config replaces the current stack; in live mode it applies immediately, otherwise it updates the draft list until you press *Apply*.
- Reconfiguring height recomputes columns to maintain the current aspect ratio (either 16:9 or the current primary source's ratio).
- The window stays in the current aspect ratio and can be resized from the corner grip; height presets, custom height entry, or fullscreen adjust the simulation resolution without introducing letterboxing.

## Persistence

- Height, depth, thresholds, modes, opacity, framerate, blend/passthrough toggles, and audio reactivity settings persist to `%AppData%\lifeviz\config.json` after the app finishes loading, so startup control events no longer overwrite prior configs.
- The source stack (windows + webcams + files + layer groups) is restored on launch when inputs are present, including ordering plus per-source blend mode, fit mode, opacity, mirror, and keying (enable/color/range); windows are matched by title, webcams by device id/name, and files by path.
- YouTube sources resolve asynchronously on startup so a slow or unavailable stream lookup does not block the UI; once resolved, the source starts feeding frames automatically (failures are logged).
- Aspect ratio lock state persists between runs (lock ratio defaults to 16:9).
- Fullscreen preference is persisted and applied after startup using the active monitor bounds so the taskbar stays hidden.

## Defaults

| Setting           | Default |
|-------------------|---------|
| Height            | 144     |
| Depth             | 24      |
| Seed RNG          | Uniform, 35% alive probability |
| Tick              | 60?ms interval |
| Sources           | None (16:9 aspect) |
| Source blend mode | Additive |
| Source fit mode   | Fill |
| Key color         | Black |
| Key range         | 10% |

## Engine Guards

- Height is clamped between 72 and 2160 (rows).
- Depth is clamped between 3 and 96.
- Rows never fall below 9, avoiding degenerate grids when height values are tiny.
- Window captures gracefully detach if a source closes or becomes inaccessible; the next source in the stack becomes primary, and removing the last source restores the default aspect ratio.
