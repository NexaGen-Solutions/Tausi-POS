<#
  Creates the NexaGen Solutions code-signing certificate: RSA 3072, valid 3 years, for signing code only
  (it can't sign other certificates or identify a website). Run it once, on a PC you trust, on Windows:

    pwsh tools/signing/create-nexagen-cert.ps1

  It writes, outside the repo:
    nexagen-code-signing.pfx   the private key, protected by the password you choose. Keep it secret.
    nexagen-code-signing.cer   the public certificate.

  Then store the .pfx and its password as the GitHub secrets it prints. See docs/INSTALL.md, "Code signing".
#>
param(
  [string] $OutFolder = (Join-Path $HOME 'tausi-signing-keys'),
  [securestring] $Password
)
$ErrorActionPreference = 'Stop'

$OutFolder = [IO.Path]::GetFullPath($OutFolder)
New-Item -ItemType Directory -Force $OutFolder | Out-Null
# A private key must never be able to reach the repo.
Push-Location $OutFolder
try {
  git rev-parse --is-inside-work-tree 2>$null | Out-Null
  if ($LASTEXITCODE -eq 0) { throw "$OutFolder is inside a git repository. Choose a folder outside it." }
}
catch [System.Management.Automation.CommandNotFoundException] { }
finally { Pop-Location; $global:LASTEXITCODE = 0 }

$pfx = Join-Path $OutFolder 'nexagen-code-signing.pfx'
$cer = Join-Path $OutFolder 'nexagen-code-signing.cer'
if (Test-Path $pfx) { throw "$pfx already exists. Move it away first: a new certificate means every PC has to trust a new one." }
if (-not $Password) { $Password = Read-Host -AsSecureString 'Choose a password for the .pfx' }

$cert = New-SelfSignedCertificate -Type CodeSigningCert `
  -Subject 'CN=NexaGen Solutions, O=NexaGen Solutions, C=KE' -FriendlyName 'NexaGen Solutions code signing' `
  -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyUsage DigitalSignature -KeyExportPolicy Exportable `
  -NotAfter (Get-Date).AddYears(3) -CertStoreLocation 'Cert:\CurrentUser\My' `
  -TextExtension @('2.5.29.19={critical}{text}ca=false')
try {
  Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $Password | Out-Null
  Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
}
finally {
  # Only the password-protected file keeps the private key.
  Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

Write-Host "Created, valid until $($cert.NotAfter.ToString('d MMM yyyy')). Thumbprint: $($cert.Thumbprint)"
Write-Host "  $pfx"
Write-Host "  $cer"
Write-Host ''
Write-Host 'Store them as GitHub secrets (gh asks for the password when you run the second line):'
Write-Host "  [Convert]::ToBase64String([IO.File]::ReadAllBytes('$pfx')) | gh secret set WINDOWS_CERTIFICATE"
Write-Host '  gh secret set WINDOWS_CERTIFICATE_PASSWORD'
