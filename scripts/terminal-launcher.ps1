<#
  Builds the terminal's launcher (terminal/launcher, tausi-launcher.exe), signs it when a certificate is
  set ($env:CERT, as scripts/sign.ps1), and prints the Tauri config that bundles it into the installer.
  The installer copies it to ...\Tausi POS\Launcher\ (terminal/src-tauri/windows/hooks.nsh).
    $launcher = scripts/terminal-launcher.ps1
#>
$ErrorActionPreference = 'Stop'
$crate = Join-Path $PSScriptRoot '..\terminal\launcher'
cargo build --release --manifest-path (Join-Path $crate 'Cargo.toml') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'The launcher did not build.' }
$exe = (Resolve-Path (Join-Path $crate 'target\release\tausi-launcher.exe')).Path
if ($env:CERT) {
  & (Join-Path $PSScriptRoot 'sign.ps1') -Files $exe | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'The launcher was not signed.' }
}
# Tauri's resources map a file to its name in the install folder; --config merges it with the others.
@{ ($exe -replace '\\', '/') = 'tausi-launcher.exe' }
