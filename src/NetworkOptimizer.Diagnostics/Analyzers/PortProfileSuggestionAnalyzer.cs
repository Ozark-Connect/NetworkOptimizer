using Microsoft.Extensions.Logging;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes trunk ports to find groups with identical configurations that could
/// benefit from using a shared port profile.
/// </summary>
public class PortProfileSuggestionAnalyzer
{
    private readonly ILogger<PortProfileSuggestionAnalyzer>? _logger;

    public PortProfileSuggestionAnalyzer(ILogger<PortProfileSuggestionAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze trunk ports for port profile simplification opportunities.
    /// </summary>
    /// <param name="devices">All network devices with port tables</param>
    /// <param name="portProfiles">Existing port profiles</param>
    /// <param name="networks">All network configurations (for display names)</param>
    /// <returns>List of port profile suggestions</returns>
    public List<PortProfileSuggestion> Analyze(
        IEnumerable<UniFiDeviceResponse> devices,
        IEnumerable<UniFiPortProfile> portProfiles,
        IEnumerable<UniFiNetworkConfig> networks)
    {
        var suggestions = new List<PortProfileSuggestion>();
        var profileList = portProfiles.ToList();
        var profilesById = profileList.ToDictionary(p => p.Id);
        var networksById = networks.ToDictionary(n => n.Id);

        // Filter out WAN and VPN networks - they're not relevant for switch port profiles
        var networkList = networks.ToList();
        var vlanNetworks = networkList.Where(VlanAnalysisHelper.IsVlanNetwork).ToList();
        var excludedNetworks = networkList.Where(n => !VlanAnalysisHelper.IsVlanNetwork(n)).ToList();
        var allNetworkIds = vlanNetworks.Select(n => n.Id).ToHashSet();

        _logger?.LogInformation("Port profile analysis: {TotalNetworks} total networks, {VlanNetworks} VLAN networks included",
            networkList.Count, vlanNetworks.Count);

        if (excludedNetworks.Count > 0)
        {
            _logger?.LogDebug("Excluded networks (WAN/VPN): {Networks}",
                string.Join(", ", excludedNetworks.Select(n => $"{n.Name} (purpose={n.Purpose})")));
        }

        _logger?.LogDebug("VLAN networks for profile analysis: {Networks}",
            string.Join(", ", vlanNetworks.Select(n => $"{n.Name} (VLAN {n.Vlan})")));

        // Collect all trunk ports with their effective configurations
        var trunkPorts = CollectTrunkPorts(devices, profilesById, networksById, allNetworkIds);

        if (trunkPorts.Count == 0)
            return suggestions;

        // Build profile signatures for matching
        var profileSignatures = BuildProfileSignatures(profileList, networksById, allNetworkIds);

        // Group ports by their configuration signature
        var portGroups = trunkPorts
            .GroupBy(p => p.Signature, new PortConfigSignatureEqualityComparer())
            .Where(g => g.Count() >= 2) // At least 2 ports to be interesting
            .ToList();

        foreach (var group in portGroups)
        {
            var ports = group.ToList();
            var signature = group.Key;

            // Check if any ports in this group already use a profile
            var portsWithProfile = ports.Where(p => !string.IsNullOrEmpty(p.Reference.CurrentProfileId)).ToList();
            var portsWithoutProfile = ports.Where(p => string.IsNullOrEmpty(p.Reference.CurrentProfileId)).ToList();

            // Check if there's an existing profile that matches this signature
            var matchingProfile = FindMatchingProfile(signature, profileSignatures);

            PortProfileSuggestion suggestion;

            if (matchingProfile != null)
            {
                if (portsWithoutProfile.Count > 0)
                {
                    // Some ports match an existing profile but don't use it
                    suggestion = new PortProfileSuggestion
                    {
                        Type = portsWithProfile.Count > 0
                            ? PortProfileSuggestionType.ExtendUsage
                            : PortProfileSuggestionType.ApplyExisting,
                        MatchingProfileId = matchingProfile.Value.ProfileId,
                        MatchingProfileName = matchingProfile.Value.ProfileName,
                        Configuration = signature,
                        AffectedPorts = ports.Select(p => p.Reference).ToList(),
                        PortsWithoutProfile = portsWithoutProfile.Count,
                        PortsAlreadyUsingProfile = portsWithProfile.Count,
                        Recommendation = GenerateRecommendation(
                            matchingProfile.Value.ProfileName,
                            portsWithoutProfile.Select(p => p.Reference).ToList(),
                            portsWithProfile.Count > 0)
                    };
                }
                else
                {
                    // All ports already use a profile - no suggestion needed
                    continue;
                }
            }
            else if (ports.Count >= 3 && portsWithoutProfile.Count > 0)
            {
                // No matching profile and enough ports to warrant creating one
                suggestion = new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.CreateNew,
                    SuggestedProfileName = GenerateProfileName(signature, networksById),
                    Configuration = signature,
                    AffectedPorts = ports.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = portsWithoutProfile.Count,
                    PortsAlreadyUsingProfile = portsWithProfile.Count,
                    Recommendation = GenerateCreateRecommendation(
                        ports.Count,
                        signature,
                        networksById)
                };
            }
            else
            {
                // Not enough ports or all already have profiles
                continue;
            }

            suggestions.Add(suggestion);

            _logger?.LogDebug(
                "Port profile suggestion: {Type} - {Count} ports, {ProfileName}",
                suggestion.Type,
                suggestion.AffectedPorts.Count,
                suggestion.MatchingProfileName ?? suggestion.SuggestedProfileName);
        }

        return suggestions;
    }

    private List<(PortReference Reference, PortConfigSignature Signature)> CollectTrunkPorts(
        IEnumerable<UniFiDeviceResponse> devices,
        Dictionary<string, UniFiPortProfile> profilesById,
        Dictionary<string, UniFiNetworkConfig> networksById,
        HashSet<string> allNetworkIds)
    {
        var trunkPorts = new List<(PortReference, PortConfigSignature)>();

        foreach (var device in devices)
        {
            if (device.PortTable == null)
                continue;

            foreach (var port in device.PortTable)
            {
                // Get profile if assigned
                var profile = !string.IsNullOrEmpty(port.PortConfId) && profilesById.TryGetValue(port.PortConfId, out var p) ? p : null;
                var settings = VlanAnalysisHelper.GetEffectiveVlanSettings(port, null, profile);

                // Only analyze trunk ports
                if (!VlanAnalysisHelper.IsTrunkPort(settings))
                    continue;

                // Build configuration signature
                var allowedVlans = VlanAnalysisHelper.GetAllowedVlansOnTrunk(settings, allNetworkIds);

                var signature = new PortConfigSignature
                {
                    NativeNetworkId = settings.NativeNetworkId,
                    NativeNetworkName = GetNetworkName(settings.NativeNetworkId, networksById),
                    AllowedVlanIds = allowedVlans,
                    AllowedVlanNames = allowedVlans
                        .Select(id => GetNetworkName(id, networksById))
                        .Where(n => n != null)
                        .Cast<string>()
                        .OrderBy(n => n)
                        .ToList()
                };

                var reference = new PortReference
                {
                    DeviceMac = device.Mac,
                    DeviceName = device.Name,
                    PortIndex = port.PortIdx,
                    PortName = port.Name,
                    CurrentProfileId = port.PortConfId,
                    CurrentProfileName = profile?.Name
                };

                trunkPorts.Add((reference, signature));
            }
        }

        return trunkPorts;
    }

    private Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature)> BuildProfileSignatures(
        List<UniFiPortProfile> profiles,
        Dictionary<string, UniFiNetworkConfig> networksById,
        HashSet<string> allNetworkIds)
    {
        var signatures = new Dictionary<string, (string, string, PortConfigSignature)>();

        foreach (var profile in profiles)
        {
            // Only consider trunk profiles
            if (profile.Forward != "customize" || profile.TaggedVlanMgmt != "custom")
                continue;

            var excludedSet = new HashSet<string>(profile.ExcludedNetworkConfIds ?? new List<string>());
            var allowedVlans = allNetworkIds.Where(id => !excludedSet.Contains(id)).ToHashSet();

            var signature = new PortConfigSignature
            {
                NativeNetworkId = profile.NativeNetworkId,
                NativeNetworkName = GetNetworkName(profile.NativeNetworkId, networksById),
                AllowedVlanIds = allowedVlans,
                AllowedVlanNames = allowedVlans
                    .Select(id => GetNetworkName(id, networksById))
                    .Where(n => n != null)
                    .Cast<string>()
                    .OrderBy(n => n)
                    .ToList(),
                PoeMode = profile.PoeMode != "auto" ? profile.PoeMode : null,
                Isolation = profile.Isolation ? true : null
            };

            signatures[profile.Id] = (profile.Id, profile.Name, signature);
        }

        return signatures;
    }

    private static (string ProfileId, string ProfileName)? FindMatchingProfile(
        PortConfigSignature portSignature,
        Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature)> profileSignatures)
    {
        foreach (var (id, name, profileSig) in profileSignatures.Values)
        {
            if (portSignature.Equals(profileSig))
            {
                return (id, name);
            }
        }

        return null;
    }

    private static string? GetNetworkName(string? networkId, Dictionary<string, UniFiNetworkConfig> networksById)
    {
        if (string.IsNullOrEmpty(networkId))
            return null;

        return networksById.TryGetValue(networkId, out var network) ? network.Name : null;
    }

    private static string GenerateProfileName(
        PortConfigSignature signature,
        Dictionary<string, UniFiNetworkConfig> networksById)
    {
        // Try to generate a meaningful name based on the VLANs
        var vlanNames = signature.AllowedVlanNames;

        if (vlanNames.Count == 0)
            return "Trunk - All VLANs";

        if (vlanNames.Count <= 3)
            return $"Trunk - {string.Join(", ", vlanNames)}";

        // If there's a native VLAN, use that
        if (!string.IsNullOrEmpty(signature.NativeNetworkName))
            return $"Trunk - {signature.NativeNetworkName} Native";

        return $"Trunk - {vlanNames.Count} VLANs";
    }

    private static string GenerateRecommendation(
        string profileName,
        List<PortReference> portsWithoutProfile,
        bool hasExistingUsage)
    {
        var portList = string.Join(", ",
            portsWithoutProfile.Take(3).Select(p => $"{p.DeviceName} port {p.PortIndex}"));

        if (portsWithoutProfile.Count > 3)
            portList += $" and {portsWithoutProfile.Count - 3} more";

        if (hasExistingUsage)
        {
            return $"Some ports with this configuration already use the \"{profileName}\" profile. " +
                   $"Apply this profile to: {portList} for consistent configuration.";
        }

        return $"Apply the existing \"{profileName}\" profile to: {portList} " +
               "for consistent configuration and easier maintenance.";
    }

    private static string GenerateCreateRecommendation(
        int portCount,
        PortConfigSignature signature,
        Dictionary<string, UniFiNetworkConfig> networksById)
    {
        var vlanInfo = signature.AllowedVlanNames.Count <= 5
            ? string.Join(", ", signature.AllowedVlanNames)
            : $"{signature.AllowedVlanNames.Count} VLANs";

        return $"{portCount} trunk ports share identical VLAN configuration ({vlanInfo}). " +
               "Create a port profile to ensure consistent configuration across all these ports " +
               "and simplify future maintenance.";
    }

}

/// <summary>
/// Equality comparer for PortConfigSignature that uses the IEquatable implementation.
/// </summary>
internal class PortConfigSignatureEqualityComparer : IEqualityComparer<PortConfigSignature>
{
    public bool Equals(PortConfigSignature? x, PortConfigSignature? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return x.Equals(y);
    }

    public int GetHashCode(PortConfigSignature obj)
    {
        return obj.GetHashCode();
    }
}
