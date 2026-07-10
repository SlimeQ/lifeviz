param(
    [string]$Configuration = 'Release',
    [string]$PublishProfile = 'Properties/PublishProfiles/WinClickOnce.pubxml',
    [switch]$RegisterClickOnce
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Invoke-Step {
    param (
        [string]$Message,
        [scriptblock]$Action
    )

    Write-Host "[deploy] $Message" -ForegroundColor Cyan
    & $Action
    if (-not $?) {
        throw "Step failed: $Message"
    }
}

Invoke-Step 'Building project' {
    dotnet build -c $Configuration
}

$now = Get-Date
$buildComponent = "{0:yyMM}" -f $now
$instrumentedRevision = (($now.Day - 1) * 1440) + ($now.Hour * 60) + $now.Minute
$appVersion = "1.0.$buildComponent.0"

Invoke-Step 'Publishing ClickOnce installer via MSBuild' {
    .\Publish-Installer.ps1 `
        -Configuration $Configuration `
        -PublishProfile $PublishProfile `
        -ApplicationVersion $appVersion `
        -ApplicationRevision $instrumentedRevision
}

$publishDir = Join-Path $root "bin\$Configuration\net9.0-windows\publish"
$installHelper = Join-Path $root 'Install-ClickOnce.ps1'
if (Test-Path $installHelper) {
    Copy-Item -Path $installHelper -Destination (Join-Path $publishDir 'Install-ClickOnce.ps1') -Force
}
Invoke-Step 'Staging install and refreshing shortcuts' {
    if ($RegisterClickOnce) {
        & $installHelper -SourcePath $publishDir -RegisterClickOnce
    }
    else {
        & $installHelper -SourcePath $publishDir
    }
}
