<#
.SYNOPSIS
  Authenticode-signs one or more files (binaries or the installer).

.DESCRIPTION
  Pass a certificate either by thumbprint (from the CurrentUser/LocalMachine
  "My" store) or by PFX file. A SHA-256 signature is applied and timestamped.

  A self-signed certificate is fine for testing that this pipeline works, but
  it will NOT satisfy SmartScreen or Smart App Control on other people's
  machines — real distribution needs a certificate from a trusted CA.

.EXAMPLE
  .\sign.ps1 -Path dist\Lookout-x64\Lookout.exe -Thumbprint AB12...CD

.EXAMPLE
  .\sign.ps1 -Path dist\*.exe -PfxPath mycert.pfx -PfxPassword hunter2
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(ParameterSetName = 'Store')][string]$Thumbprint,
    [Parameter(ParameterSetName = 'Pfx', Mandatory)][string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')][string]$PfxPassword,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

if ($PSCmdlet.ParameterSetName -eq 'Pfx') {
    if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
    $securePwd = if ($PfxPassword) {
        ConvertTo-SecureString $PfxPassword -AsPlainText -Force
    } else { $null }
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 `
        @($PfxPath, $securePwd)
}
else {
    if (-not $Thumbprint) { throw "Provide -Thumbprint, or use -PfxPath/-PfxPassword." }
    $cert = Get-ChildItem "Cert:\CurrentUser\My\$Thumbprint", "Cert:\LocalMachine\My\$Thumbprint" `
        -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $cert) {
        throw "No certificate with thumbprint $Thumbprint found in CurrentUser\My or LocalMachine\My."
    }
}

$files = $Path | ForEach-Object { Get-Item $_ } | Select-Object -ExpandProperty FullName -Unique
if (-not $files) { throw "No files matched: $($Path -join ', ')" }

$warned = $false
foreach ($file in $files) {
    $result = Set-AuthenticodeSignature -FilePath $file -Certificate $cert `
        -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
    Write-Host ("  {0,-12} {1}" -f $result.Status, $file)

    if (-not $result.SignerCertificate) {
        throw "Signing failed for $file - $($result.StatusMessage)"
    }
    if ($result.Status -ne 'Valid') {
        $warned = $true
    }
}

if ($warned) {
    Write-Warning "Signature applied, but not trusted on this machine (expected for a self-signed cert)."
}
Write-Host "Signed $($files.Count) file(s) with: $($cert.Subject)"
