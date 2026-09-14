param([string]$ExecutablePath = 'bin/Release/net9.0-windows/lifeviz.exe')
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$appDirectory = Split-Path -Parent $executable
$ffmpeg = Join-Path $appDirectory 'ffmpeg\ffmpeg.exe'
$repository = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repository ('artifacts\projectm-bake-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$tone = Join-Path $testRoot 'tone.mp4'
& $ffmpeg -hide_banner -loglevel error -f lavfi -i 'color=c=black:s=256x144:r=30:d=2' -f lavfi -i 'sine=frequency=110:sample_rate=48000:duration=2' -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $tone
if ($LASTEXITCODE -ne 0) { throw 'Could not create the bake audio fixture.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $appDirectory 'projectm\assets.zip'))
try { $presets = @($archive.Entries | Where-Object { $_.FullName -like 'presets/Waveform/Spectrum/*.milk' } | Select-Object -First 3 | ForEach-Object { $_.FullName.Substring(8) }) }
finally { $archive.Dispose() }
if ($presets.Count -ne 3) { throw 'The published projectM preset collection is incomplete.' }
$scene = @{
    ConfigVersion = 1; Height = 144; Depth = 1; Framerate = 30; Fullscreen = $false
    AspectRatioLocked = $true; LockedAspectRatio = 16.0 / 9.0; RecordingQuality = 'Low'
    RecordingOutputFolder = (Join-Path $testRoot 'output'); SourceAudioMasterEnabled = $true; SourceAudioMasterVolume = 1
    SimulationLayers = @()
    Sources = @(
        @{ Type = 'File'; FilePath = $tone; DisplayName = 'Bake audio'; Enabled = $true; Opacity = 0; VideoAudioEnabled = $true; VideoAudioVolume = 1 },
        @{ Type = 'ProjectM'; DisplayName = 'MilkDrop bake'; Enabled = $true; BlendMode = 'Normal'; FitMode = 'Stretch'; Opacity = 1;
           ProjectM = @{ Presets = $presets; Order = 'Ordered'; Advance = 'Timed'; DurationSeconds = 0.7; TransitionSeconds = 0.2; MinimumSeconds = 0; BeatsPerPreset = 4; ShuffleSeed = 1 } }
    )
}
$request = @{ SceneJson = ($scene | ConvertTo-Json -Depth 30 -Compress); DurationSeconds = 2; OutputFps = 30 }
[IO.File]::WriteAllText((Join-Path $testRoot 'request.json'), ($request | ConvertTo-Json -Depth 35))
$process = Start-Process -FilePath $executable -ArgumentList '--background-bake', ('"' + $testRoot + '"') -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot 'status.log') -RedirectStandardError (Join-Path $testRoot 'stderr.log')
try {
    if (-not $process.WaitForExit(180000)) {
        $process.Kill(); $process.WaitForExit()
        throw "projectM bake timed out; inspect $testRoot"
    }
    if ($process.ExitCode -ne 0) { throw "projectM bake failed; inspect $testRoot" }
} finally { $process.Dispose() }
$statusLines = @(Get-Content -LiteralPath (Join-Path $testRoot 'status.log') | Where-Object { $_.StartsWith('LIFEVIZ_BAKE_STATUS_V1 ') })
if ($statusLines.Count -eq 0) { throw 'The bake worker published no status.' }
$status = $statusLines[-1].Substring('LIFEVIZ_BAKE_STATUS_V1 '.Length) | ConvertFrom-Json
if ($status.State -ne 'Completed' -or $status.CompletedFrames -ne 60 -or -not (Test-Path -LiteralPath $status.OutputPath)) { throw "Incomplete projectM bake: $($status | ConvertTo-Json -Compress)" }
$hashes = @(& $ffmpeg -hide_banner -loglevel error -i $status.OutputPath -map 0:v:0 -f framemd5 - | Where-Object { $_ -match '^\d+,' })
if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne 60) { throw 'The exported video did not decode to exactly 60 frames.' }
$uniqueFrames = @($hashes | ForEach-Object { ($_ -split ',')[-1].Trim() } | Select-Object -Unique).Count
if ($uniqueFrames -lt 30) { throw "The exported projectM video did not animate: $uniqueFrames distinct frames." }
Write-Host "projectM background bake passed: 60 decoded frames, $uniqueFrames distinct frames."
Write-Host "Video and diagnostics: $testRoot"
