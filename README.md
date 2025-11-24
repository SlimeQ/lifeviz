# LifeViz

Windows 11-ready WPF visualization of a 3D-stacked Game of Life grid. The UI stays minimalist—16:9 canvas, no chrome, context menu for everything—but now it also supports tapping into any open desktop window as a live depth source.

## Development

```powershell
dotnet build
dotnet run
```

The new Rider solution (`lifeviz.sln`) includes a "lifeviz: Run App" configuration so IDE runs mirror `dotnet run`.

## Live Window Injection

Right-click the scene to pick **Window Input ? <any open window>**. Once selected:

- The Game of Life grid locks to the window's aspect ratio (and auto-resizes rows).
- Each simulation tick captures that window, converts it to a boolean mask (grayscale + threshold), and injects it into the z-stack's newest layer.
- Choosing **Window Input ? None** releases the aspect ratio back to 16:9 and resumes fully procedural simulation.

## Packaging & Deployment

ClickOnce remains the primary distribution path. Packaging requires the full .NET Framework MSBuild that ships with Visual Studio/Build Tools:

```powershell
# Developer PowerShell where msbuild.exe is available
msbuild lifeviz.csproj `
  /t:Publish `
  /p:PublishProfile=Properties\PublishProfiles\WinClickOnce.pubxml `
  /p:Configuration=Release
```

The repo now bundles two helper scripts:

- `Publish-Installer.ps1` – resolves `MSBuild.exe`, runs the publish target, and writes manifests + installer assets into `bin/Release/net9.0-windows/publish/`.
- `deploy.ps1` – builds, publishes with an auto-generated version (so ClickOnce always sees an update), then launches the `lifeviz.application` manifest to trigger an in-place update of the installed app.

Artifacts:

- `Application Files/lifeviz_<version>/...` – versioned payload.
- `lifeviz.application` – ClickOnce manifest; launching it after `deploy.ps1` applies the latest build.
- `setup.exe` – optional bootstrapper for clean machines (installs prerequisites + shortcuts). Only needed for first-time installs.

> **NOTE:** `.NET CLI` alone cannot produce ClickOnce installers (MSB4803). Always use the full MSBuild toolchain, either directly (`msbuild`) or through the scripts above.

## Wiki

All technical details (rendering pipeline, controls, install flow) live in `/wiki`. Update the relevant page whenever you change the app; see `agents.md` for documentation expectations.
