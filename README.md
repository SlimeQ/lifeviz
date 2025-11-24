# LifeViz

Windows 11-ready WPF visualization of a 3D-stacked Game of Life grid. The UI is a distraction-free 16:9 canvas with every action exposed through a right-click context menu.

## Development

```powershell
dotnet build
dotnet run
```

## Packaging

Generate a ClickOnce one-click installer (creates `setup.exe` plus application manifests):

```powershell
dotnet publish /p:PublishProfile=Properties/PublishProfiles/WinClickOnce.pubxml
```

Artifacts land in `publish/clickonce/`. Distribute `setup.exe`; clicking it walks through the familiar ClickOnce experience with shortcuts and automatic icon usage.
