# Build Network Optimizer Windows Installer
# Creates a self-contained MSI package

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\..\publish",
    # Stamps the build explicitly instead of deriving it from the nearest tag. Needed to rebuild an
    # already-released version without moving its tag, which would retrigger the release pipeline.
    [string]$Version
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$WebProject = Join-Path $RepoRoot "src\NetworkOptimizer.Web\NetworkOptimizer.Web.csproj"
$InstallerProject = Join-Path $RepoRoot "src\NetworkOptimizer.Installer\NetworkOptimizer.Installer.wixproj"
$PublishDir = Join-Path $RepoRoot "src\NetworkOptimizer.Web\bin\Release\net10.0\win-x64\publish"

# Get version from git tags (MinVer style) unless one was passed in
if (-not $Version) {
    Push-Location $RepoRoot
    try {
        $gitDescribe = git describe --tags --abbrev=0 2>$null
        if ($gitDescribe) {
            $Version = $gitDescribe -replace '^v', ''
        } else {
            # Fallback: count commits for version
            $commitCount = git rev-list --count HEAD 2>$null
            $Version = "0.0.$commitCount"
        }
    } catch {
        $Version = "0.0.0"
    }
    Pop-Location
}

Write-Host "=== Building Network Optimizer Windows Installer ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version: $Version"
Write-Host "Configuration: $Configuration"
Write-Host ""

# Step 1: Publish self-contained single-file application
Write-Host "[1/5] Publishing self-contained single-file application for win-x64..." -ForegroundColor Yellow

# Always start from an empty publish folder. Publishing incrementally over a warm
# tree - no compilable change since the last build, e.g. a docs-only release or a
# rebuild after a failed upload - recreates package content folders such as
# LatoFont EMPTY. WiX then harvests the empty folder and packages an MSI that is
# missing files, with no warning and a successful build. That silently cost the
# v2.5.3 MSI its 19 Lato font files. Removing the folder forces the publish target
# to repopulate it; the build output is untouched, so this costs a file copy
# rather than a recompile.
if (Test-Path $PublishDir) {
    Write-Host "  Cleaning previous publish output..." -ForegroundColor DarkGray
    Remove-Item -Recurse -Force $PublishDir
}

dotnet publish $WebProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:MinVerVersionOverride=$Version `
    -p:Version=$Version `
    -p:FileVersion=$Version `
    -p:AssemblyVersion=$Version `
    -p:IncludeSourceRevisionInInformationalVersion=false `
    /nodeReuse:false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

Write-Host "Published to: $PublishDir" -ForegroundColor Green
Write-Host ""

# Step 2: Build uwnspeedtest binaries
Write-Host "[2/5] Building uwnspeedtest binaries..." -ForegroundColor Yellow
$UwnSpeedTestSrc = Join-Path $RepoRoot "src\uwnspeedtest"
$ToolsDir = Join-Path $PublishDir "tools"

if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir | Out-Null }

$GoCmd = Get-Command go -ErrorAction SilentlyContinue
if ($GoCmd) {
    Push-Location $UwnSpeedTestSrc

    # Build targets: local Windows binary + gateway linux/arm64 binary
    $targets = @(
        @{ GOOS = "windows"; GOARCH = "amd64"; Output = "uwnspeedtest-windows-amd64.exe"; Label = "windows/amd64" },
        @{ GOOS = "windows"; GOARCH = "arm64"; Output = "uwnspeedtest-windows-arm64.exe"; Label = "windows/arm64" },
        @{ GOOS = "windows"; GOARCH = "386";   Output = "uwnspeedtest-windows-386.exe";   Label = "windows/386" },
        @{ GOOS = "linux";   GOARCH = "arm64"; Output = "uwnspeedtest-linux-arm64";       Label = "linux/arm64 (gateway)" }
    )

    foreach ($target in $targets) {
        $env:CGO_ENABLED = "0"
        $env:GOOS = $target.GOOS
        $env:GOARCH = $target.GOARCH
        go build -trimpath -ldflags "-s -w -X main.version=$Version" -o "$ToolsDir\$($target.Output)" .

        if ($LASTEXITCODE -ne 0) {
            Write-Warning "uwnspeedtest build failed for $($target.Label)"
        } else {
            Write-Host "Built uwnspeedtest for $($target.Label)" -ForegroundColor Green
        }
    }

    $env:CGO_ENABLED = $null
    $env:GOOS = $null
    $env:GOARCH = $null
    Pop-Location
} else {
    Write-Warning "Go not installed - uwnspeedtest binaries will not be available in this installer"
}
Write-Host ""

# Step 3: Build the Go binaries deployed onto UniFi devices over SSH
Write-Host "[3/5] Building wansteer and apagent binaries..." -ForegroundColor Yellow
$WanSteerSrc = Join-Path $RepoRoot "src\wansteer"

if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir | Out-Null }

if ($GoCmd) {
    Push-Location $WanSteerSrc

    $env:CGO_ENABLED = "0"
    $env:GOOS = "linux"
    $env:GOARCH = "arm64"
    go build -trimpath -ldflags "-s -w -X main.version=$Version" -o "$ToolsDir\wansteer-linux-arm64" .

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "wansteer build failed for linux/arm64"
    } else {
        Write-Host "Built wansteer for linux/arm64 (gateway)" -ForegroundColor Green
    }

    $env:CGO_ENABLED = $null
    $env:GOOS = $null
    $env:GOARCH = $null
    Pop-Location
} else {
    Write-Warning "Go not installed - wansteer binary will not be available in this installer"
}

# AP Agent, pushed into tmpfs on each access point. Every measured U7-class AP is armv7l and an
# arm64 build will not exec on them, so there is deliberately no arm64 target.
$ApAgentSrc = Join-Path $RepoRoot "src\apagent"

if ($GoCmd) {
    Push-Location $ApAgentSrc

    $env:CGO_ENABLED = "0"
    $env:GOOS = "linux"
    $env:GOARCH = "arm"
    $env:GOARM = "7"
    go build -trimpath -ldflags "-s -w -X main.version=$Version" -o "$ToolsDir\apagent-linux-arm" .

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "apagent build failed for linux/arm/v7"
    } else {
        Write-Host "Built apagent for linux/arm/v7 (access point)" -ForegroundColor Green
    }

    $env:CGO_ENABLED = $null
    $env:GOOS = $null
    $env:GOARCH = $null
    $env:GOARM = $null
    Pop-Location
} else {
    Write-Warning "Go not installed - apagent binary will not be available in this installer"
}
Write-Host ""

# Step 4: Build WiX installer
Write-Host "[4/5] Building MSI installer with WiX..." -ForegroundColor Yellow
dotnet build $InstallerProject -c $Configuration /nodeReuse:false

if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX build failed!"
    exit 1
}

Write-Host ""

# Step 5: Copy to output
Write-Host "[5/5] Copying installer to publish folder..." -ForegroundColor Yellow

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$InstallerBin = Join-Path $RepoRoot "src\NetworkOptimizer.Installer\bin\$Configuration"
$MsiFile = Get-ChildItem -Path $InstallerBin -Filter "*.msi" -Recurse | Select-Object -First 1

if ($MsiFile) {
    $OutputName = "NetworkOptimizer-$Version-win-x64.msi"
    $OutputPath = Join-Path $OutputDir $OutputName
    Copy-Item $MsiFile.FullName $OutputPath -Force

    $SizeMB = [math]::Round((Get-Item $OutputPath).Length / 1MB, 2)

    Write-Host ""
    Write-Host "=== Build Complete ===" -ForegroundColor Green
    Write-Host "Installer: $OutputPath"
    Write-Host "Size: $SizeMB MB"
}
else {
    Write-Error "MSI file not found in $InstallerBin"
    exit 1
}
