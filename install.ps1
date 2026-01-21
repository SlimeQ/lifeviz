param(
    [string]$Configuration = 'Release',
    [switch]$NoRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Build-SingleInstaller {
    param(
        [string]$PublishDir,
        [string]$OutputExe
    )

    Write-Host "Bundling installer from $PublishDir to $OutputExe..." -ForegroundColor Cyan

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
    
    # Run dotnet publish silently
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

# 1. Publish the app using the existing script
# Use a dynamic dev version to force ClickOnce update (1.0.YY.MMdd)
# Revision is handled by Publish-Installer, but we'll set a base version here.
$now = Get-Date
$devVersion = "1.0.{0}.{1}" -f $now.ToString("yy"), $now.ToString("MMdd")
$revision = [int]$now.ToString("HHmm")

Write-Host "Building LifeViz $devVersion.$revision ($Configuration)..." -ForegroundColor Cyan

.\Publish-Installer.ps1 `
    -Configuration $Configuration `
    -PublishProfile 'Properties/PublishProfiles/WinClickOnce.pubxml' `
    -ApplicationVersion $devVersion `
    -ApplicationRevision $revision

# 2. Locate the publish output
$publishDir = Join-Path $root "bin\$Configuration\net9.0-windows\publish"
if (-not (Test-Path $publishDir)) {
    throw "Publish directory not found at $publishDir"
}

# 3. Ensure the helper script is there (Install-ClickOnce.ps1)
$installHelper = Join-Path $root 'Install-ClickOnce.ps1'
if (Test-Path $installHelper) {
    Copy-Item -Path $installHelper -Destination (Join-Path $publishDir 'Install-ClickOnce.ps1') -Force
} else {
    Write-Warning "Install-ClickOnce.ps1 not found in root; installer may fail."
}

# 4. Bundle it
$artifactsDir = Join-Path $root 'artifacts/local-install'
if (-not (Test-Path $artifactsDir)) {
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
}

$installerExe = Join-Path $artifactsDir 'lifeviz_installer.exe'
Build-SingleInstaller -PublishDir $publishDir -OutputExe $installerExe | Out-Null

Write-Host "Installer created at: $installerExe" -ForegroundColor Green

# 5. Run it
if (-not $NoRun) {
    Write-Host "Launching installer..." -ForegroundColor Cyan
    Start-Process $installerExe
}

