param(
    [string]$SourcePath,
    [string]$InstallRoot = "$env:LOCALAPPDATA\lifeviz-clickonce",
    [switch]$SkipCacheClear,
    [switch]$RegisterClickOnce,
    [switch]$NoLaunch,
    [int]$WaitForProcessId = 0
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

function Wait-ForStagedLifeVizProcesses {
    param(
        [string]$StagedRoot,
        [int]$ExplicitProcessId
    )

    $rootPath = [IO.Path]::GetFullPath($StagedRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    $processIds = @()
    if ($ExplicitProcessId -gt 0 -and $ExplicitProcessId -ne $PID) {
        $explicitProcess = Get-Process -Id $ExplicitProcessId -ErrorAction SilentlyContinue
        if ($explicitProcess) {
            try {
                $explicitPath = $explicitProcess.Path
                if ($explicitProcess.ProcessName -eq 'lifeviz' -and
                    $explicitPath -and
                    $explicitPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
                    $processIds += $ExplicitProcessId
                }
            } catch {}
        }
    }

    Get-Process -Name 'lifeviz' -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            $candidatePath = $_.Path
            if ($candidatePath -and $candidatePath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
                $processIds += $_.Id
            }
        } catch {}
    }

    foreach ($processId in ($processIds | Sort-Object -Unique)) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if (-not $process) {
            continue
        }

        Write-Host "[install] Waiting for running LifeViz process $processId to exit..." -ForegroundColor Cyan
        if (-not $process.WaitForExit(60000)) {
            throw "LifeViz process $processId did not exit within 60 seconds. Close LifeViz and run the installer again."
        }
    }

    if ($processIds.Count -gt 0) {
        Start-Sleep -Milliseconds 300
    }
}

function Remove-DirectoryWithRetry {
    param(
        [string]$Path,
        [int]$MaxAttempts = 20
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }

        try {
            Remove-Item -Recurse -Force -LiteralPath $Path
            return
        } catch {
            if (-not (Test-Path -LiteralPath $Path)) {
                return
            }
            if ($attempt -eq $MaxAttempts) {
                throw
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Move-DirectoryWithRetry {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$MaxAttempts = 10
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            Move-Item -LiteralPath $Source -Destination $Destination
            return
        } catch {
            if ($attempt -eq $MaxAttempts) {
                throw "Failed to move '$Source' to '$Destination' after $MaxAttempts attempts: $_"
            }
            Start-Sleep -Milliseconds 250
        }
    }
}

function Copy-PayloadWithRetry {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$MaxAttempts = 5
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $robocopyLog = [IO.Path]::GetTempFileName()
        $robocode = 16
        $details = ''
        try {
            $robocopyArgs = @(
                $Source,
                $Destination,
                '/E',
                '/R:1',
                '/W:1',
                '/NFL',
                '/NDL',
                '/NC',
                '/NS',
                '/NP',
                '/XF',
                'payload.zip'
            )
            robocopy @robocopyArgs | Tee-Object -FilePath $robocopyLog | Out-Null
            $robocode = $LASTEXITCODE
            if (Test-Path -LiteralPath $robocopyLog) {
                $details = Get-Content -Raw -LiteralPath $robocopyLog
            }
        } finally {
            if (Test-Path -LiteralPath $robocopyLog) {
                Remove-Item -LiteralPath $robocopyLog -Force -ErrorAction SilentlyContinue
            }
        }

        if ($robocode -le 7) {
            return
        }

        if ($attempt -eq $MaxAttempts) {
            throw "robocopy failed with exit code $robocode after $MaxAttempts attempts.`n$details"
        }

        Write-Warning "Payload copy attempt $attempt failed with robocopy exit code $robocode; retrying."
        Start-Sleep -Milliseconds (500 * $attempt)
    }
}

function Promote-ValidatedPayload {
    param(
        [string]$StagingRoot,
        [string]$InstallRoot,
        [string]$BackupRoot
    )

    $backupCreated = $false
    if (Test-Path -LiteralPath $InstallRoot) {
        Write-Host "[install] Moving previous install to rollback backup: $BackupRoot" -ForegroundColor Cyan
        Move-DirectoryWithRetry -Source $InstallRoot -Destination $BackupRoot
        $backupCreated = $true
    }

    try {
        Move-DirectoryWithRetry -Source $StagingRoot -Destination $InstallRoot

        $promotedManifest = Join-Path $InstallRoot 'lifeviz.application'
        if (-not (Test-Path -LiteralPath $promotedManifest)) {
            throw "Promoted payload is missing its manifest at $promotedManifest"
        }
        $null = Resolve-StagedApplicationExe -ManifestPath $promotedManifest
    } catch {
        $promotionError = $_
        if ($backupCreated) {
            Write-Warning "New payload promotion failed; restoring the previous install from $BackupRoot."
            if (Test-Path -LiteralPath $InstallRoot) {
                Remove-DirectoryWithRetry -Path $InstallRoot
            }
            Move-DirectoryWithRetry -Source $BackupRoot -Destination $InstallRoot
            $backupCreated = $false
        }
        throw $promotionError
    }

    if ($backupCreated -and (Test-Path -LiteralPath $BackupRoot)) {
        try {
            Remove-DirectoryWithRetry -Path $BackupRoot
        } catch {
            Write-Warning "The new install is active, but rollback-backup cleanup failed at ${BackupRoot}: $_"
        }
    }
}

$payloadRoot = Resolve-Source $SourcePath
$manifest = Join-Path $payloadRoot 'lifeviz.application'
if (-not (Test-Path $manifest)) {
    throw "lifeviz.application not found under $payloadRoot. Point -SourcePath at the published ClickOnce folder."
}

Write-Host "[install] Payload root: $payloadRoot" -ForegroundColor Cyan
Write-Host "[install] Target location: $InstallRoot" -ForegroundColor Cyan

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$stagingPrefix = "$InstallRoot.installing-"
$stagingRoot = $stagingPrefix + [Guid]::NewGuid().ToString('N')
$backupPrefix = "$InstallRoot.backup-"
$backupRoot = $backupPrefix + [Guid]::NewGuid().ToString('N')
if (-not $stagingRoot.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage outside the install-root sibling path: $stagingRoot"
}
if (-not $backupRoot.StartsWith($backupPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to back up outside the install-root sibling path: $backupRoot"
}

$promoted = $false
try {
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    Write-Host "[install] Copying new payload to transaction staging: $stagingRoot" -ForegroundColor Cyan
    Copy-PayloadWithRetry -Source $payloadRoot -Destination $stagingRoot

    $candidateManifest = Join-Path $stagingRoot 'lifeviz.application'
    if (-not (Test-Path -LiteralPath $candidateManifest)) {
        throw "Copied payload is missing its manifest at $candidateManifest"
    }
    $null = Resolve-StagedApplicationExe -ManifestPath $candidateManifest

    Wait-ForStagedLifeVizProcesses -StagedRoot $InstallRoot -ExplicitProcessId $WaitForProcessId

    if ($RegisterClickOnce -and -not $SkipCacheClear) {
        $mage = Resolve-Mage
        if ($mage) {
            Write-Host "[install] Clearing ClickOnce cache (mage -cc) to avoid prior subscription conflicts..." -ForegroundColor Cyan
            & $mage -cc | Out-Null
        } else {
            Write-Warning "mage.exe not found; skipping ClickOnce cache clear. If install complains about a different location, re-run with mage installed or uninstall the previous LifeViz entry first."
        }
    }

    Promote-ValidatedPayload -StagingRoot $stagingRoot -InstallRoot $InstallRoot -BackupRoot $backupRoot
    $promoted = $true
} catch {
    if (-not $promoted -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-DirectoryWithRetry -Path $stagingRoot -MaxAttempts 4
    }
    throw
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
