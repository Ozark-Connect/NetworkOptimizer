# Download Traefik for Windows
# Run this during build to fetch the Traefik binary and config templates

param(
    [string]$OutputDir = "$PSScriptRoot",
    [string]$Version = "3.6.9"
)

$ErrorActionPreference = "Stop"

$TraefikZip = "traefik_v${Version}_windows_amd64.zip"
$TraefikUrl = "https://github.com/traefik/traefik/releases/download/v${Version}/$TraefikZip"
$TempFile = Join-Path $env:TEMP $TraefikZip

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

# The binary is version-pinned and 170 MB, so fetch it only when it is missing.
# The templates below are refreshed on every build instead - they track the
# companion repo and are the part that actually drifts.
$TraefikExePath = Join-Path $OutputDir "traefik.exe"
if (Test-Path $TraefikExePath) {
    Write-Host "traefik.exe already staged, skipping binary download"
}
else {
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

    # Copy traefik.exe to output
    Copy-Item $TraefikExe.FullName -Destination $OutputDir -Force
    Write-Host "Copied traefik.exe to $OutputDir"

    # Cleanup
    Remove-Item -Recurse -Force $ExtractPath

    Write-Host "Traefik v$Version ready at $OutputDir"
}

# Download config templates from NetworkOptimizer-Proxy repo
$TemplatesDir = Join-Path $OutputDir "templates"
if (-not (Test-Path $TemplatesDir)) {
    New-Item -ItemType Directory -Path $TemplatesDir | Out-Null
}

$BaseUrl = "https://raw.githubusercontent.com/Ozark-Connect/NetworkOptimizer-Proxy/main/windows"
$Templates = @("traefik.yml.template", "config.yml.template")

# Always re-fetch the templates. They previously downloaded only when absent, so
# the first MSI build on a machine froze them forever - a build box shipped
# five-month-old templates that way, missing the multi-site agent tunnel route
# the companion repo had since added. They are a few KB, so refreshing every
# build is free.
#
# Download to a temp file and move into place only on success, so a failed or
# partial fetch can never truncate a good staged template. If the fetch fails and
# a copy is already staged, keep it and warn: that keeps offline builds working.
# With no staged copy there is nothing to fall back to, so that stays fatal.
foreach ($Template in $Templates) {
    $DestPath = Join-Path $TemplatesDir $Template
    $TmpPath = "$DestPath.download"
    Write-Host "Downloading $Template..."
    try {
        Invoke-WebRequest -Uri "$BaseUrl/$Template" -OutFile $TmpPath
        Move-Item -Path $TmpPath -Destination $DestPath -Force
        Write-Host "  Saved to $DestPath"
    }
    catch {
        Remove-Item $TmpPath -Force -ErrorAction SilentlyContinue
        if (Test-Path $DestPath) {
            Write-Warning "Could not refresh $Template ($_). Using the staged copy at $DestPath."
        }
        else {
            Write-Error "Failed to download $Template from $BaseUrl/$Template. Error: $_"
            exit 1
        }
    }
}

# List contents
Get-ChildItem $OutputDir -Recurse -File | ForEach-Object { Write-Host "  $($_.FullName.Substring($OutputDir.Length + 1))" }
