param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:LOCALAPPDATA\lifeviz-clickonce",
    [switch]$SkipCacheClear
)

$ErrorActionPreference = 'Stop'

function Resolve-Source {
    param([string]$PathArg)
    if ($PathArg) {
        return (Resolve-Path $PathArg).Path
    }

    # Default to the directory that contains this script (expected to be the ClickOnce publish payload).
    return (Split-Path -Parent $MyInvocation.MyCommand.Path)
}

function Resolve-Mage {
    $candidates = @(
        "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\mage.exe",
        "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\mage.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

$payloadRoot = Resolve-Source $SourcePath
$manifest = Join-Path $payloadRoot 'lifeviz.application'
if (-not (Test-Path $manifest)) {
    throw "lifeviz.application not found under $payloadRoot. Point -SourcePath at the published ClickOnce folder."
}

Write-Host "[install] Payload root: $payloadRoot" -ForegroundColor Cyan
Write-Host "[install] Target location: $InstallRoot" -ForegroundColor Cyan

if (-not $SkipCacheClear) {
    $mage = Resolve-Mage
    if ($mage) {
        Write-Host "[install] Clearing ClickOnce cache (mage -cc) to avoid prior subscription conflicts..." -ForegroundColor Cyan
        & $mage -cc | Out-Null
    } else {
        Write-Warning "mage.exe not found; skipping ClickOnce cache clear. If install complains about a different location, re-run with mage installed or uninstall the previous LifeViz entry first."
    }
}

if (Test-Path $InstallRoot) {
    Write-Host "[install] Removing previous staged payload at $InstallRoot" -ForegroundColor Cyan
    Remove-Item -Recurse -Force -Path $InstallRoot
}

New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null

# Use robocopy for speed and to preserve structure.
$robocopyLog = New-TemporaryFile
try {
    $robocopyArgs = @($payloadRoot, $InstallRoot, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS')
    robocopy @robocopyArgs | Tee-Object -FilePath $robocopyLog | Out-Null
    $robocode = $LASTEXITCODE
    if ($robocode -gt 7) {
        throw "robocopy failed with exit code $robocode. See $robocopyLog for details."
    }
} finally {
    if (Test-Path $robocopyLog) { Remove-Item $robocopyLog -Force }
}

$stagedManifest = Join-Path $InstallRoot 'lifeviz.application'
if (-not (Test-Path $stagedManifest)) {
    throw "Staged manifest missing at $stagedManifest"
}

Write-Host "[install] Launching ClickOnce manifest from $stagedManifest" -ForegroundColor Cyan
Start-Process -FilePath $stagedManifest
