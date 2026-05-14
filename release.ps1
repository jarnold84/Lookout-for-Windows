<#
.SYNOPSIS
  Builds a signed installer + portable ZIP and publishes them as a GitHub Release.

.DESCRIPTION
  Requires the GitHub CLI (gh) to be installed and authenticated, and this folder
  to be a git repo with a GitHub remote. Run build-installer.ps1's signing flags
  through this script for a signed release.

.EXAMPLE
  .\release.ps1 -Version 1.0.0 -DevSign
  .\release.ps1 -Version 1.0.0 -Thumbprint AB12...CD
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$DevSign
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$tag = "v$Version"

# --- build the signed installer -------------------------------------------
$buildArgs = @{}
if ($DevSign) { $buildArgs.DevSign = $true }
if ($Thumbprint) { $buildArgs.Thumbprint = $Thumbprint }
if ($PfxPath) { $buildArgs.PfxPath = $PfxPath }
if ($PfxPassword) { $buildArgs.PfxPassword = $PfxPassword }
& (Join-Path $root "build-installer.ps1") @buildArgs

# --- zip the portable build -----------------------------------------------
$dist = Join-Path $root "dist"
$zip = Join-Path $dist "Lookout-$tag-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $dist "Lookout-x64\*") -DestinationPath $zip
Write-Host "Portable ZIP: $zip"

$installer = Get-ChildItem $dist -Filter "Lookout-Setup-*.exe" |
    Sort-Object LastWriteTime | Select-Object -Last 1

# --- publish the GitHub Release -------------------------------------------
Write-Host "Creating GitHub Release $tag..."
gh release create $tag $installer.FullName $zip `
    --title "Lookout $Version" `
    --notes "Lookout $Version - AI screen assistant for Windows. Run the installer, or unzip the portable build and launch Lookout.exe."

if ($LASTEXITCODE -ne 0) { throw "gh release create failed (exit code $LASTEXITCODE)." }
Write-Host "Release $tag published." -ForegroundColor Green
