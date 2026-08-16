# Live GUI test for dsh-launcher.exe (dev tool).
# Usage: powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\test-launcher-gui.ps1
# Starts the launcher on an isolated port with a temporary DSH_HOME,
# verifies readiness, single-instance guard, graceful close (WM_CLOSE) and
# process-tree cleanup. Requires a real desktop session.
param(
    [int]$Port = 3456,
    [string]$Launcher = 'dsh-launcher.exe'
)
$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $scriptDir
Set-Location $repo

$cfg = Join-Path $repo 'launcher-config.json'
Set-Content -Path $cfg -Value @"
{
  "port": $Port,
  "lan": false,
  "autoOpenBrowser": false,
  "closeAction": "stop"
}
"@ -Encoding UTF8

$testHome = Join-Path $env:TEMP ("dsh-launcher-test-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $testHome | Out-Null
$env:DSH_HOME = $testHome

$launcherPath = Join-Path $repo $Launcher
$p1 = Start-Process -FilePath $launcherPath -WorkingDirectory $repo -PassThru
"launcher pid = $($p1.Id)"

# 1) wait for the service port
$deadline = (Get-Date).AddSeconds(150)
$ready = $false
while ((Get-Date) -lt $deadline) {
    if (Test-NetConnection 127.0.0.1 -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue) { $ready = $true; break }
    if ($p1.HasExited) { "launcher exited early, code=$($p1.ExitCode)"; break }
    Start-Sleep -Milliseconds 800
}
"CHECK port $Port ready        = $ready"

# 2) single-instance guard: a second instance must exit without starting a service
$p2 = Start-Process -FilePath $launcherPath -ArgumentList "/port $($Port + 1)", "/nobrowser" -WorkingDirectory $repo -PassThru
Start-Sleep -Seconds 6
$secondExited = $p2.HasExited
$p2port = Test-NetConnection 127.0.0.1 -Port ($Port + 1) -InformationLevel Quiet -WarningAction SilentlyContinue
"CHECK 2nd instance exited    = $secondExited (should be True)"
"CHECK port $($Port + 1) busy = $p2port (should be False)"

# 3) graceful close via WM_CLOSE (taskkill without /F)
& "$env:WINDIR\System32\taskkill.exe" /PID $p1.Id 2>&1 | Out-Null
$exited = $false
$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    if ($p1.HasExited) { $exited = $true; break }
    Start-Sleep -Milliseconds 500
}
"CHECK launcher exited        = $exited (code=$($p1.ExitCode))"

# 4) process-tree cleanup
$portClosed = -not (Test-NetConnection 127.0.0.1 -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue)
"CHECK port $Port closed      = $portClosed"
$orphans = @(Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match ('--port ' + $Port) })
"CHECK orphan node procs      = $($orphans.Count) (should be 0)"
$orphans | ForEach-Object { "  orphan: pid=$($_.ProcessId) $($_.CommandLine)" }

# cleanup
Remove-Item $cfg -ErrorAction SilentlyContinue
Remove-Item $testHome -Recurse -Force -ErrorAction SilentlyContinue
"test done"
