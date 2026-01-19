[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'lifeviz.csproj'
$publishDir = Join-Path $root 'artifacts\local-install'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'

Write-Host "[install] Publishing to $publishDir" -ForegroundColor Cyan

if (Test-Path $publishDir) {
    try {
        Remove-Item -Path $publishDir -Recurse -Force
    }
    catch {
        $timestamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $publishDir = Join-Path $root "artifacts\local-install_$timestamp"
        Write-Warning "[install] Previous install is locked. Publishing to $publishDir instead."
    }
}

New-Item -Path $publishDir -ItemType Directory | Out-Null

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:UseAppHost=true `
    -p:PublishSingleFile=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $publishDir

$exePath = Join-Path $publishDir 'lifeviz.exe'
if (!(Test-Path $exePath)) {
    throw "Publish succeeded but lifeviz.exe was not found at $exePath"
}

if (-not $NoRun) {
    Write-Host "[install] Launching $exePath" -ForegroundColor Cyan
    Start-Process $exePath
}

function New-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$TargetPath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory
    )

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $TargetPath
    $shortcut.Description = 'LifeViz (local install)'
    $shortcut.Save()
}

if (Test-Path $startMenu) {
    Write-Host "[install] Updating Start Menu shortcuts" -ForegroundColor Cyan
    $clickOnceMatches = Get-ChildItem -Path $startMenu -Recurse -Filter 'lifeviz*.appref-ms' -ErrorAction SilentlyContinue
    foreach ($match in $clickOnceMatches) {
        if ($match.Name -notmatch 'ClickOnce') {
            $targetName = 'LifeViz (ClickOnce).appref-ms'
            $targetPath = Join-Path $match.DirectoryName $targetName
            if (Test-Path $targetPath) {
                $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
                $targetPath = Join-Path $match.DirectoryName "LifeViz (ClickOnce) $stamp.appref-ms"
            }
            Rename-Item -Path $match.FullName -NewName (Split-Path -Leaf $targetPath) -Force
        }
    }

    $primaryShortcut = Join-Path $startMenu 'LifeViz.lnk'
    $localShortcut = Join-Path $startMenu 'LifeViz (Local).lnk'
    New-StartMenuShortcut -ShortcutPath $primaryShortcut -TargetPath $exePath -WorkingDirectory $publishDir
    New-StartMenuShortcut -ShortcutPath $localShortcut -TargetPath $exePath -WorkingDirectory $publishDir
}
