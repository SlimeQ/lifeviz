param(
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

function Parse-Version {
    param([string]$Value)
    if ($Value -match '^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$') {
        return [pscustomobject]@{
            Major = [int]$Matches[1]
            Minor = [int]$Matches[2]
            Patch = [int]$Matches[3]
            Revision = if ($Matches[4]) { [int]$Matches[4] } else { 0 }
            Raw = $Value
        }
    }
    return $null
}

function Fetch-Tags {
    try {
        git fetch --tags --quiet 2>$null
    } catch {}
}

function TagOrRelease-Exists {
    param([string]$Tag)

    $tagExists = git tag --list $Tag 2>$null
    if ($tagExists) {
        return $true
    }

    $releaseExists = $false
    try {
        gh release view $Tag --json name 1>$null 2>$null
        if ($LASTEXITCODE -eq 0) { $releaseExists = $true }
    } catch {}

    return $releaseExists
}

function Bump-Patch {
    param([pscustomobject]$Version)
    return [pscustomobject]@{
        Major = $Version.Major
        Minor = $Version.Minor
        Patch = $Version.Patch + 1
        Revision = $Version.Revision
        Raw = "v{0}.{1}.{2}" -f $Version.Major, $Version.Minor, ($Version.Patch + 1)
    }
}

function Build-SingleInstaller {
    param(
        [string]$PublishDir,
        [string]$VersionTag,
        [string]$OutputExe
    )

    $workDir = Join-Path ([IO.Path]::GetTempPath()) ("lifeviz_bootstrapper_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $workDir | Out-Null

    $payloadZip = Join-Path $workDir 'payload.zip'
    Compress-Archive -Path (Join-Path $PublishDir '*') -DestinationPath $payloadZip -Force

    $programCs = @"
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using System.Linq;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "lifeviz_installer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        var assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            MessageBox.Show("Installer payload is missing.", "LifeViz Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (Stream? payload = assembly.GetManifestResourceStream(resourceName))
        {
            string zipPath = Path.Combine(tempRoot, "payload.zip");
            using (FileStream fs = File.Create(zipPath))
            {
                payload?.CopyTo(fs);
            }
            ZipFile.ExtractToDirectory(zipPath, tempRoot);
        }

        string scriptPath = Path.Combine(tempRoot, "Install-ClickOnce.ps1");
        if (!File.Exists(scriptPath))
        {
            MessageBox.Show("Installer payload is incomplete (missing Install-ClickOnce.ps1).", "LifeViz Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var psi = new ProcessStartInfo("powershell")
        {
            Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -SourcePath \"{tempRoot}\"",
            UseShellExecute = true
        };

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to launch installer: " + ex.Message, "LifeViz Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
"@

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <UseWindowsForms>true</UseWindowsForms>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>
  <ItemGroup>
    <EmbeddedResource Include="payload.zip" />
  </ItemGroup>
</Project>
"@

    $programPath = Join-Path $workDir 'Program.cs'
    $projPath = Join-Path $workDir 'InstallerBootstrapper.csproj'
    Set-Content -Path $programPath -Value $programCs -NoNewline
    Set-Content -Path $projPath -Value $csproj -NoNewline
    dotnet publish $projPath -c Release -r win-x64 --self-contained true | Out-Null

    $publishedExe = Get-ChildItem -Path $workDir -Recurse -Filter '*.exe' |
        Where-Object { $_.Name -notmatch 'vshost' } |
        Sort-Object Length -Descending |
        Select-Object -First 1

    if (-not $publishedExe) {
        throw "Failed to locate built installer executable."
    }

    Copy-Item -Path $publishedExe.FullName -Destination $OutputExe -Force
    try { Remove-Item -Recurse -Force $workDir } catch {}
    return $OutputExe
}

function Get-HighestTagVersion {
    $tags = git tag --list 'v*' 2>$null
    if (-not $tags) { return $null }
    $parsed = @()
    foreach ($t in $tags) {
        $v = Parse-Version $t
        if ($v) { $parsed += $v }
    }
    if ($parsed.Count -eq 0) { return $null }
    return $parsed | Sort-Object -Descending -Property Major, Minor, Patch, Revision | Select-Object -First 1
}

function Prompt-VersionBump {
    param([pscustomobject]$BaseVersion)

    $base = if ($BaseVersion) { $BaseVersion } else { Parse-Version 'v1.0.0' }
    Write-Host ""
    Write-Host "✨ Version vibes ✨" -ForegroundColor Magenta
    Write-Host "1) Tiny tweak / bugfix (patch bump)" -ForegroundColor Gray
    Write-Host "2) Noticeable glow-up (minor bump)" -ForegroundColor Gray
    Write-Host "3) Whole new era (major bump)" -ForegroundColor Gray
    do {
        $choice = Read-Host "Pick 1, 2, or 3 to set the vibe"
    } while ($choice -notin @('1','2','3'))

    $major, $minor, $patch = $base.Major, $base.Minor, $base.Patch
    switch ($choice) {
        '1' { $patch++ }
        '2' { $minor++; $patch = 0 }
        '3' { $major++; $minor = 0; $patch = 0 }
    }

    $versionString = "{0}.{1}.{2}" -f $major, $minor, $patch
    $tagString = "v$versionString"
    Write-Host "Selected version: $tagString (from $($base.Raw))" -ForegroundColor Cyan
    return $tagString
}

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

$tagInput = $Tag
if ($tagInput) {
    $tagInput = $tagInput.Trim()
}

if (-not $tagInput) {
    Fetch-Tags
    $latest = Get-HighestTagVersion
    $tagInput = Prompt-VersionBump -BaseVersion $latest
}

$parsedTag = Parse-Version $tagInput
if (-not $parsedTag) {
    throw "Release tag must look like v1.2.3 or v1.2.3.4; received '$Tag'."
}

# Avoid collisions with existing tags/releases by patch-bumping until free.
while (TagOrRelease-Exists $tagInput) {
    $parsedTag = Bump-Patch -Version $parsedTag
    $tagInput = $parsedTag.Raw
    Write-Host "Tag/release $tagInput already exists; nudging to next patch: $tagInput" -ForegroundColor Yellow
}

$gh = Get-Command 'gh' -ErrorAction SilentlyContinue
if (-not $gh) {
    throw 'GitHub CLI (gh) not found. Install it from https://cli.github.com and run gh auth login.'
}

$normalizedTag = $tagInput
if ($normalizedTag.StartsWith('v')) {
    $normalizedTag = $normalizedTag.Substring(1)
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

$installerExe = Join-Path $artifactsDir 'lifeviz_installer.exe'
if (Test-Path $installerExe) {
    Remove-Item $installerExe -Force
}

Invoke-Step "Bundling single-file installer to $installerExe" {
    Build-SingleInstaller -PublishDir $publishDir -VersionTag $tagInput -OutputExe $installerExe | Out-Null
}

$assets = @($installerExe)

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
