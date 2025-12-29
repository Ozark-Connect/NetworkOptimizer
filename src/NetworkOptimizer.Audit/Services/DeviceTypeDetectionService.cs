using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services.Detectors;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Services;

/// <summary>
/// Orchestrates multi-source device type detection for security auditing.
/// Uses hierarchical detection: Fingerprint > MAC OUI > Name patterns
/// </summary>
public class DeviceTypeDetectionService
{
    private readonly ILogger<DeviceTypeDetectionService>? _logger;
    private readonly FingerprintDetector _fingerprintDetector;
    private readonly MacOuiDetector _macOuiDetector;
    private readonly NamePatternDetector _namePatternDetector;

    public DeviceTypeDetectionService(
        ILogger<DeviceTypeDetectionService>? logger = null,
        UniFiFingerprintDatabase? fingerprintDb = null)
    {
        _logger = logger;
        _fingerprintDetector = new FingerprintDetector(fingerprintDb);
        _macOuiDetector = new MacOuiDetector();
        _namePatternDetector = new NamePatternDetector();
    }

    /// <summary>
    /// Detect device type from all available signals
    /// </summary>
    /// <param name="client">UniFi client response (optional - for fingerprint and MAC)</param>
    /// <param name="portName">Switch port name (optional)</param>
    /// <param name="deviceName">User-assigned device name (optional)</param>
    /// <returns>Best detection result</returns>
    public DeviceDetectionResult DetectDeviceType(
        UniFiClientResponse? client = null,
        string? portName = null,
        string? deviceName = null)
    {
        var results = new List<DeviceDetectionResult>();
        var mac = client?.Mac ?? "unknown";
        var displayName = client?.Name ?? client?.Hostname ?? portName ?? mac;

        _logger?.LogDebug("[Detection] Starting detection for '{DisplayName}' (MAC: {Mac})",
            displayName, mac);

        // Priority 1: UniFi Fingerprint (if client has fingerprint data)
        if (client != null && (client.DevCat.HasValue || client.DevIdOverride.HasValue))
        {
            var fpResult = _fingerprintDetector.Detect(client);
            if (fpResult.Category != ClientDeviceCategory.Unknown)
            {
                results.Add(fpResult);
                var isUserOverride = fpResult.Metadata?.ContainsKey("user_override") == true;
                object? inferredDeviceName = null;
                var inferredFromName = fpResult.Metadata?.TryGetValue("inferred_from_name", out inferredDeviceName) == true;

                if (isUserOverride && inferredFromName)
                {
                    _logger?.LogDebug("[Detection] Fingerprint: {Category} (user override, inferred from '{DeviceName}')",
                        fpResult.Category, inferredDeviceName);
                }
                else if (isUserOverride)
                {
                    _logger?.LogDebug("[Detection] Fingerprint: {Category} (user override, dev_id_override={DevIdOverride})",
                        fpResult.Category, client.DevIdOverride);
                }
                else
                {
                    // Check if there's an unmatched user override we need to add to our mapping
                    if (fpResult.Metadata?.TryGetValue("dev_id_override_unmatched", out var unmatchedOverride) == true)
                    {
                        _logger?.LogWarning("[Detection] Fingerprint: {Category} (dev_cat={DevCat}) - UNMATCHED dev_id_override={DevIdOverride} needs mapping!",
                            fpResult.Category, client.DevCat, unmatchedOverride);
                    }
                    else
                    {
                        _logger?.LogDebug("[Detection] Fingerprint: {Category} (dev_cat={DevCat}, dev_vendor={DevVendor})",
                            fpResult.Category, client.DevCat, client.DevVendor);
                    }
                }
            }
            else
            {
                _logger?.LogDebug("[Detection] Fingerprint: No match (dev_cat={DevCat}, dev_id_override={DevIdOverride})",
                    client.DevCat, client.DevIdOverride);
            }
        }
        else
        {
            _logger?.LogDebug("[Detection] Fingerprint: No fingerprint data available");
        }

        // Priority 2: UniFi OUI name (manufacturer from controller)
        if (!string.IsNullOrEmpty(client?.Oui))
        {
            var ouiNameResult = DetectFromUniFiOui(client.Oui);
            if (ouiNameResult.Category != ClientDeviceCategory.Unknown)
            {
                results.Add(ouiNameResult);
                _logger?.LogDebug("[Detection] UniFi OUI: {Category} from manufacturer '{Oui}'",
                    ouiNameResult.Category, client.Oui);
            }
            else
            {
                _logger?.LogDebug("[Detection] UniFi OUI: No match for manufacturer '{Oui}'", client.Oui);
            }
        }

        // Priority 3: MAC OUI lookup (our hardcoded database)
        if (!string.IsNullOrEmpty(client?.Mac))
        {
            var ouiResult = _macOuiDetector.Detect(client.Mac);
            if (ouiResult.Category != ClientDeviceCategory.Unknown)
            {
                results.Add(ouiResult);
                _logger?.LogDebug("[Detection] MAC OUI: {Category} ({Vendor}) for prefix {Prefix}",
                    ouiResult.Category, ouiResult.VendorName, client.Mac[..8]);
            }
            else
            {
                _logger?.LogDebug("[Detection] MAC OUI: No match for prefix {Prefix}", client.Mac[..Math.Min(8, client.Mac.Length)]);
            }
        }

        // Priority 4: Name pattern matching (device name, hostname, port name)
        var namesToCheck = new List<(string Name, bool IsPortName)>();

        // Client name/hostname
        if (!string.IsNullOrEmpty(client?.Name))
            namesToCheck.Add((client.Name, false));
        if (!string.IsNullOrEmpty(client?.Hostname) && client.Hostname != client?.Name)
            namesToCheck.Add((client.Hostname, false));

        // Explicit device name
        if (!string.IsNullOrEmpty(deviceName) && deviceName != client?.Name)
            namesToCheck.Add((deviceName, false));

        // Port name (slightly lower confidence)
        if (!string.IsNullOrEmpty(portName))
            namesToCheck.Add((portName, true));

        foreach (var (name, isPortName) in namesToCheck)
        {
            var nameResult = isPortName
                ? _namePatternDetector.DetectFromPortName(name)
                : _namePatternDetector.Detect(name);

            if (nameResult.Category != ClientDeviceCategory.Unknown)
            {
                results.Add(nameResult);
                _logger?.LogDebug("[Detection] Name pattern: {Category} from '{Name}' (isPort={IsPort})",
                    nameResult.Category, name, isPortName);
            }
        }

        // Return best result
        if (results.Count == 0)
        {
            _logger?.LogInformation("[Detection] '{DisplayName}' ({Mac}): No detection → Unknown",
                displayName, mac);
            return DeviceDetectionResult.Unknown;
        }

        // Sort by source priority (lower = better) then by confidence
        var best = results
            .OrderBy(r => (int)r.Source)
            .ThenByDescending(r => r.ConfidenceScore)
            .First();

        // If multiple sources agree, boost confidence
        if (results.Count > 1)
        {
            var agreementCount = results.Count(r => r.Category == best.Category);
            if (agreementCount > 1)
            {
                var boostedConfidence = Math.Min(100, best.ConfidenceScore + (agreementCount - 1) * 10);
                _logger?.LogDebug("[Detection] Multiple sources ({Count}) agree on {Category}, boosting confidence to {Confidence}%",
                    agreementCount, best.Category, boostedConfidence);

                var combinedResult = new DeviceDetectionResult
                {
                    Category = best.Category,
                    Source = DetectionSource.Combined,
                    ConfidenceScore = boostedConfidence,
                    VendorName = best.VendorName,
                    ProductName = best.ProductName,
                    RecommendedNetwork = best.RecommendedNetwork,
                    Metadata = new Dictionary<string, object>
                    {
                        ["agreement_count"] = agreementCount,
                        ["original_source"] = best.Source.ToString(),
                        ["all_sources"] = string.Join(", ", results.Select(r => r.Source.ToString()).Distinct())
                    }
                };

                _logger?.LogInformation("[Detection] '{DisplayName}' ({Mac}): {Sources} → {Category} ({Confidence}%, {Source})",
                    displayName, mac,
                    string.Join("+", results.Select(r => r.Source.ToString()).Distinct()),
                    combinedResult.Category, combinedResult.ConfidenceScore, combinedResult.Source);

                return combinedResult;
            }
        }

        _logger?.LogInformation("[Detection] '{DisplayName}' ({Mac}): {Source} → {Category} ({Confidence}%)",
            displayName, mac, best.Source, best.Category, best.ConfidenceScore);

        return best;
    }

    /// <summary>
    /// Detect device type from UniFi's resolved OUI manufacturer name
    /// </summary>
    private DeviceDetectionResult DetectFromUniFiOui(string ouiName)
    {
        var name = ouiName.ToLowerInvariant();

        // IoT / Smart Home manufacturers
        if (name.Contains("ikea")) return CreateOuiResult(ClientDeviceCategory.SmartHub, ouiName, 80);
        if (name.Contains("philips lighting") || name.Contains("signify")) return CreateOuiResult(ClientDeviceCategory.SmartLighting, ouiName, 85);
        if (name.Contains("lutron")) return CreateOuiResult(ClientDeviceCategory.SmartLighting, ouiName, 85);
        if (name.Contains("belkin")) return CreateOuiResult(ClientDeviceCategory.SmartPlug, ouiName, 75);
        if (name.Contains("tp-link") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartPlug, ouiName, 75);
        if (name.Contains("ecobee")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, 90);
        if (name.Contains("nest")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, 85);
        if (name.Contains("honeywell")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, 70);
        if (name.Contains("august") || name.Contains("yale") || name.Contains("schlage")) return CreateOuiResult(ClientDeviceCategory.SmartLock, ouiName, 85);
        if (name.Contains("sonos")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, 90);
        if (name.Contains("amazon") && !name.Contains("aws")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, 70);
        if (name.Contains("google") && !name.Contains("cloud")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, 70);
        if (name.Contains("irobot") || name.Contains("roborock") || name.Contains("ecovacs")) return CreateOuiResult(ClientDeviceCategory.RoboticVacuum, ouiName, 90);
        if (name.Contains("samsung") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartAppliance, ouiName, 70);
        if (name.Contains("lg") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartAppliance, ouiName, 70);

        // Security cameras
        if (name.Contains("ring")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 85);
        if (name.Contains("arlo")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 90);
        if (name.Contains("wyze")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 85);
        if (name.Contains("blink")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 85);
        if (name.Contains("reolink")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 90);
        if (name.Contains("hikvision") || name.Contains("dahua") || name.Contains("amcrest")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 90);
        if (name.Contains("eufy")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, 80);

        // Media/Entertainment
        if (name.Contains("roku")) return CreateOuiResult(ClientDeviceCategory.StreamingDevice, ouiName, 90);
        if (name.Contains("apple") && name.Contains("tv")) return CreateOuiResult(ClientDeviceCategory.StreamingDevice, ouiName, 90);

        return DeviceDetectionResult.Unknown;
    }

    private static DeviceDetectionResult CreateOuiResult(ClientDeviceCategory category, string vendor, int confidence)
    {
        return new DeviceDetectionResult
        {
            Category = category,
            Source = DetectionSource.MacOui, // Using MacOui as closest match
            ConfidenceScore = confidence,
            VendorName = vendor,
            RecommendedNetwork = FingerprintDetector.GetRecommendedNetwork(category),
            Metadata = new Dictionary<string, object>
            {
                ["detection_method"] = "unifi_oui_name",
                ["oui_name"] = vendor
            }
        };
    }

    /// <summary>
    /// Detect device type from just a port name (for audit rules)
    /// </summary>
    public DeviceDetectionResult DetectFromPortName(string portName)
    {
        return DetectDeviceType(portName: portName);
    }

    /// <summary>
    /// Detect device type from just a MAC address
    /// </summary>
    public DeviceDetectionResult DetectFromMac(string macAddress)
    {
        return _macOuiDetector.Detect(macAddress);
    }

    /// <summary>
    /// Check if a device category should be on an IoT VLAN
    /// </summary>
    public static bool ShouldBeOnIoTVlan(ClientDeviceCategory category)
    {
        return category.IsIoT();
    }

    /// <summary>
    /// Check if a device category should be on a Security VLAN
    /// </summary>
    public static bool ShouldBeOnSecurityVlan(ClientDeviceCategory category)
    {
        return category.IsSurveillance();
    }

    /// <summary>
    /// Check if a device category is network infrastructure (management VLAN)
    /// </summary>
    public static bool IsInfrastructure(ClientDeviceCategory category)
    {
        return category.IsInfrastructure();
    }

    /// <summary>
    /// Get recommended network purpose for a category
    /// </summary>
    public static NetworkPurpose GetRecommendedNetwork(ClientDeviceCategory category)
    {
        return FingerprintDetector.GetRecommendedNetwork(category);
    }
}
