# Build & Install

## Prerequisites

- .NET SDK 9 (for local dev & running)
- Visual Studio Build Tools 2022 or Visual Studio with MSBuild & ClickOnce components (for publishing)
- GitHub CLI (`gh`) authenticated to your repo account (required only for pushing GitHub releases)
- `ffmpeg` on PATH (required for auto-transcoding unsupported video file sources)

## Local Development

```powershell
dotnet build
dotnet run
```

Rider users can open `lifeviz.sln` and choose the built-in **lifeviz: Run App** configuration.
For ClickOnce packages inside Rider, use the shared **lifeviz: Publish Installer (MSBuild.exe)** run configuration (in `.run/`); it shells out to `Publish-Installer.ps1` so full MSBuild is used. The auto-generated Rider publish config uses `dotnet msbuild` and will trip `MSB4803`.
If you still publish via Rider's generated config, the project automatically disables the ClickOnce bootstrapper under `dotnet msbuild` so the build completes (no `setup.exe`); use the MSBuild.exe-backed script/config when you need the bootstrapper.

## Local Smoke Test Install

`install.ps1` publishes a framework-dependent build to `artifacts\local-install` and launches it (if the folder is locked, it uses a timestamped path):

```powershell
.\install.ps1
```

Optional parameters:

```powershell
.\install.ps1 -Configuration Release -Runtime win-x64 -NoRun
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

