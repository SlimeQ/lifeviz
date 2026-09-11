param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Keep the version, URL and independently pinned upstream checksum together.
$version = '9.0.1'
$archiveName = "ffmpeg-$version-essentials_build.zip"
$archiveUrl = "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName"
$expectedHash = 'fec81ae03971d9dd4be3ebe02e263bd2ec1d789483f931bdba5f5715e65da2e9'
$cacheRoot = Join-Path $PSScriptRoot 'artifacts\ffmpeg'
$archivePath = Join-Path $cacheRoot $archiveName
$bundleRoot = Join-Path $cacheRoot 'runtime'
New-Item -ItemType Directory -Force -Path $cacheRoot, $bundleRoot | Out-Null

function Get-Sha256 {
    param([string]$Path)
    # Avoid Get-FileHash's script-module autoload, which can resolve to a
    # PowerShell 7 module when Windows PowerShell is launched by MSBuild.
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

if (-not (Test-Path -LiteralPath $archivePath)) {
    Write-Host "Downloading bundled FFmpeg $version..."
    $downloadPath = "$archivePath.$([Guid]::NewGuid().ToString('N')).download"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $archiveUrl -OutFile $downloadPath
        if ((Get-Sha256 $downloadPath) -ne $expectedHash) {
            throw 'Downloaded FFmpeg archive failed SHA-256 verification.'
        }
        Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
    } finally {
        if (Test-Path -LiteralPath $downloadPath) {
            Remove-Item -LiteralPath $downloadPath -Force
        }
    }
}

if ((Get-Sha256 $archivePath) -ne $expectedHash) {
    throw "Cached FFmpeg archive failed SHA-256 verification. Remove '$archivePath' and retry."
}

# Extract only the executable and upstream notices; no ffplay/ffprobe or external DLLs.
# Compare each entry with the staged file so repeat builds repair corruption without
# changing timestamps or causing unnecessary copies when the bundle is unchanged.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($relativePath in @('bin/ffmpeg.exe', 'LICENSE', 'README.txt')) {
        $entry = $archive.GetEntry("ffmpeg-$version-essentials_build/$relativePath")
        if (-not $entry) { throw "FFmpeg archive is missing $relativePath." }
        $destination = Join-Path $bundleRoot ([IO.Path]::GetFileName($relativePath))
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $entryHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose(); $stream.Dispose() }
        if (-not (Test-Path -LiteralPath $destination) -or
            (Get-Sha256 $destination) -ne $entryHash) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
        }
    }
} finally {
    $archive.Dispose()
}
Write-Host "Bundled FFmpeg $version verified at $bundleRoot"
