# Rendering Pipeline

## Layout

- `MainWindow.xaml` hosts a `Viewbox`-wrapped `Image` control to enforce a constant 16:9 aspect ratio regardless of window size.
- The backing surface is a `WriteableBitmap` sized to the current simulation grid (`columns x rows`) so each logical cell maps to a single pixel before scaling.

## Simulation Loop

- `DispatcherTimer` in `MainWindow.xaml.cs` ticks every ~60?ms.
- Each tick invokes `_engine.Step()` (unless paused) then calls `RenderFrame()`.
- The engine maintains a depth stack (`List<bool[,]>`) where index 0 is the newest frame.

## Color Encoding

- The depth is partitioned into three slices (R/G/B). If depth = 24, each slice gets 8 layers; remainders are distributed so `Depth % 3` spreads extra layers across R and G first.
- For each slice, the engine walks the relevant frames and treats alive cells as set bits, forming a binary number. That integer is normalized against the maximum for that bit-length to derive a 0–255 channel value.
- The renderer writes BGRA bytes into `_pixelBuffer`, then blits the buffer via `WriteableBitmap.WritePixels`.

## Z-Stack Effect

Because every new frame pushes down the history stack, movement leaves chromatic trails: older generations fade toward whichever channel owns that slice. Adjusting depth changes both the persistence and the color fidelity.

## Performance Notes

- Rows = round(columns * 9/16) to stay in aspect.
- Random fill uses 35% seed density to encourage interesting evolution.
- All rendering is CPU-side; WPF’s scaling handles presentation without smoothing (`NearestNeighbor`).
