# Build the AP Agent binary for UniFi access points.
#
# Deliberately standalone. The AP Agent is NOT a release asset and is NOT harvested into the MSI:
# it is transferred to the AP over SSH into tmpfs on every boot, so build-installer.ps1 and the
# release pipeline are left untouched until the deployment service (W6) lands.
#
# Target is linux/arm/v7 only. Every measured U7-class AP is armv7l, and an arm64 build will not
# exec on them, so there is no arm64 target here.

param(
    [string]$OutputDir,
    [string]$Version
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$ApAgentSrc = Join-Path $RepoRoot "src\apagent"

if (-not $OutputDir) { $OutputDir = Join-Path $ApAgentSrc "bin" }

if (-not $Version) {
    Push-Location $RepoRoot
    try {
        $gitDescribe = git describe --tags --abbrev=0 2>$null
        if ($gitDescribe) { $Version = $gitDescribe -replace '^v', '' } else { $Version = "0.0.0" }
    } catch {
        $Version = "0.0.0"
    }
    Pop-Location
}

Write-Host "=== Building AP Agent ===" -ForegroundColor Cyan
Write-Host "Version: $Version"
Write-Host "Output: $OutputDir"
Write-Host ""

$GoCmd = Get-Command go -ErrorAction SilentlyContinue
if (-not $GoCmd) {
    Write-Error "Go is not installed - cannot build the AP Agent"
    exit 1
}

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

Push-Location $ApAgentSrc
try {
    $env:CGO_ENABLED = "0"
    $env:GOOS = "linux"
    $env:GOARCH = "arm"
    $env:GOARM = "7"

    # -s -w keeps the binary small, which matters because it is transferred on every AP boot.
    go build -trimpath -ldflags "-s -w -X main.version=$Version" -o "$OutputDir\apagent-linux-arm" .

    if ($LASTEXITCODE -ne 0) {
        Write-Error "apagent build failed for linux/arm/v7"
        exit 1
    }
} finally {
    $env:CGO_ENABLED = $null
    $env:GOOS = $null
    $env:GOARCH = $null
    $env:GOARM = $null
    Pop-Location
}

# The sh wrapper ships beside the binary: it turns a wrong-arch "Exec format error" into a
# readable refusal with exit 78.
Copy-Item (Join-Path $ApAgentSrc "apagent.sh") (Join-Path $OutputDir "apagent.sh") -Force

$binary = Get-Item (Join-Path $OutputDir "apagent-linux-arm")
Write-Host ("Built apagent for linux/arm/v7 ({0:N0} bytes)" -f $binary.Length) -ForegroundColor Green
Write-Host "Wrapper: $(Join-Path $OutputDir 'apagent.sh')" -ForegroundColor Green
