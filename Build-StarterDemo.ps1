param(
    [string]$BlenderPath = 'C:\Program Files\Blender Foundation\Blender 4.4\blender.exe',
    [switch]$PreviewOnly
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$output = Join-Path $root 'artifacts\starter-demo'
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (-not (Test-Path -LiteralPath $BlenderPath)) {
    throw 'Blender 4.4 was not found. Pass -BlenderPath with the installed blender.exe path.'
}
$arguments = @('--background', '--python', (Join-Path $root 'tools\render_starter_demo.py'), '--', '--output', $output)
if ($PreviewOnly) { $arguments += '--preview' }
& $BlenderPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Blender failed with exit code $LASTEXITCODE" }
if ($PreviewOnly) { return }

& (Join-Path $root 'Prepare-Ffmpeg.ps1')
$ffmpeg = Join-Path $root 'artifacts\ffmpeg\runtime\ffmpeg.exe'
$first = Join-Path $output 'frames\0001.png'
$endpoint = Join-Path $output 'loop-endpoint.png'
$seam = & $ffmpeg -hide_banner -i $first -i $endpoint -lavfi psnr -f null NUL 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $seam -notmatch 'average:inf') {
    throw "The loop endpoints are not pixel-identical: $seam"
}
$video = Join-Path $output 'lifeviz-loop.mp4'
& $ffmpeg -y -hide_banner -loglevel error -framerate 30 -start_number 1 -i (Join-Path $output 'frames\%04d.png') -frames:v 180 -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -g 30 -an -movflags +faststart $video
if ($LASTEXITCODE -ne 0) { throw "FFmpeg encode failed with exit code $LASTEXITCODE" }
$assetRoot = Join-Path $root 'Assets\Starter'
New-Item -ItemType Directory -Force -Path $assetRoot | Out-Null
Copy-Item -LiteralPath $video -Destination (Join-Path $assetRoot 'lifeviz-loop.mp4') -Force
Write-Host 'Starter demo ready: 180 frames at 30 fps; six seconds; pixel-identical loop endpoints.'
