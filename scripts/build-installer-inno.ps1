# Build Network Optimizer Windows Installer using Inno Setup
# Requires: Inno Setup 6.x (https://jrsoftware.org/isdl.php)

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$WebProject = Join-Path $RepoRoot "src\NetworkOptimizer.Web\NetworkOptimizer.Web.csproj"
$InstallerDir = Join-Path $RepoRoot "src\NetworkOptimizer.Installer"
$SetupScript = Join-Path $InstallerDir "setup.iss"
$OutputDir = Join-Path $RepoRoot "publish"

# Find Inno Setup compiler
$IsccPaths = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$Iscc = $null
foreach ($path in $IsccPaths) {
    if (Test-Path $path) {
        $Iscc = $path
        break
    }
}

if (-not $Iscc) {
    Write-Host "ERROR: Inno Setup 6 not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install Inno Setup 6 from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host "Download the stable release (innosetup-6.x.x.exe)" -ForegroundColor Yellow
    exit 1
}

# Get version from git tags
Push-Location $RepoRoot
try {
    $gitDescribe = git describe --tags --abbrev=0 2>$null
    if ($gitDescribe) {
        $Version = $gitDescribe -replace '^v', ''
    } else {
        $commitCount = git rev-list --count HEAD 2>$null
        $Version = "0.0.$commitCount"
    }
} catch {
    $Version = "0.0.0"
}
Pop-Location

Write-Host "=== Building Network Optimizer Windows Installer (Inno Setup) ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Version: $Version"
Write-Host "Configuration: $Configuration"
Write-Host "Inno Setup: $Iscc"
Write-Host ""

# Step 1: Publish self-contained application
Write-Host "[1/2] Publishing self-contained application for win-x64..." -ForegroundColor Yellow
dotnet publish $WebProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed!"
    exit 1
}

Write-Host "Published successfully" -ForegroundColor Green
Write-Host ""

# Step 2: Build installer with Inno Setup
Write-Host "[2/2] Building installer with Inno Setup..." -ForegroundColor Yellow

# Update version in setup.iss
$SetupContent = Get-Content $SetupScript -Raw
$SetupContent = $SetupContent -replace '#define MyAppVersion ".*"', "#define MyAppVersion `"$Version`""
Set-Content $SetupScript $SetupContent -NoNewline

# Run Inno Setup compiler
& $Iscc $SetupScript

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup build failed!"
    exit 1
}

Write-Host ""
Write-Host "=== Build Complete ===" -ForegroundColor Green

$OutputFile = Join-Path $OutputDir "NetworkOptimizer-$Version-win-x64-setup.exe"
if (Test-Path $OutputFile) {
    $SizeMB = [math]::Round((Get-Item $OutputFile).Length / 1MB, 2)
    Write-Host "Installer: $OutputFile"
    Write-Host "Size: $SizeMB MB"
} else {
    Write-Host "Output file: $OutputDir\NetworkOptimizer-*-setup.exe"
}
