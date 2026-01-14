# Download iperf3 for Windows
# Run this during build to fetch iperf3 binaries

param(
    [string]$OutputDir = "$PSScriptRoot",
    [string]$Version = "3.20"  # Match Docker version for consistency
)

$ErrorActionPreference = "Stop"

# Use community Windows builds (ar51an provides well-maintained builds)
$Iperf3Zip = "iperf-$Version-win64.zip"
$Iperf3Url = "https://github.com/ar51an/iperf3-win-builds/releases/download/$Version/$Iperf3Zip"
$TempFile = Join-Path $env:TEMP $Iperf3Zip

Write-Host "Downloading iperf3 $Version for Windows..."

# Download iperf3
if (-not (Test-Path $TempFile)) {
    try {
        Invoke-WebRequest -Uri $Iperf3Url -OutFile $TempFile
        Write-Host "Downloaded to $TempFile"
    }
    catch {
        Write-Error "Failed to download iperf3 from $Iperf3Url. Error: $_"
        exit 1
    }
}
else {
    Write-Host "Using cached download at $TempFile"
}

# Extract to temp directory
$ExtractPath = Join-Path $env:TEMP "iperf3-extract"
if (Test-Path $ExtractPath) {
    Remove-Item -Recurse -Force $ExtractPath
}

Write-Host "Extracting..."
Expand-Archive -Path $TempFile -DestinationPath $ExtractPath -Force

# Find the iperf3.exe in the extracted contents
$Iperf3Exe = Get-ChildItem -Path $ExtractPath -Recurse -Filter "iperf3.exe" | Select-Object -First 1
$Cygwin1Dll = Get-ChildItem -Path $ExtractPath -Recurse -Filter "cygwin1.dll" | Select-Object -First 1

if (-not $Iperf3Exe) {
    Write-Error "iperf3.exe not found in downloaded archive"
    exit 1
}

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# Copy iperf3.exe and required DLLs
Copy-Item $Iperf3Exe.FullName -Destination $OutputDir -Force
Write-Host "Copied iperf3.exe to $OutputDir"

if ($Cygwin1Dll) {
    Copy-Item $Cygwin1Dll.FullName -Destination $OutputDir -Force
    Write-Host "Copied cygwin1.dll to $OutputDir"
}

# Also copy any other DLLs in the same directory as iperf3.exe
$Iperf3Dir = $Iperf3Exe.DirectoryName
Get-ChildItem -Path $Iperf3Dir -Filter "*.dll" | ForEach-Object {
    Copy-Item $_.FullName -Destination $OutputDir -Force
    Write-Host "Copied $($_.Name) to $OutputDir"
}

# Cleanup
Remove-Item -Recurse -Force $ExtractPath

Write-Host "iperf3 $Version ready at $OutputDir"

# List contents
Get-ChildItem $OutputDir | ForEach-Object { Write-Host "  $_" }
