# Build & Install

## Prerequisites

- .NET SDK 9 (for local dev & running)
- Visual Studio Build Tools 2022 or Visual Studio with MSBuild & ClickOnce components (for publishing)
- GitHub CLI (`gh`) authenticated to your repo account (required only for pushing GitHub releases)
- `ffmpeg` on PATH (required for auto-transcoding unsupported video file sources and lossless FFV1 recording)

## Local Development

```powershell
dotnet build
dotnet run
```

Rider users can open `lifeviz.sln` and choose the built-in **lifeviz: Run App** configuration.
For ClickOnce packages inside Rider, use the shared **lifeviz: Publish Installer (MSBuild.exe)** run configuration (in `.run/`); it shells out to `Publish-Installer.ps1` so full MSBuild is used. The auto-generated Rider publish config uses `dotnet msbuild` and will trip `MSB4803`.
If you still publish via Rider's generated config, the project automatically disables the ClickOnce bootstrapper under `dotnet msbuild` so the build completes (no `setup.exe`); use the MSBuild.exe-backed script/config when you need the bootstrapper.

## Runtime Smoke Tests

After building the sandbox output, you can validate the GPU sim path and startup path directly:

```powershell
dotnet build /p:UseAppHost=false /p:OutputPath=bin\Debug\net9.0-windows-sbx\
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-benchmark
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-handoff
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-rgb-threshold
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-frequency-hue
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-injection-mode
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-file-injection-mode C:\path\to\video.mp4
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-sim
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-source
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test source-reset
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test gpu-render
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-mainloop
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-240
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-480
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-rgb-240
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-rgb-480
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-file-240 C:\path\to\video.mp4
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-file-480 C:\path\to\video.mp4
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-file-rgb-240 C:\path\to\video.mp4
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-file-rgb-480 C:\path\to\video.mp4
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-current-scene
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-current-scene-visible
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test profile-current-scene-interaction
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test frame-pump-thread-safety
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test dimensions
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test shutdown
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test startup
dotnet bin\Debug\net9.0-windows-sbx\lifeviz.dll --smoke-test all
```

- `gpu-benchmark` logs average GPU sim inject/step/fill timings plus GPU source-composite upload/draw/readback timings using synthetic workloads so you can see whether readback is still dominating.
- `gpu-handoff` instantiates `MainWindow` without showing it and verifies that a GPU-built composite can inject directly into the GPU simulation backend with zero CPU composite readback bytes.
- `gpu-rgb-threshold` drives the RGB composite-injection threshold path with a pure-white source and fails if white pixels stop injecting into all three channels.
- `gpu-frequency-hue` verifies that a simulation layer's per-layer `Freq -> Hue` setting changes the resolved presentation hue numerically while the underlying RGB simulation buffer remains unchanged in the live GPU path.
- `gpu-injection-mode` drives the GPU composite-injection path with a mid-gray source and fails unless `Threshold`, `Random Pulse`, and `Pulse Width Modulation` still produce distinct output densities.
- `gpu-file-injection-mode <video>` runs that same density comparison against a real decoded file frame, which is the right tool when a report reproduces only on actual media.
- `gpu-sim` verifies the D3D11 simulation backend end to end in both `Naive Grayscale` and `RGB Channel Bins`.
- `gpu-source` instantiates `MainWindow` without showing it and verifies that the GPU source compositor actually executes through the normal `BuildCompositeFrame` path.
- `source-reset` clears the scene source stack, preserves passthrough state, re-adds a synthetic source, and fails unless the source composite becomes visible again.
- `gpu-render` launches a hidden `MainWindow` and verifies that the real GPU composite pipeline initializes through the normal render backend path.
- `profile-mainloop` runs the real frame loop against a hidden synthetic scene, writes a JSON timing report to `%LOCALAPPDATA%\lifeviz\profiles` in normal app runs, and writes to `bin\Debug\net9.0-windows-sbx\profiles\` in smoke-test mode so local test runs stay self-contained. The exported report also includes frame-gap spike counters (`>25ms`, `>33ms`, `>50ms`) so pacing regressions can be diagnosed separately from average stage cost.
- `profile-240` / `profile-480` run the same hidden-scene profiler at fixed grayscale resolutions.
- `profile-rgb-240` / `profile-rgb-480` run the profiler with the reference simulation layer forced to `RGB Channel Bins`, which is the right target when performance work touches RGB injection or Conway stepping.
- `profile-file-240` / `profile-file-480` run that same profiler against a real file source instead of synthetic buffers.
- `profile-file-rgb-240` / `profile-file-rgb-480` do the same with the reference simulation layer pinned to `RGB Channel Bins`, which is the right target when file-video playback performance only collapses once RGB layers are enabled.
- `profile-current-scene` loads the persisted user config/scene and profiles it headlessly, which is the fastest way to capture real-stage timings from the current setup without manually reading the on-screen overlay.
- `profile-current-scene-visible` does the same in a visible window so display/presentation pacing regressions can be measured against the actual desktop composition path. It now also logs file-video freshness/age metrics (`capture_file_fresh_frame_ratio`, `capture_file_frame_age_ms`) so "smooth life, slideshow underlay" regressions can be diagnosed directly.
- `profile-current-scene-interaction` performs that same real-scene visible run but opens and closes the root context menu mid-test, then fails unless post-interaction frame pacing recovers to the pre-interaction baseline.
- `frame-pump-thread-safety` opens the real Scene Editor and then evaluates the frame-pump interaction state from a worker thread, which catches cross-thread WPF access regressions in the timer-driven frame scheduler.
- Pass the file path as the third argument or set `LIFEVIZ_SMOKE_VIDEO` before launching the smoke test.
- `dimensions` applies a live height/depth change through `MainWindow`, forces the reference simulation layer into `RGB Channel Bins`, then drives the real Scene Editor height dropdown in both Live Mode and deferred Apply mode, and verifies that every simulation layer plus the presentation surface resize together.
- `shutdown` opens the real `MainWindow`, opens the Scene Editor, then closes the main window and fails if close-time teardown captures any exception or if the owned editor-close path throws.
- `startup` launches `MainWindow` in a dedicated smoke-test mode that skips loading the persisted project plus file/video/audio capture pipelines, so WPF/render startup can be validated in isolation and should exit quickly.
- `all` runs `gpu-sim` plus the combined GPU handoff/passthrough-render/source/render UI smoke suite.

## Local Smoke Test Install

`install.ps1` publishes a ClickOnce payload and, by default, runs the local `Install-ClickOnce.ps1` flow against that fresh output:

```powershell
.\install.ps1
```

By default, `install.ps1` now publishes and then runs `Install-ClickOnce.ps1` directly against the fresh publish output (more reliable for repeated in-place updates than relaunching through a generated bootstrapper each time). If you specifically need the single-file wrapper installer, pass `-BundleInstaller`.

Optional parameters:

```powershell
.\install.ps1 -Configuration Release -NoRun
.\install.ps1 -BundleInstaller
```

## Branded Icon

The custom neon "LV" mark lives in `Assets/lifeviz.ico` and is referenced via `<ApplicationIcon>` inside `lifeviz.csproj`, so binaries and installers both pick it up.

## One-Click Installer (ClickOnce)

1. Open a **Developer PowerShell** or Command Prompt that exposes `MSBuild.exe` from Visual Studio / Build Tools.
2. From the repo root, run the deployment script:

   ```powershell
   .\deploy.ps1
   ```

   Behind the scenes this script:
   - Executes `dotnet build -c Release`.
   - Calls `Publish-Installer.ps1`, which locates `MSBuild.exe` and runs:

     ```
     msbuild lifeviz.csproj /t:Publish /p:PublishProfile=Properties/PublishProfiles/WinClickOnce.pubxml /p:Configuration=Release
     ```

     while stamping a time-based ClickOnce version so updates are always detected.
   - Launches the newly published `lifeviz.application` manifest to trigger the ClickOnce update/install flow.

3. Artifacts land in `bin\Release\net9.0-windows\publish\`:
   - `lifeviz.application` - ClickOnce manifest (what the deploy script launches).
- `Application Files\lifeviz_<version>\` - versioned payload.
- `setup.exe` - optional bootstrapper for first-time installs.
- `Install-ClickOnce.ps1` - helper script that stages the payload to `%LOCALAPPDATA%\lifeviz-clickonce`, clears the old ClickOnce cache, and launches the manifest from that stable path (prevents the "already installed from a different location" error when you install from different folders).

## Updating Existing Installs

Because `deploy.ps1` embeds a unique version for every publish and opens `lifeviz.application`, the installed app always refreshes to the latest build. Use `setup.exe` only when onboarding a clean machine that lacks prerequisites.

For end users, the context menu now includes **Update to Latest Release...**, which downloads the newest GitHub release `lifeviz_installer.exe` and launches it to upgrade in place (LifeViz closes while the installer runs).

## Publish a Windows Release to GitHub

Use `Publish-GitHubRelease.ps1` to package and upload a ClickOnce build as a GitHub release:

```powershell
gh auth login # one-time
.\Publish-GitHubRelease.ps1 -NotesPath release-notes.md
```

What it does:

- Prompts for the release vibe (tiny tweak / glow-up / new era) and auto-bumps the semantic version/tag based on the existing highest `v*` tag. You can still pass `-Tag` to override if needed.
- Builds in Release, then calls `Publish-Installer.ps1` with `ApplicationVersion` derived from the new tag and optional `-ApplicationRevision`.
- Bundles the publish payload into a single self-extracting `lifeviz_installer.exe` (stored in `artifacts/github-release/`).
- Creates a GitHub release for the supplied tag (draftable via `-Draft`) and uploads only that exe as the release asset. If the tag does not yet exist, `gh release create` will create it.

Downloaders should grab the single `lifeviz_installer.exe` from the release and run it; it self-extracts the payload and launches `Install-ClickOnce.ps1` from a stable location (the manifest is rewritten to `%LOCALAPPDATA%\lifeviz-clickonce\lifeviz.application` to avoid "installed from a different location" warnings on future updates).

## Troubleshooting

- `MSB4803` or similar errors usually mean you ran `dotnet publish` instead of full MSBuild; re-run through `Publish-Installer.ps1`/`deploy.ps1` (or the **lifeviz: Publish Installer (MSBuild.exe)** run config in Rider).
- If Rider/VS doesn't see the run configs, ensure the `.run/` folder and `.idea` contents are checked out.
- Window capture requires desktop composition (Aero); minimized or hidden windows cannot be sampled.
- ClickOnce "already installed from a different location" error: use `Install-ClickOnce.ps1` to stage to `%LOCALAPPDATA%\lifeviz-clickonce`, which uses a stable deployment URI and clears the cache before installing. If you prefer manual cleanup, uninstall the previous LifeViz entry first.

