# Rendering Pipeline

## Layout

- `MainWindow.xaml` hosts a `Viewbox`-wrapped `Image` control to enforce a constant aspect ratio. Default is 16:9, but the primary source in the *Sources* stack overrides the ratio automatically.
- The backing surface is a `WriteableBitmap` sized to the current simulation grid (`columns x rows`), so each logical cell maps to a single pixel before scaling.

## Simulation Loop

- `DispatcherTimer` in `MainWindow.xaml.cs` ticks at a user-selectable rate (15 / 30 / 60 fps) via the context menu.
- Each tick (unless paused) captures every active source, composites them (per-source blend modes, ordered by the stack), injects the resulting buffer into the simulation, advances `_engine.Step()`, then calls `RenderFrame()`.
- The engine maintains a depth stack (`List<bool[,]>`) where index 0 is the newest frame.

## Color Encoding

- The depth is partitioned into three slices (R/G/B). If depth = 24, each slice gets 8 layers; remainders are distributed so `Depth % 3` spreads extra layers across R and G first.
- For each slice, the engine walks the relevant frames and treats alive cells as set bits, forming a binary number. That integer is normalized against the maximum for that bit-length to derive a 0-255 channel value.
- The renderer writes BGRA bytes into `_pixelBuffer`, then blits the buffer via `WriteableBitmap.WritePixels`.

## Source Capture Injection

1. `WindowCaptureService` enumerates all visible, non-minimized windows (excluding LifeViz itself), grabs their DWM extended frame bounds to get physical pixel sizes (avoids DPI virtualization cropping), and captures each active window via BitBlt into a `System.Drawing.Bitmap`. `WebcamCaptureService` uses the modern WinRT `MediaCapture` API, which provides a high-performance path to stream frames directly into reusable memory buffers, avoiding the GC pressure associated with frequent allocations during capture. `FileCaptureService` loads static images (including WEBP) and GIFs via WPF bitmap decoders and uses `MediaPlayer` for videos; animated files loop, and frames are converted to BGRA buffers for compositing.
2. Each capture is downscaled to the grid size and stored as BGRA using the per-source fit mode (Fit default, plus Fill/Stretch/Center/Tile/Span); the primary source only materializes a source-resolution buffer when preserve-res rendering is enabled, and buffers are reused per window/webcam to avoid per-frame allocations.
3. Sources are composited CPU-side in stack order using their selected blend modes (Normal, Additive, Multiply, Screen, Overlay, Lighten, Darken, Subtractive) into a shared downscaled buffer (and an optional high-res buffer when preserve-res is enabled).
4. The composited downscaled buffer feeds the injection path: luminance masks for *Naive Grayscale* or per-channel masks for *RGB Channel Bins*. If a source disappears, it is removed automatically; removing the last source restores the default aspect ratio (and webcam capture is released).

## Passthrough Underlay

- When **Passthrough Underlay** is enabled, the composited buffer is preserved for presentation as well as injection.
- Rendering blends the composited underlay with the simulation output per-pixel. Supported blend modes: Additive (default), Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive.
- Underlay rendering is skipped when no sources are active or the buffer dimensions disagree with the current surface (e.g., immediately after resizing).
- **Preserve Window Resolution** renders the composite at the primary source's native resolution and samples the underlay bilinearly, then scales the Game of Life grid up to that size, reducing underlay pixelation.
- Final blending still happens in a WPF pixel shader (GPU) so passthrough stays responsive even when rendering at source resolution; per-source blends occur CPU-side during the composite build.

## Life Modes

- **Naive Grayscale** (default): one simulation drives all channels, derived from a thresholded luminance mask of the capture.
- **RGB Channel Bins:** three independent simulations occupy the R/G/B depth bins (same partition used for rendering). Window captures inject per-channel masks into their respective bin zero; propagation stays within each channel's slice (e.g., depth 24 yields bins 0-7, 8-15, 16-23).

## Binning Modes

- **Fill** (default) - channel intensity is the ratio of alive cells within the bin (frames in that channel slice). Produces smoother intensity ramps across depth.
- **Binary** - original bit-packed depth encoding; most significant bits represent newer frames, normalized to 0-255.

## Z-Stack Effect

Because every new frame pushes down the history stack, movement leaves chromatic trails. Injected window captures therefore colorize based on how recent pixels were alive, giving a time-scrubbed representation of the source window.

## Performance Notes

- Rows = round(columns / aspectRatio). The primary source replaces the default 16:9 ratio with its current ratio; removing all sources restores 16:9.
- Capture buffers are pooled (including the raw window/webcam readback), so toggling **Preserve Window Resolution** is the only time a source-resolution copy is kept alive; this removes GC spikes that caused occasional lurches.
- Random fill uses 35% seed density to encourage interesting evolution when no source is selected.
- All rendering is CPU-side; WPF's scaling handles presentation without smoothing (`NearestNeighbor`). Window capture uses GDI BitBlt, so extremely large or numerous sources may require smaller grids or lower tick rates if compositing falls behind.
