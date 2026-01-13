# Start-SpeedTest.ps1
# Starts nginx for OpenSpeedTest
# Note: When running as Windows Service, NginxHostedService manages this automatically

param(
    [string]$InstallDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$nginxPath = Join-Path $InstallDir "nginx.exe"
$confPath = Join-Path $InstallDir "conf\nginx.conf"

if (-not (Test-Path $nginxPath)) {
    Write-Error "nginx.exe not found at $nginxPath"
    exit 1
}

Write-Host "Starting nginx for OpenSpeedTest..."
Start-Process -FilePath $nginxPath -ArgumentList "-c", $confPath -WorkingDirectory $InstallDir -NoNewWindow
Write-Host "nginx started on port 3005"
