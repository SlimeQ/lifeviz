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
    Remove-Item -Path $publishDir -Recurse -Force
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
