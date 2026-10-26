<#
  Builds the Tausi POS Server installer on Windows (WiX Toolset v5):
    TausiPOS-Server.msi (inside) and TausiPOS-Server-Setup-<version>.exe (the bundle people run).

  .\build.ps1 -Version 1.2.0 -Server ..\..\out\server -Out ..\..\out\installer
             [-CertificateFile cert.pfx -CertificatePassword ...]   # Authenticode; without it the files are unsigned
             [-FailMigration]                                        # test build for the rollback test only

  -Server is `dotnet publish src/Tausi.Api -r win-x64 --self-contained` output (with wwwroot and, for a
  release, updates\terminal\ holding the matching terminal installer and terminal.json).
#>
param(
  [Parameter(Mandatory)] [string] $Version,
  [Parameter(Mandatory)] [string] $Server,
  [Parameter(Mandatory)] [string] $Out,
  [string] $CertificateFile,
  [string] $CertificatePassword,
  [switch] $FailMigration
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$Server = (Resolve-Path $Server).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$work = Join-Path $Out 'work'
New-Item -ItemType Directory -Force $work | Out-Null

function Sign([string[]] $files) {
  if (-not $CertificateFile) { return }
  $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
  & $signtool.FullName sign /f $CertificateFile /p $CertificatePassword /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d 'Tausi POS Server' $files
  if ($LASTEXITCODE -ne 0) { throw "signing failed" }
}

# The rollback test build: migrate fails on purpose.
$programFiles = $Server
if ($FailMigration) {
  $programFiles = Join-Path $work 'server-failing'
  Remove-Item -Recurse -Force $programFiles -ErrorAction SilentlyContinue
  Copy-Item -Recurse $Server $programFiles
  $settings = Join-Path $programFiles 'appsettings.json'
  $json = Get-Content $settings -Raw | ConvertFrom-Json
  $json.Tausi | Add-Member -NotePropertyName Test -NotePropertyValue ([pscustomobject]@{ FailMigration = $true }) -Force
  $json | ConvertTo-Json -Depth 20 | Set-Content $settings -Encoding utf8
}

# The setup screens and the SQL Server Express helper (.NET Framework 4.7.2).
dotnet build "$here\TausiSetup\TausiSetup.csproj" -c Release -p:Version=$Version -o "$work\setup"
if ($LASTEXITCODE -ne 0) { throw "TausiSetup build failed" }
dotnet build "$here\TausiSqlSetup\TausiSqlSetup.csproj" -c Release -p:Version=$Version -o "$work\setup"
if ($LASTEXITCODE -ne 0) { throw "TausiSqlSetup build failed" }
Sign @("$programFiles\TausiServer.exe", "$work\setup\TausiSetup.exe", "$work\setup\TausiSqlSetup.exe")

$ext = @('-ext', 'WixToolset.Util.wixext', '-ext', 'WixToolset.Firewall.wixext')
Push-Location $here
try {
  wix build -arch x64 -d "Version=$Version" @ext -bindpath "Server=$programFiles" Package.wxs -o "$work\TausiPOS-Server.msi"
  if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }
  Sign @("$work\TausiPOS-Server.msi")

  $bundle = Join-Path $Out "TausiPOS-Server-Setup-$Version.exe"
  wix build -arch x64 -d "Version=$Version" @ext -bindpath "Setup=$work\setup" -bindpath "Msi=$work" Bundle.wxs -o $bundle
  if ($LASTEXITCODE -ne 0) { throw "bundle build failed" }

  if ($CertificateFile) {
    # A bundle is signed in two steps: its engine, then the whole.
    $engine = Join-Path $work 'engine.exe'
    wix burn detach $bundle -engine $engine
    Sign @($engine)
    wix burn reattach $bundle -engine $engine -o $bundle
    Sign @($bundle)
  }
}
finally {
  Pop-Location
}
Write-Host "Built $bundle$(if (-not $CertificateFile) { ' (unsigned)' })"
