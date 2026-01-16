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
