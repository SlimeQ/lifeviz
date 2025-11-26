param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$Configuration = 'Release',
    [string]$PublishProfile = 'Properties/PublishProfiles/WinClickOnce.pubxml',
    [string]$ReleaseName,
    [string]$NotesPath,
    [switch]$Draft,
    [int]$ApplicationRevision = 0
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Invoke-Step {
    param (
        [string]$Message,
        [scriptblock]$Action
    )

    Write-Host "[release] $Message" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Action
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Step failed with exit code $LASTEXITCODE"
    }
}

$tagInput = $Tag.Trim()

$gh = Get-Command 'gh' -ErrorAction SilentlyContinue
if (-not $gh) {
    throw 'GitHub CLI (gh) not found. Install it from https://cli.github.com and run gh auth login.'
}

$normalizedTag = $tagInput
if ($normalizedTag.StartsWith('v')) {
    $normalizedTag = $normalizedTag.Substring(1)
}

if ($normalizedTag -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw "Release tag must look like v1.2.3 or v1.2.3.4; received '$Tag'."
}

if ($ApplicationRevision -lt 0) {
    throw 'ApplicationRevision must be non-negative.'
}

$applicationVersion = if ($normalizedTag -match '^\d+\.\d+\.\d+$') { "$normalizedTag.0" } else { $normalizedTag }

if (-not $ReleaseName) {
    $ReleaseName = "LifeViz Windows $tagInput"
}

if ($NotesPath) {
    $NotesPath = (Resolve-Path $NotesPath).Path
}

Invoke-Step 'Checking GitHub authentication' {
    gh auth status --hostname github.com | Out-Null
}

Invoke-Step 'Building project' {
    dotnet build -c $Configuration
}

Invoke-Step 'Publishing ClickOnce installer via MSBuild' {
    .\Publish-Installer.ps1 `
        -Configuration $Configuration `
        -PublishProfile $PublishProfile `
        -ApplicationVersion $applicationVersion `
        -ApplicationRevision $ApplicationRevision
}

$publishDir = Join-Path $root "bin\$Configuration\net9.0-windows\publish"
if (-not (Test-Path $publishDir)) {
    throw "Publish directory not found at $publishDir"
}

$setupExe = Join-Path $publishDir 'setup.exe'
$appManifest = Join-Path $publishDir 'lifeviz.application'

if (-not (Test-Path $setupExe)) {
    throw "Required asset missing: $setupExe"
}

if (-not (Test-Path $appManifest)) {
    throw "Required asset missing: $appManifest"
}

# Ensure the installer helper script is packaged with the payload.
$installHelper = Join-Path $root 'Install-ClickOnce.ps1'
if (Test-Path $installHelper) {
    Copy-Item -Path $installHelper -Destination (Join-Path $publishDir 'Install-ClickOnce.ps1') -Force
}

$artifactsDir = Join-Path $root 'artifacts/github-release'
if (-not (Test-Path $artifactsDir)) {
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
}

$zipPath = Join-Path $artifactsDir ("lifeviz-clickonce-{0}.zip" -f $normalizedTag)
if (Test-Path $zipPath) {
    Remove-Item $zipPath
}

Invoke-Step "Zipping ClickOnce payload to $zipPath" {
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
}

$assets = @($zipPath)

Invoke-Step "Creating GitHub release $tagInput" {
    $ghArgs = @('release', 'create', $tagInput) + $assets + @('--title', $ReleaseName)

    if ($Draft) {
        $ghArgs += '--draft'
    }

    if ($NotesPath) {
        $ghArgs += @('--notes-file', $NotesPath)
    } else {
        $ghArgs += @('--notes', "Windows ClickOnce release for $Tag")
    }

    gh @ghArgs
}
