# diag (overlay latency experiment, 2026-10-26): a second, independent PresentMon session with display tracking on, to
# see the game's PresentMode with the overlay off and on, and whether PresentMon reports AlienControl.exe's own
# overlay presents (mode, MsUntilDisplayed). Read only: it starts nothing in the app and changes no setting.
# Run from an elevated PowerShell while the game is in the foreground:
#   .\capture-present-modes.ps1 -Game sp26-cod.exe -Label overlay-off
#   .\capture-present-modes.ps1 -Game sp26-cod.exe -Label overlay-on
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$Label = "run",
    [int]$Seconds = 30
)
$ErrorActionPreference = "Stop"
$pm = Join-Path $env:ProgramFiles "AlienControl\tools\PresentMon\PresentMon.exe"
if (-not (Test-Path $pm)) { $pm = Join-Path $PSScriptRoot "..\tools\PresentMon\PresentMon.exe" }
if (-not (Test-Path $pm)) { throw "PresentMon.exe not found (Program Files copy or tools\PresentMon next to the app)" }
$dir = Join-Path $env:APPDATA "AlienControl\diag"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$out = Join-Path $dir ("modes-{0}-{1}.csv" -f $Label, (Get-Date -Format "yyyyMMdd-HHmmss"))
Write-Host "Capturing $Seconds s of $Game and AlienControl.exe into $out"
& $pm --process_name $Game --process_name AlienControl.exe --qpc_time --session_name AlienControlsDiagModes `
      --output_file $out --timed $Seconds --terminate_after_timed --no_console_stats
Write-Host "Done: $out"
