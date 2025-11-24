# LifeViz Wiki

Welcome to the documentation hub for LifeViz, a minimalist Windows 11 WPF experience that renders a depth-aware Game of Life stack on a pristine 16:9 canvas.

## Pages

- [Rendering Pipeline](Rendering-Pipeline.md)
- [Configuration & Controls](Configuration-and-Controls.md)
- [Build & Install](Build-and-Install.md)

## Quick Facts

- **Tech stack:** .NET 9 WPF, WriteableBitmap rendering, dispatcher-driven simulation loop.
- **Visual goals:** borderless feel, strict 16:9 ratio, no visible chrome—everything is mediated through a right-click context menu.
- **Data model:** 3D Game of Life with configurable columns and depth; historical frames drive per-pixel RGB derived from binary slices.

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
