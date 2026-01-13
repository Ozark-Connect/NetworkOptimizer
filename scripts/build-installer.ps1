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
Write-Host "[1/3] Publishing self-contained single-file application for win-x64..." -ForegroundColor Yellow
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

Write-Host "Published to: $PublishDir" -ForegroundColor Green
Write-Host ""

# Step 2: Build WiX installer
Write-Host "[2/3] Building MSI installer with WiX..." -ForegroundColor Yellow
dotnet build $InstallerProject -c $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Error "WiX build failed!"
    exit 1
}

Write-Host ""

# Step 3: Copy to output
Write-Host "[3/3] Copying installer to publish folder..." -ForegroundColor Yellow

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir | Out-Null
}

$InstallerBin = Join-Path $RepoRoot "src\NetworkOptimizer.Installer\bin\$Configuration"
$MsiFile = Get-ChildItem -Path $InstallerBin -Filter "*.msi" | Select-Object -First 1

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
