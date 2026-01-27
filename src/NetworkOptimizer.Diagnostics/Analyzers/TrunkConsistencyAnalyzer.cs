using Microsoft.Extensions.Logging;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes trunk links between network devices to find VLAN mismatches
/// where one side allows a VLAN that the other blocks.
/// </summary>
public class TrunkConsistencyAnalyzer
{
    private readonly ILogger<TrunkConsistencyAnalyzer>? _logger;

    public TrunkConsistencyAnalyzer(ILogger<TrunkConsistencyAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze trunk links for VLAN consistency issues.
    /// </summary>
    /// <param name="devices">All network devices (switches, gateways, APs)</param>
    /// <param name="portProfiles">All port profiles</param>
    /// <param name="networks">All network configurations</param>
    /// <returns>List of trunk consistency issues found</returns>
    public List<TrunkConsistencyIssue> Analyze(
        IEnumerable<UniFiDeviceResponse> devices,
        IEnumerable<UniFiPortProfile> portProfiles,
        IEnumerable<UniFiNetworkConfig> networks)
    {
        var issues = new List<TrunkConsistencyIssue>();
        var deviceList = devices.ToList();
        var profilesById = portProfiles.ToDictionary(p => p.Id);
        var networksById = networks.ToDictionary(n => n.Id);
        var allNetworkIds = networks.Select(n => n.Id).ToHashSet();

        // Build device lookup by MAC
        var devicesByMac = deviceList.ToDictionary(
            d => d.Mac.ToLowerInvariant(),
            d => d);

        // Find all trunk links by examining uplink relationships
        var trunkLinks = DiscoverTrunkLinks(deviceList, devicesByMac, profilesById, allNetworkIds);

        // Count VLAN occurrences across all trunks for confidence calculation
        var vlanTrunkCounts = CountVlanOccurrences(trunkLinks, allNetworkIds);
        var totalTrunkCount = trunkLinks.Count;

        // Analyze each trunk link for VLAN mismatches
        foreach (var link in trunkLinks)
        {
            var mismatches = FindVlanMismatches(link, networksById);

            if (mismatches.Count == 0)
                continue;

            // Calculate confidence based on how common these VLANs are
            var confidence = CalculateConfidence(mismatches, vlanTrunkCounts, totalTrunkCount);

            var issue = new TrunkConsistencyIssue
            {
                Link = link,
                Mismatches = mismatches,
                Confidence = confidence,
                Recommendation = GenerateRecommendation(link, mismatches, confidence)
            };

            issues.Add(issue);

            _logger?.LogDebug(
                "Trunk mismatch: {DeviceA}:{PortA} <-> {DeviceB}:{PortB} - {Count} VLANs ({Confidence})",
                link.DeviceAName, link.DeviceAPort,
                link.DeviceBName, link.DeviceBPort,
                mismatches.Count, confidence);
        }

        return issues;
    }

    private List<TrunkLink> DiscoverTrunkLinks(
        List<UniFiDeviceResponse> devices,
        Dictionary<string, UniFiDeviceResponse> devicesByMac,
        Dictionary<string, UniFiPortProfile> profilesById,
        HashSet<string> allNetworkIds)
    {
        var trunkLinks = new List<TrunkLink>();
        var processedLinks = new HashSet<string>(); // Avoid duplicates

        foreach (var deviceA in devices)
        {
            if (deviceA.Uplink == null || string.IsNullOrEmpty(deviceA.Uplink.UplinkMac))
                continue;

            var uplinkMacLower = deviceA.Uplink.UplinkMac.ToLowerInvariant();
            if (!devicesByMac.TryGetValue(uplinkMacLower, out var deviceB))
                continue;

            // Create a unique key for this link (sorted to avoid duplicates)
            var linkKey = string.Compare(deviceA.Mac, deviceB.Mac, StringComparison.OrdinalIgnoreCase) < 0
                ? $"{deviceA.Mac}:{deviceB.Mac}"
                : $"{deviceB.Mac}:{deviceA.Mac}";

            if (processedLinks.Contains(linkKey))
                continue;

            processedLinks.Add(linkKey);

            // Find the ports involved
            // Device A's uplink port connects to Device B's port (uplink_remote_port)
            var deviceBPort = deviceA.Uplink.UplinkRemotePort;

            // Find Device A's port that connects to Device B
            // This is typically a port where the uplink goes out
            var deviceAPort = FindUplinkPort(deviceA);
            if (deviceAPort == null)
                continue;

            // Get port details
            var portA = deviceA.PortTable?.FirstOrDefault(p => p.PortIdx == deviceAPort.Value);
            var portB = deviceB.PortTable?.FirstOrDefault(p => p.PortIdx == deviceBPort);

            if (portA == null || portB == null)
                continue;

            // Get effective settings for both ports
            var profileA = !string.IsNullOrEmpty(portA.PortConfId) && profilesById.TryGetValue(portA.PortConfId, out var pa) ? pa : null;
            var profileB = !string.IsNullOrEmpty(portB.PortConfId) && profilesById.TryGetValue(portB.PortConfId, out var pb) ? pb : null;

            var settingsA = VlanAnalysisHelper.GetEffectiveVlanSettings(portA, null, profileA);
            var settingsB = VlanAnalysisHelper.GetEffectiveVlanSettings(portB, null, profileB);

            // Only analyze if both are trunk ports
            if (!VlanAnalysisHelper.IsTrunkPort(settingsA) || !VlanAnalysisHelper.IsTrunkPort(settingsB))
                continue;

            // Calculate allowed VLANs for each side
            var allowedA = VlanAnalysisHelper.GetAllowedVlansOnTrunk(settingsA, allNetworkIds);
            var allowedB = VlanAnalysisHelper.GetAllowedVlansOnTrunk(settingsB, allNetworkIds);

            trunkLinks.Add(new TrunkLink
            {
                DeviceAMac = deviceA.Mac,
                DeviceAName = deviceA.Name,
                DeviceAPort = deviceAPort.Value,
                DeviceBMac = deviceB.Mac,
                DeviceBName = deviceB.Name,
                DeviceBPort = deviceBPort,
                DeviceAAllowedVlans = allowedA,
                DeviceBAllowedVlans = allowedB
            });
        }

        return trunkLinks;
    }

    private static int? FindUplinkPort(UniFiDeviceResponse device)
    {
        // For switches, look for a port marked as uplink or with "uplink" in name
        if (device.PortTable != null)
        {
            // First try to find explicitly marked uplink
            var uplinkPort = device.PortTable.FirstOrDefault(p => p.IsUplink);
            if (uplinkPort != null)
                return uplinkPort.PortIdx;

            // Then try port name
            uplinkPort = device.PortTable.FirstOrDefault(p =>
                p.Name?.Contains("uplink", StringComparison.OrdinalIgnoreCase) == true);
            if (uplinkPort != null)
                return uplinkPort.PortIdx;

            // For switches, the highest numbered port is often the uplink
            // This is a fallback heuristic
            var highestPort = device.PortTable.OrderByDescending(p => p.PortIdx).FirstOrDefault();
            if (highestPort != null)
                return highestPort.PortIdx;
        }

        return null;
    }

    private static Dictionary<string, int> CountVlanOccurrences(
        List<TrunkLink> trunkLinks,
        HashSet<string> allNetworkIds)
    {
        var counts = new Dictionary<string, int>();

        foreach (var vlanId in allNetworkIds)
        {
            counts[vlanId] = 0;
        }

        foreach (var link in trunkLinks)
        {
            foreach (var vlanId in link.DeviceAAllowedVlans)
            {
                if (counts.ContainsKey(vlanId))
                    counts[vlanId]++;
            }

            foreach (var vlanId in link.DeviceBAllowedVlans)
            {
                if (counts.ContainsKey(vlanId))
                    counts[vlanId]++;
            }
        }

        return counts;
    }

    private static List<VlanMismatch> FindVlanMismatches(
        TrunkLink link,
        Dictionary<string, UniFiNetworkConfig> networksById)
    {
        var mismatches = new List<VlanMismatch>();

        // Find VLANs on A but not on B
        foreach (var vlanId in link.DeviceAAllowedVlans.Except(link.DeviceBAllowedVlans))
        {
            if (!networksById.TryGetValue(vlanId, out var network))
                continue;

            mismatches.Add(new VlanMismatch
            {
                NetworkId = vlanId,
                NetworkName = network.Name ?? "Unknown",
                VlanId = network.Vlan ?? 0,
                Purpose = network.Purpose ?? string.Empty,
                MissingSide = "B",
                MissingSideName = link.DeviceBName
            });
        }

        // Find VLANs on B but not on A
        foreach (var vlanId in link.DeviceBAllowedVlans.Except(link.DeviceAAllowedVlans))
        {
            if (!networksById.TryGetValue(vlanId, out var network))
                continue;

            mismatches.Add(new VlanMismatch
            {
                NetworkId = vlanId,
                NetworkName = network.Name ?? "Unknown",
                VlanId = network.Vlan ?? 0,
                Purpose = network.Purpose ?? string.Empty,
                MissingSide = "A",
                MissingSideName = link.DeviceAName
            });
        }

        return mismatches;
    }

    private static DiagnosticConfidence CalculateConfidence(
        List<VlanMismatch> mismatches,
        Dictionary<string, int> vlanTrunkCounts,
        int totalTrunkCount)
    {
        if (totalTrunkCount == 0)
            return DiagnosticConfidence.Low;

        // Calculate average presence percentage for mismatched VLANs
        var avgPresence = mismatches
            .Where(m => vlanTrunkCounts.ContainsKey(m.NetworkId))
            .Select(m => (double)vlanTrunkCounts[m.NetworkId] / (totalTrunkCount * 2)) // *2 because each link has 2 sides
            .DefaultIfEmpty(0)
            .Average();

        // High confidence: VLAN is on >80% of trunk sides
        if (avgPresence > 0.8)
            return DiagnosticConfidence.High;

        // Medium confidence: VLAN is on 50-80% of trunk sides
        if (avgPresence > 0.5)
            return DiagnosticConfidence.Medium;

        // Low confidence: VLAN is rare, might be intentional
        return DiagnosticConfidence.Low;
    }

    private static string GenerateRecommendation(
        TrunkLink link,
        List<VlanMismatch> mismatches,
        DiagnosticConfidence confidence)
    {
        var vlanList = string.Join(", ", mismatches.Select(m => $"{m.NetworkName} (VLAN {m.VlanId})"));

        var confidenceText = confidence switch
        {
            DiagnosticConfidence.High => "This is likely a configuration error since these VLANs are present on most other trunk links.",
            DiagnosticConfidence.Medium => "Review whether these VLANs should be allowed on this trunk.",
            DiagnosticConfidence.Low => "This may be intentional if these VLANs are only needed in specific network segments.",
            _ => ""
        };

        var missingOnA = mismatches.Where(m => m.MissingSide == "A").ToList();
        var missingOnB = mismatches.Where(m => m.MissingSide == "B").ToList();

        var recommendations = new List<string>();

        if (missingOnA.Count > 0)
        {
            var vlans = string.Join(", ", missingOnA.Select(m => m.NetworkName));
            recommendations.Add($"Add VLANs ({vlans}) to {link.DeviceAName} port {link.DeviceAPort}");
        }

        if (missingOnB.Count > 0)
        {
            var vlans = string.Join(", ", missingOnB.Select(m => m.NetworkName));
            recommendations.Add($"Add VLANs ({vlans}) to {link.DeviceBName} port {link.DeviceBPort}");
        }

        return $"VLAN mismatch on trunk link between {link.DeviceAName} and {link.DeviceBName}. " +
               $"Mismatched VLANs: {vlanList}. " +
               string.Join(" OR ", recommendations) + ". " +
               confidenceText;
    }
}
