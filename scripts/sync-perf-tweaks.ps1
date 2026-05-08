# Syncs performance tweak scripts from the unifi-perf-tweaks source repo
# into the NetworkOptimizer embedded resources directory.
#
# Run before building to pick up the latest scripts:
#   pwsh scripts/sync-perf-tweaks.ps1
#
# Source repo: https://github.com/tvancott42/unifi-perf-tweaks (private)

param(
    [string]$SourceRepo = "$env:USERPROFILE\OneDrive\PersonalProjects\OpenSource\unifi-perf-tweaks"
)

$DestDir = Join-Path $PSScriptRoot "..\src\NetworkOptimizer.Web\Resources\PerfTweaks"

if (-not (Test-Path $SourceRepo)) {
    Write-Error "Source repo not found at: $SourceRepo"
    exit 1
}

if (-not (Test-Path $DestDir)) {
    New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
}

$scripts = @(
    "scripts/06-mongodb-ssd-offload.sh",
    "scripts/07-mongodb-ssd-backup.sh",
    "scripts/10-journald-volatile.sh",
    "scripts/15-fan-control-tuning.sh",
    "scripts/19-sfp-sgmiiplus-eth5.sh",
    "scripts/20-sfp-sgmiiplus.sh"
)

$binaries = @(
    "modules/force-uniphy1-sgmiiplus/force_uniphy1_sgmiiplus.ko",
    "modules/force-uniphy2-sgmiiplus/force_uniphy2_sgmiiplus.ko"
)

foreach ($file in $scripts + $binaries) {
    $src = Join-Path $SourceRepo $file
    $dest = Join-Path $DestDir (Split-Path $file -Leaf)
    if (Test-Path $src) {
        Copy-Item $src $dest -Force
        Write-Host "  Copied: $(Split-Path $file -Leaf)"
    } else {
        Write-Warning "  Missing: $file"
    }
}

Write-Host "Sync complete."
