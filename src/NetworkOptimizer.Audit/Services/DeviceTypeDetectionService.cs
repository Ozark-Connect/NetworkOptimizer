using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services.Detectors;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;

using static NetworkOptimizer.Audit.Constants.DetectionConstants;

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

    // Client history lookup for enhanced offline device detection
    private Dictionary<string, UniFiClientDetailResponse>? _clientHistoryByMac;

    // UniFi Protect cameras (highest priority detection)
    private ProtectCameraCollection? _protectCameras;

    public DeviceTypeDetectionService(
        ILogger<DeviceTypeDetectionService>? logger = null,
        UniFiFingerprintDatabase? fingerprintDb = null,
        IeeeOuiDatabase? ieeeOuiDb = null,
        ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        var fpLogger = loggerFactory?.CreateLogger<FingerprintDetector>();
        _fingerprintDetector = new FingerprintDetector(fingerprintDb, fpLogger);
        _macOuiDetector = ieeeOuiDb != null ? new MacOuiDetector(ieeeOuiDb) : new MacOuiDetector();
        _namePatternDetector = new NamePatternDetector();
    }

    /// <summary>
    /// Set client history for enhanced offline device detection.
    /// When detecting devices by MAC, we'll first check if the MAC exists in client history
    /// to get fingerprint data, then fall back to IEEE OUI lookup.
    /// </summary>
    public void SetClientHistory(List<UniFiClientDetailResponse>? clientHistory)
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
    /// Set known UniFi Protect devices that require Security VLAN.
    /// Includes cameras, doorbells, NVRs, and AI processors.
    /// These are detected with 100% confidence, bypassing all other detection methods.
    /// </summary>
    public void SetProtectCameras(ProtectCameraCollection? protectCameras)
    {
        _protectCameras = protectCameras;
        if (protectCameras != null && protectCameras.Count > 0)
        {
            _logger?.LogInformation("Loaded {Count} UniFi Protect devices for priority detection", protectCameras.Count);
        }
    }

    /// <summary>
    /// Get the Protect camera name for a MAC address, if known
    /// </summary>
    public string? GetProtectCameraName(string? mac) => _protectCameras?.GetName(mac);

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
        return DetectDeviceTypeCore(client, portName, deviceName);
    }

    /// <summary>
    /// Detect device type from a historical/offline client response.
    /// Converts the detail response to match the main detection method.
    /// </summary>
    /// <param name="client">UniFi client detail (history) data</param>
    /// <param name="portName">Switch port name (optional)</param>
    /// <param name="deviceName">User-assigned device name (optional)</param>
    /// <returns>Best detection result</returns>
    public DeviceDetectionResult DetectDeviceType(
        UniFiClientDetailResponse? client,
        string? portName = null,
        string? deviceName = null)
    {
        if (client == null)
            return DetectDeviceTypeCore(null, portName, deviceName);

        // Convert detail response to standard response for detection
        var clientResponse = new UniFiClientResponse
        {
            Mac = client.Mac ?? string.Empty,
            Name = client.DisplayName ?? client.Name ?? string.Empty,
            Hostname = client.Hostname ?? string.Empty,
            Oui = client.Oui ?? string.Empty,
            DevCat = client.Fingerprint?.DevCat,
            DevVendor = client.Fingerprint?.DevVendor,
            DevIdOverride = client.Fingerprint?.DevIdOverride
        };

        return DetectDeviceTypeCore(clientResponse, portName, deviceName);
    }

    /// <summary>
    /// Core device type detection logic.
    /// </summary>
    private DeviceDetectionResult DetectDeviceTypeCore(
        UniFiClientResponse? client,
        string? portName,
        string? deviceName)
    {
        var results = new List<DeviceDetectionResult>();
        var mac = client?.Mac ?? "unknown";
        var displayName = client?.Name ?? client?.Hostname ?? portName ?? mac;

        _logger?.LogDebug("[Detection] Starting detection for '{DisplayName}' (MAC: {Mac})",
            displayName, mac);

        // Priority -1: UniFi Protect device (100% confidence from controller API)
        // Includes cameras, doorbells, NVRs, and AI processors - all require Security VLAN
        if (_protectCameras != null && !string.IsNullOrEmpty(client?.Mac) &&
            _protectCameras.TryGetName(client.Mac, out var protectCameraName))
        {
            _logger?.LogDebug("[Detection] '{DisplayName}': UniFi Protect device '{CameraName}' (confirmed by controller)",
                displayName, protectCameraName);
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Camera,  // All Protect security devices use Camera category for VLAN rules
                Source = DetectionSource.UniFiFingerprint,
                ConfidenceScore = 100,
                VendorName = "Ubiquiti",
                ProductName = protectCameraName ?? "UniFi Protect",
                RecommendedNetwork = NetworkPurpose.Security,
                Metadata = new Dictionary<string, object>
                {
                    ["detection_method"] = "unifi_protect_api",
                    ["mac"] = client.Mac,
                    ["protect_name"] = protectCameraName ?? ""
                }
            };
        }

        // Priority 0: Check for obvious name keywords that should OVERRIDE fingerprint
        // This handles cases where vendor fingerprint is wrong (e.g., Cync plugs detected as cameras)
        var obviousNameResult = CheckObviousNameOverride(client?.Name, client?.Hostname, client?.Oui);
        if (obviousNameResult != null)
        {
            _logger?.LogDebug("[Detection] '{DisplayName}': Name override → {Category} (name clearly indicates device type)",
                displayName, obviousNameResult.Category);
            return ApplyCloudSecurityOverride(obviousNameResult, client);
        }

        // Priority 0.5: Check OUI for vendors that need special handling
        // - Cync/Wyze/GE have camera fingerprints but most devices are actually plugs/bulbs
        // - Apple with SmartSensor fingerprint is likely Apple Watch
        var vendorOverrideResult = CheckVendorDefaultOverride(client?.Oui, client?.Name, client?.Hostname, client?.DevCat);
        if (vendorOverrideResult != null)
        {
            _logger?.LogDebug("[Detection] '{DisplayName}': Vendor override → {Category} (vendor defaults to plug unless camera indicated)",
                displayName, vendorOverrideResult.Category);
            return ApplyCloudSecurityOverride(vendorOverrideResult, client);
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
            var ouiNameResult = DetectFromUniFiOui(client.Oui, client.Name ?? client.Hostname);
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
            // Try camera name supplement for devices with camera-like names
            var supplement = ApplyCameraNameSupplement(DeviceDetectionResult.Unknown, client);
            if (supplement.Category != ClientDeviceCategory.Unknown)
            {
                _logger?.LogDebug("[Detection] '{DisplayName}' ({Mac}): Supplemented → {Category}",
                    displayName, mac, supplement.Category);
                return supplement;
            }

            // Try watch name supplement for devices with watch-like names
            supplement = ApplyWatchNameSupplement(DeviceDetectionResult.Unknown, client);
            if (supplement.Category != ClientDeviceCategory.Unknown)
            {
                _logger?.LogDebug("[Detection] '{DisplayName}' ({Mac}): Supplemented → {Category}",
                    displayName, mac, supplement.Category);
                return supplement;
            }

            _logger?.LogDebug("[Detection] '{DisplayName}' ({Mac}): No detection → Unknown",
                displayName, mac);
            return DeviceDetectionResult.Unknown;
        }

        // Sort by source priority (lower = better) then by confidence
        var best = results
            .OrderBy(r => (int)r.Source)
            .ThenByDescending(r => r.ConfidenceScore)
            .First();

        // Post-processing: Upgrade Camera/SecuritySystem to cloud variants for cloud vendors
        best = ApplyCloudSecurityOverride(best, client);

        // Post-processing: Supplement classification for camera-like names that weren't classified
        best = ApplyCameraNameSupplement(best, client);

        // Post-processing: Correct misfingerprinted watches (often show as Desktop/Camera)
        best = ApplyWatchNameSupplement(best, client);

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
    /// Detect device type from UniFi's resolved OUI manufacturer name.
    /// For multi-purpose vendors (Nest, Google, Amazon), uses device name to disambiguate.
    /// </summary>
    private DeviceDetectionResult DetectFromUniFiOui(string ouiName, string? deviceName = null)
    {
        var name = ouiName.ToLowerInvariant();
        var deviceNameLower = deviceName?.ToLowerInvariant() ?? "";

        // IoT / Smart Home manufacturers
        if (name.Contains("ikea")) return CreateOuiResult(ClientDeviceCategory.SmartHub, ouiName, OuiStandardConfidence);
        if (name.Contains("philips lighting") || name.Contains("signify")) return CreateOuiResult(ClientDeviceCategory.SmartLighting, ouiName, OuiMediumConfidence);
        if (name.Contains("lutron")) return CreateOuiResult(ClientDeviceCategory.SmartLighting, ouiName, OuiMediumConfidence);
        if (name.Contains("belkin")) return CreateOuiResult(ClientDeviceCategory.SmartPlug, ouiName, OuiLowerConfidence);
        if (name.Contains("tp-link") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartPlug, ouiName, OuiLowerConfidence);
        if (name.Contains("ecobee")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, OuiHighConfidence);
        if (name.Contains("august") || name.Contains("yale") || name.Contains("schlage")) return CreateOuiResult(ClientDeviceCategory.SmartLock, ouiName, OuiMediumConfidence);
        if (name.Contains("sonos")) return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiHighConfidence);
        if (name.Contains("irobot") || name.Contains("roborock") || name.Contains("ecovacs")) return CreateOuiResult(ClientDeviceCategory.RoboticVacuum, ouiName, OuiHighConfidence);
        if (name.Contains("samsung") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartAppliance, ouiName, OuiLowestConfidence);
        if (name.Contains("lg") && name.Contains("smart")) return CreateOuiResult(ClientDeviceCategory.SmartAppliance, ouiName, OuiLowestConfidence);

        // Multi-purpose vendors: Nest/Google make thermostats, cameras, speakers
        // Use device name to disambiguate, default to thermostat if unclear
        if (name.Contains("nest") || (name.Contains("google") && !name.Contains("cloud")))
        {
            if (IsCameraName(deviceNameLower))
                return CreateOuiResult(ClientDeviceCategory.CloudCamera, ouiName, OuiMediumConfidence);
            if (deviceNameLower.Contains("speaker") || deviceNameLower.Contains("home") || deviceNameLower.Contains("hub") || deviceNameLower.Contains("mini"))
                return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiMediumConfidence);
            // Default to thermostat for Nest, speaker for Google
            if (name.Contains("nest"))
                return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, OuiMediumConfidence);
            return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiLowestConfidence);
        }

        // Multi-purpose vendor: Amazon makes cameras (Ring/Blink), speakers (Echo), etc.
        if (name.Contains("amazon") && !name.Contains("aws"))
        {
            if (IsCameraName(deviceNameLower))
                return CreateOuiResult(ClientDeviceCategory.CloudCamera, ouiName, OuiMediumConfidence);
            return CreateOuiResult(ClientDeviceCategory.SmartSpeaker, ouiName, OuiLowestConfidence);
        }

        if (name.Contains("honeywell")) return CreateOuiResult(ClientDeviceCategory.SmartThermostat, ouiName, OuiLowestConfidence);

        // Cloud cameras (require internet/cloud services) - note: Wyze handled in CheckVendorDefaultOverride
        if (name.Contains("ring")) return CreateOuiResult(ClientDeviceCategory.CloudCamera, ouiName, OuiMediumConfidence);
        if (name.Contains("arlo")) return CreateOuiResult(ClientDeviceCategory.CloudCamera, ouiName, OuiHighConfidence);
        if (name.Contains("blink")) return CreateOuiResult(ClientDeviceCategory.CloudCamera, ouiName, OuiMediumConfidence);

        // SimpliSafe: check device name for basestation vs camera
        if (name.Contains("simplisafe"))
        {
            if (deviceNameLower.Contains("basestation") || deviceNameLower.Contains("base station"))
                return CreateOuiResult(ClientDeviceCategory.CloudSecuritySystem, ouiName, OuiMediumConfidence);
            return CreateOuiResult(ClientDeviceCategory.CloudCamera, ouiName, OuiMediumConfidence);
        }

        // Self-hosted cameras (local storage/NVR)
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
    private DeviceDetectionResult? CheckObviousNameOverride(string? name, string? hostname, string? oui = null)
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
                VendorName = oui,  // Preserve OUI vendor for generic matches
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
                VendorName = oui,  // Preserve OUI vendor for generic matches
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Name contains 'bulb/lamp' - overrides vendor fingerprint",
                    ["matched_name"] = checkName
                }
            };
        }

        // Printers - UniFi often miscategorizes as "Network & Peripheral" (IoTGeneric)
        if (nameLower.Contains("printer"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Printer,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = oui,  // Preserve OUI vendor for generic matches
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Name contains 'printer' - overrides vendor fingerprint",
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

        // iPhone - explicitly smartphone (backup for fingerprint edge cases)
        if (nameLower.Contains("iphone"))
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
                    ["override_reason"] = "iPhone is a smartphone",
                    ["matched_name"] = checkName
                }
            };
        }

        // Pixel phone - Google smartphone (exclude Pixel Tablet, Pixelbook, Pixel Slate)
        // Pixel phones are named "Pixel [number]" like "Pixel 6", "Pixel 7 Pro", "Pixel 8a"
        if (nameLower.Contains("pixel") &&
            !nameLower.Contains("tablet") &&
            !nameLower.Contains("book") &&
            !nameLower.Contains("slate") &&
            System.Text.RegularExpressions.Regex.IsMatch(nameLower, @"pixel\s*\d"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.Smartphone,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Google",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Pixel phone is a Google smartphone",
                    ["matched_name"] = checkName
                }
            };
        }

        // VR headsets with vendor-specific detection - often misdetected as Smartphone
        // Meta Quest / Oculus
        if (nameLower.Contains("quest") || nameLower.Contains("oculus") || nameLower.Contains("meta quest"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.GameConsole,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Meta",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Quest/Oculus is a Meta VR headset",
                    ["matched_name"] = checkName
                }
            };
        }

        // HTC Vive
        if (nameLower.Contains("vive"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.GameConsole,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "HTC",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Vive is an HTC VR headset",
                    ["matched_name"] = checkName
                }
            };
        }

        // Valve Index
        if (nameLower.Contains("valve index"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.GameConsole,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Valve",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Index is a Valve VR headset",
                    ["matched_name"] = checkName
                }
            };
        }

        // Sony PSVR
        if (nameLower.Contains("psvr"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.GameConsole,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Sony",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "PSVR is a Sony VR headset",
                    ["matched_name"] = checkName
                }
            };
        }

        // Pico VR
        if (nameLower.Contains("pico"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.GameConsole,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Pico",
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Pico is a Pico VR headset",
                    ["matched_name"] = checkName
                }
            };
        }

        // Generic VR tag (e.g., "[VR]" in name)
        if (nameLower.Contains("[vr]"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.GameConsole,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = oui,  // Preserve OUI vendor for generic VR tag
                RecommendedNetwork = NetworkPurpose.Corporate,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Name contains [VR] tag indicating VR headset",
                    ["matched_name"] = checkName
                }
            };
        }

        // Cloud cameras with vendor-specific detection (require internet/cloud services → IoT VLAN)
        // Ring (Amazon)
        if (nameLower.Contains("ring") && IsCameraName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.CloudCamera,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Ring",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Ring is a cloud camera requiring internet access",
                    ["matched_name"] = checkName
                }
            };
        }

        // Nest/Google cameras
        if ((nameLower.Contains("nest") || nameLower.Contains("google")) && IsCameraName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.CloudCamera,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Google",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Nest/Google is a cloud camera requiring internet access",
                    ["matched_name"] = checkName
                }
            };
        }

        // Wyze cameras
        if (nameLower.Contains("wyze") && IsCameraName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.CloudCamera,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Wyze",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Wyze is a cloud camera requiring internet access",
                    ["matched_name"] = checkName
                }
            };
        }

        // Blink cameras (Amazon)
        if (nameLower.Contains("blink") && IsCameraName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.CloudCamera,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Amazon",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Blink is an Amazon cloud camera requiring internet access",
                    ["matched_name"] = checkName
                }
            };
        }

        // Arlo cameras
        if (nameLower.Contains("arlo") && IsCameraName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.CloudCamera,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Arlo",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Arlo is a cloud camera requiring internet access",
                    ["matched_name"] = checkName
                }
            };
        }

        // SimpliSafe devices (cloud-based security system) - specific vendor+noun
        if (nameLower.Contains("simplisafe"))
        {
            // SimpliSafe Basestation - cloud security system hub
            if (nameLower.Contains("basestation") || nameLower.Contains("base station"))
            {
                return new DeviceDetectionResult
                {
                    Category = ClientDeviceCategory.CloudSecuritySystem,
                    Source = DetectionSource.DeviceName,
                    ConfidenceScore = NameOverrideConfidence,
                    VendorName = "SimpliSafe",
                    RecommendedNetwork = NetworkPurpose.IoT,
                    Metadata = new Dictionary<string, object>
                    {
                        ["override_reason"] = "SimpliSafe Basestation is a cloud security system requiring internet access",
                        ["matched_name"] = checkName
                    }
                };
            }

            // SimpliSafe camera - specific vendor+camera combo
            if (IsCameraName(nameLower))
            {
                return new DeviceDetectionResult
                {
                    Category = ClientDeviceCategory.CloudCamera,
                    Source = DetectionSource.DeviceName,
                    ConfidenceScore = NameOverrideConfidence,
                    VendorName = "SimpliSafe",
                    RecommendedNetwork = NetworkPurpose.IoT,
                    Metadata = new Dictionary<string, object>
                    {
                        ["override_reason"] = "SimpliSafe is a cloud camera requiring internet access",
                        ["matched_name"] = checkName
                    }
                };
            }
        }

        // NOTE: Generic camera names (e.g., "Front Yard Camera") are NOT handled here.
        // They flow through to fingerprint/OUI detection so vendor can be properly determined.
        // Post-processing (ApplyCameraNameSupplement) will catch camera names that weren't classified.

        // Thermostats with vendor-specific detection
        // Ecobee
        if (nameLower.Contains("ecobee"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartThermostat,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Ecobee",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Ecobee is a smart thermostat",
                    ["matched_name"] = checkName
                }
            };
        }

        // Nest thermostat (Google)
        if (nameLower.Contains("nest") && IsThermostatName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartThermostat,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Google",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Nest thermostat is a Google device",
                    ["matched_name"] = checkName
                }
            };
        }

        // Generic thermostat - preserve OUI vendor
        if (IsThermostatName(nameLower))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartThermostat,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = oui,  // Preserve OUI vendor for generic matches
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Name contains thermostat keyword",
                    ["matched_name"] = checkName
                }
            };
        }

        // Smart speakers with vendor-specific detection
        // Apple HomePod
        if (nameLower.Contains("homepod"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartSpeaker,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Apple",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "HomePod is an Apple smart speaker",
                    ["matched_name"] = checkName
                }
            };
        }

        // Amazon Echo devices
        if (nameLower.Contains("echo dot") || nameLower.Contains("echo show") ||
            nameLower.Contains("echo pop") || nameLower.Contains("echo studio") ||
            (nameLower.Contains("echo") && nameLower.Contains("amazon")))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartSpeaker,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Amazon",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Echo is an Amazon smart speaker",
                    ["matched_name"] = checkName
                }
            };
        }

        // Google/Nest speakers
        if (nameLower.Contains("google home") || nameLower.Contains("nest mini") ||
            nameLower.Contains("nest audio") || nameLower.Contains("nest hub"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.SmartSpeaker,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Google",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Google Home/Nest is a Google smart speaker",
                    ["matched_name"] = checkName
                }
            };
        }

        // Apple TV - UniFi categorizes as SmartTV (dev_type_id=47) but it's a streaming device
        if (nameLower.Contains("apple tv") || nameLower.Contains("appletv"))
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.StreamingDevice,
                Source = DetectionSource.DeviceName,
                ConfidenceScore = NameOverrideConfidence,
                VendorName = "Apple",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["override_reason"] = "Apple TV is a streaming device - overrides SmartTV fingerprint",
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
        // Use word boundary check for "cam" to avoid matching "Cambridge" etc.
        return System.Text.RegularExpressions.Regex.IsMatch(nameLower, @"\bcam\b") ||
               nameLower.Contains("camera") ||
               nameLower.Contains("doorbell") ||
               nameLower.Contains("video") ||
               nameLower.Contains("security") ||
               nameLower.Contains("nvr") ||
               nameLower.Contains("ptz");
    }

    /// <summary>
    /// Apply cloud security vendor override if applicable.
    /// Upgrades Camera → CloudCamera and SecuritySystem → CloudSecuritySystem for cloud vendors.
    /// VendorName priority: result.VendorName → client.Oui fallback
    /// </summary>
    private DeviceDetectionResult ApplyCloudSecurityOverride(DeviceDetectionResult result, UniFiClientResponse? client)
    {
        // Only upgrade Camera or SecuritySystem categories
        if (result.Category != ClientDeviceCategory.Camera &&
            result.Category != ClientDeviceCategory.SecuritySystem)
            return result;

        var resolvedVendor = result.VendorName ?? client?.Oui;
        if (string.IsNullOrEmpty(resolvedVendor))
            return result;

        var vendorLower = resolvedVendor.ToLowerInvariant();
        if (!IsCloudSecurityVendor(vendorLower))
            return result;

        // Determine target category based on original type
        var targetCategory = result.Category == ClientDeviceCategory.Camera
            ? ClientDeviceCategory.CloudCamera
            : ClientDeviceCategory.CloudSecuritySystem;

        _logger?.LogDebug("[Detection] Overriding {Original} → {Target} for cloud vendor '{Vendor}'",
            result.Category, targetCategory, resolvedVendor);

        return new DeviceDetectionResult
        {
            Category = targetCategory,
            Source = result.Source,
            ConfidenceScore = result.ConfidenceScore,
            VendorName = result.VendorName ?? resolvedVendor,
            ProductName = result.ProductName,
            RecommendedNetwork = NetworkPurpose.IoT,
            Metadata = new Dictionary<string, object>(result.Metadata ?? new Dictionary<string, object>())
            {
                ["cloud_vendor_override"] = true,
                ["vendor"] = resolvedVendor
            }
        };
    }

    /// <summary>
    /// Post-process supplement: If a device wasn't well-classified but has an obvious camera-like name,
    /// classify it based on vendor. This runs AFTER fingerprint/OUI detection, so vendor is known.
    /// Also upgrades low-confidence categories like IoTGeneric when the name clearly indicates a camera.
    /// </summary>
    private DeviceDetectionResult ApplyCameraNameSupplement(DeviceDetectionResult result, UniFiClientResponse? client)
    {
        // Override obviously wrong fingerprints when name clearly indicates a camera
        // - Unknown/IoTGeneric: always supplement
        // - Desktop/Laptop/Phone/Tablet: fingerprint is clearly wrong if named "camera"
        // - Don't override actual surveillance categories (Camera, CloudCamera, etc.)
        var isGenericOrUnknown = result.Category == ClientDeviceCategory.Unknown ||
                                 result.Category == ClientDeviceCategory.IoTGeneric;
        var isMisfingerprinted = result.Category == ClientDeviceCategory.Desktop ||
                                 result.Category == ClientDeviceCategory.Laptop ||
                                 result.Category == ClientDeviceCategory.Smartphone ||
                                 result.Category == ClientDeviceCategory.Tablet;

        if (!isGenericOrUnknown && !isMisfingerprinted)
            return result;

        var checkName = client?.Name ?? client?.Hostname;
        if (string.IsNullOrEmpty(checkName))
            return result;

        var nameLower = checkName.ToLowerInvariant();

        // Check if name indicates a camera
        if (!IsCameraName(nameLower))
            return result;

        // Resolve vendor from OUI
        var resolvedVendor = client?.Oui;

        // Build reason for supplement/override
        var reason = isMisfingerprinted
            ? $"Name clearly indicates camera, overriding misfingerprinted {result.Category}"
            : "Name contains camera keyword but no fingerprint/OUI match";

        // Create a Camera result (will be upgraded to CloudCamera if cloud vendor)
        var cameraResult = new DeviceDetectionResult
        {
            Category = ClientDeviceCategory.Camera,
            Source = DetectionSource.DeviceName,
            ConfidenceScore = 60, // Lower confidence - only name-based
            VendorName = resolvedVendor,
            RecommendedNetwork = NetworkPurpose.Security,
            Metadata = new Dictionary<string, object>
            {
                ["supplement_reason"] = reason,
                ["matched_name"] = checkName,
                ["original_category"] = result.Category.ToString()
            }
        };

        _logger?.LogDebug("[Detection] Supplementing {Original} → Camera for name '{Name}' (vendor: {Vendor})",
            result.Category, checkName, resolvedVendor ?? "unknown");

        // Run through cloud vendor upgrade
        return ApplyCloudSecurityOverride(cameraResult, client);
    }

    /// <summary>
    /// Post-process supplement: If a device is misfingerprinted but has "watch" in the name,
    /// reclassify as Smartphone. Smartwatches are network-wise equivalent to phones.
    /// Uses word boundary matching to avoid false positives (e.g., "Watcher", "watching").
    /// </summary>
    private DeviceDetectionResult ApplyWatchNameSupplement(DeviceDetectionResult result, UniFiClientResponse? client)
    {
        // Only correct obviously wrong fingerprints when name contains "watch"
        // - Don't override Smartphone (already correct for smartwatches)
        // - Don't override wearables that are already correctly classified
        var isMisfingerprinted = result.Category == ClientDeviceCategory.Desktop ||
                                 result.Category == ClientDeviceCategory.Laptop ||
                                 result.Category == ClientDeviceCategory.Camera ||
                                 result.Category == ClientDeviceCategory.CloudCamera ||
                                 result.Category == ClientDeviceCategory.SmartTV ||
                                 result.Category == ClientDeviceCategory.IoTGeneric ||
                                 result.Category == ClientDeviceCategory.Unknown;

        if (!isMisfingerprinted)
            return result;

        var checkName = client?.Name ?? client?.Hostname;
        if (string.IsNullOrEmpty(checkName))
            return result;

        var nameLower = checkName.ToLowerInvariant();

        // Use word boundary to match "watch" but not "watcher", "watching", etc.
        if (!System.Text.RegularExpressions.Regex.IsMatch(nameLower, @"\bwatch\b"))
            return result;

        // Resolve vendor from OUI or name hints
        var resolvedVendor = client?.Oui;
        if (string.IsNullOrEmpty(resolvedVendor))
        {
            // Try to infer vendor from name
            if (nameLower.Contains("apple")) resolvedVendor = "Apple";
            else if (nameLower.Contains("samsung") || nameLower.Contains("galaxy")) resolvedVendor = "Samsung";
            else if (nameLower.Contains("fitbit")) resolvedVendor = "Fitbit";
            else if (nameLower.Contains("garmin")) resolvedVendor = "Garmin";
        }

        _logger?.LogDebug("[Detection] Supplementing {Original} → Smartphone for watch name '{Name}' (vendor: {Vendor})",
            result.Category, checkName, resolvedVendor ?? "unknown");

        return new DeviceDetectionResult
        {
            Category = ClientDeviceCategory.Smartphone,
            Source = DetectionSource.DeviceName,
            ConfidenceScore = 60, // Lower confidence - only name-based
            VendorName = resolvedVendor,
            RecommendedNetwork = NetworkPurpose.Corporate,
            Metadata = new Dictionary<string, object>
            {
                ["supplement_reason"] = $"Name contains 'watch', overriding misfingerprinted {result.Category}",
                ["matched_name"] = checkName,
                ["original_category"] = result.Category.ToString()
            }
        };
    }

    /// <summary>
    /// Check if a vendor is a cloud-dependent security vendor (cameras, security systems).
    /// Cloud devices require internet access and should be on IoT VLAN, not Security VLAN.
    /// Uses word boundary matching to avoid false positives (e.g., "Springfield" matching "ring").
    /// </summary>
    private static bool IsCloudSecurityVendor(string vendorLower)
    {
        // Word boundary pattern for each vendor - prevents substring false positives
        return System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bring\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bnest\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bgoogle\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bwyze\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bblink\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\barlo\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bsimplisafe\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\btp-link\b") ||
               System.Text.RegularExpressions.Regex.IsMatch(vendorLower, @"\bcanary\b");
    }

    /// <summary>
    /// Check if a name indicates a thermostat device (used for generic matching after vendor-specific checks)
    /// </summary>
    private static bool IsThermostatName(string nameLower)
    {
        return nameLower.Contains("thermostat") ||
               nameLower.Contains("hvac");
    }

    /// <summary>
    /// Check if vendor OUI indicates a device that needs special handling.
    /// - Cync, Wyze, and GE devices have camera fingerprints but are usually plugs/bulbs.
    /// - Apple devices with SmartSensor fingerprint are usually Apple Watches (Smartphone).
    /// - GoPro action cameras share devCat 106 with security cameras but aren't security devices.
    /// </summary>
    private DeviceDetectionResult? CheckVendorDefaultOverride(string? oui, string? name, string? hostname, int? devCat)
    {
        var ouiLower = oui?.ToLowerInvariant() ?? "";
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
                    ["oui"] = oui ?? "",
                    ["dev_cat"] = devCat ?? 0
                }
            };
        }

        // GoPro action cameras use the same devCat (106) as security cameras - they're not security devices
        // This OUI check is a fallback; primary detection is in FingerprintDetector via vendor ID
        if (ouiLower.Contains("gopro") && devCat == 106)
        {
            return new DeviceDetectionResult
            {
                Category = ClientDeviceCategory.IoTGeneric,
                Source = DetectionSource.MacOui,
                ConfidenceScore = VendorOverrideConfidence,
                VendorName = "GoPro",
                RecommendedNetwork = NetworkPurpose.IoT,
                Metadata = new Dictionary<string, object>
                {
                    ["vendor_override_reason"] = "GoPro action camera - not a security camera",
                    ["oui"] = oui ?? "",
                    ["dev_cat"] = devCat ?? 0
                }
            };
        }

        if (string.IsNullOrEmpty(oui))
            return null;

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
            var displayName = historyClient.DisplayName ?? historyClient.Name ?? historyClient.Hostname;
            _logger?.LogDebug("[Detection] Found MAC {Mac} in client history: {Name}",
                macAddress, displayName);

            // Priority 0: Check for obvious name overrides BEFORE fingerprint
            // (same logic as DetectDeviceType - name overrides wrong fingerprints)
            var nameOverride = CheckObviousNameOverride(historyClient.Name, historyClient.Hostname);
            if (nameOverride == null && !string.IsNullOrEmpty(displayName))
            {
                // Also check DisplayName which may have user's naming convention
                nameOverride = CheckObviousNameOverride(displayName, null);
            }
            if (nameOverride != null)
            {
                _logger?.LogDebug("[Detection] Client history name override: {Category} (name clearly indicates device type)",
                    nameOverride.Category);
                return nameOverride;
            }

            // Try fingerprint detection
            if (historyClient.Fingerprint != null)
            {
                // Create a pseudo-client with the fingerprint data to use the existing detector
                var pseudoClient = new UniFiClientResponse
                {
                    Mac = historyClient.Mac,
                    Name = historyClient.Name ?? string.Empty,
                    Hostname = historyClient.Hostname ?? string.Empty,
                    Oui = historyClient.Oui ?? string.Empty,
                    DevIdOverride = historyClient.Fingerprint.DevIdOverride,
                    DevCat = historyClient.Fingerprint.DevCat,
                    DevFamily = historyClient.Fingerprint.DevFamily,
                    DevVendor = historyClient.Fingerprint.DevVendor
                };

                var fpResult = _fingerprintDetector.Detect(pseudoClient);
                if (fpResult.Category != ClientDeviceCategory.Unknown)
                {
                    // Apply cloud vendor override (same as in DetectDeviceType)
                    fpResult = ApplyCloudSecurityOverride(fpResult, pseudoClient);
                    _logger?.LogDebug("[Detection] Client history fingerprint detected: {Category} ({Confidence}%)",
                        fpResult.CategoryName, fpResult.ConfidenceScore);
                    return fpResult;
                }
            }

            // Try name-based detection from history (displayName already set above)
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
