[CmdletBinding()]
param(
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 45,
    [switch]$KeepArtifacts,
    [string]$PublishedPayloadPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'The installer transaction regression test requires Windows.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installerHelper = Join-Path $repoRoot 'Install-ClickOnce.ps1'
$releaseScript = Join-Path $repoRoot 'Publish-GitHubRelease.ps1'
$localInstallScript = Join-Path $repoRoot 'install.ps1'
$mainWindowSource = Join-Path $repoRoot 'MainWindow.xaml.cs'
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$pingExe = Join-Path $env:WINDIR 'System32\PING.EXE'

foreach ($requiredPath in @($installerHelper, $releaseScript, $localInstallScript, $mainWindowSource, $windowsPowerShell, $pingExe)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required test input is missing: $requiredPath"
    }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$auditRoot = Join-Path $tempRoot ('lifeviz-installer-regression-' + [Guid]::NewGuid().ToString('N'))
$startedProcesses = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Stop-TestProcess {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return
    }

    try {
        $Process.Refresh()
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            $null = $Process.WaitForExit(5000)
        }
    } catch {
        # The process may have exited between Refresh and Stop-Process.
    }
}

function Invoke-TestHelperProcess {
    param(
        [string]$HelperPath,
        [string]$PayloadRoot,
        [string]$InstallRoot,
        [string]$WorkingDirectory
    )

    $arguments =
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass " +
        "-File `"$HelperPath`" " +
        "-SourcePath `"$PayloadRoot`" " +
        "-InstallRoot `"$InstallRoot`" " +
        '-NoLaunch'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $windowsPowerShell
    $startInfo.Arguments = $arguments
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Windows PowerShell did not start for the disposable installer helper.'
    }
    $startedProcesses.Add($process)

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-TestProcess $process
        throw "Installer helper exceeded the $TimeoutSeconds-second test timeout."
    }
    $process.WaitForExit()

    return [pscustomobject]@{
        Process = $process
        ExitCode = $process.ExitCode
        Stdout = $stdoutTask.GetAwaiter().GetResult()
        Stderr = $stderrTask.GetAwaiter().GetResult()
    }
}

function Wait-ForDirectoryMove {
    param(
        [string]$Source,
        [string]$Destination,
        [int]$TimeoutMilliseconds = 5000
    )

    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $lastError = $null
    while ($deadline.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        try {
            [IO.Directory]::Move($Source, $Destination)
            return
        } catch {
            $lastError = $_
            Start-Sleep -Milliseconds 100
        }
    }

    throw "Directory remained locked after its holder exited: $($lastError.Exception.Message)"
}

function Assert-DirectoryMoveIsBlocked {
    param(
        [string]$Name,
        [scriptblock]$StartHolder
    )

    $caseRoot = Join-Path $auditRoot $Name
    $installRoot = Join-Path $caseRoot 'lifeviz-clickonce'
    $versionRoot = Join-Path $installRoot 'Application Files\lifeviz_old'
    $backupRoot = Join-Path $caseRoot 'lifeviz-clickonce.backup'
    New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null

    $holder = $null
    $moveWasBlocked = $false
    try {
        $holder = & $StartHolder $versionRoot $caseRoot
        if ($null -eq $holder) {
            throw "$Name did not start its lock-holder process."
        }
        $startedProcesses.Add($holder)
        Start-Sleep -Milliseconds 750
        $holder.Refresh()
        Assert-True (-not $holder.HasExited) "$Name lock-holder exited before the rename check."

        try {
            [IO.Directory]::Move($installRoot, $backupRoot)
        } catch {
            $moveWasBlocked = $true
        }

        Assert-True $moveWasBlocked "$Name did not block the install-root rename as expected."
    } finally {
        Stop-TestProcess $holder
    }

    if (Test-Path -LiteralPath $installRoot) {
        Wait-ForDirectoryMove -Source $installRoot -Destination $backupRoot
    }
    Assert-True (Test-Path -LiteralPath $backupRoot) "$Name rename did not succeed after the holder exited."
    Write-Host "[pass] $Name blocks promotion while held and releases it after exit."
}

function Assert-RunningImageMoveBehavior {
    $caseRoot = Join-Path $auditRoot 'running-image'
    $installRoot = Join-Path $caseRoot 'lifeviz-clickonce'
    $versionRoot = Join-Path $installRoot 'Application Files\lifeviz_old'
    $backupRoot = Join-Path $caseRoot 'lifeviz-clickonce.backup'
    New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null

    $heldImage = Join-Path $versionRoot 'held-ping.exe'
    Copy-Item -LiteralPath $pingExe -Destination $heldImage
    $startHolderParameters = @{
        FilePath = $heldImage
        ArgumentList = '-t 127.0.0.1'
        WorkingDirectory = $caseRoot
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    $holder = Start-Process @startHolderParameters
    $startedProcesses.Add($holder)

    try {
        Start-Sleep -Milliseconds 750
        $holder.Refresh()
        Assert-True (-not $holder.HasExited) 'Running-image holder exited before the rename check.'

        # Windows permits an atomic parent-directory rename while an image in that
        # directory is executing, provided that the process CWD is somewhere else.
        [IO.Directory]::Move($installRoot, $backupRoot)
        $holder.Refresh()
        Assert-True (Test-Path -LiteralPath $backupRoot) 'The install root was not renamed around the running image.'
        Assert-True (-not $holder.HasExited) 'The running image was terminated by the parent-directory rename.'
    } finally {
        Stop-TestProcess $holder
    }

    Write-Host '[pass] A running image alone permits promotion when its process CWD is outside the install root.'
}

function New-MinimalPayload {
    param([string]$PayloadRoot)

    $versionRoot = Join-Path $PayloadRoot 'Application Files\lifeviz_9_9_9_9'
    New-Item -ItemType Directory -Force -Path $versionRoot | Out-Null

    $manifest = @'
<?xml version="1.0" encoding="utf-8"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1"
          xmlns:asmv2="urn:schemas-microsoft-com:asm.v2"
          manifestVersion="1.0">
  <asmv2:deployment install="true">
    <asmv2:deploymentProvider codebase="file:///placeholder/lifeviz.application" />
  </asmv2:deployment>
  <dependency>
    <asmv2:dependentAssembly dependencyType="install"
                              codebase="Application Files\lifeviz_9_9_9_9\lifeviz.exe.manifest"
                              size="1" />
  </dependency>
</assembly>
'@

    [IO.File]::WriteAllText(
        (Join-Path $PayloadRoot 'lifeviz.application'),
        $manifest,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $versionRoot 'lifeviz.exe.manifest'),
        '<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0" />',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $versionRoot 'lifeviz.exe'),
        'installer-regression-placeholder',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $versionRoot 'new-version.txt'),
        'new',
        [Text.UTF8Encoding]::new($false))

    $bundleRoot = Join-Path $versionRoot 'ffmpeg'
    New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null
    $fileEntries = foreach ($name in @('ffmpeg.exe', 'LICENSE', 'README.txt')) {
        $bytes = [Text.Encoding]::UTF8.GetBytes("fixture-$name")
        [IO.File]::WriteAllBytes((Join-Path $bundleRoot $name), $bytes)
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $digest = [Convert]::ToBase64String($sha.ComputeHash($bytes)) }
        finally { $sha.Dispose() }
        '<file name="ffmpeg\{0}"><hash><DigestMethod Algorithm="http://www.w3.org/2000/09/xmldsig#sha256"/><DigestValue>{1}</DigestValue></hash></file>' -f $name, $digest
    }
    [IO.File]::WriteAllText((Join-Path $versionRoot 'lifeviz.exe.manifest'),
        '<assembly xmlns="urn:schemas-microsoft-com:asm.v1">' + ($fileEntries -join '') + '</assembly>')
}

function New-TestableInstallerHelperCopy {
    param(
        [string]$Destination,
        [switch]$ReduceMoveRetries
    )

    $source = [IO.File]::ReadAllText($installerHelper)
    $shortcutCall = 'Install-DirectShortcuts -TargetPath $stagedExe'
    $matches = [regex]::Matches($source, [regex]::Escape($shortcutCall)).Count
    if ($matches -ne 1) {
        throw "Expected one direct-shortcut install call in Install-ClickOnce.ps1; found $matches."
    }

    # Only the disposable copy is changed. The transaction remains production-identical,
    # but the regression test cannot rewrite the user's real Start Menu/Desktop shortcuts.
    $source = $source.Replace(
        $shortcutCall,
        "Write-Host '[test] Shortcut mutation suppressed for disposable transaction.'")

    if ($ReduceMoveRetries) {
        $moveCall = 'Move-DirectoryWithRetry -Source $InstallRoot -Destination $BackupRoot'
        $moveMatches = [regex]::Matches($source, [regex]::Escape($moveCall)).Count
        if ($moveMatches -ne 1) {
            throw "Expected one old-install move call in Install-ClickOnce.ps1; found $moveMatches."
        }
        $source = $source.Replace($moveCall, "$moveCall -MaxAttempts 2")
    }

    [IO.File]::WriteAllText($Destination, $source, [Text.UTF8Encoding]::new($false))
}

function Assert-SourceHandoffHardening {
    $mainSource = [IO.File]::ReadAllText($mainWindowSource)
    $releaseSource = [IO.File]::ReadAllText($releaseScript)
    $localInstallSource = [IO.File]::ReadAllText($localInstallScript)

    $updaterLaunch = [regex]::Match(
        $mainSource,
        '(?s)Process\.Start\(new ProcessStartInfo\s*\{\s*FileName\s*=\s*installerPath,.*?\}\);')
    Assert-True $updaterLaunch.Success 'Could not locate the in-app installer launch block.'
    Assert-True -Condition ($updaterLaunch.Value -match 'WorkingDirectory\s*=') -Message 'The in-app updater must set an explicit safe WorkingDirectory for the installer.'

    $bootstrapChangesDirectory =
        $releaseSource -match 'Directory\.SetCurrentDirectory\s*\(' -or
        $releaseSource -match 'Environment\.CurrentDirectory\s*='
    Assert-True -Condition $bootstrapChangesDirectory -Message 'The generated bootstrapper must move its own current directory outside the install root.'

    $bootstrapChildLaunch = [regex]::Match(
        $releaseSource,
        '(?s)var psi = new ProcessStartInfo\("powershell"\)\s*\{.*?\};')
    Assert-True $bootstrapChildLaunch.Success 'Could not locate the bootstrapper PowerShell launch block.'
    Assert-True -Condition ($bootstrapChildLaunch.Value -match 'WorkingDirectory\s*=') -Message 'The bootstrapper must set an explicit safe WorkingDirectory for the helper process.'

    $localBootstrapChangesDirectory =
        $localInstallSource -match 'Directory\.SetCurrentDirectory\s*\(' -or
        $localInstallSource -match 'Environment\.CurrentDirectory\s*='
    Assert-True -Condition $localBootstrapChangesDirectory -Message 'The local bundled installer must move its current directory outside the install root.'

    $localBootstrapChildLaunch = [regex]::Match(
        $localInstallSource,
        '(?s)var psi = new ProcessStartInfo\("powershell"\)\s*\{.*?\};')
    Assert-True $localBootstrapChildLaunch.Success 'Could not locate the local bootstrapper PowerShell launch block.'
    Assert-True -Condition ($localBootstrapChildLaunch.Value -match 'WorkingDirectory\s*=') -Message 'The local bootstrapper must set an explicit safe WorkingDirectory for the helper process.'

    Write-Host '[pass] Updater plus release/local bootstrappers explicitly relocate their handoff working directories.'
}

function Invoke-ScopedFfmpegCleanupTransaction {
    $caseRoot = Join-Path $auditRoot 'scoped-ffmpeg-cleanup'
    $payloadRoot = Join-Path $caseRoot 'payload'
    $installRoot = Join-Path $caseRoot 'lifeviz-clickonce'
    $oldVersionRoot = Join-Path $installRoot 'Application Files\lifeviz_old'
    $harnessRoot = Join-Path $caseRoot 'harness'
    $externalToolsRoot = Join-Path $caseRoot 'external-tools'
    $helperCopy = Join-Path $harnessRoot 'Install-ClickOnce.ps1'

    New-Item -ItemType Directory -Force -Path $payloadRoot, $oldVersionRoot, $harnessRoot, $externalToolsRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $oldVersionRoot 'old-version.txt'),
        'old',
        [Text.UTF8Encoding]::new($false))
    New-MinimalPayload -PayloadRoot $payloadRoot
    New-TestableInstallerHelperCopy -Destination $helperCopy

    # Simulate a v4.4.x decoder: its image lives outside the install, but it has
    # inherited a current directory inside the versioned application folder.
    $fakeFfmpegPath = Join-Path $externalToolsRoot 'ffmpeg.exe'
    Copy-Item -LiteralPath $pingExe -Destination $fakeFfmpegPath
    $startFfmpegParameters = @{
        FilePath = $fakeFfmpegPath
        ArgumentList = '-t 127.0.0.1'
        WorkingDirectory = $oldVersionRoot
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    $fakeFfmpeg = Start-Process @startFfmpegParameters
    $startedProcesses.Add($fakeFfmpeg)

    $startUnrelatedFfmpegParameters = @{
        FilePath = $fakeFfmpegPath
        ArgumentList = '-t 127.0.0.1'
        WorkingDirectory = $externalToolsRoot
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    $unrelatedFfmpeg = Start-Process @startUnrelatedFfmpegParameters
    $startedProcesses.Add($unrelatedFfmpeg)
    Start-Sleep -Milliseconds 500
    $fakeFfmpeg.Refresh()
    $unrelatedFfmpeg.Refresh()
    Assert-True (-not $fakeFfmpeg.HasExited) 'The fake legacy FFmpeg holder exited before helper launch.'
    Assert-True (-not $unrelatedFfmpeg.HasExited) 'The unrelated FFmpeg control process exited before helper launch.'

    $helperParameters = @{
        HelperPath = $helperCopy
        PayloadRoot = $payloadRoot
        InstallRoot = $installRoot
        # Reproduce the old direct/bootstrap handoff too: the helper itself must
        # release this inherited install-root CWD before it can promote.
        WorkingDirectory = $oldVersionRoot
    }
    $helperResult = Invoke-TestHelperProcess @helperParameters
    $helperExitCode = $helperResult.ExitCode
    $stdout = $helperResult.Stdout
    $stderr = $helperResult.Stderr

    Assert-True -Condition ($helperExitCode -eq 0) -Message "Installer helper failed to clean up its scoped FFmpeg holder (exit $helperExitCode).`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"

    $fakeFfmpeg.Refresh()
    $unrelatedFfmpeg.Refresh()
    Assert-True $fakeFfmpeg.HasExited 'The helper did not retire the legacy FFmpeg current-directory holder.'
    Assert-True (-not $unrelatedFfmpeg.HasExited) 'The helper terminated an unrelated FFmpeg process outside the install root.'
    Assert-True (($stdout + $stderr) -match ([regex]::Escape("FFmpeg process $($fakeFfmpeg.Id)"))) 'The helper log did not identify the scoped FFmpeg process it retired.'

    $newMarker = Join-Path $installRoot 'Application Files\lifeviz_9_9_9_9\new-version.txt'
    $oldMarker = Join-Path $installRoot 'Application Files\lifeviz_old\old-version.txt'
    Assert-True (Test-Path -LiteralPath $newMarker) 'The validated new payload was not promoted.'
    Assert-True (-not (Test-Path -LiteralPath $oldMarker)) 'The old payload remained active after promotion.'

    $debris = @(Get-ChildItem -LiteralPath $caseRoot -Directory -Force | Where-Object {
        $_.Name -like 'lifeviz-clickonce.installing-*' -or
        $_.Name -like 'lifeviz-clickonce.backup-*'
    })
    Assert-True ($debris.Count -eq 0) 'The successful transaction left staging or rollback directories behind.'
    Write-Host '[pass] Helper retires only the scoped legacy FFmpeg holder and promotes a disposable payload.'
}

function Invoke-NonFfmpegHolderFailureTransaction {
    $caseRoot = Join-Path $auditRoot 'non-ffmpeg-holder'
    $payloadRoot = Join-Path $caseRoot 'payload'
    $installRoot = Join-Path $caseRoot 'lifeviz-clickonce'
    $oldVersionRoot = Join-Path $installRoot 'Application Files\lifeviz_old'
    $harnessRoot = Join-Path $caseRoot 'harness'
    $externalToolsRoot = Join-Path $caseRoot 'external-tools'
    $helperCopy = Join-Path $harnessRoot 'Install-ClickOnce.ps1'
    $oldRootMarker = Join-Path $installRoot 'old-root-marker.txt'
    $oldVersionMarker = Join-Path $oldVersionRoot 'old-version.txt'

    New-Item -ItemType Directory -Force -Path $payloadRoot, $oldVersionRoot, $harnessRoot, $externalToolsRoot | Out-Null
    [IO.File]::WriteAllText($oldRootMarker, 'old-root', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($oldVersionMarker, 'old-version', [Text.UTF8Encoding]::new($false))
    New-MinimalPayload -PayloadRoot $payloadRoot
    New-TestableInstallerHelperCopy -Destination $helperCopy -ReduceMoveRetries

    $holderPath = Join-Path $externalToolsRoot 'render-helper.exe'
    Copy-Item -LiteralPath $pingExe -Destination $holderPath
    $startHolderParameters = @{
        FilePath = $holderPath
        ArgumentList = '-t 127.0.0.1'
        WorkingDirectory = $oldVersionRoot
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    $holder = Start-Process @startHolderParameters
    $startedProcesses.Add($holder)
    Start-Sleep -Milliseconds 500
    $holder.Refresh()
    Assert-True (-not $holder.HasExited) 'The non-FFmpeg holder exited before helper launch.'

    $helperParameters = @{
        HelperPath = $helperCopy
        PayloadRoot = $payloadRoot
        InstallRoot = $installRoot
        WorkingDirectory = $harnessRoot
    }
    $helperResult = Invoke-TestHelperProcess @helperParameters
    $helperExitCode = $helperResult.ExitCode
    $stdout = $helperResult.Stdout
    $stderr = $helperResult.Stderr
    $combinedLog = $stdout + $stderr

    Assert-True -Condition ($helperExitCode -ne 0) -Message "Helper unexpectedly promoted through a non-FFmpeg holder.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
    $holder.Refresh()
    Assert-True (-not $holder.HasExited) 'The installer killed a non-FFmpeg current-directory holder.'
    Assert-True ($combinedLog -match 'Non-FFmpeg process') 'The installer did not report the non-FFmpeg holder.'
    Assert-True ($combinedLog -match ([regex]::Escape("PID $($holder.Id)"))) 'The installer diagnostic did not include the non-FFmpeg holder PID.'
    Assert-True (Test-Path -LiteralPath $oldRootMarker) 'The failed atomic promotion removed the old root marker.'
    Assert-True (Test-Path -LiteralPath $oldVersionMarker) 'The failed atomic promotion removed the old version marker.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'Application Files\lifeviz_9_9_9_9\new-version.txt'))) 'The failed transaction activated the new payload.'

    $debris = @(Get-ChildItem -LiteralPath $caseRoot -Directory -Force | Where-Object {
        $_.Name -like 'lifeviz-clickonce.installing-*' -or
        $_.Name -like 'lifeviz-clickonce.backup-*'
    })
    Assert-True ($debris.Count -eq 0) 'The rejected non-FFmpeg-holder transaction left staging or rollback directories behind.'
    Write-Host '[pass] Non-FFmpeg CWD holder is reported and preserved; atomic failure leaves the old install intact.'
}

function Invoke-FfmpegUpgradeTransaction {
    $caseRoot = Join-Path $auditRoot 'ffmpeg-upgrade'
    $payloadRoot = Join-Path $caseRoot 'payload'
    $installRoot = Join-Path $caseRoot 'lifeviz-clickonce'
    $oldVersionRoot = Join-Path $installRoot 'Application Files\lifeviz_4_8_8_0'
    $helperCopy = Join-Path $caseRoot 'Install-ClickOnce.ps1'
    New-Item -ItemType Directory -Force -Path $payloadRoot, $oldVersionRoot | Out-Null
    Copy-Item -Path (Join-Path (Resolve-Path $PublishedPayloadPath).Path '*') -Destination $payloadRoot -Recurse
    New-TestableInstallerHelperCopy -Destination $helperCopy
    # The previous payload can be incomplete too. Repair must not require its
    # executable, manifest, or an FFmpeg installed by another Windows account.
    $oldMarker = Join-Path $oldVersionRoot 'broken-old-install.txt'
    [IO.File]::WriteAllText($oldMarker, 'old install without FFmpeg')
    $incomingExe = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Filter lifeviz.exe)
    Assert-True ($incomingExe.Count -eq 1) 'Expected one app in the published test payload.'
    $relativeApp = $incomingExe[0].FullName.Substring($payloadRoot.Length + 1)
    $incomingFfmpeg = Join-Path $incomingExe[0].DirectoryName 'ffmpeg\ffmpeg.exe'
    $savedFfmpeg = Join-Path $caseRoot 'verified-ffmpeg.exe'
    Copy-Item -LiteralPath $incomingFfmpeg -Destination $savedFfmpeg

    $parameters = @{
        HelperPath = $helperCopy
        PayloadRoot = $payloadRoot
        InstallRoot = $installRoot
        WorkingDirectory = $oldVersionRoot
    }
    foreach ($damage in @('missing', 'corrupt')) {
        if ($damage -eq 'missing') { Remove-Item -LiteralPath $incomingFfmpeg }
        else { [IO.File]::WriteAllText($incomingFfmpeg, 'corrupt FFmpeg') }
        $result = Invoke-TestHelperProcess @parameters
        Assert-True ($result.ExitCode -ne 0) "Installer accepted $damage FFmpeg."
        Assert-True (($result.Stdout + $result.Stderr) -match 'Bundled FFmpeg payload') 'Failure did not identify FFmpeg validation.'
        Assert-True (Test-Path -LiteralPath $oldMarker) 'Rejected payload changed the old install.'
        Write-Host "[pass] $damage incoming FFmpeg is rejected before changing the old install."
    }

    Copy-Item -LiteralPath $savedFfmpeg -Destination $incomingFfmpeg -Force
    $result = Invoke-TestHelperProcess @parameters
    Assert-True ($result.ExitCode -eq 0) "Upgrade from missing-FFmpeg install failed: $($result.Stderr)"
    Assert-True (-not (Test-Path -LiteralPath $oldMarker)) 'Upgrade retained the old version.'
    $installedApp = Join-Path $installRoot $relativeApp
    $installedFfmpeg = Join-Path (Split-Path -Parent $installedApp) 'ffmpeg\ffmpeg.exe'
    $installedDemo = Join-Path (Split-Path -Parent $installedApp) 'Assets\Starter\lifeviz-loop.mp4'
    $sourceDemo = Join-Path $repoRoot 'Assets\Starter\lifeviz-loop.mp4'
    Assert-True ((Get-FileHash $installedFfmpeg).Hash -eq (Get-FileHash $savedFfmpeg).Hash) 'Upgrade did not install the verified FFmpeg.'
    Assert-True ((Get-FileHash $installedDemo).Hash -eq (Get-FileHash $sourceDemo).Hash) 'Upgrade did not install the starter video.'

    # Reinstalling the same version also repairs missing files; there is no
    # version-only short circuit in the automatic update/install path.
    Remove-Item -LiteralPath $installedFfmpeg
    Remove-Item -LiteralPath $installedDemo
    $parameters.WorkingDirectory = Split-Path -Parent $installedApp
    $result = Invoke-TestHelperProcess @parameters
    Assert-True ($result.ExitCode -eq 0) "Same-version repair failed: $($result.Stderr)"
    Assert-True ((Get-FileHash $installedFfmpeg).Hash -eq (Get-FileHash $savedFfmpeg).Hash) 'Same-version repair did not restore FFmpeg.'
    Assert-True ((Get-FileHash $installedDemo).Hash -eq (Get-FileHash $sourceDemo).Hash) 'Same-version repair did not restore the starter video.'
    $debris = @(Get-ChildItem -LiteralPath $caseRoot -Directory | Where-Object {
        $_.Name -like 'lifeviz-clickonce.installing-*' -or $_.Name -like 'lifeviz-clickonce.backup-*'
    })
    Assert-True ($debris.Count -eq 0) 'Upgrade/repair left transaction directories behind.'

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    & $dotnet (Join-Path (Split-Path -Parent $installedApp) 'lifeviz.dll') --smoke-test ffmpeg-bundle
    Assert-True ($LASTEXITCODE -eq 0) 'Installed app failed media/cleanup checks with PATH cleared.'
    $savedPath = $env:PATH
    try {
        $env:PATH = ''
        & $dotnet (Join-Path (Split-Path -Parent $installedApp) 'lifeviz.dll') --smoke-test starter-project
        Assert-True ($LASTEXITCODE -eq 0) 'Repaired app failed starter playback/New Project checks with PATH cleared.'
    }
    finally { $env:PATH = $savedPath }
    Write-Host '[pass] Old-install upgrade and same-version repair restore bundled FFmpeg and the starter scene.'
}

New-Item -ItemType Directory -Force -Path $auditRoot | Out-Null
try {
    Assert-DirectoryMoveIsBlocked -Name 'child-current-directory' -StartHolder {
        param($versionRoot, $caseRoot)
        $startHolderParameters = @{
            FilePath = $windowsPowerShell
            ArgumentList = '-NoProfile -NonInteractive -Command "Start-Sleep -Seconds 60"'
            WorkingDirectory = $versionRoot
            WindowStyle = 'Hidden'
            PassThru = $true
        }
        Start-Process @startHolderParameters
    }

    Assert-RunningImageMoveBehavior

    Assert-SourceHandoffHardening
    Invoke-ScopedFfmpegCleanupTransaction
    Invoke-NonFfmpegHolderFailureTransaction
    if ($PublishedPayloadPath) { Invoke-FfmpegUpgradeTransaction }
    Write-Host '[pass] Installer transaction regression suite completed.' -ForegroundColor Green
} finally {
    foreach ($process in $startedProcesses) {
        Stop-TestProcess $process
    }

    if ($KeepArtifacts) {
        Write-Host "[info] Preserving disposable artifacts at $auditRoot"
    } elseif (Test-Path -LiteralPath $auditRoot) {
        $resolvedAuditRoot = [IO.Path]::GetFullPath($auditRoot)
        $expectedPrefix = $tempRoot + [IO.Path]::DirectorySeparatorChar
        $auditLeaf = [IO.Path]::GetFileName($resolvedAuditRoot)
        if (-not $resolvedAuditRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not $auditLeaf.StartsWith('lifeviz-installer-regression-', [StringComparison]::Ordinal)) {
            throw "Refusing to clean an unsafe regression-test path: $resolvedAuditRoot"
        }
        Remove-Item -LiteralPath $resolvedAuditRoot -Recurse -Force
    }
}
