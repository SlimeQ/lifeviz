param(
    [string]$Configuration = 'Release',
    [string]$PublishProfile = 'Properties/PublishProfiles/WinClickOnce.pubxml',
    [string]$ApplicationVersion,
    [int]$ApplicationRevision
)

# Force the use of .NET Framework MSBuild, which is required for ClickOnce.
$msbuild = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"

if (-not (Test-Path $msbuild)) {
    throw "Required .NET Framework MSBuild.exe not found at $msbuild"
}

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
