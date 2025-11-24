# Rendering Pipeline

## Layout

- `MainWindow.xaml` hosts a `Viewbox`-wrapped `Image` control to enforce a constant aspect ratio. Default is 16:9, but selecting a window input swaps the ratio to match the source window automatically.
- The backing surface is a `WriteableBitmap` sized to the current simulation grid (`columns x rows`), so each logical cell maps to a single pixel before scaling.

## Simulation Loop

- `DispatcherTimer` in `MainWindow.xaml.cs` ticks every ~60?ms.
- Each tick invokes `_engine.Step()` (unless paused), grabs a window capture (if selected), injects it into the stack, then calls `RenderFrame()`.
- The engine maintains a depth stack (`List<bool[,]>`) where index 0 is the newest frame.

## Color Encoding

- The depth is partitioned into three slices (R/G/B). If depth = 24, each slice gets 8 layers; remainders are distributed so `Depth % 3` spreads extra layers across R and G first.
- For each slice, the engine walks the relevant frames and treats alive cells as set bits, forming a binary number. That integer is normalized against the maximum for that bit-length to derive a 0–255 channel value.
- The renderer writes BGRA bytes into `_pixelBuffer`, then blits the buffer via `WriteableBitmap.WritePixels`.

## Window Capture Injection

1. `WindowCaptureService` enumerates all visible, non-minimized windows (excluding LifeViz itself) and captures the chosen handle via BitBlt into a `System.Drawing.Bitmap`.
2. The bitmap is downscaled to the grid size, converted to grayscale, thresholded (default 0.55), and turned into a `bool[,]` mask.
3. `_engine.InjectFrame(mask)` inserts the capture as the newest depth layer (z=0), pushing older frames down the stack.
4. If the window disappears, selection is automatically cleared and the aspect ratio reverts to default.

## Z-Stack Effect

Because every new frame pushes down the history stack, movement leaves chromatic trails. Injected window captures therefore colorize based on how recent pixels were alive, giving a time-scrubbed representation of the source window.

## Performance Notes

- Rows = round(columns / aspectRatio). Window selections replace the default 16:9 ratio with the source window’s ratio.
- Random fill uses 35% seed density to encourage interesting evolution when no window input is selected.
- All rendering is CPU-side; WPF’s scaling handles presentation without smoothing (`NearestNeighbor`). Window capture uses GDI BitBlt, so extremely large sources may require pausing or smaller grids if ticks fall behind.
