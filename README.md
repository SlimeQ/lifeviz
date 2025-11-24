# LifeViz

Windows 11-ready WPF visualization of a 3D-stacked Game of Life grid. The UI is a distraction-free 16:9 canvas with every action exposed through a right-click context menu.

## Development

```powershell
dotnet build
dotnet run
```

## Packaging

ClickOnce gives you the familiar one-click installer (desktop shortcut, start menu entry, automatic icon, etc.). Creating the bootstrapper (`setup.exe`) requires the full .NET Framework MSBuild that ships with Visual Studio or the Build Tools workload:

```powershell
# From a Developer Command Prompt / PowerShell where msbuild.exe is available
msbuild lifeviz.csproj `
  /t:Publish `
  /p:PublishProfile=Properties\PublishProfiles\WinClickOnce.pubxml `
  /p:Configuration=Release
```

Publishing drops the ClickOnce payload under `bin\Release\net9.0-windows\publish\` (subfolder `Application Files\lifeviz_*`). Share `setup.exe` for the true one-click experience or the `.application` manifest for framework-dependent installs. The .NET SDK CLI cannot run the ClickOnce manifest + bootstrapper tasks, so stick to the full MSBuild toolset when packaging.
