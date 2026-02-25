# Download Traefik for Windows
# Run this during build to fetch the Traefik binary

param(
    [string]$OutputDir = "$PSScriptRoot",
    [string]$Version = "3.3.3"
)

$ErrorActionPreference = "Stop"

$TraefikZip = "traefik_v${Version}_windows_amd64.zip"
$TraefikUrl = "https://github.com/traefik/traefik/releases/download/v${Version}/$TraefikZip"
$TempFile = Join-Path $env:TEMP $TraefikZip

Write-Host "Downloading Traefik v$Version for Windows..."

# Download Traefik
if (-not (Test-Path $TempFile)) {
    try {
        Invoke-WebRequest -Uri $TraefikUrl -OutFile $TempFile
        Write-Host "Downloaded to $TempFile"
    }
    catch {
        Write-Error "Failed to download Traefik from $TraefikUrl. Error: $_"
        exit 1
    }
}
else {
    Write-Host "Using cached download at $TempFile"
}

# Extract to temp directory
$ExtractPath = Join-Path $env:TEMP "traefik-extract"
if (Test-Path $ExtractPath) {
    Remove-Item -Recurse -Force $ExtractPath
}

Write-Host "Extracting..."
Expand-Archive -Path $TempFile -DestinationPath $ExtractPath -Force

# Find traefik.exe in the extracted contents
$TraefikExe = Get-ChildItem -Path $ExtractPath -Recurse -Filter "traefik.exe" | Select-Object -First 1

if (-not $TraefikExe) {
    Write-Error "traefik.exe not found in downloaded archive"
    exit 1
}

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# Copy traefik.exe to output
Copy-Item $TraefikExe.FullName -Destination $OutputDir -Force
Write-Host "Copied traefik.exe to $OutputDir"

# Cleanup
Remove-Item -Recurse -Force $ExtractPath

Write-Host "Traefik v$Version ready at $OutputDir"

# List contents
Get-ChildItem $OutputDir -Filter "traefik*" | ForEach-Object { Write-Host "  $_" }
