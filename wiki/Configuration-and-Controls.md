# Configuration & Controls

The UI stays invisible until you right-click anywhere on the canvas, revealing the context menu.

## Menu Actions

- **Pause Simulation / Resume Simulation** - toggles the dispatcher tick loop.
- **Randomize** - re-seeds every frame in the depth stack to a fresh random state.
- **Start Recording / Stop Recording** - toggles capture with a pixel-perfect integer upscale to the nearest HD height (720/1080/1440/2160 when divisible). Recordings are saved to `%UserProfile%\\Videos\\LifeViz`; the encoder favors quality-based VBR with bitrate caps to keep pixel edges crisp without runaway file sizes, and the taskbar icon shows a red overlay while active.
- **Recording Quality** - choose Lossless (AVI, huge files) or H.264 tiers (High/Balanced/Compact). Lossless records uncompressed RGB for pixel-perfect output; H.264 options trade size for quality.
- **Height** - quick presets for common screen heights (144p, 240p, 480p, 720p, 1080p, 1440p/2K, 2160p/4K) plus a *Custom...* dialog to pick any value between 72 and 2160. Height maps to the simulation's row count; width is derived from the current aspect ratio.
- **Depth** - quick presets (12-48) plus a *Custom...* dialog for 3-96 layers.
- **Lock Aspect Ratio (16:9)** - freezes the simulation grid at a 16:9 ratio; disable to let the primary source drive the aspect ratio.
- **Sources** - stack multiple live feeds. Use *Add Window Source* to pull in any visible, non-minimized top-level window, *Add Webcam Source* to attach a camera, *Add File Source* to load a static image (PNG/JPG/BMP/WEBP), GIF, or video (animated files loop), or *Add Layer Group* to create a nested stack. Layer groups expose their own submenu for adding sources or more groups; they composite their children internally and blend as a single layer, with the first child setting the group's aspect ratio. Each entry exposes *Make Primary* (adopts aspect ratio), *Move Up/Down*, *Remove*, *Rename Group...* (layer groups only), an *Animations* stack (Zoom In, Translate, Rotate with loop + speed controls), a per-source blend mode (Normal, Additive, Multiply, Screen, Overlay, Lighten, Darken, Subtractive), and a per-source fit mode (Fit default, plus Fill/Stretch/Center/Tile/Span). The top-most source drives the aspect ratio and the native-resolution target when preserve-res is enabled; clearing all sources restores the default 16:9 ratio. Webcam capture retries initialization once and waits longer for first frames before a source is removed; capture releases automatically when you close the app or clear sources.
- **Passthrough Underlay** - when sources are active, enable a live underlay of the composited stack behind the Game of Life output.
- **Composite Blend** - choose how the underlay mixes with the simulation (Additive default; Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive).
- **Preserve Window Resolution** - renders the scene at the primary source's resolution (bilinear sampling) instead of snapping to the grid size to reduce pixelation.
- **Framerate** - lock the render loop to 15 / 30 / 60 fps to tune performance.
- **Life Modes** - switch between *Naive Grayscale* (single simulation thresholded from luminance) and *RGB Channel Bins* (independent simulations in R/G/B bins, with per-channel capture injection).
- **Binning Mode** - choose *Fill* (default; channel intensity = fraction of alive cells within the bin) or *Binary* (original bit-packed depth encoding).
- **Capture Threshold Window** - two sliders (min/max) plus *Invert Window*. Pixels inside the window set cells alive when injecting captures; invert selects the wrapped outside range instead.
- **Injection Noise** - slider (0-1) that randomly skips injection per pixel to introduce noise.
- **Fullscreen** - toggles a topmost, borderless view sized to the active monitor (covers the taskbar); state is remembered between runs.
- **Animation BPM** - global tempo for layer animations (Zoom/Translate/Rotate). Each animation can run at half time, normal, or double time, and can loop forward or reverse.

## Custom Inputs

- Both *Custom...* entries open a lightweight modal dialog (no standard window chrome) that clamps input to supported ranges.
- Dialogs default to the current value and report validation errors inline.

## Applying Changes

- When you change height (rows), depth, or the source stack (including changing the primary), the simulation pauses, reconfigures the engine, rebuilds the `WriteableBitmap`, then resumes if it was running.
- Reconfiguring height recomputes columns to maintain the current aspect ratio (either 16:9 or the current primary source's ratio).
- The window stays in the current aspect ratio and can be resized from the corner grip; height presets, custom height entry, or fullscreen adjust the simulation resolution without introducing letterboxing.

## Persistence

- Height, depth, thresholds, modes, opacity, framerate, blend/passthrough toggles, and preserve-res settings persist to `%AppData%\lifeviz\config.json` after the app finishes loading, so startup control events no longer overwrite prior configs.
- The source stack (windows + webcams + files + layer groups) is restored on launch when inputs are present, including ordering plus per-source blend mode, fit mode, opacity, and mirror; windows are matched by title, webcams by device id/name, and files by path.
- Aspect ratio lock state persists between runs (lock ratio defaults to 16:9).
- Fullscreen preference is persisted and applied after startup using the active monitor bounds so the taskbar stays hidden.

## Defaults

| Setting  | Default |
|----------|---------|
| Height   | 144     |
| Depth    | 24      |
| Seed RNG | Uniform, 35% alive probability |
| Tick     | 60?ms interval |
| Sources  | None (16:9 aspect) |

## Engine Guards

- Height is clamped between 72 and 2160 (rows).
- Depth is clamped between 3 and 96.
- Rows never fall below 9, avoiding degenerate grids when height values are tiny.
- Window captures gracefully detach if a source closes or becomes inaccessible; the next source in the stack becomes primary, and removing the last source restores the default aspect ratio.
