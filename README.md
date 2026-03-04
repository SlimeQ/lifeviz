# LifeViz

Windows 11-ready WPF visualization of a 3D-stacked Game of Life grid. The UI stays minimalist-16:9 canvas, no chrome, with controls centered in the right-click context menu plus a dedicated Scene Editor for source stack workflows-and it supports tapping into open desktop windows, webcams, and media files as live depth sources.

## Development

```powershell
dotnet build
dotnet run
```

The new Rider solution (`lifeviz.sln`) includes a "lifeviz: Run App" configuration so IDE runs mirror `dotnet run`.

For a quick local ClickOnce install/update smoke test:

```powershell
.\install.ps1
```

By default, `install.ps1` now runs `Install-ClickOnce.ps1` directly against the fresh publish output (more reliable for in-place updates). Use `-BundleInstaller` if you explicitly want a generated single-file `lifeviz_installer.exe`.

## Live Window Injection

Right-click the scene and use **Sources** to stack multiple windows, OBS-style:

- Add entries via **Sources > Add Window Source**, **Add Webcam Source**, **Add File Source**, **Add Video Sequence**, or **Add Layer Group** (checked items are already in the stack). File sources accept static images (PNG/JPG/BMP/WEBP), GIFs, and videos; animated files loop. Video sources always loop, and video sequences build a playlist that advances on end and loops back to the first clip. Video layers expose **Restart**, **Pause/Play**, and **Scrub** controls so you can seek directly to a position. Layer groups have their own submenu stack; they composite their children internally and blend as a single layer, using the first child to determine the group's aspect ratio. The top entry is the primary: it drives the canvas aspect ratio (unless the aspect ratio lock is enabled). Make Primary, Move Up/Down, or Remove/Remove All to resequence quickly; clearing all sources drops back to the 16:9 default.
- Video, video-sequence, and YouTube layers include a **Play Audio** toggle (default off) plus a per-layer **Audio Volume** control. Enable audio per source when you want that layer's soundtrack routed to your default output device, then trim that source independently. Sources without an audio stream stay silent and log a warning instead of continuously retrying playback.
- Audio playback is lifecycle-managed per source: muting a layer, removing it, or closing the app now shuts down that source's in-app audio pipeline.
- Audio playback startup is debounced per source so rapid state changes do not spawn duplicate decode/playback pipelines.
- Enabling **Play Audio** now seeks to the current video playback position (best effort), so toggling audio on mid-playback stays close to visual timing instead of always starting at 0:00.
- Audio decode is clocked in real-time to avoid fast decode churn/restarts that can cause stuttery playback artifacts.
- Each source exposes a wallpaper-style fit mode (Fill default, plus Fit/Stretch/Center/Tile/Span) that controls how the layer scales into the frame.
- Each source has its own blend mode applied during compositing (Additive default; Normal, Multiply, Screen, Overlay, Lighten, Darken, Subtractive). Normal respects per-pixel transparency in sources like PNGs, and can optionally key out a background color (default black) with an adjustable range.
- **Scene Editor...** opens a dedicated two-pane source manager with a draggable nested tree on the left and selected-layer settings on the right. It covers the full per-source workflow from the Sources context menu (add/remove/reorder/nest, primary selection, blend/fit/opacity/keying, animations, restart, pause/play, and group naming) and includes an **App Controls...** button that opens the full main context menu at the editor location for global settings parity. For video-capable layers it adds a scrub slider with live time readout so you can seek playback position. The Scene Editor header also provides global source-audio controls: **Master Audio** (toggle) and a master volume slider that scale/mute all video/YouTube/sequence audio without changing per-layer settings. Enable **Live Mode** for immediate updates, or turn it off and use **Apply** to batch changes. The editor includes **Save...** and **Load...** buttons to export/import layer stacks as `.lifevizlayers.json`; live mode applies loads immediately, otherwise load into the draft and hit **Apply**.
- Each source can stack animations (Zoom In, Translate, Rotate, Beat Shake, Audio Granular, Fade, DVD Bounce) synced to the global animation BPM, with forward or reverse loops plus expanded speed steps (1/8x–8x) and per-animation cycle lengths for long fades; Beat Shake responds to detected audio beats when a device is selected (falls back to BPM if not) and includes an intensity slider (speed/cycle do not affect its amplitude), Audio Granular now uses a gated/compressed response curve with a per-layer 3-band EQ (Low/Mid/High) and a wider intensity range up to 1000% (with a stronger neutral 100/100/100 EQ baseline), and DVD Bounce exposes a size control. Use **Animation BPM > Sync to Audio BPM** to beat-match all animations, and **Audio Source > None** to clear the input.
- **Audio Reactivity** adds configurable simulation modulation from the selected audio source: enable level-driven framerate scaling (`Level -> Framerate`, scales from a configurable minimum % to 100% of target FPS via `Framerate Minimum`), level-driven life opacity scalar (`Level -> Life Opacity`, with `Opacity Min Scalar` applied against the base `Life Opacity` slider), continuous level-driven seeding (`Level -> Seeder`, with `Max Level Seeds`), and beat-driven pattern seeding (`Beat -> Seeder`, with glider / R-pentomino / random burst patterns, seeds-per-beat, and cooldown controls). You can boost weak signal via `Input Gain` (remembered separately for input vs output devices), and **Audio Source** includes output devices (including `System Output (Default)` via WASAPI loopback) plus input devices.
- Context menu responsiveness: the **Sources** and **Audio Source** submenus now refresh device lists when those submenus are opened, instead of doing that work every time the root menu opens.
- Audio level percent shown in the FPS overlay is transient-focused (short-term energy above recent baseline), so it drops much more between hits than a long averaged loudness meter.
- Video file sources are decoded with `ffmpeg`; if frames render blank, LifeViz will auto-transcode to H.264 using `ffmpeg` (cached under `%LOCALAPPDATA%\\lifeviz\\video-cache`). While transcoding, the layer is temporarily skipped (so it won't blank out the stack). Install `ffmpeg` on your PATH or transcode manually if needed.
- **Composite Blend** still controls how the finished composite mixes with the Game of Life output (Additive default via the pixel shader; Normal is transparency-aware).
- **Passthrough Underlay** shows the composite behind the simulation.
- **Fullscreen** toggle lives in the context menu and persists; it now sizes to the active monitor bounds, stays topmost, and covers the taskbar.
- **Update to Latest Release...** pulls down the newest GitHub release installer and runs it to upgrade the current installation in place (LifeViz closes while the installer runs).
- Capture uses DPI-correct window bounds (via DWM) so the full surface is normalized even for PiP/scaled windows, and the composited buffer feeds the injection path (threshold window + noise + life/binning modes) on every tick.
- Capture buffers are pooled and only the downscaled composite is retained, eliminating GC spikes from per-frame allocations.
- Webcam sources stream via WinRT `MediaCapture`; clearing sources or closing the app releases the camera. Cameras retry initialization once and wait longer for first frames before being removed.
- Framerate lock: choose 15 / 30 / 60 / 144 fps from the context menu to match capture needs or ease CPU/GPU load. `Level -> Framerate` scales simulation speed between a configurable minimum floor and that selected target, based on audio level.
- **Show FPS** now includes reactive diagnostics (target FPS, input gain, reactive multiplier, effective life opacity, beat count, and seed burst counts) so you can verify audio mapping is live.
- Capture threshold window: adjustable min/max sliders (with optional invert) in the context menu; only pixels inside the window set cells alive during injection, applied before each simulation step.
- Injection noise: adjustable slider (0-1) that randomly skips cell injection per pixel to introduce controlled noise.
- **RGB Hue Shift** rotates **RGB Channel Bins** with an `Offset` slider (0-360 deg) and animated `Speed` slider (-180..180 deg/s). The same shift is used for channel injection mapping, so source colors are remapped into the rotated bin basis (e.g., if the red bin rotates toward purple, purple input drives that bin more strongly).
  - Double-click the `Speed` slider to instantly reset animation speed to `0 deg/s`.
- Built-in recording: **Start Recording** writes to `%UserProfile%\\Videos\\LifeViz` using a pixel-perfect integer upscale to the nearest HD height (720/1080/1440/2160 when divisible). Use **Recording Quality** to pick Lossless (FFV1 in MKV, compressed), Crisp (H.264 in MP4, Windows Media Player compatible), Uncompressed (AVI, huge files), or H.264 tiers (High/Balanced/Compact); encoding favors quality-based VBR with bitrate caps so pixel lines stay crisp, and a taskbar overlay appears while active. Lossless and crisp recording use `ffmpeg` on PATH.

## Configuration

- Settings persist to `%AppData%\lifeviz\config.json` after the app finishes loading (height/rows, depth, framerate, blend/composite toggles, thresholds, opacity, RGB hue shift offset/speed, passthrough, audio reactivity controls, etc.) and restore on next launch. The window keeps the current aspect ratio and can be resized from the corner grip; use height presets or fullscreen to change simulation resolution without letterboxing.
- The source stack is restored too: window sources are matched by title, webcams by device id/name, file sources by path (video sequences restore their ordered path list), keeping order (including nested layer groups) plus per-layer blend mode, fit mode, opacity, video-audio toggle, video-audio volume, mirror, and keying settings (enable/color/range) when the inputs are available. The global source-audio master toggle/volume are also restored.
- YouTube sources resolve asynchronously on launch so the UI can come up even if the stream lookup is slow; the layer starts once the stream URL is resolved (check logs for failures).
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
