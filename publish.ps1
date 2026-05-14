# Publishes Lookout as a self-contained, unpackaged Windows app.
# The output folder is fully portable — no install, no .NET or Windows App SDK
# runtime required on the target machine.
#
# Usage:   .\publish.ps1            (x64, the default)
#          .\publish.ps1 -Platform arm64
param(
    [ValidateSet("x64", "arm64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"

$rid     = "win-$Platform"
$project = Join-Path $PSScriptRoot "src\Lookout\Lookout.csproj"
$output  = Join-Path $PSScriptRoot "dist\Lookout-$Platform"

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

Write-Host "Publishing Lookout ($rid) -> $output"

& $dotnet publish $project `
    -c Release `
    -p:Platform=$Platform `
    -r $rid `
    --self-contained `
    -p:WindowsAppSDKSelfContained=true `
    -o $output

if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

Write-Host ""
Write-Host "Done. Portable build is in: $output"
Write-Host "Launch it with: `"$output\Lookout.exe`""
