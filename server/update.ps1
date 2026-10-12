# ============================================================
#  Fire Pit — build & deploy the web app to the Pi.
#  Run from a PowerShell prompt:   .\update.ps1
#  (You'll be asked for the Pi's password once, for sudo.)
# ============================================================
param([string]$Pi = $env:FIREPIT_PI)   # e.g. pi@firepit.local
$ErrorActionPreference = 'Stop'
if (-not $Pi) { throw 'Set FIREPIT_PI (for example pi@firepit.local) or pass -Pi user@host' }
Set-Location $PSScriptRoot

Write-Host '== Building (Release, linux-arm64) ==' -ForegroundColor Cyan
dotnet publish src -c Release -r linux-arm64 --self-contained false -o src/publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '== Copying to the Pi ==' -ForegroundColor Cyan
# Fresh staging dir each time (avoids scp nesting), then copy the build + apply script.
ssh $Pi 'rm -rf ~/firepit-update'
scp -r src/publish "${Pi}:~/firepit-update"
scp deploy/update-on-pi.sh "${Pi}:~/update-on-pi.sh"

Write-Host '== Applying on the Pi (enter the Pi password for sudo when prompted) ==' -ForegroundColor Cyan
ssh -t $Pi 'sudo bash ~/update-on-pi.sh'

Write-Host ''
Write-Host 'Done. Refresh the web UI on your phone (pull-to-refresh / hard reload if styling looks cached).' -ForegroundColor Green
