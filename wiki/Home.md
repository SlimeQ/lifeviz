# LifeViz Wiki

Welcome to the documentation hub for LifeViz, a minimalist Windows 11 WPF experience that renders a depth-aware Game of Life stack on a pristine canvas.

## Pages

- [Rendering Pipeline](Rendering-Pipeline.md)
- [Configuration & Controls](Configuration-and-Controls.md)
- [Build & Install](Build-and-Install.md)

## Quick Facts

- **Tech stack:** .NET 9 WPF, WriteableBitmap rendering, dispatcher-driven simulation loop, optional live window injection via GDI capture.
- **Visual goals:** borderless feel, aspect-locked canvas, no visible chrome-everything is mediated through a right-click context menu.
- **Data model:** 3D Game of Life with configurable columns/depth; historical frames drive per-pixel RGB derived from binning modes (*Fill* default or *Binary*). Window captures inject new frames directly into the stack and can render as an underlay with selectable blend modes and optional native-resolution preservation; life modes include Naive Grayscale and RGB Channel Bins; framerate is selectable (15/30/60 fps).

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
