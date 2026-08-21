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

function Test-PathWithinRoot {
    param(
        [string]$CandidatePath,
        [string]$RootPath
    )

    if ([string]::IsNullOrWhiteSpace($CandidatePath) -or [string]::IsNullOrWhiteSpace($RootPath)) {
        return $false
    }

    try {
        $candidate = [IO.Path]::GetFullPath($CandidatePath).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        $root = [IO.Path]::GetFullPath($RootPath).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        return $candidate.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
            $candidate.StartsWith(
                $root + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Set-InstallerWorkingDirectoryOutsideRoot {
    param(
        [string]$RootPath,
        [string]$PreferredPath
    )

    $candidates = @(
        $PreferredPath,
        [IO.Path]::GetTempPath(),
        $env:SystemRoot
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidatePath in $candidates) {
        try {
            $resolved = (Resolve-Path -LiteralPath $candidatePath -ErrorAction Stop).Path
            if (Test-PathWithinRoot -CandidatePath $resolved -RootPath $RootPath) {
                continue
            }

            # PowerShell's provider location and the process-wide .NET current
            # directory are independent. Relocate both so neither retains a
            # directory handle below the install root during its atomic rename.
            Set-Location -LiteralPath $resolved
            [Environment]::CurrentDirectory = $resolved

            $providerPath = (Get-Location).ProviderPath
            $processPath = [Environment]::CurrentDirectory
            if ((Test-PathWithinRoot -CandidatePath $providerPath -RootPath $RootPath) -or
                (Test-PathWithinRoot -CandidatePath $processPath -RootPath $RootPath)) {
                throw "Working-directory relocation remained inside '$RootPath'."
            }

            Write-Host "[install] Installer working directory: $processPath" -ForegroundColor Cyan
            return
        } catch {
            $lastError = $_
        }
    }

    throw "Could not relocate the installer working directory outside '$RootPath': $lastError"
}

function Initialize-ProcessCurrentDirectoryReader {
    if ('LifeVizInstaller.ProcessCurrentDirectoryReader' -as [type]) {
        return
    }

    # Win32_Process does not expose a process's current directory. Read the
    # native PEB/process-parameter layout so cleanup is restricted to exact
    # ffmpeg.exe processes that demonstrably inherited the LifeViz install path.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LifeVizInstaller
{
    public static class ProcessCurrentDirectoryReader
    {
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessQueryInformation = 0x0400;
        private const int ProcessBasicInformationClass = 0;
        private const int ProcessWow64InformationClass = 26;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessBasicInformation
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr baseAddress,
            [Out] byte[] buffer,
            int size,
            out IntPtr bytesRead);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int QueryBasicInformation(
            IntPtr process,
            int informationClass,
            out ProcessBasicInformation information,
            int informationLength,
            out int returnLength);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryInformationProcess")]
        private static extern int QueryWow64Information(
            IntPtr process,
            int informationClass,
            out IntPtr information,
            int informationLength,
            out int returnLength);

        public static bool TryGet(int processId, out string directory)
        {
            directory = null;
            IntPtr process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            if (process == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                ProcessBasicInformation basic;
                int returned;
                if (QueryBasicInformation(
                        process,
                        ProcessBasicInformationClass,
                        out basic,
                        Marshal.SizeOf(typeof(ProcessBasicInformation)),
                        out returned) != 0)
                {
                    return false;
                }

                bool target32Bit = !Environment.Is64BitProcess;
                IntPtr peb = basic.PebBaseAddress;
                if (Environment.Is64BitProcess)
                {
                    IntPtr wow64Peb;
                    if (QueryWow64Information(
                            process,
                            ProcessWow64InformationClass,
                            out wow64Peb,
                            IntPtr.Size,
                            out returned) == 0 &&
                        wow64Peb != IntPtr.Zero)
                    {
                        target32Bit = true;
                        peb = wow64Peb;
                    }
                    else
                    {
                        target32Bit = false;
                    }
                }

                int pointerSize = target32Bit ? 4 : 8;
                int processParametersOffset = target32Bit ? 0x10 : 0x20;
                int currentDirectoryOffset = target32Bit ? 0x24 : 0x38;
                int unicodeBufferOffset = target32Bit ? 4 : 8;

                IntPtr processParameters;
                if (!TryReadPointer(
                        process,
                        IntPtr.Add(peb, processParametersOffset),
                        pointerSize,
                        out processParameters) ||
                    processParameters == IntPtr.Zero)
                {
                    return false;
                }

                byte[] header = new byte[unicodeBufferOffset + pointerSize];
                IntPtr bytesRead;
                if (!ReadProcessMemory(
                        process,
                        IntPtr.Add(processParameters, currentDirectoryOffset),
                        header,
                        header.Length,
                        out bytesRead) ||
                    bytesRead.ToInt64() != header.Length)
                {
                    return false;
                }

                int byteLength = BitConverter.ToUInt16(header, 0);
                if (byteLength <= 0 || byteLength > 32768 || (byteLength & 1) != 0)
                {
                    return false;
                }

                long bufferValue = pointerSize == 8
                    ? BitConverter.ToInt64(header, unicodeBufferOffset)
                    : BitConverter.ToUInt32(header, unicodeBufferOffset);
                if (bufferValue == 0)
                {
                    return false;
                }

                byte[] pathBytes = new byte[byteLength];
                if (!ReadProcessMemory(
                        process,
                        new IntPtr(bufferValue),
                        pathBytes,
                        pathBytes.Length,
                        out bytesRead) ||
                    bytesRead.ToInt64() != pathBytes.Length)
                {
                    return false;
                }

                directory = Encoding.Unicode.GetString(pathBytes);
                return !String.IsNullOrWhiteSpace(directory);
            }
            catch
            {
                directory = null;
                return false;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        private static bool TryReadPointer(
            IntPtr process,
            IntPtr address,
            int pointerSize,
            out IntPtr value)
        {
            value = IntPtr.Zero;
            byte[] bytes = new byte[pointerSize];
            IntPtr bytesRead;
            if (!ReadProcessMemory(process, address, bytes, bytes.Length, out bytesRead) ||
                bytesRead.ToInt64() != bytes.Length)
            {
                return false;
            }

            long raw = pointerSize == 8
                ? BitConverter.ToInt64(bytes, 0)
                : BitConverter.ToUInt32(bytes, 0);
            value = new IntPtr(raw);
            return true;
        }
    }
}
'@
}

function Get-ProcessCurrentDirectory {
    param([int]$ProcessId)

    $directory = $null
    if ([LifeVizInstaller.ProcessCurrentDirectoryReader]::TryGet($ProcessId, [ref]$directory)) {
        return $directory
    }
    return $null
}

function Get-InstallRootCurrentDirectoryHolders {
    param([string]$RootPath)

    $holders = @()
    Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
        $process = $_
        $currentDirectory = Get-ProcessCurrentDirectory -ProcessId $process.Id
        if ($currentDirectory -and (Test-PathWithinRoot -CandidatePath $currentDirectory -RootPath $RootPath)) {
            $processPath = $null
            try { $processPath = $process.Path } catch {}
            $startTimeUtcTicks = $null
            try { $startTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks } catch {}
            $holders += [pscustomobject]@{
                Id = $process.Id
                Name = $process.ProcessName
                Path = $processPath
                CurrentDirectory = $currentDirectory
                StartTimeUtcTicks = $startTimeUtcTicks
                Process = $process
            }
        } else {
            $process.Dispose()
        }
    }
    return @($holders)
}

function Stop-LegacyFfmpegInstallRootHolders {
    param(
        [string]$RootPath,
        [int]$WaitMilliseconds = 10000
    )

    $holders = @(Get-InstallRootCurrentDirectoryHolders -RootPath $RootPath)
    $ffmpegHolders = @($holders | Where-Object {
        $_.Name -and ($_.Name + '.exe').Equals('ffmpeg.exe', [StringComparison]::OrdinalIgnoreCase)
    })

    foreach ($holder in $ffmpegHolders) {
        try {
            if ($holder.Process.HasExited) {
                continue
            }
        } catch {
            continue
        }

        $currentProcess = Get-Process -Id $holder.Id -ErrorAction SilentlyContinue
        if (-not $currentProcess) {
            continue
        }
        try {
            $currentStartTimeUtcTicks = $null
            try { $currentStartTimeUtcTicks = $currentProcess.StartTime.ToUniversalTime().Ticks } catch {}
            $currentDirectory = Get-ProcessCurrentDirectory -ProcessId $currentProcess.Id
            if (-not $holder.StartTimeUtcTicks -or
                -not $currentStartTimeUtcTicks -or
                $holder.StartTimeUtcTicks -ne $currentStartTimeUtcTicks -or
                -not $currentProcess.ProcessName.Equals('ffmpeg', [StringComparison]::OrdinalIgnoreCase) -or
                -not $currentDirectory -or
                -not (Test-PathWithinRoot -CandidatePath $currentDirectory -RootPath $RootPath)) {
                Write-Warning "[install] FFmpeg process $($holder.Id) changed identity or working directory before cleanup; it will not be terminated."
                continue
            }

            Write-Warning (
                "[install] Stopping legacy FFmpeg process {0} whose current directory is inside the old install: {1}" -f
                $holder.Id,
                $currentDirectory)
            try {
                & "$env:SystemRoot\System32\taskkill.exe" /PID $holder.Id /T /F | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "[install] taskkill returned exit code $LASTEXITCODE for FFmpeg process $($holder.Id)."
                }
            } catch {
                Write-Warning "[install] Failed to request termination of FFmpeg process $($holder.Id): $_"
            }
        } finally {
            $currentProcess.Dispose()
        }
    }

    $deadlineUtc = [DateTime]::UtcNow.AddMilliseconds([Math]::Max(0, $WaitMilliseconds))
    foreach ($holder in $ffmpegHolders) {
        try {
            $remaining = [Math]::Max(0, [int]($deadlineUtc - [DateTime]::UtcNow).TotalMilliseconds)
            if ($remaining -gt 0) {
                $null = $holder.Process.WaitForExit($remaining)
            }
        } catch {
            # The process may already have exited between enumeration and waiting.
        } finally {
            $holder.Process.Dispose()
        }
    }

    foreach ($holder in @($holders | Where-Object {
        -not ($_.Name -and ($_.Name + '.exe').Equals('ffmpeg.exe', [StringComparison]::OrdinalIgnoreCase))
    })) {
        $holder.Process.Dispose()
    }

    $remainingHolders = @(Get-InstallRootCurrentDirectoryHolders -RootPath $RootPath)
    try {
        $remainingFfmpeg = @($remainingHolders | Where-Object {
            $_.Name -and ($_.Name + '.exe').Equals('ffmpeg.exe', [StringComparison]::OrdinalIgnoreCase)
        })
        if ($remainingFfmpeg.Count -gt 0) {
            $details = ($remainingFfmpeg | ForEach-Object {
                "PID $($_.Id) ($($_.Name).exe), cwd=$($_.CurrentDirectory)"
            }) -join '; '
            throw "Legacy FFmpeg process(es) still hold the install directory after cleanup: $details"
        }

        $otherHolders = @($remainingHolders | Where-Object {
            -not ($_.Name -and ($_.Name + '.exe').Equals('ffmpeg.exe', [StringComparison]::OrdinalIgnoreCase))
        })
        if ($otherHolders.Count -gt 0) {
            $details = ($otherHolders | ForEach-Object {
                $name = if ($_.Name) { "$($_.Name).exe" } else { 'unknown' }
                "PID $($_.Id) ($name), cwd=$($_.CurrentDirectory)"
            }) -join '; '
            Write-Warning "[install] Non-FFmpeg process(es) still have a current directory inside the install root; they will not be terminated: $details"
        }
    } finally {
        foreach ($holder in $remainingHolders) {
            $holder.Process.Dispose()
        }
    }
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
            # The updater supplied this PID specifically so the installer cannot
            # race application shutdown. Do not discard the wait because Path is
            # temporarily unreadable or the process is already partway through exit.
            $processIds += $ExplicitProcessId
            $explicitProcess.Dispose()
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
        try {
            if (-not $process.WaitForExit(60000)) {
                throw "LifeViz process $processId did not exit within 60 seconds. Close LifeViz and run the installer again."
            }
        } finally {
            $process.Dispose()
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
        [int]$MaxAttempts = 60
    )

    $sourcePath = [IO.Path]::GetFullPath($Source).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $destinationPath = [IO.Path]::GetFullPath($Destination).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $sourceVolume = [IO.Path]::GetPathRoot($sourcePath)
    $destinationVolume = [IO.Path]::GetPathRoot($destinationPath)
    if (-not $sourceVolume.Equals($destinationVolume, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing non-transactional cross-volume directory move from '$sourcePath' to '$destinationPath'."
    }

    if (-not [IO.Directory]::Exists($sourcePath)) {
        throw "Directory move source does not exist: '$sourcePath'."
    }
    if ([IO.Directory]::Exists($destinationPath)) {
        throw "Refusing to move '$sourcePath' because destination '$destinationPath' already exists."
    }

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $sourceExists = [IO.Directory]::Exists($sourcePath)
        $destinationExists = [IO.Directory]::Exists($destinationPath)
        if (-not $sourceExists -and $destinationExists) {
            return
        }
        if (-not $sourceExists -and -not $destinationExists) {
            throw "Directory move lost both source and destination: '$sourcePath' -> '$destinationPath'."
        }
        if ($sourceExists -and $destinationExists) {
            throw "Refusing to move '$sourcePath' because destination '$destinationPath' already exists."
        }

        try {
            # Directory.Move is an atomic same-volume rename. Unlike Move-Item,
            # it cannot fall back to recursively populating/nesting a destination
            # when one locked descendant prevents the rename.
            [IO.Directory]::Move($sourcePath, $destinationPath)
            if ([IO.Directory]::Exists($sourcePath) -or -not [IO.Directory]::Exists($destinationPath)) {
                throw "Directory move returned with an invalid state: '$sourcePath' -> '$destinationPath'."
            }
            return
        } catch {
            $moveError = $_
            $sourceExists = [IO.Directory]::Exists($sourcePath)
            $destinationExists = [IO.Directory]::Exists($destinationPath)
            if (-not $sourceExists -and $destinationExists) {
                return
            }
            if (-not $sourceExists -or $destinationExists) {
                throw "Directory move entered an inconsistent state (sourceExists=$sourceExists, destinationExists=$destinationExists): '$sourcePath' -> '$destinationPath'. $moveError"
            }
            if ($attempt -eq $MaxAttempts) {
                $holderSuffix = ''
                $moveHolders = @()
                try {
                    if ('LifeVizInstaller.ProcessCurrentDirectoryReader' -as [type]) {
                        $moveHolders = @(Get-InstallRootCurrentDirectoryHolders -RootPath $sourcePath)
                        if ($moveHolders.Count -gt 0) {
                            $holderDetails = ($moveHolders | ForEach-Object {
                                $name = if ($_.Name) { "$($_.Name).exe" } else { 'unknown' }
                                "PID $($_.Id) ($name), cwd=$($_.CurrentDirectory)"
                            }) -join '; '
                            $holderSuffix = " Current-directory holder(s): $holderDetails"
                        }
                    }
                } catch {
                    # The atomic move error remains authoritative if diagnostics fail.
                } finally {
                    foreach ($holder in $moveHolders) {
                        $holder.Process.Dispose()
                    }
                }
                throw "Failed to move '$sourcePath' to '$destinationPath' after $MaxAttempts attempts: $moveError$holderSuffix"
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
Set-InstallerWorkingDirectoryOutsideRoot -RootPath $InstallRoot -PreferredPath $payloadRoot

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
    Initialize-ProcessCurrentDirectoryReader
    Stop-LegacyFfmpegInstallRootHolders -RootPath $InstallRoot

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
