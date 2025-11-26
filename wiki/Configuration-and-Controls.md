# Configuration & Controls

The UI stays invisible until you right-click anywhere on the canvas, revealing the context menu.

## Menu Actions

- **Pause Simulation / Resume Simulation** - toggles the dispatcher tick loop.
- **Randomize** - re-seeds every frame in the depth stack to a fresh random state.
- **Columns** - quick presets (64-256) plus a *Custom...* dialog to pick any value between 32 and 512.
- **Depth** - quick presets (12-48) plus a *Custom...* dialog for 3-96 layers.
- **Sources** - stack multiple live window feeds. Use *Add Window Source* to pull in any visible, non-minimized top-level window (checked entries are already in the stack). Each entry exposes *Make Primary* (adopts aspect ratio), *Move Up/Down*, *Remove*, and a per-source blend mode (Normal, Additive, Multiply, Screen, Overlay, Lighten, Darken, Subtractive). The top-most source drives the aspect ratio and the native-resolution target when preserve-res is enabled; clearing all sources restores the default 16:9 ratio.
- **Passthrough Underlay** - when sources are active, enable a live underlay of the composited stack behind the Game of Life output.
- **Composite Blend** - choose how the underlay mixes with the simulation (Additive default; Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive).
- **Preserve Window Resolution** - renders the scene at the primary source's resolution (bilinear sampling) instead of snapping to the grid size to reduce pixelation.
- **Framerate** - lock the render loop to 15 / 30 / 60 fps to tune performance.
- **Life Modes** - switch between *Naive Grayscale* (single simulation thresholded from luminance) and *RGB Channel Bins* (independent simulations in R/G/B bins, with per-channel capture injection).
- **Binning Mode** - choose *Fill* (default; channel intensity = fraction of alive cells within the bin) or *Binary* (original bit-packed depth encoding).
- **Capture Threshold Window** - two sliders (min/max) plus *Invert Window*. Pixels inside the window set cells alive when injecting captures; invert selects the wrapped outside range instead.
- **Injection Noise** - slider (0-1) that randomly skips injection per pixel to introduce noise.

## Custom Inputs

- Both *Custom...* entries open a lightweight modal dialog (no standard window chrome) that clamps input to supported ranges.
- Dialogs default to the current value and report validation errors inline.

## Applying Changes

- When you change columns, depth, or the source stack (including changing the primary), the simulation pauses, reconfigures the engine, rebuilds the `WriteableBitmap`, then resumes if it was running.
- Reconfiguring columns automatically recomputes rows to maintain the current aspect ratio (either 16:9 or the current primary source's ratio).

## Defaults

| Setting  | Default |
|----------|---------|
| Columns  | 128     |
| Depth    | 24      |
| Seed RNG | Uniform, 35% alive probability |
| Tick     | 60?ms interval |
| Sources  | None (16:9 aspect) |

## Engine Guards

- Columns are clamped between 32 and 512.
- Depth is clamped between 3 and 96.
- Rows never fall below 9, avoiding degenerate grids when columns are tiny.
- Window captures gracefully detach if a source closes or becomes inaccessible; the next source in the stack becomes primary, and removing the last source restores the default aspect ratio.
