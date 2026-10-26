<#
  The installer tests (CI on windows-latest, with SQL Server Express already installed as .\SQLEXPRESS):

    .\test-install.ps1 -First out\a\TausiPOS-Server-Setup-0.15.1.exe -Upgrade out\b\TausiPOS-Server-Setup-0.15.2.exe `
                       -Broken out\c\TausiPOS-Server-Setup-0.15.3.exe [-Terminal TausiPOS-Setup.exe]

  1. A silent install on the existing SQL Server: the service runs, /health is OK, the firewall rule
     exists, the setup token is written; the service stops cleanly and starts again.
  2. An upgrade: a backup is taken first, the database is migrated, the new version answers.
  3. An upgrade whose migration fails (a test build): the backup and the previous version come back.
  4. Uninstall: the service, program and firewall rule go; C:\ProgramData\Tausi POS stays.
  5. The terminal installer (silent, with SERVER=): Program Files\Tausi POS\Terminal, the Run key, server.txt.
#>
param(
  [Parameter(Mandatory)] [string] $First,
  [Parameter(Mandatory)] [string] $Upgrade,
  [Parameter(Mandatory)] [string] $Broken,
  [string] $Terminal,
  [string] $Logs = "$PSScriptRoot\..\..\out\install-logs"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Logs | Out-Null
$Logs = (Resolve-Path $Logs).Path
$home_ = 'C:\ProgramData\Tausi POS'
$program = 'C:\Program Files\Tausi POS\Server'
$failures = @()

function Check([bool] $ok, [string] $what) {
  if ($ok) { Write-Host "  ok   $what" -ForegroundColor Green }
  else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:failures += $what }
}

function Setup([string] $exe, [string] $log, [string[]] $more) {
  $args_ = @('/quiet', '/log', (Join-Path $Logs $log)) + $more
  $p = Start-Process -FilePath $exe -ArgumentList $args_ -PassThru
  # A hung installer fails the test after 15 minutes instead of holding the job for hours.
  if (-not $p.WaitForExit(15 * 60 * 1000)) {
    Write-Host "  (${log}: still running after 15 minutes; stopping it)" -ForegroundColor Red
    Get-Process | Where-Object { $_.ProcessName -match 'TausiPOS-Server-Setup|TausiSetup|msiexec|TausiServer' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-ChildItem "$env:TEMP\TausiSetup-error.txt", "$env:TEMP\Tausi_POS_Server_*.log" -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_ $Logs }
    return -1
  }
  return $p.ExitCode
}

function Health() {
  try { return Invoke-RestMethod -Uri 'http://127.0.0.1:5080/health' -TimeoutSec 10 } catch { return $null }
}

function WaitHealth([int] $seconds = 120) {
  $until = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $until) {
    $h = Health
    if ($h -and $h.database -eq 'ok') { return $h }
    Start-Sleep 3
  }
  return $null
}

Write-Host '1. Silent install on the existing SQL Server Express'
$code = Setup $First 'install.log' @('DB=existing', 'SQLINSTANCE=.\SQLEXPRESS', 'SQLGRANTSYSTEM=1', 'PORT=5080')
Check ($code -eq 0) "the installer finished (exit $code)"
$service = Get-Service TausiPOSServer -ErrorAction SilentlyContinue
Check ($service -and $service.Status -eq 'Running') 'the service runs'
$config = Get-CimInstance Win32_Service -Filter "Name='TausiPOSServer'"
Check ($config.StartName -eq 'NT SERVICE\TausiPOSServer') "it runs as its own account ($($config.StartName))"
Check ((sc.exe qc TausiPOSServer | Out-String) -match 'DELAYED') 'automatic (delayed start)'
Check ((sc.exe qfailure TausiPOSServer | Out-String) -match 'RESTART -- Delay = 60000') 'restarts after 1 minute on failure'
$h = WaitHealth
Check ($null -ne $h) '/health answers with the database connected'
$firstVersion = $h.version
Check ($null -ne (Get-NetFirewallRule -DisplayName 'Tausi POS Server' -ErrorAction SilentlyContinue)) 'the firewall rule exists'
Check (Test-Path "$home_\setup-token.txt") 'the setup token is written'
Check (Test-Path "$home_\config\appsettings.Production.json") 'the settings file is written'
Check (-not ((Get-Acl "$home_\data").Access | Where-Object { $_.IdentityReference -match 'Users' })) 'ordinary users can''t read the data folder'
try { Get-EventLog -LogName Application -Source 'Tausi POS' -Newest 1 -ErrorAction Stop | Out-Null; $source = $true } catch { $source = -not ($_.Exception.Message -match 'source') }
Check $source 'the Event Log source exists'

Write-Host '   The service stops cleanly and starts again'
Stop-Service TausiPOSServer
Check ((Get-Service TausiPOSServer).Status -eq 'Stopped') 'stopped'
Start-Service TausiPOSServer
Check ($null -ne (WaitHealth)) 'started again'

Write-Host '2. Upgrade: a backup first, then the new version'
$before = @(Get-ChildItem "$home_\backups\*.tausibak" -ErrorAction SilentlyContinue).Count
$code = Setup $Upgrade 'upgrade.log' @()
Check ($code -eq 0) "the upgrade finished (exit $code)"
$h = WaitHealth
Check ($h -and $h.version -ne $firstVersion) "the new version answers ($($h.version))"
$upgradeBackups = @(Get-ChildItem "$home_\backups\*-upgrade.tausibak" -ErrorAction SilentlyContinue)
Check ($upgradeBackups.Count -ge 1 -and @(Get-ChildItem "$home_\backups\*.tausibak").Count -gt $before) 'a backup was taken before upgrading'
$upgraded = $h.version

Write-Host '3. An upgrade whose migration fails puts the previous version back'
$code = Setup $Broken 'broken.log' @()
Check ($code -ne 0) "the broken upgrade reported failure (exit $code)"
$h = WaitHealth 180
Check ($h -and $h.version -eq $upgraded) "the previous version runs again ($($h.version))"
Check ((Get-ItemProperty 'HKLM:\SOFTWARE\NexaGen Solutions\Tausi POS Server').Version -eq $upgraded) 'the previous program is back'

Write-Host '4. Uninstall keeps the data'
$code = Setup $Upgrade 'uninstall.log' @('/uninstall')
Check ($code -eq 0) "uninstalled (exit $code)"
Check ($null -eq (Get-Service TausiPOSServer -ErrorAction SilentlyContinue)) 'the service is gone'
Check (-not (Test-Path "$program\TausiServer.exe")) 'the program is gone'
Check ($null -eq (Get-NetFirewallRule -DisplayName 'Tausi POS Server' -ErrorAction SilentlyContinue)) 'the firewall rule is gone'
Check (Test-Path "$home_\config\appsettings.Production.json") 'the settings are kept'
Check (@(Get-ChildItem "$home_\backups\*.tausibak").Count -ge 1) 'the backups are kept'

if ($Terminal) {
  Write-Host '5. The terminal installer'
  $p = Start-Process -FilePath $Terminal -ArgumentList @('/S', 'SERVER=http://192.168.1.10:5080') -Wait -PassThru
  Check ($p.ExitCode -eq 0) "installed silently (exit $($p.ExitCode))"
  $exe = Get-ChildItem 'C:\Program Files\Tausi POS\Terminal\*.exe' -ErrorAction SilentlyContinue | Where-Object Name -ne 'uninstall.exe'
  Check ($null -ne $exe) 'in C:\Program Files\Tausi POS\Terminal'
  $run = (Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).'Tausi POS'
  Check ($run -like '*Tausi POS\Terminal*') 'starts for every user at sign-in'
  Check ([string](Get-Content 'C:\ProgramData\Tausi POS\Terminal\server.txt' -Raw -ErrorAction SilentlyContinue) -match '192.168.1.10:5080') 'the server address is pre-filled'
}

if ($failures.Count -gt 0) {
  Write-Host "`n$($failures.Count) check(s) failed:" -ForegroundColor Red
  $failures | ForEach-Object { Write-Host "  - $_" }
  Get-ChildItem "$home_\logs\*.log" -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_ $Logs }
  Get-ChildItem "$env:TEMP\TausiSetup-error.txt", "$env:TEMP\Tausi_POS_Server_*.log", "C:\Windows\Temp\tausi-crash.log", "C:\Windows\SystemTemp\tausi-crash.log" -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_ $Logs }
  exit 1
}
Write-Host "`nAll installer checks passed." -ForegroundColor Green
