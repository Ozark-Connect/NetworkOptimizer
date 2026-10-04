# Build the AP Agent binary for UniFi access points.
#
# Deliberately standalone. The AP Agent is NOT a release asset and is NOT harvested into the MSI:
# it is transferred to the AP over SSH into tmpfs on every boot, so build-installer.ps1 and the
# release pipeline are left untouched until the deployment service (W6) lands.
#
# Targets are linux/arm/v7, linux/arm64, and 32-bit MIPS in both byte orders (soft-float: no FPU
# on those SoCs). U7-class APs are armv7l; arm64 is for UniFi OS gateways with Wi-Fi (UDR7, UX7,
# UCG-Industrial).

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
    $targets = @(
        @{ GOARCH = "arm";    GOARM = "7";   GOMIPS = $null;       Output = "apagent-linux-arm" },
        @{ GOARCH = "arm64";  GOARM = $null; GOMIPS = $null;       Output = "apagent-linux-arm64" },
        @{ GOARCH = "mipsle"; GOARM = $null; GOMIPS = "softfloat"; Output = "apagent-linux-mipsle" },
        @{ GOARCH = "mips";   GOARM = $null; GOMIPS = "softfloat"; Output = "apagent-linux-mips" }
    )

    foreach ($target in $targets) {
        $env:GOARCH = $target.GOARCH
        $env:GOARM = $target.GOARM
        $env:GOMIPS = $target.GOMIPS

        # -s -w keeps the binary small, which matters because it is transferred on every AP boot.
        go build -trimpath -ldflags "-s -w -X main.version=$Version" -o (Join-Path $OutputDir $target.Output) .

        if ($LASTEXITCODE -ne 0) {
            Write-Error "apagent build failed for linux/$($target.GOARCH)"
            exit 1
        }
    }
} finally {
    $env:CGO_ENABLED = $null
    $env:GOOS = $null
    $env:GOARCH = $null
    $env:GOARM = $null
    $env:GOMIPS = $null
    Pop-Location
}

# The sh wrapper ships beside the binary: it turns a wrong-arch "Exec format error" into a
# readable refusal with exit 78.
Copy-Item (Join-Path $ApAgentSrc "apagent.sh") (Join-Path $OutputDir "apagent.sh") -Force

foreach ($target in $targets) {
    $binary = Get-Item (Join-Path $OutputDir $target.Output)
    Write-Host ("Built {0} ({1:N0} bytes)" -f $binary.Name, $binary.Length) -ForegroundColor Green
}
Write-Host "Wrapper: $(Join-Path $OutputDir 'apagent.sh')" -ForegroundColor Green
