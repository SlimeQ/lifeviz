# Build & Install

## Prerequisites

- .NET SDK 9 (for local dev & running)
- Visual Studio Build Tools 2022 or Visual Studio with MSBuild & ClickOnce components (for publishing)
- GitHub CLI (`gh`) authenticated to your repo account (required only for pushing GitHub releases)

## Local Development

```powershell
dotnet build
dotnet run
```

Rider users can open `lifeviz.sln` and choose the built-in **lifeviz: Run App** configuration.

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
.\Publish-GitHubRelease.ps1 -Tag v1.2.3 -NotesPath release-notes.md
```

What it does:

- Builds in Release, then calls `Publish-Installer.ps1` with `ApplicationVersion` derived from the tag and optional `-ApplicationRevision`.
- Zips `bin/<Configuration>/net9.0-windows/publish/` to `artifacts/github-release/lifeviz-clickonce-<tag>.zip`.
- Creates a GitHub release for the supplied tag (draftable via `-Draft`) and uploads only the zip as the release asset. If the tag does not yet exist, `gh release create` will create it.

Downloaders should pull that zip, extract it locally, and then run `setup.exe` (or `lifeviz.application`) from inside the extracted folder. Downloading `setup.exe` by itself will not include the ClickOnce payload.
- To avoid ClickOnce location conflicts on target machines, run `Install-ClickOnce.ps1` from inside the extracted publish folder:

  ```powershell
  # From the extracted publish directory
  powershell -ExecutionPolicy Bypass -File .\Install-ClickOnce.ps1
  ```

## Troubleshooting

- `MSB4803` or similar errors usually mean you ran `dotnet publish` instead of full MSBuild; re-run through `Publish-Installer.ps1`/`deploy.ps1`.
- If Rider/VS doesn't see the run configs, ensure the `.run/` folder and `.idea` contents are checked out.
- Window capture requires desktop composition (Aero); minimized or hidden windows cannot be sampled.
- ClickOnce "already installed from a different location" error: use `Install-ClickOnce.ps1` to stage to `%LOCALAPPDATA%\lifeviz-clickonce`, which uses a stable deployment URI and clears the cache before installing. If you prefer manual cleanup, uninstall the previous LifeViz entry first.

