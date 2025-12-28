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

        // Priority 1: UniFi Fingerprint (if client has fingerprint data)
        if (client != null && client.DevCat.HasValue)
        {
            var fpResult = _fingerprintDetector.Detect(client);
            if (fpResult.Category != ClientDeviceCategory.Unknown)
            {
                results.Add(fpResult);
                _logger?.LogDebug("Fingerprint detected: {Category} for {Mac}",
                    fpResult.Category, client.Mac);
            }
        }

        // Priority 2: MAC OUI lookup
        var mac = client?.Mac;
        if (!string.IsNullOrEmpty(mac))
        {
            var ouiResult = _macOuiDetector.Detect(mac);
            if (ouiResult.Category != ClientDeviceCategory.Unknown)
            {
                results.Add(ouiResult);
                _logger?.LogDebug("MAC OUI detected: {Category} ({Vendor}) for {Mac}",
                    ouiResult.Category, ouiResult.VendorName, mac);
            }
        }

        // Priority 3: Name pattern matching (device name, hostname, port name)
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
                _logger?.LogDebug("Name pattern detected: {Category} from '{Name}' (port={IsPort})",
                    nameResult.Category, name, isPortName);
            }
        }

        // Return best result
        if (results.Count == 0)
        {
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
                _logger?.LogDebug("Multiple sources ({Count}) agree on {Category}, boosting confidence to {Confidence}",
                    agreementCount, best.Category, boostedConfidence);

                return new DeviceDetectionResult
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
            }
        }

        return best;
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
