<#
  Authenticode-signs files with the code-signing certificate in $env:CERT (a .pfx, base64) and
  $env:CERT_PASSWORD. Without a certificate it does nothing (the files stay unsigned).
#>
param([Parameter(Mandatory)] [string[]] $Files)
$ErrorActionPreference = 'Stop'
if (-not $env:CERT) { Write-Host 'No certificate: unsigned.'; exit 0 }
$pfx = Join-Path $env:RUNNER_TEMP 'cert.pfx'
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:CERT))
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
& $signtool.FullName sign /f $pfx /p $env:CERT_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d 'Tausi POS' $Files
if ($LASTEXITCODE -ne 0) { throw 'signing failed' }
Remove-Item $pfx
