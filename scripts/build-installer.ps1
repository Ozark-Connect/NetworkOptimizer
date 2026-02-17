# Build Network Optimizer Windows Installer
# Creates a self-contained MSI package

param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "$PSScriptRoot\..\publish"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$WebProject = Join-Path $RepoRoot "src\NetworkOptimizer.Web\NetworkOptimizer.Web.csproj"
$InstallerProject = Join-Path $RepoRoot "src\NetworkOptimizer.Installer\NetworkOptimizer.Installer.wixproj"
$PublishDir = Join-Path $RepoRoot "src\NetworkOptimizer.Web\bin\Release\net10.0\win-x64\publish"

# Get version from git tags (MinVer style)
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

Write-Host "=== Building Network Optimizer Windows Installer ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version: $Version"
Write-Host "Configuration: $Configuration"
Write-Host ""

# Step 1: Publish self-contained single-file application
Write-Host "[1/4] Publishing self-contained single-file application for win-x64..." -ForegroundColor Yellow
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
    -p:IncludeSourceRevisionInInformationalVersion=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

Write-Host "Published to: $PublishDir" -ForegroundColor Green
Write-Host ""

# Step 2: Build uwnspeedtest binary for gateway (linux/arm64)
Write-Host "[2/4] Building uwnspeedtest binary for gateway..." -ForegroundColor Yellow
$UwnSpeedTestSrc = Join-Path $RepoRoot "src\uwnspeedtest"
$ToolsDir = Join-Path $PublishDir "tools"

if (-not (Test-Path $ToolsDir)) { New-Item -ItemType Directory -Path $ToolsDir | Out-Null }

$GoCmd = Get-Command go -ErrorAction SilentlyContinue
if ($GoCmd) {
    Push-Location $UwnSpeedTestSrc
    $env:CGO_ENABLED = "0"
    $env:GOOS = "linux"
    $env:GOARCH = "arm64"
    go build -trimpath -ldflags "-s -w -X main.version=$Version" -o "$ToolsDir\uwnspeedtest-linux-arm64" .
    $env:CGO_ENABLED = $null
    $env:GOOS = $null
    $env:GOARCH = $null
    Pop-Location

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "uwnspeedtest build failed - gateway speed test will not be available in this installer"
    } else {
        Write-Host "Built uwnspeedtest for linux/arm64" -ForegroundColor Green
    }
} else {
    $PrebuiltBinary = Join-Path $UwnSpeedTestSrc "bin\uwnspeedtest-linux-arm64"
    if (Test-Path $PrebuiltBinary) {
        Copy-Item $PrebuiltBinary "$ToolsDir\uwnspeedtest-linux-arm64" -Force
        Write-Host "Copied pre-built uwnspeedtest binary" -ForegroundColor Green
    } else {
        Write-Warning "Go not installed and no pre-built binary found - gateway speed test will not be available"
    }
}
Write-Host ""

# Step 3: Build WiX installer
Write-Host "[3/4] Building MSI installer with WiX..." -ForegroundColor Yellow
dotnet build $InstallerProject -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX build failed!"
    exit 1
}

Write-Host ""

# Step 4: Copy to output
Write-Host "[4/4] Copying installer to publish folder..." -ForegroundColor Yellow

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
