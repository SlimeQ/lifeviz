param(
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$Configuration = 'Release',
    [string]$PublishProfile = 'Properties/PublishProfiles/WinClickOnce.pubxml',
    [string]$ApplicationVersion,
    [string]$ProductVersion,
    [int]$ApplicationRevision
)

function Resolve-MsBuild {
    # Try to find MSBuild using vswhere
    $vswherePath = $null

    if ($env:ProgramFiles) {
        $candidatePath = Join-Path $env:ProgramFiles (Join-Path 'Microsoft Visual Studio\Installer' 'vswhere.exe')
        if (Test-Path $candidatePath) {
            $vswherePath = $candidatePath
        }
    }

    if (-not $vswherePath -and $env:ProgramFilesX86) {
        $candidatePath = Join-Path $env:ProgramFilesX86 (Join-Path 'Microsoft Visual Studio\Installer' 'vswhere.exe')
        if (Test-Path $candidatePath) {
            $vswherePath = $candidatePath
        }
    }

    if ($vswherePath) {
        Write-Host "Attempting to find MSBuild.exe via vswhere.exe at $vswherePath..." -ForegroundColor DarkYellow
        # -prerelease includes VS Preview versions, -requires ensures MSBuild component is installed
        $msbuildPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe'
        if ($msbuildPath) {
            Write-Host "SUCCESS: Found MSBuild.exe via vswhere: $msbuildPath" -ForegroundColor Green
            return $msbuildPath
        }
        Write-Host "vswhere.exe found, but could not locate a suitable MSBuild installation. Falling back." -ForegroundColor Yellow
    } else {
        Write-Host "vswhere.exe not found. Falling back to registry and known paths." -ForegroundColor Yellow
    }

    # Fallback to original logic if vswhere is not found or fails
    $regPaths = @(
        'HKLM:\SOFTWARE\Microsoft\MSBuild\ToolsVersions\Current',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\MSBuild\ToolsVersions\Current',
        'HKLM:\SOFTWARE\Microsoft\VisualStudio\SxS\VS7',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\SxS\VS7'
    )

    foreach ($path in $regPaths) {
        try {
            $props = Get-ItemProperty -Path $path -ErrorAction SilentlyContinue
            if ($props.MSBuildToolsPath) {
                $exe = Join-Path $props.MSBuildToolsPath 'MSBuild.exe'
                if (Test-Path $exe) {
                    Write-Host "SUCCESS: Found MSBuild.exe from registry path ${path}: ${exe}" -ForegroundColor Green
                    return $exe
                }
            }
            if ($props.'17.0') { # For VS 2022+
                $vsBase = $props.'17.0'
                $candidate = Join-Path $vsBase 'MSBuild\Current\Bin\MSBuild.exe'
                if (Test-Path $candidate) {
                    Write-Host "SUCCESS: Found MSBuild.exe from VS 2022+ discovery: ${candidate}" -ForegroundColor Green
                    return $candidate
                }
            }
        } catch {}
    }

    $known = @(
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional\MSBuild\Current\Bin\MSBuild.exe'
    )

    Write-Host "Checking well-known hardcoded paths..." -ForegroundColor DarkYellow
    foreach ($path in $known) {
        if (Test-Path $path) {
            Write-Host "SUCCESS: Found MSBuild.exe at well-known path: ${path}" -ForegroundColor Green
            return $path
        }
    }

    throw 'MSBuild.exe not found. Install Visual Studio Build Tools or Visual Studio with the MSBuild component.'
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $root 'lifeviz.csproj'
$publishDirectory = Join-Path $root "bin\$Configuration\net9.0-windows\publish"
$rootPrefix = [IO.Path]::GetFullPath($root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$publishDirectory = [IO.Path]::GetFullPath($publishDirectory)
if (-not $publishDirectory.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean publish directory outside the repository: $publishDirectory"
}

$msbuild = Resolve-MsBuild
Write-Host "Using MSBuild at $msbuild" -ForegroundColor Cyan

if (Test-Path -LiteralPath $publishDirectory) {
    Write-Host "Removing stale publish payload at $publishDirectory" -ForegroundColor Cyan
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

$arguments = @(
    $projectPath,
    '/t:Publish',
    "/p:PublishProfile=$PublishProfile",
    "/p:Configuration=$Configuration"
)

if ($ApplicationVersion) {
    $arguments += "/p:ApplicationVersion=$ApplicationVersion"
}

if ($ProductVersion) {
    $arguments += "/p:Version=$ProductVersion"
}

if ($PSBoundParameters.ContainsKey('ApplicationRevision')) {
    $arguments += "/p:ApplicationRevision=$ApplicationRevision"
}

& $msbuild @arguments
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild publish failed with exit code $LASTEXITCODE"
}
