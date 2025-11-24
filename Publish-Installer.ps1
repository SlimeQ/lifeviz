param(
    [string]$Configuration = 'Release',
    [string]$PublishProfile = 'Properties/PublishProfiles/WinClickOnce.pubxml',
    [string]$ApplicationVersion,
    [int]$ApplicationRevision
)

function Resolve-MsBuild {
    $regPaths = @(
        'HKLM:\SOFTWARE\Microsoft\MSBuild\ToolsVersions\Current'
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\MSBuild\ToolsVersions\Current'
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\SxS\VS7'
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\SxS\VS7'
    )

    foreach ($path in $regPaths) {
        try {
            $props = Get-ItemProperty -Path $path -ErrorAction Stop
            if ($props.MSBuildToolsPath) {
                $exe = Join-Path $props.MSBuildToolsPath 'MSBuild.exe'
                if (Test-Path $exe) { return $exe }
            }
            if ($props.'17.0') {
                $vsBase = $props.'17.0'
                $candidate = Join-Path $vsBase 'MSBuild\Current\Bin\MSBuild.exe'
                if (Test-Path $candidate) { return $candidate }
            }
        } catch {}
    }

    $known = @(
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe'
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'
    )

    foreach ($path in $known) {
        if (Test-Path $path) { return $path }
    }

    throw 'MSBuild.exe not found. Install Visual Studio Build Tools or Visual Studio with the MSBuild component.'
}

$msbuild = Resolve-MsBuild
Write-Host "Using MSBuild at $msbuild" -ForegroundColor Cyan

$arguments = @(
    (Resolve-Path 'lifeviz.csproj').Path,
    '/t:Publish',
    "/p:PublishProfile=$PublishProfile",
    "/p:Configuration=$Configuration"
)

if ($ApplicationVersion) {
    $arguments += "/p:ApplicationVersion=$ApplicationVersion"
}

if ($PSBoundParameters.ContainsKey('ApplicationRevision')) {
    $arguments += "/p:ApplicationRevision=$ApplicationRevision"
}

& $msbuild @arguments
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild publish failed with exit code $LASTEXITCODE"
}
