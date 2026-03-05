# LifeViz Wiki

Welcome to the documentation hub for LifeViz, a minimalist Windows 11 WPF experience that renders a depth-aware Game of Life stack on a pristine canvas.

## Pages

- [Rendering Pipeline](Rendering-Pipeline.md)
- [Configuration & Controls](Configuration-and-Controls.md)
- [Build & Install](Build-and-Install.md)

## Quick Facts

- **Tech stack:** .NET 9 WPF, WriteableBitmap rendering, dispatcher-driven simulation loop, optional multi-source window injection via GDI capture plus webcam capture via WinRT `MediaCapture`, and audio reactivity via WinRT input capture + WASAPI loopback output capture.
- **Visual goals:** borderless feel, aspect-locked canvas, no visible chrome with controls primarily mediated through the right-click context menu plus the dedicated Scene Editor for source stack management.
- **Data model:** 3D Game of Life with configurable columns/depth; historical frames drive per-pixel RGB derived from binning modes (*Fill* default or *Binary*). Multiple window sources inject new frames directly into the stack and can render as an underlay after per-source compositing (Normal/Additive/Multiply/Screen/Overlay/Lighten/Darken/Subtractive, with optional Normal-mode keying). With passthrough enabled, the underlay is normally precomposited into the render buffer before simulation layers blend on top; a GPU blend fallback is used when buffers temporarily disagree (for example during resize). Simulation output composites a user-managed stack of simulation layers; each layer has its own enable state, blend mode, and input function (Direct `y=x` or Inverse `y=1-rgb`) and injects from the same final source composite into its own Conway simulation state. Life modes include Naive Grayscale and RGB Channel Bins, with optional hue shift/animation that rotates both RGB output and per-channel injection mapping. Video/YouTube layers can optionally play source audio (default off), with per-layer volume plus a global master toggle/volume in the Scene Editor header, and now support pause/play + scrubbing while always looping (video sequences loop by playlist). Sources without a playable audio stream stay silent and are logged. Framerate is selectable (15/30/60/144 fps), with optional audio-reactive level-to-FPS, level-to-opacity scalar, continuous level seeding, and beat-seeded pattern injection.

## Using this Wiki on GitHub

GitHub wikis are just git repos. To publish these pages:

```bash
git remote add wiki git@github.com:<org>/<repo>.wiki.git  # first time only
rsync -av --delete wiki/ ./tmp-lifeviz-wiki
cd ./tmp-lifeviz-wiki
git add .
git commit -m "Update wiki"
git push wiki main
```

Adjust remotes/branch names to match your setup. You can also copy the markdown files manually through GitHub's UI if preferred.
