param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:LOCALAPPDATA\lifeviz-clickonce",
    [switch]$SkipCacheClear,
    [switch]$RegisterClickOnce,
    [switch]$NoLaunch
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

function Resolve-StagedApplicationExe {
    param([string]$ManifestPath)

    [xml]$xml = Get-Content $ManifestPath
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace("asmv1","urn:schemas-microsoft-com:asm.v1")
    $ns.AddNamespace("asmv2","urn:schemas-microsoft-com:asm.v2")

    $dependency = $xml.SelectSingleNode("//asmv2:dependentAssembly[contains(@codebase,'.manifest')]", $ns)
    if ($dependency -and $dependency.codebase) {
        $appManifest = Join-Path (Split-Path -Parent $ManifestPath) $dependency.codebase
        if (Test-Path $appManifest) {
            $appDir = Split-Path -Parent $appManifest
            $exe = Join-Path $appDir 'lifeviz.exe'
            if (Test-Path $exe) {
                return (Resolve-Path $exe).Path
            }
        }
    }

    $applicationFiles = Join-Path (Split-Path -Parent $ManifestPath) 'Application Files'
    $candidate = Get-ChildItem -Path $applicationFiles -Recurse -Filter 'lifeviz.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($candidate) {
        return $candidate.FullName
    }

    throw "Could not resolve staged lifeviz.exe from $ManifestPath"
}

function Remove-ClickOnceShortcuts {
    $roots = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        ([Environment]::GetFolderPath('DesktopDirectory'))
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($rootPath in $roots) {
        Get-ChildItem -Path $rootPath -Recurse -Filter '*.appref-ms' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'lifeviz' -or $_.FullName -match 'lifeviz' } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $clickOnceFolder = Join-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs') 'lifeviz'
    if ((Test-Path $clickOnceFolder) -and -not (Get-ChildItem -Path $clickOnceFolder -Force -ErrorAction SilentlyContinue)) {
        Remove-Item -Path $clickOnceFolder -Force -ErrorAction SilentlyContinue
    }
}

function New-LifeVizShortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath
    )

    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = Split-Path -Parent $TargetPath
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Save()
}

function Install-DirectShortcuts {
    param([string]$TargetPath)

    Remove-ClickOnceShortcuts

    $programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $startShortcut = Join-Path $programs 'LifeViz.lnk'
    $legacyLocalShortcut = Join-Path $programs 'LifeViz (Local).lnk'
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'LifeViz.lnk'

    New-LifeVizShortcut -ShortcutPath $startShortcut -TargetPath $TargetPath
    New-LifeVizShortcut -ShortcutPath $desktopShortcut -TargetPath $TargetPath

    if (Test-Path $legacyLocalShortcut) {
        Remove-Item -Path $legacyLocalShortcut -Force -ErrorAction SilentlyContinue
    }

    Write-Host "[install] Start Menu shortcut: $startShortcut" -ForegroundColor Cyan
    Write-Host "[install] Desktop shortcut: $desktopShortcut" -ForegroundColor Cyan
}

$payloadRoot = Resolve-Source $SourcePath
$manifest = Join-Path $payloadRoot 'lifeviz.application'
if (-not (Test-Path $manifest)) {
    throw "lifeviz.application not found under $payloadRoot. Point -SourcePath at the published ClickOnce folder."
}

Write-Host "[install] Payload root: $payloadRoot" -ForegroundColor Cyan
Write-Host "[install] Target location: $InstallRoot" -ForegroundColor Cyan

if ($RegisterClickOnce -and -not $SkipCacheClear) {
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

# Stamp a stable deployment provider URI so installs/updates always point to the same path.
try {
    [xml]$xml = Get-Content $stagedManifest
    $ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
    $ns.AddNamespace("asmv1","urn:schemas-microsoft-com:asm.v1")
    $ns.AddNamespace("asmv2","urn:schemas-microsoft-com:asm.v2")
    $providerNode = $xml.SelectSingleNode("//asmv2:deploymentProvider", $ns)
    if (-not $providerNode) {
        $providerNode = $xml.CreateElement("deploymentProvider","urn:schemas-microsoft-com:asm.v2")
        $deployNode = $xml.SelectSingleNode("//asmv2:deployment",$ns)
        if ($deployNode) { $deployNode.AppendChild($providerNode) | Out-Null }
    }
    if ($providerNode) {
        $providerUri = (New-Object System.Uri((Resolve-Path $stagedManifest).Path)).AbsoluteUri
        $null = $providerNode.SetAttribute("codebase",$providerUri)
        $xml.Save($stagedManifest)
    }
} catch {
    Write-Warning "Failed to stamp stable deployment provider URI: $_"
}

$stagedExe = Resolve-StagedApplicationExe -ManifestPath $stagedManifest
Install-DirectShortcuts -TargetPath $stagedExe

if ($NoLaunch) {
    Write-Host "[install] Launch skipped. Use the LifeViz Start Menu shortcut to run $stagedExe" -ForegroundColor Cyan
} elseif ($RegisterClickOnce) {
    Write-Host "[install] Launching ClickOnce manifest from $stagedManifest" -ForegroundColor Cyan
    Start-Process -FilePath $stagedManifest
} else {
    Write-Host "[install] Launching staged app directly from $stagedExe" -ForegroundColor Cyan
    Start-Process -FilePath $stagedExe -WorkingDirectory (Split-Path -Parent $stagedExe)
}
