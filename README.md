# LifeViz

Windows 11-ready WPF visualization of a 3D-stacked Game of Life grid. The UI stays minimalist-16:9 canvas, no chrome, context menu for everything-but now it also supports tapping into open desktop windows, webcams, and media files as live depth sources.

## Development

```powershell
dotnet build
dotnet run
```

The new Rider solution (`lifeviz.sln`) includes a "lifeviz: Run App" configuration so IDE runs mirror `dotnet run`.

For a quick local install-style smoke test (publish to `artifacts\local-install`, add Start Menu shortcuts, and launch the app; if the folder is locked it falls back to a timestamped path):

```powershell
.\install.ps1
```

The script also creates `LifeViz.lnk` and `LifeViz (Local).lnk` in the Start Menu and renames any ClickOnce `lifeviz*.appref-ms` entries to `LifeViz (ClickOnce)...` so search defaults to the local build.

## Live Window Injection

Right-click the scene and use **Sources** to stack multiple windows, OBS-style:

- Add entries via **Sources > Add Window Source**, **Add Webcam Source**, **Add File Source**, **Add Video Sequence**, or **Add Layer Group** (checked items are already in the stack). File sources accept static images (PNG/JPG/BMP/WEBP), GIFs, and videos; animated files loop. Video sequences build a playlist of videos that advances on end and loops back to the first, and video layers expose a **Restart** action. Layer groups have their own submenu stack; they composite their children internally and blend as a single layer, using the first child to determine the group's aspect ratio. The top entry is the primary: it drives the canvas aspect ratio and the native-resolution target when preserve-res is on (unless the aspect ratio lock is enabled). Make Primary, Move Up/Down, or Remove/Remove All to resequence quickly; clearing all sources drops back to the 16:9 default.
- Each source exposes a wallpaper-style fit mode (Fill default, plus Fit/Stretch/Center/Tile/Span) that controls how the layer scales into the frame.
- Each source has its own blend mode applied during compositing (Additive default; Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive).
- **Layer Editor...** opens a separate window to manage the stack on another monitor. Enable **Live Mode** for immediate updates, or turn it off and use **Apply** to batch changes.
- Each source can stack animations (Zoom In, Translate, Rotate, Fade, DVD Bounce) synced to the global animation BPM, with forward or reverse loops plus expanded speed steps (1/8x–8x) and per-animation cycle lengths for long fades; DVD Bounce exposes a size control.
- Video file sources depend on system codecs; if frames render blank, LifeViz will auto-transcode to H.264 using `ffmpeg` (cached under `%LOCALAPPDATA%\\lifeviz\\video-cache`). While transcoding, the layer is temporarily skipped (so it won't blank out the stack). Install `ffmpeg` on your PATH or transcode manually if needed.
- **Composite Blend** still controls how the finished composite mixes with the Game of Life output (Additive default via the pixel shader).
- **Passthrough Underlay** shows that composite behind the simulation; **Preserve Window Resolution** renders at the primary source's native size before scaling.
- **Fullscreen** toggle lives in the context menu and persists; it now sizes to the active monitor bounds, stays topmost, and covers the taskbar.
- Capture uses DPI-correct window bounds (via DWM) so the full surface is normalized even for PiP/scaled windows, and the composited buffer feeds the injection path (threshold window + noise + life/binning modes) on every tick.
- Capture buffers are pooled and source-resolution copies are only produced when **Preserve Window Resolution** is enabled, eliminating GC spikes from per-frame allocations.
- Webcam sources stream via WinRT `MediaCapture`; clearing sources or closing the app releases the camera. Cameras retry initialization once and wait longer for first frames before being removed.
- Framerate lock: choose 15 / 30 / 60 fps from the context menu to match capture needs or ease CPU/GPU load.
- Capture threshold window: adjustable min/max sliders (with optional invert) in the context menu; only pixels inside the window set cells alive during injection, applied before each simulation step.
- Injection noise: adjustable slider (0-1) that randomly skips cell injection per pixel to introduce controlled noise.
- Built-in recording: **Start Recording** writes to `%UserProfile%\\Videos\\LifeViz` using a pixel-perfect integer upscale to the nearest HD height (720/1080/1440/2160 when divisible). Use **Recording Quality** to pick Lossless (FFV1 in MKV, compressed), Crisp (H.264 in MP4, Windows Media Player compatible), Uncompressed (AVI, huge files), or H.264 tiers (High/Balanced/Compact); encoding favors quality-based VBR with bitrate caps so pixel lines stay crisp, and a taskbar overlay appears while active. Lossless and crisp recording use `ffmpeg` on PATH.

## Configuration

- Settings persist to `%AppData%\lifeviz\config.json` after the app finishes loading (height/rows, depth, framerate, blend/composite toggles, thresholds, opacity, passthrough, etc.) and restore on next launch. The window keeps the current aspect ratio and can be resized from the corner grip; use height presets or fullscreen to change simulation resolution without letterboxing.
- The source stack is restored too: window sources are matched by title, webcams by device id/name, file sources by path (video sequences restore their ordered path list), keeping order (including nested layer groups) plus per-layer blend mode, fit mode, opacity, and mirror settings when the inputs are available.
- Aspect ratio lock state persists (default lock ratio is 16:9).
- Fullscreen preference is remembered and re-applied on launch using the active monitor bounds so the taskbar stays hidden.


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
- `Publish-GitHubRelease.ps1` - builds, publishes a ClickOnce payload, bundles it into a single `lifeviz_installer.exe` (self-extracting + auto-install), and creates a GitHub release that uploads that one exe (requires `gh` CLI authenticated to your repo). It asks a quick vibe check (tiny tweak / glow-up / new era) and auto-bumps the version/tag for you-no need to invent numbers.
- `Install-ClickOnce.ps1` - bundled alongside published payloads; stages the ClickOnce files to `%LOCALAPPDATA%\lifeviz-clickonce`, clears the old ClickOnce cache, and launches the manifest from that stable path so future installs/updates don't break when the zip is extracted to a new folder.
- Rider users: run the **lifeviz: Publish Installer (MSBuild.exe)** configuration (stored in `.run/`), which shells out to `Publish-Installer.ps1` so full MSBuild is used. The auto-generated Rider publish config uses `dotnet msbuild` and will fail with `MSB4803`.
- If you do publish via `dotnet msbuild` (e.g., Rider's generated publish config), the project automatically disables the ClickOnce bootstrapper so the build succeeds; use the MSBuild.exe-backed scripts to produce the optional `setup.exe` bootstrapper.

Artifacts:

- `Application Files/lifeviz_<version>/...` - versioned payload.
- `lifeviz.application` - ClickOnce manifest; launching it after `deploy.ps1` applies the latest build.
- `setup.exe` - optional bootstrapper for clean machines (installs prerequisites + shortcuts). Only needed for first-time installs.

To push a Windows release to GitHub:

```powershell
gh auth login # one-time
.\Publish-GitHubRelease.ps1 -NotesPath release-notes.md
```

Use `-Draft` to stage without publishing. The script reuses `Publish-Installer.ps1` to generate assets and emits a single `lifeviz_installer.exe` into `artifacts/github-release/`; the GitHub release uploads just that executable.

> **NOTE:** `.NET CLI` alone cannot produce ClickOnce installers (MSB4803). Always use the full MSBuild toolchain, either directly (`msbuild`) or through the scripts above.

## Wiki

All technical details (rendering pipeline, controls, install flow) live in `/wiki`. Update the relevant page whenever you change the app; see `agents.md` for documentation expectations.
