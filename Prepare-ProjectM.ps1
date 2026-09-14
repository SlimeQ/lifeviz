param([switch]$Rebuild)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$revision = '1e7ef7803b69024d1e0656705670adda2ffac817'
$evalRevision = '22fb0cfd8f2dfbcd2b68f2443e7f44e19b32c09a'
$assetUrl = 'https://github.com/projectM-visualizer/frontend-sdl-cpp/releases/download/2.0.0-pre1/projectMSDL-2.0.0-win64.zip'
$assetHash = '7129cae0757970bd4fb2bf96aa7d6153fe9872d414c88c82b04a6396bdfe235b'
$cache = Join-Path $PSScriptRoot 'artifacts\projectm'
$runtime = Join-Path $cache 'bundle'
$source = Join-Path $PSScriptRoot 'Native\projectm'
$build = Join-Path $cache 'build'
$stamp = Join-Path $runtime 'bundle-version.txt'
$noticeRoot = Join-Path $PSScriptRoot 'ThirdParty\projectM'
$recipeSha = [Security.Cryptography.SHA256]::Create()
$recipeData = [IO.File]::ReadAllText($PSCommandPath) + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ProjectM-NOTICES.md'))
foreach ($noticeFile in Get-ChildItem -LiteralPath $noticeRoot -File | Sort-Object Name) { $recipeData += [IO.File]::ReadAllText($noticeFile.FullName) }
try { $recipeHash = [BitConverter]::ToString($recipeSha.ComputeHash([Text.Encoding]::UTF8.GetBytes($recipeData))).Replace('-', '') }
finally { $recipeSha.Dispose() }
$bundleVersion = "$revision-$assetHash-v3-$recipeHash"
New-Item -ItemType Directory -Force -Path $cache, $runtime | Out-Null
# WPF temporary projects and parallel builds can enter this target concurrently.
$mutex = New-Object Threading.Mutex($false, 'Local\LifeViz-PrepareProjectM')
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne(600000) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Timed out waiting for projectM preparation.' }
    $manifestPath = Join-Path $runtime 'bundle-files.txt'
    if (-not $Rebuild -and (Test-Path -LiteralPath $stamp) -and
        [IO.File]::ReadAllText($stamp) -eq $bundleVersion -and (Test-Path -LiteralPath $manifestPath)) {
        $missing = @([IO.File]::ReadAllLines($manifestPath) | Where-Object { -not [IO.File]::Exists((Join-Path $runtime $_)) })
        if ($missing.Count -eq 0) { Write-Host 'Bundled projectM is ready.'; return }
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $archivePath = Join-Path $cache 'upstream.zip'
    if (-not (Test-Path -LiteralPath $archivePath)) {
        Write-Host 'Downloading the pinned projectM preset and texture collection...'
        Invoke-WebRequest -UseBasicParsing -Uri $assetUrl -OutFile "$archivePath.download"
        Move-Item -LiteralPath "$archivePath.download" -Destination $archivePath -Force
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($archivePath)
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne $assetHash) { throw "projectM asset checksum mismatch. Remove '$archivePath' and retry." }

    if (-not (Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))) {
        & git -C $PSScriptRoot submodule update --init --recursive -- Native/projectm
        if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the pinned projectM submodule.' }
    }
    $actual = & git -C $source rev-parse HEAD
    $actualEval = & git -C (Join-Path $source 'vendor/projectm-eval') rev-parse HEAD
    if ($actual -ne $revision -or $actualEval -ne $evalRevision) { throw 'Unexpected projectM source revision in the build cache.' }
    if (& git -C $source status --porcelain) { throw 'The projectM source cache has local edits; preserve them elsewhere before rebuilding the bundle.' }
    $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
    if (-not $cmake) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $cmake = & $vswhere -latest -products '*' -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
        }
    }
    if (-not $cmake) { throw 'Install Visual Studio 2022 Desktop development with C++ (including CMake) to build bundled projectM.' }
    & $cmake -S $source -B $build -G 'Visual Studio 17 2022' -A x64 '-DENABLE_SYSTEM_PROJECTM_EVAL=OFF' '-DENABLE_PLAYLIST=OFF' '-DBUILD_SHARED_LIBS=ON' '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded'
    if ($LASTEXITCODE -ne 0) { throw 'projectM CMake configuration failed.' }
    & $cmake --build $build --config Release --parallel 6
    if ($LASTEXITCODE -ne 0) { throw 'projectM native build failed.' }
    Copy-Item -LiteralPath (Join-Path $build 'src\libprojectM\Release\projectM-4.dll') -Destination $runtime -Force

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    $assetsPath = Join-Path $runtime 'assets.zip'
    if (Test-Path -LiteralPath $assetsPath) { Remove-Item -LiteralPath $assetsPath -Force }
    $assets = [IO.Compression.ZipFile]::Open($assetsPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.Name -eq '') { continue }
            $relative = $entry.FullName -replace '^projectMSDL-2\.0\.0-win64/', ''
            if ($relative -notmatch '^(presets|textures)/') { continue }
            # Flatten only the pack wrapper; keep categories and original preset filenames.
            $relative = $relative -replace '^presets/presets-cream-of-the-crop/', 'presets/' -replace '^textures/textures/', 'textures/'
            if ($relative -match '(^|/)\.\.(/|$)') { throw 'Unsafe path in preset archive.' }
            $copy = $assets.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $inputStream = $entry.Open(); $outputStream = $copy.Open()
            try { $inputStream.CopyTo($outputStream) } finally { $inputStream.Dispose(); $outputStream.Dispose() }
        }
    } finally { $archive.Dispose(); $assets.Dispose() }
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE.txt') -Destination (Join-Path $runtime 'LICENSE-projectM.txt') -Force
    Copy-Item -LiteralPath (Join-Path $source 'COPYING') -Destination (Join-Path $runtime 'COPYING-projectM.txt') -Force
    Copy-Item -LiteralPath (Join-Path $source 'vendor\projectm-eval\LICENSE.md') -Destination (Join-Path $runtime 'LICENSE-projectm-eval.md') -Force
    Copy-Item -LiteralPath (Join-Path $source 'vendor\hlslparser\LICENSE') -Destination (Join-Path $runtime 'LICENSE-hlslparser.txt') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ProjectM-NOTICES.md') -Destination $runtime -Force
    $licensesPath = Join-Path $runtime 'licenses.zip'
    if (Test-Path -LiteralPath $licensesPath) { Remove-Item -LiteralPath $licensesPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory($noticeRoot, $licensesPath)
    # Complete corresponding source, including the pinned submodule and vendored headers.
    $sourceZip = Join-Path $runtime 'projectM-source.zip'
    if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip -Force }
    $zip = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File -Force) {
            $relative = $file.FullName.Substring($source.Length + 1).Replace('\', '/')
            if ($relative -match '(^|/)\.git(/|$)') { continue }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "projectm/$relative") | Out-Null
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $PSCommandPath, 'Prepare-ProjectM.ps1') | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot 'ProjectM-NOTICES.md'), 'ProjectM-NOTICES.md') | Out-Null
        foreach ($noticeFile in Get-ChildItem -LiteralPath $noticeRoot -File) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $noticeFile.FullName, ('ThirdParty/projectM/' + $noticeFile.Name)) | Out-Null
        }
    } finally { $zip.Dispose() }
    $files = @(Get-ChildItem -LiteralPath $runtime -File -Recurse | Where-Object { $_.FullName -ne $stamp -and $_.FullName -ne $manifestPath } | ForEach-Object { $_.FullName.Substring($runtime.Length + 1) })
    [IO.File]::WriteAllLines($manifestPath, $files)
    [IO.File]::WriteAllText($stamp, $bundleVersion)
    Write-Host "Bundled projectM and $($files.Count) runtime files prepared."
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
