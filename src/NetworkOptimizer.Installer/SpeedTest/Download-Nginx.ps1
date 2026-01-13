# Download nginx for Windows
# Run this during build to fetch nginx binaries

param(
    [string]$OutputDir = "$PSScriptRoot\nginx",
    [string]$Version = "1.26.2"  # Match Docker version for consistency
)

$ErrorActionPreference = "Stop"

$NginxZip = "nginx-$Version.zip"
$NginxUrl = "https://nginx.org/download/$NginxZip"
$TempFile = Join-Path $env:TEMP $NginxZip

Write-Host "Downloading nginx $Version for Windows..."

# Download nginx
if (-not (Test-Path $TempFile)) {
    Invoke-WebRequest -Uri $NginxUrl -OutFile $TempFile
    Write-Host "Downloaded to $TempFile"
}
else {
    Write-Host "Using cached download at $TempFile"
}

# Extract to output directory
if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}

Write-Host "Extracting to $OutputDir..."
Expand-Archive -Path $TempFile -DestinationPath $env:TEMP -Force

# Move extracted folder to output
$ExtractedDir = Join-Path $env:TEMP "nginx-$Version"
Move-Item -Path $ExtractedDir -Destination $OutputDir -Force

Write-Host "nginx $Version ready at $OutputDir"

# List contents
Get-ChildItem $OutputDir | ForEach-Object { Write-Host "  $_" }
