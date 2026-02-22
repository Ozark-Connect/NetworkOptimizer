<#
.SYNOPSIS
    Parses Ubiquiti .ant antenna pattern files from zip archives into a single JSON file.

.DESCRIPTION
    Extracts .ant files from all zip archives in the antenna-patterns directory,
    parses the gain values, and outputs a consolidated JSON file for the web app.

    Each .ant file contains 719+ gain values:
    - 360 azimuth values (0-359 degrees)
    - 359 elevation values (0-358 degrees)

    Files may be UTF-16LE or UTF-8 encoded (both are tried automatically).
    macOS resource fork files (._prefix) are skipped.

    Antenna variant files (e.g., U7-Outdoor-Omni-Antenna.zip) are stored under
    variant keys like "U7-Outdoor:omni". The base model uses the standard key.

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
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Band name extraction from filename
function Get-BandFromFilename($filename) {
    if ($filename -match "2\.4GHz|2\.4 GHz|2_4GHz|2\.45GHz") { return "2.4" }
    if ($filename -match "(?<!\d)2GHz|(?<!\d)2 GHz|(?<!\d)2G(?!Hz)\.ant") { return "2.4" }
    if ($filename -match "5GHz|5 GHz|5_GHz|(?<!\d)5G\.ant") { return "5" }
    if ($filename -match "6GHz|6 GHz|6_GHz") { return "6" }
    return $null
}

# Extract model name and variant from zip filename
# e.g., "U7-Outdoor-Omni-Antenna" -> ("U7-Outdoor", "omni")
#        "UACC-UK-Ultra-Panel-Antenna" -> ("UK-Ultra", "panel")
#        "U7-Pro" -> ("U7-Pro", $null)
function Get-ModelAndVariant($zipName) {
    # Variant patterns and their normalized names
    $variantPatterns = @(
        @{ Pattern = "-Omni-Antenna$"; Variant = "omni"; StripPrefix = "UACC-" },
        @{ Pattern = "-Panel-Antenna$"; Variant = "panel"; StripPrefix = "UACC-" },
        @{ Pattern = "-Narrow-Angle-High-Gain$"; Variant = "narrow" },
        @{ Pattern = "-Narrow-Angle$"; Variant = "narrow" },
        @{ Pattern = "-Wide-Angle-Low-Gain$"; Variant = "wide" },
        @{ Pattern = "-Wide-Angle$"; Variant = "wide" }
    )

    foreach ($vp in $variantPatterns) {
        if ($zipName -match $vp.Pattern) {
            $baseName = $zipName -replace $vp.Pattern, ""
            # Strip accessory prefix (UACC-) if present
            if ($vp.StripPrefix -and $baseName.StartsWith($vp.StripPrefix)) {
                $baseName = $baseName.Substring($vp.StripPrefix.Length)
            }
            return @{ Model = $baseName; Variant = $vp.Variant }
        }
    }

    return @{ Model = $zipName; Variant = $null }
}

# Try to parse gain values from raw content string
function Parse-GainValues($content) {
    $values = @()
    foreach ($line in $content.Split("`n")) {
        $trimmed = $line.Trim()
        if ($trimmed -ne "" -and $trimmed -match "^-?\d") {
            $values += [float]$trimmed
        }
    }
    return $values
}

# Parse a single .ant file, trying UTF-16LE first then UTF-8
function Parse-AntFile($entry) {
    # Try UTF-16LE first (newer files)
    $stream = $entry.Open()
    $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::Unicode)
    $content = $reader.ReadToEnd()
    $reader.Dispose()
    $stream.Dispose()

    $values = Parse-GainValues $content

    # Fall back to UTF-8 (older/macOS-created files)
    if ($values.Count -lt 719) {
        $stream = $entry.Open()
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
        $content = $reader.ReadToEnd()
        $reader.Dispose()
        $stream.Dispose()

        $values = Parse-GainValues $content
    }

    if ($values.Count -lt 719) {
        Write-Warning "  Expected 719+ values, got $($values.Count) in $($entry.Name)"
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
$zipFiles = Get-ChildItem -Path $InputDir -Filter "*.zip" -ErrorAction SilentlyContinue | Sort-Object Name

Write-Host "Found $($zipFiles.Count) zip files"
Write-Host ""

foreach ($zip in $zipFiles) {
    $rawName = [System.IO.Path]::GetFileNameWithoutExtension($zip.Name)
    $parsed = Get-ModelAndVariant $rawName

    # Build the key: "ModelName" for base, "ModelName:variant" for variants
    if ($parsed.Variant) {
        $patternKey = "$($parsed.Model):$($parsed.Variant)"
    } else {
        $patternKey = $parsed.Model
    }

    Write-Host "Processing: $rawName -> $patternKey"

    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)

        # Filter to .ant files, skip macOS resource forks (._prefix)
        $antFiles = $archive.Entries | Where-Object {
            $_.Name -match "\.(ant|amt)$" -and $_.Name -notmatch "^\._"
        }

        if ($antFiles.Count -eq 0) {
            Write-Warning "  No .ant files found in $($zip.Name)"
            $archive.Dispose()
            continue
        }

        if (-not $patterns.ContainsKey($patternKey)) {
            $patterns[$patternKey] = @{}
        }

        foreach ($entry in $antFiles) {
            $band = Get-BandFromFilename $entry.Name
            if (-not $band) {
                Write-Warning "  Could not determine band for: $($entry.Name)"
                continue
            }

            Write-Host "  Band $band : $($entry.Name)"

            $result = Parse-AntFile $entry

            if ($result) {
                $patterns[$patternKey][$band] = $result
            }
        }

        $archive.Dispose()
    }
    catch {
        Write-Warning "  Error processing $($zip.Name): $_"
    }
}

# Remove models with no band data (failed to parse anything)
$emptyModels = $patterns.Keys | Where-Object { $patterns[$_].Count -eq 0 }
foreach ($key in $emptyModels) {
    Write-Warning "Removing $key (no band data parsed)"
    $patterns.Remove($key)
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
$totalBands = ($patterns.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
$variantCount = ($patterns.Keys | Where-Object { $_ -match ":" }).Count
Write-Host ""
Write-Host "Done! Wrote $($patterns.Count) models ($variantCount with variants, $totalBands total band patterns) to $OutputFile ($([math]::Round($fileSize / 1024))KB)"
