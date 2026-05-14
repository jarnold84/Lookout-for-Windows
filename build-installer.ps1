<#
.SYNOPSIS
  One-shot: publish Lookout, optionally code-sign it, and build a Windows installer.

.DESCRIPTION
  Pipeline: publish.ps1  ->  sign binaries  ->  compile installer (Inno Setup)
  ->  sign installer. Signing is skipped unless a certificate is supplied.

  -DevSign generates/reuses a self-signed certificate just to prove the signing
  pipeline works end to end. Self-signed signatures do NOT satisfy SmartScreen
  or Smart App Control on other machines — use -Thumbprint or -PfxPath with a
  real CA-issued certificate for actual distribution.

.EXAMPLE
  .\build-installer.ps1                                  # unsigned installer
  .\build-installer.ps1 -DevSign                         # signed with a test cert
  .\build-installer.ps1 -Thumbprint AB12...CD            # signed with a real cert
  .\build-installer.ps1 -PfxPath cert.pfx -PfxPassword x # signed from a PFX
#>
param(
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [switch]$DevSign
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$platform = "x64"
$dist = Join-Path $root "dist\Lookout-$platform"
$iss = Join-Path $root "installer\Lookout.iss"
$signScript = Join-Path $root "sign.ps1"

$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it: winget install JRSoftware.InnoSetup"
}

# --- 1. publish the self-contained app ------------------------------------
Write-Host "[1/4] Publishing..." -ForegroundColor Cyan
& (Join-Path $root "publish.ps1") -Platform $platform

# --- 2. work out signing ---------------------------------------------------
$signArgs = $null
if ($DevSign) {
    $devSubject = "CN=Lookout Dev (self-signed)"
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $devSubject } | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Creating self-signed dev certificate (testing only)..."
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $devSubject `
            -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
            -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
    }
    $signArgs = @{ Thumbprint = $cert.Thumbprint }
}
elseif ($Thumbprint) {
    $signArgs = @{ Thumbprint = $Thumbprint }
}
elseif ($PfxPath) {
    $signArgs = @{ PfxPath = $PfxPath }
    if ($PfxPassword) { $signArgs.PfxPassword = $PfxPassword }
}

# --- 3. sign our own binaries before packaging ----------------------------
if ($signArgs) {
    Write-Host "[2/4] Signing binaries..." -ForegroundColor Cyan
    & $signScript -Path (Join-Path $dist "Lookout.exe"), (Join-Path $dist "Lookout.dll") @signArgs
}
else {
    Write-Host "[2/4] No certificate supplied - skipping binary signing." -ForegroundColor Yellow
}

# --- 4. compile the installer ---------------------------------------------
Write-Host "[3/4] Compiling installer..." -ForegroundColor Cyan
& $iscc "/DSourceDir=$dist" $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed (exit code $LASTEXITCODE)." }

$setupExe = Get-ChildItem (Join-Path $root "dist") -Filter "Lookout-Setup-*.exe" |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $setupExe) { throw "Installer was not produced." }

# --- 5. sign the installer itself -----------------------------------------
if ($signArgs) {
    Write-Host "[4/4] Signing installer..." -ForegroundColor Cyan
    & $signScript -Path $setupExe.FullName @signArgs
}
else {
    Write-Host "[4/4] No certificate supplied - installer left unsigned." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Installer ready: $($setupExe.FullName)" -ForegroundColor Green
