param(
  [Parameter(Mandatory = $true, Position = 0)]
  [string]$InputPath,

  [Parameter(Position = 1)]
  [string]$OutputPath = '',

  [int]$SegSeconds = 2,

  [int]$Crf = 28,   # higher = smaller file (18 = huge, 28 = much smaller)

  [string]$WorkDir = '',

  [switch]$NoRemux,

  [switch]$KeepTemps
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$FFMPEG  = (Get-Command ffmpeg  -ErrorAction Stop).Source
$FFPROBE = (Get-Command ffprobe -ErrorAction Stop).Source

function Run-FFmpeg {
  param([string[]]$FFArgs)
  & $script:FFMPEG @FFArgs
  if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg failed (exit $LASTEXITCODE): $($FFArgs -join ' ')"
  }
}

function Get-DurationSeconds {
  param([string]$FilePath)
  $d = & $script:FFPROBE -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $FilePath
  if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($d)) {
    throw "ffprobe couldn't read duration for: $FilePath"
  }
  return [double]$d
}

# ---- Resolve input ----
if (-not (Test-Path $InputPath)) { throw "Input file not found: $InputPath" }
$InputPath = (Resolve-Path $InputPath).Path

# ---- Default output path ----
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
  $dir  = Split-Path $InputPath -Parent
  $base = [System.IO.Path]::GetFileNameWithoutExtension($InputPath)
  $OutputPath = Join-Path $dir ($base + '_reversed.mp4')
}

# ---- WorkDir defaults to output folder (so it uses that drive’s free space) ----
if ([string]::IsNullOrWhiteSpace($WorkDir)) {
  $WorkDir = Split-Path $OutputPath -Parent
}
if (-not (Test-Path $WorkDir)) { throw "WorkDir not found: $WorkDir" }
$WorkDir = (Resolve-Path $WorkDir).Path

$workRoot = Join-Path $WorkDir ("ffreverse_" + [Guid]::NewGuid().ToString('N'))
$tempTs   = Join-Path $workRoot 'temp.ts'
$masterTs = Join-Path $workRoot 'master.ts'

New-Item -ItemType Directory -Force $workRoot | Out-Null

Write-Host "Input : $InputPath"
Write-Host "Output: $OutputPath"
Write-Host "Chunk : $SegSeconds sec"
Write-Host "CRF   : $Crf"
Write-Host "Work  : $workRoot"
Write-Host ""

$duration = Get-DurationSeconds -FilePath $InputPath
$totalSeg = [int][math]::Ceiling($duration / $SegSeconds)

Write-Host ("Duration: {0:N2} sec ({1} segments)" -f $duration, $totalSeg)
Write-Host ""

# Open master stream once (fast + low overhead)
$masterStream = [System.IO.File]::Open($masterTs, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)

try {
  for ($i = $totalSeg - 1; $i -ge 0; $i--) {

    $start = $i * $SegSeconds
    $len   = [math]::Min($SegSeconds, $duration - $start)

    # Pretty progress every 100 segments
    if (($i % 100) -eq 0) {
      $done = $totalSeg - 1 - $i
      Write-Host ("Segment {0}/{1} (start={2:N2}s, len={3:N2}s)" -f $done, $totalSeg, $start, $len)
    }

    $ss = "{0:F3}" -f $start
    $tt = "{0:F3}" -f $len

    # Reverse ONLY this small window (video only)
    Run-FFmpeg @(
      '-y',
      '-ss', $ss,
      '-t',  $tt,
      '-err_detect', 'ignore_err',
      '-i', $InputPath,
      '-map', '0:v:0',
      '-an', '-sn', '-dn',
      '-vf', 'reverse',
      '-c:v', 'libx264',
      '-crf', "$Crf",
      '-preset', 'veryfast',
      '-pix_fmt', 'yuv420p',
      '-f', 'mpegts',
      $tempTs
    )

    # Append temp.ts -> master.ts
    $src = [System.IO.File]::OpenRead($tempTs)
    $src.CopyTo($masterStream)
    $src.Close()
    Remove-Item $tempTs -Force
  }
}
finally {
  $masterStream.Close()
}

Write-Host ""
Write-Host "Built: $masterTs"

if ($NoRemux) {
  Write-Host "✅ Done (TS output)."
  Write-Host "Your reversed video is: $masterTs"
  if (-not $KeepTemps) { Write-Host "Temp kept because master.ts IS the output." }
  exit 0
}

Write-Host ""
Write-Host "[Final] Remuxing to MP4..."
Run-FFmpeg @(
  '-y',
  '-fflags', '+genpts',
  '-i', $masterTs,
  '-c', 'copy',
  '-movflags', '+faststart',
  $OutputPath
)

Write-Host ""
Write-Host "✅ Done!"
Write-Host "Saved: $OutputPath"

if (-not $KeepTemps) {
  Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue
}
