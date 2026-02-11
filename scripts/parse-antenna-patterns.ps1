<#
.SYNOPSIS
    Parses Ubiquiti .ant antenna pattern files from zip archives into a single JSON file.

.DESCRIPTION
    Extracts .ant files from all zip archives in the antenna-patterns directory,
    parses the gain values, and outputs a consolidated JSON file for the web app.

    Each .ant file contains 719 gain values:
    - 360 azimuth values (0-359 degrees)
    - 359 elevation values (0-358 degrees)

    Files are UTF-16LE encoded.

.PARAMETER InputDir
    Directory containing .zip files with .ant patterns.
    Default: research/wifi-optimizer/antenna-patterns/

.PARAMETER OutputFile
    Output JSON file path.
    Default: src/NetworkOptimizer.Web/wwwroot/data/antenna-patterns.json
#>

param(
    [string]$InputDir = (Join-Path $PSScriptRoot ".." "research" "wifi-optimizer" "antenna-patterns"),
    [string]$OutputFile = (Join-Path $PSScriptRoot ".." "src" "NetworkOptimizer.Web" "wwwroot" "data" "antenna-patterns.json")
)

$ErrorActionPreference = "Stop"

# Band name extraction from filename
function Get-BandFromFilename($filename) {
    if ($filename -match "2\.4GHz|2\.4 GHz|2_4GHz") { return "2.4" }
    if ($filename -match "5GHz|5 GHz|5_GHz") { return "5" }
    if ($filename -match "6GHz|6 GHz|6_GHz") { return "6" }
    return $null
}

# Parse a single .ant file
function Parse-AntFile($stream) {
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::Unicode)
    $values = @()

    while (-not $reader.EndOfStream) {
        $line = $reader.ReadLine().Trim()
        if ($line -ne "" -and $line -match "^-?\d") {
            $values += [float]$line
        }
    }
    $reader.Dispose()

    if ($values.Count -lt 719) {
        Write-Warning "  Expected 719 values, got $($values.Count)"
        return $null
    }

    return @{
        azimuth = $values[0..359]
        elevation = $values[360..718]
    }
}

Write-Host "Parsing antenna patterns from: $InputDir"
Write-Host "Output: $OutputFile"
Write-Host ""

$patterns = @{}
$zipFiles = Get-ChildItem -Path $InputDir -Filter "*.zip" | Sort-Object Name

Write-Host "Found $($zipFiles.Count) zip files"
Write-Host ""

foreach ($zip in $zipFiles) {
    $modelName = [System.IO.Path]::GetFileNameWithoutExtension($zip.Name)

    # Skip accessories and special variants (Omni/Panel antennas, narrow/wide angle)
    if ($modelName -match "Omni-Antenna|Panel-Antenna|Narrow-Angle|Wide-Angle") {
        Write-Host "  Skipping accessory pattern: $modelName"
        continue
    }

    Write-Host "Processing: $modelName"

    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)

        $antFiles = $archive.Entries | Where-Object { $_.Name -match "\.ant$" }

        if ($antFiles.Count -eq 0) {
            Write-Warning "  No .ant files found in $($zip.Name)"
            $archive.Dispose()
            continue
        }

        if (-not $patterns.ContainsKey($modelName)) {
            $patterns[$modelName] = @{}
        }

        foreach ($entry in $antFiles) {
            $band = Get-BandFromFilename $entry.Name
            if (-not $band) {
                Write-Warning "  Could not determine band for: $($entry.Name)"
                continue
            }

            Write-Host "  Band $band : $($entry.Name)"

            $entryStream = $entry.Open()
            $result = Parse-AntFile $entryStream
            $entryStream.Dispose()

            if ($result) {
                $patterns[$modelName][$band] = $result
            }
        }

        $archive.Dispose()
    }
    catch {
        Write-Warning "  Error processing $($zip.Name): $_"
    }
}

# Ensure output directory exists
$outputDir = [System.IO.Path]::GetDirectoryName($OutputFile)
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

# Write JSON
$json = $patterns | ConvertTo-Json -Depth 5 -Compress
[System.IO.File]::WriteAllText($OutputFile, $json)

$fileSize = (Get-Item $OutputFile).Length
Write-Host ""
Write-Host "Done! Wrote $($patterns.Count) models to $OutputFile ($([math]::Round($fileSize / 1024))KB)"
