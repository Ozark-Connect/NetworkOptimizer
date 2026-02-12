using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Data;

/// <summary>
/// Loads and caches antenna pattern data from pre-parsed JSON file.
/// </summary>
public class AntennaPatternLoader
{
    private readonly ILogger<AntennaPatternLoader> _logger;
    private Dictionary<string, Dictionary<string, AntennaPattern>>? _patterns;
    private readonly object _loadLock = new();

    public AntennaPatternLoader(ILogger<AntennaPatternLoader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Maps antenna mode names from the UniFi API to pattern variant keys.
    /// API names like "OMNI" → pattern key suffix "omni".
    /// "Internal" and "Combined" use the base pattern (no variant).
    /// </summary>
    private static readonly Dictionary<string, string> AntennaModeToVariant = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OMNI"] = "omni",
        ["Panel"] = "panel",
        ["Narrow"] = "narrow",
        ["Wide"] = "wide",
    };

    /// <summary>
    /// Get the antenna pattern for a given model, band, and optional antenna mode.
    /// For outdoor APs with switchable modes, tries the variant pattern first (e.g., "U7-Outdoor:omni"),
    /// then falls back to the base pattern.
    /// Returns null if the pattern is not found.
    /// </summary>
    public AntennaPattern? GetPattern(string model, string band, string? antennaMode = null)
    {
        EnsureLoaded();

        if (_patterns == null) return null;

        // Strip color suffix (e.g., "-B" for black) that doesn't affect antenna pattern
        var patternName = model.EndsWith("-B", StringComparison.OrdinalIgnoreCase)
            ? model[..^2]
            : model;

        // Normalize band key
        var bandKey = band switch
        {
            "2.4" or "2.4GHz" or "2.4 GHz" => "2.4",
            "5" or "5GHz" or "5 GHz" => "5",
            "6" or "6GHz" or "6 GHz" => "6",
            _ => "5"
        };

        // Try variant pattern first if antenna mode is specified
        if (!string.IsNullOrEmpty(antennaMode) &&
            AntennaModeToVariant.TryGetValue(antennaMode, out var variant))
        {
            var variantKey = $"{patternName}:{variant}";
            if (_patterns.TryGetValue(variantKey, out var variantBands))
            {
                var variantPattern = variantBands.GetValueOrDefault(bandKey);
                if (variantPattern != null)
                    return variantPattern;
            }
        }

        // Fall back to base pattern
        if (!_patterns.TryGetValue(patternName, out var bands))
            return null;

        return bands.GetValueOrDefault(bandKey);
    }

    /// <summary>
    /// Get antenna gain at a specific azimuth angle for a model/band/mode.
    /// Returns 0 dBi if pattern not found.
    /// </summary>
    public float GetAzimuthGain(string model, string band, int azimuthDegrees, string? antennaMode = null)
    {
        var pattern = GetPattern(model, band, antennaMode);
        if (pattern?.Azimuth == null || pattern.Azimuth.Length == 0) return 0;

        var index = ((azimuthDegrees % 360) + 360) % 360;
        return index < pattern.Azimuth.Length ? pattern.Azimuth[index] : 0;
    }

    /// <summary>
    /// Get antenna gain at a specific elevation angle for a model/band/mode.
    /// Returns 0 dBi if pattern not found.
    /// </summary>
    public float GetElevationGain(string model, string band, int elevationDegrees, string? antennaMode = null)
    {
        var pattern = GetPattern(model, band, antennaMode);
        if (pattern?.Elevation == null || pattern.Elevation.Length == 0) return 0;

        var index = Math.Clamp(elevationDegrees, 0, pattern.Elevation.Length - 1);
        return pattern.Elevation[index];
    }

    private void EnsureLoaded()
    {
        if (_patterns != null) return;

        lock (_loadLock)
        {
            if (_patterns != null) return;

            try
            {
                var jsonPath = FindPatternFile();
                if (jsonPath == null)
                {
                    _logger.LogWarning("Antenna pattern file not found, heatmaps will use 0 dBi gain");
                    _patterns = new Dictionary<string, Dictionary<string, AntennaPattern>>();
                    return;
                }

                var json = File.ReadAllText(jsonPath);
                _patterns = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AntennaPattern>>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new Dictionary<string, Dictionary<string, AntennaPattern>>();

                _logger.LogInformation("Loaded antenna patterns for {Count} models from {Path}",
                    _patterns.Count, jsonPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load antenna patterns");
                _patterns = new Dictionary<string, Dictionary<string, AntennaPattern>>();
            }
        }
    }

    private static string? FindPatternFile()
    {
        // Look in wwwroot/data first (deployed), then relative paths for development
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "wwwroot", "data", "antenna-patterns.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data", "antenna-patterns.json"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
