param([string]$ExecutablePath = 'bin/Release/net9.0-windows/lifeviz.exe')
$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$appDirectory = Split-Path -Parent $executable
$ffmpeg = Join-Path $appDirectory 'ffmpeg\ffmpeg.exe'
$repository = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repository ('artifacts\field-effects-bake-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$fixture = Join-Path $testRoot 'source.mp4'
& $ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=s=256x144:r=30:d=3' -f lavfi -i 'sine=frequency=110:sample_rate=48000:duration=3' -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest $fixture
if ($LASTEXITCODE -ne 0) { throw 'Could not create the video/audio fixture.' }
foreach ($effect in @('FluidInk', 'TimeDisplacement', 'ReactionDiffusion')) {
    $referenceHashes = $null
    foreach ($run in 1..2) {
        $job = Join-Path $testRoot "$effect-$run"
        New-Item -ItemType Directory -Path $job | Out-Null
        $target = switch ($effect) { FluidInk { 'FluidFlow' } TimeDisplacement { 'TimeSpread' } ReactionDiffusion { 'ReactionSeed' } }
        $scene = @{
            ConfigVersion = 1; Height = 144; Depth = 24; Framerate = 30; Fullscreen = $false
            AspectRatioLocked = $true; LockedAspectRatio = 16.0 / 9.0; RecordingQuality = 'Lossless'
            RecordingOutputFolder = (Join-Path $job 'output'); SourceAudioMasterEnabled = $true; SourceAudioMasterVolume = 1
            SimulationLayers = @()
            Sources = @(
                @{ Type = 'File'; FilePath = $fixture; DisplayName = 'Fixture'; Enabled = $true; BlendMode = 'Normal'; FitMode = 'Stretch'; Opacity = 1; VideoAudioEnabled = $true; VideoAudioVolume = 1 },
                @{ Type = 'SimGroup'; DisplayName = $effect; Enabled = $true; BlendMode = 'Normal'; Opacity = 1
                   SimulationLayers = @(@{ Id = [Guid]::NewGuid().ToString(); Kind = 'Layer'; LayerType = $effect; Name = $effect; Enabled = $true; BlendMode = 'Normal'; LifeOpacity = 1
                      Effects = @{ FluidFlow = 0.55; TimeSpread = 0.7; ReactionSeed = 0.65 }
                      ReactiveMappings = @(@{ Input = 'Bass'; Output = $target; Amount = 0.2; ThresholdMin = 0; ThresholdMax = 1 }) }) }
            )
        }
        $request = @{ SceneJson = ($scene | ConvertTo-Json -Depth 30 -Compress); DurationSeconds = 3; OutputFps = 30 }
        [IO.File]::WriteAllText((Join-Path $job 'request.json'), ($request | ConvertTo-Json -Depth 35))
        $process = Start-Process -FilePath $executable -ArgumentList '--background-bake', ('"' + $job + '"') -WorkingDirectory $job -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $job 'status.log') -RedirectStandardError (Join-Path $job 'stderr.log')
        try {
            $null = $process.Handle # Cache the native handle before exit (Windows PowerShell 5.1).
            if (-not $process.WaitForExit(180000)) { $process.Kill(); $process.WaitForExit(); throw "Bake timed out: $job" }
            if ($process.ExitCode -ne 0) { throw "Bake failed: $job" }
        } finally { $process.Dispose() }
        $statusLines = @(Get-Content -LiteralPath (Join-Path $job 'status.log') | Where-Object { $_.StartsWith('LIFEVIZ_BAKE_STATUS_V1 ') })
        if ($statusLines.Count -eq 0) { throw "No bake status: $job" }
        $status = $statusLines[-1].Substring('LIFEVIZ_BAKE_STATUS_V1 '.Length) | ConvertFrom-Json
        if ($status.State -ne 'Completed' -or $status.CompletedFrames -ne 90 -or -not (Test-Path -LiteralPath $status.OutputPath)) { throw "Incomplete bake: $job" }
        $hashes = @(& $ffmpeg -hide_banner -loglevel error -i $status.OutputPath -map 0:v:0 -f framemd5 - | Where-Object { $_ -match '^\d+,' } | ForEach-Object { ($_ -split ',')[-1].Trim() })
        if ($LASTEXITCODE -ne 0 -or $hashes.Count -ne 90) { throw "Expected 90 decoded frames: $job" }
        $unique = @($hashes | Select-Object -Unique).Count
        if ($unique -lt 60) { throw "Effect did not animate: $unique distinct frames in $job" }
        if ($null -ne $referenceHashes -and ($referenceHashes -join ',') -ne ($hashes -join ',')) { throw "Repeat bake was not deterministic: $effect" }
        $referenceHashes = $hashes
        & $ffmpeg -hide_banner -loglevel error -ss 2 -i $status.OutputPath -frames:v 1 (Join-Path $job 'preview.png')
        if ($LASTEXITCODE -ne 0) { throw "Could not extract preview: $job" }
        Write-Host "$effect run ${run}: 90 frames, $unique distinct frames."
    }
}
Write-Host "All three effect bakes reproduced identical decoded frames. Videos and diagnostics: $testRoot"
