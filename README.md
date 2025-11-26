# LifeViz

Windows 11-ready WPF visualization of a 3D-stacked Game of Life grid. The UI stays minimalist-16:9 canvas, no chrome, context menu for everything-but now it also supports tapping into any open desktop window as a live depth source.

## Development

```powershell
dotnet build
dotnet run
```

The new Rider solution (`lifeviz.sln`) includes a "lifeviz: Run App" configuration so IDE runs mirror `dotnet run`.

## Live Window Injection

Right-click the scene and use **Sources** to stack multiple windows, OBS-style:

- Add entries via **Sources > Add Window Source** (checked items are already in the stack). The top entry is the primary: it drives the canvas aspect ratio and the native-resolution target when preserve-res is on. Make Primary, Move Up/Down, or Remove/Remove All to resequence quickly; clearing all sources drops back to the 16:9 default.
- Each source has its own blend mode applied during compositing (Normal, Additive, Multiply, Screen, Overlay, Lighten, Darken, Subtractive).
- **Composite Blend** still controls how the finished composite mixes with the Game of Life output (Additive default via the pixel shader).
- **Passthrough Underlay** shows that composite behind the simulation; **Preserve Window Resolution** renders at the primary source's native size before scaling.
- Capture uses DPI-correct window bounds (via DWM) so the full surface is normalized even for PiP/scaled windows, and the composited buffer feeds the injection path (threshold window + noise + life/binning modes) on every tick.
- Framerate lock: choose 15 / 30 / 60 fps from the context menu to match capture needs or ease CPU/GPU load.
- Capture threshold window: adjustable min/max sliders (with optional invert) in the context menu; only pixels inside the window set cells alive during injection, applied before each simulation step.
- Injection noise: adjustable slider (0-1) that randomly skips cell injection per pixel to introduce controlled noise.


## Packaging & Deployment

ClickOnce remains the primary distribution path. Packaging requires the full .NET Framework MSBuild that ships with Visual Studio/Build Tools:

```powershell
# Developer PowerShell where msbuild.exe is available
msbuild lifeviz.csproj `
  /t:Publish `
  /p:PublishProfile=Properties\PublishProfiles\WinClickOnce.pubxml `
  /p:Configuration=Release
```

The repo now bundles three helper scripts:

- `Publish-Installer.ps1` - resolves `MSBuild.exe`, runs the publish target, and writes manifests + installer assets into `bin/Release/net9.0-windows/publish/`.
- `deploy.ps1` - builds, publishes with an auto-generated version (so ClickOnce always sees an update), then launches the `lifeviz.application` manifest to trigger an in-place update of the installed app.
- `Publish-GitHubRelease.ps1` - builds, publishes a tagged ClickOnce payload, zips the publish folder into `artifacts/github-release/`, and creates a GitHub release that uploads that single zip asset (requires `gh` CLI authenticated to your repo).

Artifacts:

- `Application Files/lifeviz_<version>/...` - versioned payload.
- `lifeviz.application` - ClickOnce manifest; launching it after `deploy.ps1` applies the latest build.
- `setup.exe` - optional bootstrapper for clean machines (installs prerequisites + shortcuts). Only needed for first-time installs.

To push a Windows release to GitHub:

```powershell
gh auth login # one-time
.\Publish-GitHubRelease.ps1 -Tag v1.2.3 -NotesPath release-notes.md
```

Use `-Draft` to stage without publishing. The script reuses `Publish-Installer.ps1` to generate assets and keeps the zipped payload under `artifacts/github-release/`. Downloaders should grab the zip, extract it, then run `setup.exe` (or `lifeviz.application`) from inside the extracted folder—grabbing `setup.exe` alone will miss the ClickOnce payload.

> **NOTE:** `.NET CLI` alone cannot produce ClickOnce installers (MSB4803). Always use the full MSBuild toolchain, either directly (`msbuild`) or through the scripts above.

## Wiki

All technical details (rendering pipeline, controls, install flow) live in `/wiki`. Update the relevant page whenever you change the app; see `agents.md` for documentation expectations.
