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
    // Confidence score constants for detection results
    private const int MaxConfidence = 100;
    private const int MultiSourceAgreementBoost = 10;

    // Name override confidence (highest - explicit name match)
    private const int NameOverrideConfidence = 95;
    private const int VendorDefaultConfidence = 85;
    private const int AppleWatchConfidence = 90;

    // OUI-based confidence levels
    private const int OuiHighConfidence = 90;      // Dedicated IoT vendors (ecobee, sonos, arlo)
    private const int OuiMediumConfidence = 85;    // Strong signal vendors (philips, ring)
    private const int OuiStandardConfidence = 80;  // General IoT vendors
    private const int OuiLowerConfidence = 75;     // Multi-purpose vendors (belkin, tp-link)
    private const int OuiLowestConfidence = 70;    // Broad vendors (amazon, google, honeywell)

    private readonly ILogger<DeviceTypeDetectionService>? _logger;
    private readonly FingerprintDetector _fingerprintDetector;
    private readonly MacOuiDetector _macOuiDetector;
    private readonly NamePatternDetector _namePatternDetector;

    // Client history lookup for enhanced offline device detection
    private Dictionary<string, UniFiClientHistoryResponse>? _clientHistoryByMac;

    // UniFi Protect camera MACs (highest priority detection)
    private HashSet<string>? _protectCameraMacs;

    public DeviceTypeDetectionService(
        ILogger<DeviceTypeDetectionService>? logger = null,
        UniFiFingerprintDatabase? fingerprintDb = null,
        IeeeOuiDatabase? ieeeOuiDb = null)
    {
        _logger = logger;
        _fingerprintDetector = new FingerprintDetector(fingerprintDb);
        _macOuiDetector = ieeeOuiDb != null ? new MacOuiDetector(ieeeOuiDb) : new MacOuiDetector();
        _namePatternDetector = new NamePatternDetector();
    }

    /// <summary>
    /// Set client history for enhanced offline device detection.
    /// When detecting devices by MAC, we'll first check if the MAC exists in client history
    /// to get fingerprint data, then fall back to IEEE OUI lookup.
    /// </summary>
    public void SetClientHistory(List<UniFiClientHistoryResponse>? clientHistory)
    {
        if (clientHistory == null || clientHistory.Count == 0)
        {
            _clientHistoryByMac = null;
            return;
        }

        _clientHistoryByMac = clientHistory
            .Where(c => !string.IsNullOrEmpty(c.Mac))
            .ToDictionary(c => c.Mac!.ToLowerInvariant(), c => c, StringComparer.OrdinalIgnoreCase);

        _logger?.LogInformation("Loaded {Count} client history entries for offline device detection", _clientHistoryByMac.Count);
    }

    /// <summary>
    /// Set known UniFi Protect camera MAC addresses.
    /// These are detected with 100% confidence as cameras, bypassing all other detection methods.
    /// </summary>
    public void SetProtectCameraMacs(HashSet<string>? protectCameraMacs)
    {
        _protectCameraMacs = protectCameraMacs;
        if (protectCameraMacs != null && protectCameraMacs.Count > 0)
        {
            _logger?.LogInformation("Loaded {Count} UniFi Protect camera MACs for priority detection", protectCameraMacs.Count);
        }
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

        // Priority -1: UniFi Protect camera (100% confidence from controller API)
        if (_protectCameraMacs != null && !string.IsNullOrEmpty(client?.Mac) &&
            _protectCameraMacs.Contains(client.Mac.ToLowerInvariant()))
        {
            _logger?.LogDebug("[Detection] '{DisplayName}': UniFi Protect camera (confirmed by controller)",
                displayName);
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Camera,
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 100,
                VendorName = "Ubiquiti",
                ProductName = "UniFi Protect Camera",
                RecommendedNetwork = NetworkPurpose.Security,
                Metadata = new Dictionary<string, object>
                {
                    ["detection_method"] = "unifi_protect_api",
                    ["mac"] = client.Mac
                }
            };
        }

        // Priority 0: Check for obvious name keywords that should OVERRIDE fingerprint
        // This handles cases where vendor fingerprint is wrong (e.g., Cync plugs detected as cameras)
        var obviousNameResult = CheckObviousNameOverride(client?.Name, client?.Hostname);
        if (obviousNameResult != null)
        {
            _logger?.LogDebug("[Detection] '{DisplayName}': Name override → {Category} (name clearly indicates device type)",
                displayName, obviousNameResult.Category);
            return obviousNameResult;
        }

        // Priority 0.5: Check OUI for vendors that need special handling
        // - Cync/Wyze/GE have camera fingerprints but most devices are actually plugs/bulbs
        // - Apple with SmartSensor fingerprint is likely Apple Watch
        var vendorOverrideResult = CheckVendorDefaultOverride(client?.Oui, client?.Name, client?.Hostname, client?.DevCat);
        if (vendorOverrideResult != null)
        {
            _logger?.LogDebug("[Detection] '{DisplayName}': Vendor override → {Category} (vendor defaults to plug unless camera indicated)",
                displayName, vendorOverrideResult.Category);
            return vendorOverrideResult;
        }

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
        if (!string.IsNullOrEmpty(client?.Hostname) && client!.Hostname != client.Name)
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
            _logger?.LogDebug("[Detection] '{DisplayName}' ({Mac}): No detection → Unknown",
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
                var boostedConfidence = Math.Min(MaxConfidence, best.ConfidenceScore + (agreementCount - 1) * MultiSourceAgreementBoost);
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

                _logger?.LogDebug("[Detection] '{DisplayName}' ({Mac}): {Sources} → {Category} ({Confidence}%, {Source})",
                    displayName, mac,
                    string.Join("+", results.Select(r => r.Source.ToString()).Distinct()),
                    combinedResult.Category, combinedResult.ConfidenceScore, combinedResult.Source);

                return combinedResult;
            }
        }

        _logger?.LogDebug("[Detection] '{DisplayName}' ({Mac}): {Source} → {Category} ({Confidence}%)",
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
        if (name.Contains("ikea")) return CreateOuiResult(ClientDeviceCategory.SmartHub, ouiName, OuiStandardConfidence);
        if (name.Contains("philips lighting") || name.Contains("signify")) return CreateOuiResult(ClientDeviceCategory.SmartLighting, ouiName, OuiMediumConfidence);
        if (name.Contains("lutron")) return CreateOuiResult(ClientDeviceCategory.SmartLighting, ouiName, OuiMediumConfidence);
        if (name.Contains("belkin")) return CreateOuiResult(ClientDeviceCategory.SmartPlug, ouiName, OuiLowerConfidence);
        if (name.Contains("tp-link") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartPlug, ouiName, OuiLowerConfidence);
        if (name.Contains("ecobee")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, OuiHighConfidence);
        if (name.Contains("nest")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, OuiMediumConfidence);
        if (name.Contains("honeywell")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, OuiLowestConfidence);
        if (name.Contains("august") || name.Contains("yale") || name.Contains("schlage")) return CreateOuiResult(ClientDeviceCategory.SmartLock, ouiName, OuiMediumConfidence);
        if (name.Contains("sonos")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiHighConfidence);
        if (name.Contains("amazon") && !name.Contains("aws")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiLowestConfidence);
        if (name.Contains("google") && !name.Contains("cloud")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiLowestConfidence);
        if (name.Contains("irobot") || name.Contains("roborock") || name.Contains("ecovacs")) return CreateOuiResult(ClientDeviceCategory.RoboticVacuum, ouiName, OuiHighConfidence);
        if (name.Contains("samsung") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartAppliance, ouiName, OuiLowestConfidence);
        if (name.Contains("lg") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartAppliance, ouiName, OuiLowestConfidence);

        // Security cameras (note: Wyze/Cync handled earlier in CheckVendorDefaultOverride)
        if (name.Contains("ring")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, OuiMediumConfidence);
        if (name.Contains("arlo")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, OuiHighConfidence);
        if (name.Contains("blink")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, OuiMediumConfidence);
        if (name.Contains("reolink")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, OuiHighConfidence);
        if (name.Contains("hikvision") || name.Contains("dahua") || name.Contains("amcrest")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, OuiHighConfidence);
        if (name.Contains("eufy")) return CreateOuiResult(ClientDeviceCategory.Camera, ouiName, OuiStandardConfidence);

        // Media/Entertainment
        if (name.Contains("roku")) return CreateOuiResult(ClientDeviceCategory.StreamingDevice, ouiName, OuiHighConfidence);
        if (name.Contains("apple") && name.Contains("tv")) return CreateOuiResult(ClientDeviceCategory.StreamingDevice, ouiName, OuiHighConfidence);

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
    /// Check for obvious name keywords that should override fingerprint detection.
    /// This catches cases where the vendor fingerprint is wrong (e.g., Cync plugs detected as cameras).
    /// Only returns a result for VERY obvious cases where we're confident.
    /// </summary>
    private DeviceDetectionResult? CheckObviousNameOverride(string? name, string? hostname)
    {
        var checkName = name ?? hostname;
        if (string.IsNullOrEmpty(checkName))
            return null;

        var nameLower = checkName.ToLowerInvariant();

        // Obvious plug/outlet keywords - NOT a camera
        if (nameLower.Contains("plug") || nameLower.Contains("outlet") || nameLower.Contains("power strip"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartPlug,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Name contains 'plug/outlet' - overrides vendor fingerprint",
                    ["matched_name"] = checkName
                }
            };
        }

        // WYZE devices default to SmartPlug unless name indicates camera
        // (WYZE plugs often have camera fingerprint from vendor)
        if (nameLower.Contains("wyze") && !IsCameraName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartPlug,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = VendorDefaultConfidence,
                VendorName = "WYZE",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "WYZE defaults to SmartPlug unless name indicates camera",
                    ["matched_name"] = checkName
                }
            };
        }

        // Obvious light/bulb keywords - NOT a camera
        if (nameLower.Contains("bulb") || nameLower.Contains("lamp") || nameLower.Contains("light strip"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartLighting,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Name contains 'bulb/lamp' - overrides vendor fingerprint",
                    ["matched_name"] = checkName
                }
            };
        }

        // Apple Watch is a wearable/smartphone, not an IoT sensor
        if (nameLower.Contains("apple watch") || (nameLower.Contains("watch") && nameLower.Contains("apple")))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Smartphone,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Apple",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Apple Watch is a wearable, not IoT sensor",
                    ["matched_name"] = checkName
                }
            };
        }

        return null;
    }

    /// <summary>
    /// Check if a name indicates a camera device
    /// </summary>
    private static bool IsCameraName(string nameLower)
    {
        return nameLower.Contains("cam") ||
               nameLower.Contains("camera") ||
               nameLower.Contains("doorbell") ||
               nameLower.Contains("video") ||
               nameLower.Contains("security") ||
               nameLower.Contains("nvr") ||
               nameLower.Contains("ptz");
    }

    /// <summary>
    /// Check if vendor OUI indicates a device that needs special handling.
    /// - Cync, Wyze, and GE devices have camera fingerprints but are usually plugs/bulbs.
    /// - Apple devices with SmartSensor fingerprint are usually Apple Watches (Smartphone).
    /// </summary>
    private DeviceDetectionResult? CheckVendorDefaultOverride(string? oui, string? name, string? hostname, int? devCat)
    {
        if (string.IsNullOrEmpty(oui))
            return null;

        var ouiLower = oui.ToLowerInvariant();
        var nameLower = (name ?? hostname ?? "").ToLowerInvariant();

        // Apple devices with SmartSensor fingerprint (DevCat=14) are likely Apple Watches
        if (ouiLower.Contains("apple") && devCat == 14)
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Smartphone,
                Source = DetectionSource.MacOui,
                ConfidenceScore = AppleWatchConfidence,
                VendorName = "Apple",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Apple device with SmartSensor fingerprint is likely Apple Watch",
                    ["oui"] = oui,
                    ["dev_cat"] = devCat
                }
            };
        }

        // Check for vendors that default to SmartPlug
        var isPlugVendor = ouiLower.Contains("cync") ||
                           ouiLower.Contains("wyze") ||
                           ouiLower.Contains("savant") ||  // Cync parent company
                           (ouiLower.Contains("ge") && ouiLower.Contains("lighting"));

        if (!isPlugVendor)
            return null;

        // If name indicates camera, let fingerprint handle it
        if (IsCameraName(nameLower))
            return null;

        // Default these vendors to SmartPlug
        return new DeviceDetectionResult
        {
            Category = ClientDeviceCategory.SmartPlug,
            Source = DetectionSource.MacOui,
            ConfidenceScore = VendorDefaultConfidence,
            VendorName = oui,
            RecommendedNetwork = NetworkPurpose.IoT,
            Metadata = new Dictionary<string, object>
            {
                ["override_reason"] = $"Vendor '{oui}' defaults to SmartPlug unless name indicates camera",
                ["oui"] = oui
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
    /// Detect device type from just a MAC address.
    /// First checks client history for fingerprint data, then falls back to IEEE OUI lookup.
    /// </summary>
    public DeviceDetectionResult DetectFromMac(string macAddress)
    {
        if (string.IsNullOrEmpty(macAddress))
            return DeviceDetectionResult.Unknown;

        // First, check if we have this MAC in client history (for fingerprint data)
        if (_clientHistoryByMac != null &&
            _clientHistoryByMac.TryGetValue(macAddress.ToLowerInvariant(), out var historyClient))
        {
            _logger?.LogDebug("[Detection] Found MAC {Mac} in client history: {Name}",
                macAddress, historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname);

            // Try fingerprint detection first
            if (historyClient.Fingerprint != null)
            {
                // Create a pseudo-client with the fingerprint data to use the existing detector
                var pseudoClient = new UniFiClientResponse
                {
                    Mac = historyClient.Mac,
                    Name = historyClient.Name ?? string.Empty,
                    Hostname = historyClient.Hostname ?? string.Empty,
                    DevIdOverride = historyClient.Fingerprint.DevIdOverride,
                    DevCat = historyClient.Fingerprint.DevCat,
                    DevFamily = historyClient.Fingerprint.DevFamily,
                    DevVendor = historyClient.Fingerprint.DevVendor
                };

                var fpResult = _fingerprintDetector.Detect(pseudoClient);
                if (fpResult.Category != ClientDeviceCategory.Unknown)
                {
                    _logger?.LogDebug("[Detection] Client history fingerprint detected: {Category} ({Confidence}%)",
                        fpResult.CategoryName, fpResult.ConfidenceScore);
                    return fpResult;
                }
            }

            // Try name-based detection from history
            var displayName = historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname;
            if (!string.IsNullOrEmpty(displayName))
            {
                var nameResult = _namePatternDetector.Detect(displayName);
                if (nameResult.Category != ClientDeviceCategory.Unknown)
                {
                    _logger?.LogDebug("[Detection] Client history name detected: {Category} ({Confidence}%)",
                        nameResult.CategoryName, nameResult.ConfidenceScore);
                    return nameResult;
                }
            }
        }

        // Fall back to MAC OUI detection (IEEE database + built-in patterns)
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
