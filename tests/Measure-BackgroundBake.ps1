#Requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][string]$RequestPath,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [ValidateRange(1, 30)][int]$DurationSeconds = 10,
    [ValidateSet('BelowNormal', 'Normal', 'AboveNormal')][string[]]$Priorities = @('BelowNormal', 'AboveNormal'),
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$snapshot = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (!$OutputRoot) {
    $OutputRoot = Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/bake-profile-' + [Guid]::NewGuid().ToString('N'))
}
$profileRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
$index = 0
foreach ($priority in $Priorities) {
    $index++
    $jobDirectory = Join-Path $profileRoot ($index.ToString() + '-' + $priority)
    # Never overwrite a previous probe or the user's original queued request.
    if (Test-Path -LiteralPath $jobDirectory) { throw "Profile directory already exists: $jobDirectory" }
    New-Item -ItemType Directory -Path $jobDirectory | Out-Null
    $scene = $snapshot.SceneJson | ConvertFrom-Json
    $scene.RecordingOutputFolder = Join-Path $jobDirectory 'video'
    $request = @{
        SceneJson = $scene | ConvertTo-Json -Depth 100 -Compress
        DurationSeconds = $DurationSeconds
        OutputFps = $snapshot.OutputFps
        Profile = $true
        ProfilePriority = $priority
    }
    [IO.File]::WriteAllText((Join-Path $jobDirectory 'request.json'), ($request | ConvertTo-Json -Depth 100))
    $process = Start-Process -FilePath $executable -ArgumentList '--background-bake', ('"' + $jobDirectory + '"') `
        -WorkingDirectory ([IO.Path]::GetTempPath()) -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $jobDirectory 'status.log') `
        -RedirectStandardError (Join-Path $jobDirectory 'stderr.log')
    try {
        if (!$process.WaitForExit(180000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Profile worker timed out: $jobDirectory"
        }
        if ($process.ExitCode -ne 0) { throw "Profile worker failed; inspect $jobDirectory" }
        $statusLines = Get-Content -LiteralPath (Join-Path $jobDirectory 'status.log') |
            Where-Object { $_.StartsWith('LIFEVIZ_BAKE_STATUS_V1 ') }
        $status = $statusLines[-1].Substring('LIFEVIZ_BAKE_STATUS_V1 '.Length) | ConvertFrom-Json
        if ($status.State -ne 'Completed') { throw "Profile did not complete: $jobDirectory" }
        # Separate decoder startup from sustained production after two seconds of frames.
        $settled = @($statusLines | ForEach-Object {
            $_.Substring('LIFEVIZ_BAKE_STATUS_V1 '.Length) | ConvertFrom-Json
        } | Where-Object { $_.State -eq 'Rendering' -and $_.CompletedFrames -ge (2 * $snapshot.OutputFps) })
        $settledFps = $null
        if ($settled.Count -gt 1 -and $settled[-1].ElapsedSeconds -gt $settled[0].ElapsedSeconds) {
            $settledFps = [math]::Round(($settled[-1].CompletedFrames - $settled[0].CompletedFrames) /
                ($settled[-1].ElapsedSeconds - $settled[0].ElapsedSeconds), 2)
        }
        $profileFile = Get-ChildItem -LiteralPath $jobDirectory -Filter 'background-bake-*.json' | Select-Object -First 1
        $profile = Get-Content -LiteralPath $profileFile.FullName -Raw | ConvertFrom-Json
        $frame = $profile.Metrics | Where-Object Name -eq 'frame_total_ms'
        [pscustomobject]@{
            Priority = $priority
            Frames = $status.CompletedFrames
            ExportFps = [math]::Round($status.CompletedFrames / ($profile.DurationMs / 1000), 2)
            SettledFps = $settledFps
            MeanFrameMs = [math]::Round($frame.Average, 2)
            Profile = $profileFile.FullName
        } | ConvertTo-Json -Compress
    } finally { $process.Dispose() }
}
