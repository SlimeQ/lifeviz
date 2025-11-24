# Build & Install

## Prerequisites

- .NET SDK 9 (for local dev & running)
- Visual Studio Build Tools 2022 or Visual Studio with MSBuild & ClickOnce components (for the installer)

## Local Development

```powershell
dotnet build
dotnet run
```

Hotkeys aren’t required—interact via right-click.

## Branded Icon

The custom neon "LV" mark lives in `Assets/lifeviz.ico` and is referenced through the `<ApplicationIcon>` property in `lifeviz.csproj`, so binaries and installers pick it up automatically.

## One-Click Installer (ClickOnce)

1. Open a **Developer PowerShell** or command prompt that exposes `MSBuild.exe` from VS/Build Tools.
2. Run the helper script from the repo root:

   ```powershell
   .\Publish-Installer.ps1
   ```

   This script locates `MSBuild.exe` via registry + well-known paths and executes:

   ```
   msbuild lifeviz.csproj /t:Publish /p:PublishProfile=Properties/PublishProfiles/WinClickOnce.pubxml /p:Configuration=Release
   ```

3. Artifacts land in `bin\Release\net9.0-windows\publish\`:
   - `setup.exe` – bootstrapper with desktop/start-menu shortcuts.
   - `lifeviz.application` – ClickOnce manifest.
   - `Application Files\lifeviz_*` – versioned payload.

Distribute `setup.exe` for a true one-click install, or host the `.application` file if you prefer web-based ClickOnce deployment.

## Troubleshooting

- Running `dotnet publish ...` alone won’t build the ClickOnce manifests; the .NET SDK MSBuild lacks those tasks (MSB4803). Always use full MSBuild.
- If the publish step can’t find MSBuild, install **Visual Studio Build Tools** and ensure the "MSBuild" and ".NET desktop development" workloads are selected.
