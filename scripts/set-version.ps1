<#
  Puts one version (MAJOR.MINOR.PATCH, from the release tag) everywhere it is shown: every .NET assembly
  (and so /health), the web app (the admin site's About line), the terminal app and its installer.
    scripts/set-version.ps1 -Version 1.2.0
#>
param([Parameter(Mandatory)] [string] $Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be MAJOR.MINOR.PATCH, not $Version" }

$props = Join-Path $root 'server/Directory.Build.props'
(Get-Content $props -Raw) -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>" | Set-Content $props -NoNewline

foreach ($file in 'web/package.json', 'terminal/package.json', 'terminal/src-tauri/tauri.conf.json') {
  $path = Join-Path $root $file
  $text = Get-Content $path -Raw
  # Only the top-level "version" (the first one in the file).
  $text = ([regex]'"version":\s*"[^"]*"').Replace($text, "`"version`": `"$Version`"", 1)
  Set-Content $path $text -NoNewline
}

$cargo = Join-Path $root 'terminal/src-tauri/Cargo.toml'
$text = Get-Content $cargo -Raw
$text = ([regex]'(?m)^version = "[^"]*"').Replace($text, "version = `"$Version`"", 1)
Set-Content $cargo $text -NoNewline
Write-Host "Version $Version set."
