<#
.SYNOPSIS
    Resets the Network Optimizer admin password on Windows.

.DESCRIPTION
    Stops the NetworkOptimizer service, clears the admin password from the
    SQLite database, restarts the service, and extracts the auto-generated
    temporary password from the log file.

.PARAMETER InstallDir
    Override the install directory. By default, auto-detected from the
    Windows service registration or defaults to
    "C:\Program Files\Ozark Connect\Network Optimizer".

.PARAMETER Force
    Skip the confirmation prompt.

.PARAMETER TimeoutSeconds
    How long to wait for the service to become healthy (default: 60).

.EXAMPLE
    .\reset-password.ps1
    .\reset-password.ps1 -Force
    .\reset-password.ps1 -InstallDir "D:\NetworkOptimizer"
#>

[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Force,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

# =============================================================================
# Require Administrator
# =============================================================================
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator." -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as administrator', then try again."
    exit 1
}

# =============================================================================
# Constants
# =============================================================================
$ServiceName = "NetworkOptimizer"
$DbFileName  = "network_optimizer.db"
$HealthUrl   = "http://localhost:8042/api/health"

# =============================================================================
# Auto-detect Install Directory
# =============================================================================
if (-not $InstallDir) {
    # Try 1: Get path from Windows service
    $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($svc -and $svc.PathName) {
        $exePath = $svc.PathName -replace '"', ''
        $InstallDir = Split-Path $exePath -Parent
    }

    # Try 2: Registry (WiX installer writes InstallFolder)
    if (-not $InstallDir) {
        $regPaths = @(
            "HKLM:\SOFTWARE\Ozark Connect\Network Optimizer",
            "HKLM:\SOFTWARE\WOW6432Node\Ozark Connect\Network Optimizer"
        )
        foreach ($rp in $regPaths) {
            if (Test-Path $rp) {
                $regVal = Get-ItemProperty $rp -Name "InstallFolder" -ErrorAction SilentlyContinue
                if ($regVal) { $InstallDir = $regVal.InstallFolder; break }
            }
        }
    }

    # Try 3: Default path
    if (-not $InstallDir) {
        $InstallDir = "C:\Program Files\Ozark Connect\Network Optimizer"
    }
}

Write-Host ""
Write-Host "Network Optimizer - Password Reset" -ForegroundColor Cyan
Write-Host "===================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Install directory: $InstallDir"

# =============================================================================
# Verify database exists
# =============================================================================
$dbPath = Join-Path $InstallDir "data\$DbFileName"
if (-not (Test-Path $dbPath)) {
    Write-Host "ERROR: Database not found at $dbPath" -ForegroundColor Red
    Write-Host "Use -InstallDir to specify the correct installation directory."
    exit 1
}

Write-Host "Database found:    $dbPath" -ForegroundColor Green

# =============================================================================
# Check for sqlite3
# =============================================================================
$sqlite3 = Get-Command sqlite3 -ErrorAction SilentlyContinue
if (-not $sqlite3) {
    # Check in the install directory (bundled with WiX installer)
    $bundled = Join-Path $InstallDir "sqlite3.exe"
    if (Test-Path $bundled) {
        $sqlite3Path = $bundled
    } else {
        Write-Host ""
        Write-Host "ERROR: sqlite3 not found in PATH or install directory." -ForegroundColor Red
        Write-Host ""
        Write-Host "Install it with:  winget install SQLite.SQLite" -ForegroundColor Yellow
        Write-Host "Then restart this terminal and try again."
        exit 1
    }
} else {
    $sqlite3Path = $sqlite3.Source
}

Write-Host "sqlite3:           $sqlite3Path" -ForegroundColor Green
Write-Host ""

# =============================================================================
# Confirm with user
# =============================================================================
if (-not $Force) {
    Write-Host "This will:" -ForegroundColor Yellow
    Write-Host "  1. Stop the NetworkOptimizer service"
    Write-Host "  2. Clear the admin password from the database"
    Write-Host "  3. Restart the service"
    Write-Host "  4. Display the new auto-generated temporary password"
    Write-Host ""
    $confirm = Read-Host "Continue? (y/N)"
    if ($confirm -notmatch '^[Yy]') {
        Write-Host "Cancelled."
        exit 0
    }
    Write-Host ""
}

# =============================================================================
# Stop the service
# =============================================================================
$svcObj = Get-Service $ServiceName -ErrorAction SilentlyContinue
if (-not $svcObj) {
    Write-Host "ERROR: Service '$ServiceName' not found." -ForegroundColor Red
    Write-Host "Is Network Optimizer installed as a Windows service?"
    exit 1
}

if ($svcObj.Status -eq 'Running') {
    Write-Host "Stopping service..." -NoNewline
    Stop-Service $ServiceName -Force
    $svcObj.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    Write-Host " done." -ForegroundColor Green
} else {
    Write-Host "Service is already stopped." -ForegroundColor Yellow
}

# =============================================================================
# Clear admin password
# =============================================================================
Write-Host "Clearing admin password..." -NoNewline
& $sqlite3Path $dbPath "UPDATE AdminSettings SET Password = NULL, Enabled = 0;"
if ($LASTEXITCODE -ne 0) {
    Write-Host " FAILED." -ForegroundColor Red
    Write-Host "sqlite3 returned exit code $LASTEXITCODE"
    exit 1
}
Write-Host " done." -ForegroundColor Green

# =============================================================================
# Start the service
# =============================================================================
Write-Host "Starting service..." -NoNewline
Start-Service $ServiceName
Write-Host " done." -ForegroundColor Green

# =============================================================================
# Wait for health endpoint
# =============================================================================
Write-Host "Waiting for application to start..." -NoNewline
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$healthy = $false

while ((Get-Date) -lt $deadline) {
    try {
        $resp = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        if ($resp.StatusCode -eq 200) {
            $healthy = $true
            break
        }
    } catch {
        # Not ready yet
    }
    Start-Sleep -Seconds 2
    Write-Host "." -NoNewline
}

if ($healthy) {
    Write-Host " ready!" -ForegroundColor Green
} else {
    Write-Host " timed out." -ForegroundColor Yellow
    Write-Host "The service may still be starting. Check the logs manually."
}

# =============================================================================
# Extract password from log
# =============================================================================
Write-Host ""
$logDir = Join-Path $InstallDir "logs"
$today = (Get-Date).ToString("yyyyMMdd")
$logFile = Join-Path $logDir "networkoptimizer-$today.log"

$password = $null
if (Test-Path $logFile) {
    # Find the last occurrence of the password line after AUTO-GENERATED banner
    $logContent = Get-Content $logFile -Tail 100
    for ($i = $logContent.Count - 1; $i -ge 0; $i--) {
        if ($logContent[$i] -match 'Password:\s+(\S+)') {
            $password = $Matches[1]
            break
        }
    }
}

if ($password) {
    Write-Host "===================================" -ForegroundColor Green
    Write-Host "  Password reset successful!" -ForegroundColor Green
    Write-Host "===================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Temporary password: $password" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Open http://localhost:8042 and log in with this password."
    Write-Host "  Go to Settings to set a permanent password."
    Write-Host ""
} else {
    Write-Host "Password reset completed, but could not extract the new password from logs." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Check the log file manually:"
    Write-Host "  $logFile" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Or look for the password in the Windows Event Viewer under Application logs."
    Write-Host "Search for 'AUTO-GENERATED' in the log output."
    Write-Host ""
}
