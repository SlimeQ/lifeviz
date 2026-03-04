# Rendering Pipeline

## Layout

- `MainWindow.xaml` hosts a `Viewbox`-wrapped `Image` control to enforce a constant aspect ratio. Default is 16:9, but the primary source in the *Sources* stack overrides the ratio automatically (if the primary is a layer group, its first child supplies the ratio).
- The backing surface is a `WriteableBitmap` sized to the current simulation grid (`columns x rows`), so each logical cell maps to a single pixel before scaling.

## Simulation Loop

- `CompositionTarget.Rendering` drives the frame loop in `MainWindow.xaml.cs`; simulation stepping uses a target interval derived from the selected framerate (15 / 30 / 60 / 144 fps), optional FPS oscillation, and optional audio-reactive FPS boost.
- Each simulation step (unless paused) captures every active source, composites them (per-source blend modes, ordered by the stack, including layer groups), injects the resulting buffer into the simulation, applies optional audio seeding (continuous level-based and/or beat-triggered), advances `_engine.Step()`, then calls `RenderFrame()`.
- When target simulation FPS exceeds display refresh, LifeViz runs multiple simulation steps per render callback (bounded catch-up) so high-rate modulation remains visible in evolution speed.
- The engine maintains a depth stack (`List<bool[,]>`) where index 0 is the newest frame.

## Color Encoding

- The depth is partitioned into three slices (R/G/B). If depth = 24, each slice gets 8 layers; remainders are distributed so `Depth % 3` spreads extra layers across R and G first.
- For each slice, the engine walks the relevant frames and treats alive cells as set bits, forming a binary number. That integer is normalized against the maximum for that bit-length to derive a 0-255 channel value.
- In *RGB Channel Bins* mode, a post-bin hue rotation can be applied before blitting. The renderer computes a hue-rotation matrix once per frame from the configured `Offset` plus time-based `Speed` term, then remaps each pixel's RGB triplet through that matrix.
- RGB capture injection uses the inverse of that same hue rotation when building per-channel masks, remapping source colors into the rotated bin basis before threshold/PWM gating. This keeps channel activity aligned to the currently shifted palette.
- The renderer writes BGRA bytes into `_pixelBuffer`, then blits the buffer via `WriteableBitmap.WritePixels`.

## Source Capture Injection

1. `WindowCaptureService` enumerates all visible, non-minimized windows (excluding LifeViz itself), grabs their DWM extended frame bounds to get physical pixel sizes (avoids DPI virtualization cropping), and captures each active window via BitBlt into a `System.Drawing.Bitmap`. `WebcamCaptureService` uses the modern WinRT `MediaCapture` API, which provides a high-performance path to stream frames directly into reusable memory buffers, avoiding the GC pressure associated with frequent allocations during capture. `FileCaptureService` loads static images (including WEBP) and GIFs via WPF bitmap decoders and streams video frames via `ffmpeg`; animated files loop, video sequences advance on end, and frames are converted to BGRA buffers for compositing.
2. Each capture is downscaled to the grid size and stored as BGRA using the per-source fit mode (Fill default, plus Fit/Stretch/Center/Tile/Span); buffers are reused per window/webcam to avoid per-frame allocations.
3. Sources are composited CPU-side in stack order using their selected blend modes (Normal, Additive, Multiply, Screen, Overlay, Lighten, Darken, Subtractive) into a shared downscaled buffer. Normal uses the source pixel alpha (plus the layer opacity slider), so transparent PNGs blend cleanly without additive washout; optional keying (Normal-only) applies a per-layer key color + range to attenuate alpha before blending. Layer groups are composited recursively: each group blends its children into a private composite buffer sized to the group's primary aspect (fit within the engine grid), then that group composite is treated as a single layer in its parent stack.
4. The composited downscaled buffer feeds the injection path: luminance masks for *Naive Grayscale* or per-channel masks for *RGB Channel Bins*. If a source disappears, it is removed automatically; removing the last source restores the default aspect ratio (and webcam capture is released).

## Passthrough Underlay

- When **Passthrough Underlay** is enabled, the composited buffer is preserved for presentation as well as injection.
- Rendering blends the composited underlay with the simulation output per-pixel. Supported blend modes: Additive (default), Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive.
- Underlay rendering is skipped when no sources are active or the buffer dimensions disagree with the current surface (e.g., immediately after resizing).
- Final blending happens in a WPF pixel shader (GPU) so passthrough stays responsive; per-source blends occur CPU-side during the composite build.

## Layer Animations

- Each source (including layer groups) can stack animations: Zoom In, Translate, Rotate, Beat Shake, Audio Granular, Fade, and DVD Bounce (with an adjustable scale). Every animation includes a cycle length in beats, so long fades can stay locked to BPM; Beat Shake uses audio beat timestamps when *Sync to Audio BPM* is enabled (falling back to the animation BPM for a timed pulse when no audio device is configured), exposes an intensity control, and ignores speed/cycle settings so its amplitude stays consistent per beat. Audio Granular uses transient energy with a soft gate, nonlinear compression, and bounded transform caps so typical speech/noise does not stay over-animated; it also applies per-animation 3-band EQ gains (Low/Mid/High) to shape zoom/translation/rotation response and supports higher intensity scaling up to 1000%. Neutral EQ (100/100/100) now maps to a stronger baseline response so the effect is clearly visible without pushing intensity to extremes.
- Video sources are decoded with `ffmpeg` into raw BGRA frames. If a video fails decode (blank frames or zero reported dimensions), LifeViz can auto-transcode to H.264 using `ffmpeg` (cached under `%LOCALAPPDATA%\\lifeviz\\video-cache`). While transcoding, the layer is skipped so it doesn't black out the stack. If `ffmpeg` is missing or the transcode fails, a warning is shown so you can transcode manually.
- Video/YouTube layers can optionally play source audio (default off) through an in-app audio pipeline (`ffmpeg` PCM decode + NAudio output). YouTube playback resolves video and audio URLs independently so audio can still play when the render path uses a video-only stream. If a source has no playable audio stream, the layer logs a warning and remains silent. Audio startup is debounced to prevent duplicate launches during rapid state changes, the decode input is clocked in real-time to avoid fast decode/restart churn, and the pipeline is torn down when audio is toggled off, the source is removed, or the app shuts down. When audio is enabled mid-playback, the decoder seeks to the current estimated video time so audio starts near the active frame instead of restarting from the beginning. Source audio gain is applied as `master volume * per-layer volume`, with a global master toggle/slider plus per-layer volume controls (including video sequences), and both are persisted/restored with source settings.
- Video transport controls support pause/play and scrubbing. Seeking restarts decode at the requested offset (best effort) and keeps pause state; pausing stops both video decode and source audio, resuming restarts decode from the paused position. Single video/YouTube sources run in loop mode continuously, while video sequences loop at the list level.
- Animations are evaluated during compositing by transforming the destination sampling coordinates before the fit-mode mapping, so the animation affects both injection and rendering.
- All animations share a global BPM (default 140) with per-animation half/normal/double time and forward or reverse (ping-pong) loops.

## Audio Reactivity

- Audio input is analyzed continuously (`AudioBeatDetector`) for energy and beat events.
- Device selection supports both capture inputs (microphones, via AudioGraph) and render outputs (via WASAPI loopback, including a default system output entry).
- `Input Gain` scales raw samples before energy, beat, and FFT analysis so quiet microphones can drive reactivity; gain presets are tracked separately for input-device and output-device capture paths.
- Spectrum outputs are signal-gated: when RMS falls below a small floor, reported bass/frequency collapse to zero to avoid noisy "fake frequency" readings on silence.
- The displayed/reactive level is transient-weighted (current normalized energy vs a short moving baseline), not a long average loudness meter, so values can fall close to zero between hits.
- *Level -> Framerate* maps smoothed, normalized loudness (derived from RMS in dB) to a configurable minimum..1.0 drive against the selected target FPS (quiet sections stay at the floor; loud sections run at the full target).
- *Level -> Life Opacity* maps level to a configurable scalar floor..1.0 and multiplies the base Life Opacity setting, so simulation visibility breathes with the audio envelope.
- *Level -> Seeder* converts the same loudness signal into per-step seed burst counts (with configurable max bursts), injecting selected patterns continuously as level rises.
- When `Level -> Framerate` drives simulation speed near zero, level seeding still injects bursts so reactivity remains visible in the frame output.
- *Beat -> Seeder* watches beat count edges and injects configurable seed bursts (Glider, R-pentomino, Random Burst) with a configurable cooldown to avoid runaway injection.
- Audio-reactive controls are gated by audio device selection (`Audio Source`); with no device selected, reactivity remains configured but inactive.

## Recording

- **Start Recording** captures the final output buffer, then applies a pixel-perfect integer upscale to the nearest HD height (720/1080/1440/2160 when divisible). **Recording Quality** chooses between lossless FFV1 in MKV (compressed, requires `ffmpeg`), crisp H.264 in MP4 (Windows Media Player compatible, requires `ffmpeg`), uncompressed RGB (AVI), or H.264 tiers (High/Balanced/Compact). H.264 uses quality-based VBR with bitrate caps to preserve sharp pixel edges without runaway file sizes.
- Recording uses the same composite buffers as the renderer (including passthrough blend mode and invert composite), then writes frames at the configured framerate.

## Life Modes

- **Naive Grayscale** (default): one simulation drives all channels, derived from a thresholded luminance mask of the capture.
- **RGB Channel Bins:** three independent simulations occupy the R/G/B depth bins (same partition used for rendering). Window captures inject per-channel masks into their respective bin zero; propagation stays within each channel's slice (e.g., depth 24 yields bins 0-7, 8-15, 16-23).
  - Optional `RGB Hue Shift` rotates both output colors and input channel mapping (via inverse transform during mask generation), so the simulation's per-channel behavior changes with hue angle.

## Binning Modes

- **Fill** (default) - channel intensity is the ratio of alive cells within the bin (frames in that channel slice). Produces smoother intensity ramps across depth.
- **Binary** - original bit-packed depth encoding; most significant bits represent newer frames, normalized to 0-255.

## Z-Stack Effect

Because every new frame pushes down the history stack, movement leaves chromatic trails. Injected window captures therefore colorize based on how recent pixels were alive, giving a time-scrubbed representation of the source window.

## Performance Notes

- Columns = round(rows * aspectRatio). The primary source replaces the default 16:9 ratio with its current ratio; removing all sources restores 16:9, and the selected height (rows) stays fixed while the width adapts.
- Capture buffers are pooled (including the raw window/webcam readback), and only the downscaled composite is kept alive, removing GC spikes that caused occasional lurches.
- Random fill uses 35% seed density to encourage interesting evolution when no source is selected.
- All rendering is CPU-side; WPF's scaling handles presentation without smoothing (`NearestNeighbor`). Window capture uses GDI BitBlt, so extremely large or numerous sources may require smaller grids or lower tick rates if compositing falls behind.
