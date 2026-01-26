using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes wireless clients locked to specific APs and flags mobile devices
/// that should be allowed to roam.
/// </summary>
public class ApLockAnalyzer
{
    private readonly DeviceTypeDetectionService _deviceDetection;
    private readonly ILogger<ApLockAnalyzer>? _logger;

    public ApLockAnalyzer(
        DeviceTypeDetectionService deviceDetection,
        ILogger<ApLockAnalyzer>? logger = null)
    {
        _deviceDetection = deviceDetection;
        _logger = logger;
    }

    /// <summary>
    /// Analyze wireless clients for inappropriate AP locks.
    /// </summary>
    /// <param name="clients">All wireless clients</param>
    /// <param name="devices">All network devices (to resolve AP names)</param>
    /// <returns>List of AP lock issues found</returns>
    public List<ApLockIssue> Analyze(
        IEnumerable<UniFiClientResponse> clients,
        IEnumerable<UniFiDeviceResponse> devices)
    {
        var issues = new List<ApLockIssue>();

        // Build AP lookup by MAC
        var apsByMac = devices
            .Where(d => d.DeviceType == DeviceType.AccessPoint)
            .ToDictionary(d => d.Mac.ToLowerInvariant(), d => d);

        // Filter to wireless clients with AP lock enabled
        var lockedClients = clients
            .Where(c => !c.IsWired && c.FixedApEnabled == true && !string.IsNullOrEmpty(c.FixedApMac));

        foreach (var client in lockedClients)
        {
            var detection = _deviceDetection.DetectDeviceType(client);
            var severity = DetermineSeverity(detection.Category, client.RoamCount);

            // Get AP name
            var apName = "Unknown AP";
            var apMacLower = client.FixedApMac!.ToLowerInvariant();
            if (apsByMac.TryGetValue(apMacLower, out var ap))
            {
                apName = ap.Name;
            }

            // Get client display name
            var clientName = !string.IsNullOrEmpty(client.Name)
                ? client.Name
                : !string.IsNullOrEmpty(client.Hostname)
                    ? client.Hostname
                    : client.Mac;

            var issue = new ApLockIssue
            {
                ClientMac = client.Mac,
                ClientName = clientName,
                LockedApMac = client.FixedApMac!,
                LockedApName = apName,
                DeviceDetection = detection,
                RoamCount = client.RoamCount,
                Severity = severity,
                Recommendation = GenerateRecommendation(detection.Category, client.RoamCount, clientName)
            };

            issues.Add(issue);

            _logger?.LogDebug(
                "AP Lock: {ClientName} ({Category}) locked to {ApName} - {Severity}",
                clientName, detection.Category, apName, severity);
        }

        return issues;
    }

    private static ApLockSeverity DetermineSeverity(ClientDeviceCategory category, int? roamCount)
    {
        // Mobile devices locked to AP is a warning
        if (category.IsMobile())
        {
            return ApLockSeverity.Warning;
        }

        // Stationary devices locked to AP is informational (expected)
        if (category.IsStationary())
        {
            return ApLockSeverity.Info;
        }

        // Unknown device type with high roam count suggests mobile
        if (roamCount.HasValue && roamCount.Value > 10)
        {
            return ApLockSeverity.Warning;
        }

        // Unknown device type - can't determine if lock is appropriate
        return ApLockSeverity.Unknown;
    }

    private static string GenerateRecommendation(
        ClientDeviceCategory category,
        int? roamCount,
        string clientName)
    {
        if (category.IsMobile())
        {
            var roamInfo = roamCount.HasValue && roamCount.Value > 0
                ? $" This device has roamed {roamCount.Value} times, indicating it moves around."
                : "";

            return $"{clientName} is a {category.GetDisplayName()} which should be allowed to roam " +
                   $"between access points for best connectivity.{roamInfo} " +
                   "Consider removing the AP lock to allow automatic roaming.";
        }

        if (category.IsStationary())
        {
            return $"{clientName} is a {category.GetDisplayName()} which is typically stationary. " +
                   "AP lock is appropriate for stationary devices to ensure consistent connectivity.";
        }

        if (roamCount.HasValue && roamCount.Value > 10)
        {
            return $"{clientName} has roamed {roamCount.Value} times, suggesting it's a mobile device. " +
                   "Consider removing the AP lock if this device moves around frequently.";
        }

        return $"Unable to determine device type for {clientName}. " +
               "Review whether this device is mobile (should roam) or stationary (can be locked).";
    }
}
