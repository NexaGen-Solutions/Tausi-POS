<#
  Gets a terminal build ready to sign, from the certificate in $env:CERT (a .pfx, base64) and
  $env:CERT_PASSWORD. Prints the command Tauri signs with: it signs the app, the uninstaller and the
  installer as it builds, before it makes the updater's signature (signing afterwards would break that).
  A self-signed certificate is also added to the installer, which trusts it on the PC (no certificate
  or a paid one: nothing to add). Without a certificate it prints nothing.
#>
$ErrorActionPreference = 'Stop'
if (-not $env:CERT) { return }

$cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String($env:CERT), $env:CERT_PASSWORD)
if ($cert.Subject -eq $cert.Issuer) {
  $folder = Join-Path $env:RUNNER_TEMP 'terminal-cert'
  New-Item -ItemType Directory -Force $folder | Out-Null
  $cer = Join-Path $folder 'nexagen-code-signing.cer'
  [IO.File]::WriteAllBytes($cer, $cert.Export('Cert'))
  $nsh = Join-Path $PSScriptRoot '..\terminal\src-tauri\windows\trust-cert.nsh'
  @"
; Written by scripts/terminal-signing.ps1 for a self-signed certificate.
!define MUI_WELCOMEPAGE_TEXT "This installs Tausi POS on this PC.`$\r`$\n`$\r`$\nIt also adds NexaGen Solutions' code-signing certificate to this PC's trusted certificates, so Windows recognises Tausi POS and its updates.`$\r`$\n`$\r`$\nPress Next to continue."
!macro TausiTrustCert
  SetOutPath "`$INSTDIR"
  File "$cer"
  nsExec::ExecToLog '"`$SYSDIR\certutil.exe" -f -addstore Root "`$INSTDIR\nexagen-code-signing.cer"'
  Pop `$R3
  nsExec::ExecToLog '"`$SYSDIR\certutil.exe" -f -addstore TrustedPublisher "`$INSTDIR\nexagen-code-signing.cer"'
  Pop `$R3
  Delete "`$INSTDIR\nexagen-code-signing.cer"
!macroend
"@ | Set-Content $nsh -Encoding utf8
}
$script = ((Join-Path $PSScriptRoot 'sign.ps1') -replace '\\', '/')
Write-Output "pwsh -NoProfile -File $script -Files %1"
