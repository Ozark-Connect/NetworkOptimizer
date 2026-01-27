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

            // Track ports that we've already included in suggestions
            var handledPortsForExtend = new HashSet<(string Mac, int Port)>();

            // FIRST: Process each profile that ports in this group ACTUALLY use
            // This ensures we generate extend suggestions for the correct profiles
            var profilesActuallyInUse = ports
                .Where(p => !string.IsNullOrEmpty(p.Reference.CurrentProfileId))
                .Select(p => p.Reference.CurrentProfileId!)
                .Distinct()
                .Where(id => profileSignatures.ContainsKey(id))
                .ToList();

            foreach (var profileId in profilesActuallyInUse)
            {
                var profileInfo = profileSignatures[profileId];
                var portsUsingThisProfile = ports.Where(p => p.Reference.CurrentProfileId == profileId).ToList();
                // Only suggest extending to ports WITHOUT any profile - don't suggest changing ports that already have a different profile
                var portsWithoutAnyProfile = ports.Where(p => string.IsNullOrEmpty(p.Reference.CurrentProfileId)).ToList();

                if (portsWithoutAnyProfile.Count == 0)
                    continue;

                _logger?.LogDebug(
                    "Checking profile '{ProfileName}' (used by {UsingCount} ports) for {CandidateCount} ports without profiles",
                    profileInfo.ProfileName, portsUsingThisProfile.Count, portsWithoutAnyProfile.Count);

                // Filter compatible ports for this specific profile
                var compatiblePorts = FilterCompatiblePortsForProfile(
                    portsWithoutAnyProfile, profileInfo, portsUsingThisProfile);

                if (compatiblePorts.Count > 0)
                {
                    var severity = compatiblePorts.Count >= 3
                        ? PortProfileSuggestionSeverity.Recommendation
                        : PortProfileSuggestionSeverity.Info;

                    var extendSuggestion = new PortProfileSuggestion
                    {
                        Type = PortProfileSuggestionType.ExtendUsage,
                        Severity = severity,
                        MatchingProfileId = profileInfo.ProfileId,
                        MatchingProfileName = profileInfo.ProfileName,
                        Configuration = signature,
                        AffectedPorts = portsUsingThisProfile.Select(p => p.Reference)
                            .Concat(compatiblePorts.Select(p => p.Reference)).ToList(),
                        PortsWithoutProfile = compatiblePorts.Count,
                        PortsAlreadyUsingProfile = portsUsingThisProfile.Count,
                        Recommendation = GenerateRecommendation(
                            profileInfo.ProfileName,
                            compatiblePorts.Select(p => p.Reference).ToList(),
                            hasExistingUsage: true)
                    };
                    suggestions.Add(extendSuggestion);

                    _logger?.LogDebug("Created ExtendUsage suggestion for '{ProfileName}' with {Count} new ports",
                        profileInfo.ProfileName, compatiblePorts.Count);

                    // Mark these ports as handled so we don't suggest them again
                    foreach (var p in compatiblePorts)
                        handledPortsForExtend.Add((p.Reference.DeviceMac, p.Reference.PortIndex));
                }
            }

            // SECOND: Process ports without any profile - existing logic
            var portsWithProfile = ports.Where(p => !string.IsNullOrEmpty(p.Reference.CurrentProfileId)).ToList();
            var portsWithoutProfile = ports
                .Where(p => string.IsNullOrEmpty(p.Reference.CurrentProfileId))
                .Where(p => !handledPortsForExtend.Contains((p.Reference.DeviceMac, p.Reference.PortIndex)))
                .ToList();

            // Check if there's an existing profile that matches this signature
            var matchingProfile = FindMatchingProfile(signature, profileSignatures);

            PortProfileSuggestion suggestion;

            // Check if this profile was already handled in the loop above (for extend suggestions)
            var profileAlreadyHandled = matchingProfile != null && profilesActuallyInUse.Contains(matchingProfile.Value.ProfileId);

            if (matchingProfile != null && portsWithoutProfile.Count > 0)
            {
                _logger?.LogDebug(
                    "Matching profile '{ProfileName}' found for {TotalPorts} ports: {WithProfile} already using profile, {WithoutProfile} candidates",
                    matchingProfile.Value.ProfileName, ports.Count, portsWithProfile.Count, portsWithoutProfile.Count);

                if (portsWithProfile.Count > 0)
                {
                    _logger?.LogDebug("Ports already using profiles: {Ports}",
                        string.Join(", ", portsWithProfile.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex} (profile={p.Reference.CurrentProfileName}, speed={p.CurrentSpeed}, autoneg={p.PortAutoneg})")));
                }

                if (portsWithoutProfile.Count > 0)
                {
                    _logger?.LogDebug("Candidate ports for '{ProfileName}': {Ports}",
                        matchingProfile.Value.ProfileName,
                        string.Join(", ", portsWithoutProfile.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex} (speed={p.CurrentSpeed}, autoneg={p.PortAutoneg}, poe={p.HasPoEEnabled})")));

                    // Check if profile is compatible with ports that don't have it
                    // Filter out ports where applying the profile would cause issues
                    var compatiblePorts = portsWithoutProfile;

                    if (matchingProfile.Value.ForcesPoEOff)
                    {
                        // Don't suggest applying to ports with PoE enabled
                        var incompatiblePorts = portsWithoutProfile.Where(p => p.HasPoEEnabled).ToList();
                        if (incompatiblePorts.Count > 0)
                        {
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' forces PoE off - excluding {Count} ports with PoE enabled: {Ports}",
                                matchingProfile.Value.ProfileName,
                                incompatiblePorts.Count,
                                string.Join(", ", incompatiblePorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex}")));
                        }
                        compatiblePorts = portsWithoutProfile.Where(p => !p.HasPoEEnabled).ToList();
                    }
                    else
                    {
                        // Profile allows PoE - group by PoE state to avoid mixing
                        // PoE-enabled ports should be suggested together, PoE-disabled ports separately
                        var poeEnabledPorts = compatiblePorts.Where(p => p.HasPoEEnabled).ToList();
                        var poeDisabledPorts = compatiblePorts.Where(p => !p.HasPoEEnabled).ToList();

                        if (poeEnabledPorts.Count > 0 && poeDisabledPorts.Count > 0)
                        {
                            // Mixed PoE states - choose the larger group for this profile
                            // The other group will get a fallback suggestion
                            if (poeEnabledPorts.Count >= poeDisabledPorts.Count)
                            {
                                _logger?.LogDebug(
                                    "Profile '{ProfileName}' allows PoE - selecting {Count} PoE-enabled ports, excluding {ExcludedCount} PoE-disabled ports",
                                    matchingProfile.Value.ProfileName,
                                    poeEnabledPorts.Count,
                                    poeDisabledPorts.Count);
                                compatiblePorts = poeEnabledPorts;
                            }
                            else
                            {
                                _logger?.LogDebug(
                                    "Profile '{ProfileName}' allows PoE - selecting {Count} PoE-disabled ports, excluding {ExcludedCount} PoE-enabled ports",
                                    matchingProfile.Value.ProfileName,
                                    poeDisabledPorts.Count,
                                    poeEnabledPorts.Count);
                                compatiblePorts = poeDisabledPorts;
                            }
                        }
                    }

                    if (matchingProfile.Value.ForcesSpeed && portsWithProfile.Count > 0)
                    {
                        // Profile forces speed (autoneg=false) - this is fine for trunks
                        // Just ensure the port's current speed matches (autoneg setting doesn't matter)
                        var profileUserSpeeds = portsWithProfile.Select(p => p.CurrentSpeed).Distinct().ToHashSet();

                        var incompatibleSpeedPorts = compatiblePorts.Where(p => !profileUserSpeeds.Contains(p.CurrentSpeed)).ToList();
                        if (incompatibleSpeedPorts.Count > 0)
                        {
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' forces speed - excluding {Count} ports with different speeds: {Ports}",
                                matchingProfile.Value.ProfileName,
                                incompatibleSpeedPorts.Count,
                                string.Join(", ", incompatibleSpeedPorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex} ({p.CurrentSpeed}Mbps)")));
                        }

                        compatiblePorts = compatiblePorts.Where(p => profileUserSpeeds.Contains(p.CurrentSpeed)).ToList();
                    }
                    else if (matchingProfile.Value.ForcesSpeed)
                    {
                        // Profile forces speed but no ports currently use it
                        // Group by current speed and suggest to the largest group at a single speed
                        var speedGroups = compatiblePorts
                            .GroupBy(p => p.CurrentSpeed)
                            .OrderByDescending(g => g.Count())
                            .ToList();

                        if (speedGroups.Count > 0 && speedGroups[0].Count() >= 2)
                        {
                            var targetSpeed = speedGroups[0].Key;
                            var incompatibleSpeedPorts = compatiblePorts.Where(p => p.CurrentSpeed != targetSpeed).ToList();
                            if (incompatibleSpeedPorts.Count > 0)
                            {
                                _logger?.LogDebug(
                                    "Profile '{ProfileName}' forces speed - using {Speed}Mbps ports, excluding {Count} ports at different speeds: {Ports}",
                                    matchingProfile.Value.ProfileName,
                                    targetSpeed,
                                    incompatibleSpeedPorts.Count,
                                    string.Join(", ", incompatibleSpeedPorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex} ({p.CurrentSpeed}Mbps)")));
                            }
                            compatiblePorts = speedGroups[0].ToList();
                        }
                        else
                        {
                            // Not enough ports at any single speed
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' forces speed (autoneg=false) - not enough ports at any single speed",
                                matchingProfile.Value.ProfileName);
                            compatiblePorts = new List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)>();
                        }
                    }
                    else
                    {
                        // Profile uses autoneg - only suggest to ports that also use autoneg
                        // Don't change a port from forced speed to autoneg
                        var forcedSpeedPorts = compatiblePorts.Where(p => !p.PortAutoneg).ToList();
                        if (forcedSpeedPorts.Count > 0)
                        {
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' uses autoneg - excluding {Count} ports with forced speed: {Ports}",
                                matchingProfile.Value.ProfileName,
                                forcedSpeedPorts.Count,
                                string.Join(", ", forcedSpeedPorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex}")));
                        }
                        compatiblePorts = compatiblePorts.Where(p => p.PortAutoneg).ToList();
                    }

                    // Calculate excluded ports (candidates that didn't make it through filtering)
                    // When profileAlreadyHandled=true, ALL remaining ports need alternate profiles
                    // (the first loop already handled the primary profile, these ports were filtered out)
                    var excludedPorts = profileAlreadyHandled
                        ? portsWithoutProfile.ToList()  // All remaining ports need alternate profile
                        : portsWithoutProfile.Where(p =>
                            !compatiblePorts.Any(c => c.Reference.DeviceMac == p.Reference.DeviceMac &&
                                                      c.Reference.PortIndex == p.Reference.PortIndex)).ToList();

                    // Create suggestion for compatible ports if any
                    // Skip if this profile was already handled in the first loop (extend suggestions)
                    if (compatiblePorts.Count > 0 && !profileAlreadyHandled)
                    {
                        _logger?.LogDebug("Final compatible ports for '{ProfileName}': {Ports}",
                            matchingProfile.Value.ProfileName,
                            string.Join(", ", compatiblePorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex}")));

                        // Recommendation level if 3+ ports could be added to the profile
                        var severity = compatiblePorts.Count >= 3
                            ? PortProfileSuggestionSeverity.Recommendation
                            : PortProfileSuggestionSeverity.Info;

                        // Count only ports using THE MATCHING profile for Type determination
                        // (not ports using other profiles with same VLANs)
                        var portsUsingMatchingProfile = portsWithProfile
                            .Where(p => p.Reference.CurrentProfileId == matchingProfile.Value.ProfileId)
                            .ToList();

                        suggestion = new PortProfileSuggestion
                        {
                            Type = portsUsingMatchingProfile.Count > 0
                                ? PortProfileSuggestionType.ExtendUsage
                                : PortProfileSuggestionType.ApplyExisting,
                            Severity = severity,
                            MatchingProfileId = matchingProfile.Value.ProfileId,
                            MatchingProfileName = matchingProfile.Value.ProfileName,
                            Configuration = signature,
                            AffectedPorts = portsUsingMatchingProfile.Select(p => p.Reference)
                                .Concat(compatiblePorts.Select(p => p.Reference)).ToList(),
                            PortsWithoutProfile = compatiblePorts.Count,
                            PortsAlreadyUsingProfile = portsUsingMatchingProfile.Count,
                            Recommendation = GenerateRecommendation(
                                matchingProfile.Value.ProfileName,
                                compatiblePorts.Select(p => p.Reference).ToList(),
                                portsUsingMatchingProfile.Count > 0)
                        };
                        suggestions.Add(suggestion);
                    }
                    else
                    {
                        _logger?.LogDebug("No compatible ports remaining for '{ProfileName}' after filtering",
                            matchingProfile.Value.ProfileName);
                    }

                    // ALSO create fallback suggestions for excluded ports grouped by speed compatibility
                    // Strategy:
                    // 1. First, check if there's an autoneg profile that can take ALL autoneg ports together
                    //    (regardless of their current speeds - autoneg ports can adapt)
                    // 2. If not, fall back to speed-based grouping
                    if (excludedPorts.Count >= 2)
                    {
                        var compatibilityGroups = new List<List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)>>();
                        var handledPorts = new HashSet<(string Mac, int Port)>();

                        // First: Try to find an autoneg profile for ALL autoneg ports together
                        var allAutonegPorts = excludedPorts.Where(p => p.PortAutoneg).ToList();
                        if (allAutonegPorts.Count >= 2)
                        {
                            var autonegProfile = FindCompatibleProfile(
                                signature, allAutonegPorts, profileSignatures,
                                matchingProfile.Value.ProfileId);

                            if (autonegProfile != null && !autonegProfile.Value.ForcesSpeed)
                            {
                                // Found an autoneg profile - all autoneg ports can use it regardless of current speed
                                compatibilityGroups.Add(allAutonegPorts);
                                foreach (var p in allAutonegPorts)
                                    handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));

                                _logger?.LogDebug(
                                    "All {Count} autoneg ports can use alternate profile '{ProfileName}'",
                                    allAutonegPorts.Count, autonegProfile.Value.ProfileName);
                            }
                        }

                        // Second: Group remaining ports by current speed
                        var remainingPorts = excludedPorts
                            .Where(p => !handledPorts.Contains((p.Reference.DeviceMac, p.Reference.PortIndex)))
                            .ToList();

                        var speedGroups = remainingPorts
                            .GroupBy(p => p.CurrentSpeed)
                            .Where(g => g.Count() >= 2)
                            .ToList();

                        foreach (var speedGroup in speedGroups)
                        {
                            var groupList = speedGroup.ToList();
                            compatibilityGroups.Add(groupList);
                            foreach (var p in groupList)
                                handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));
                        }

                        // Third: Leftover autoneg ports at unique speeds can form their own autoneg group
                        var leftoverAutonegPorts = remainingPorts
                            .Where(p => p.PortAutoneg && !handledPorts.Contains((p.Reference.DeviceMac, p.Reference.PortIndex)))
                            .ToList();

                        if (leftoverAutonegPorts.Count >= 2)
                        {
                            compatibilityGroups.Add(leftoverAutonegPorts);
                        }

                        foreach (var groupPorts in compatibilityGroups)
                        {
                            // Check if there's ANOTHER profile that matches these excluded ports
                            var alternateProfile = FindCompatibleProfile(
                                signature, groupPorts, profileSignatures,
                                matchingProfile.Value.ProfileId);

                            if (alternateProfile != null)
                            {
                                _logger?.LogDebug(
                                    "Found alternate profile '{ProfileName}' for {Count} excluded ports",
                                    alternateProfile.Value.ProfileName, groupPorts.Count);

                                var altSeverity = groupPorts.Count >= 3
                                    ? PortProfileSuggestionSeverity.Recommendation
                                    : PortProfileSuggestionSeverity.Info;

                                var altSuggestion = new PortProfileSuggestion
                                {
                                    Type = PortProfileSuggestionType.ApplyExisting,
                                    Severity = altSeverity,
                                    MatchingProfileId = alternateProfile.Value.ProfileId,
                                    MatchingProfileName = alternateProfile.Value.ProfileName,
                                    Configuration = signature,
                                    AffectedPorts = groupPorts.Select(p => p.Reference).ToList(),
                                    PortsWithoutProfile = groupPorts.Count,
                                    PortsAlreadyUsingProfile = 0,
                                    Recommendation = GenerateRecommendation(
                                        alternateProfile.Value.ProfileName,
                                        groupPorts.Select(p => p.Reference).ToList(),
                                        hasExistingUsage: false)
                                };
                                suggestions.Add(altSuggestion);
                            }
                            else
                            {
                                // No compatible alternate profile - create fallback suggestion
                                var isAutonegOnlyGroup = groupPorts.All(p => p.PortAutoneg) &&
                                    groupPorts.Select(p => p.CurrentSpeed).Distinct().Count() > 1;
                                _logger?.LogDebug(
                                    "Creating fallback suggestion for {Count} excluded ports ({Type})",
                                    groupPorts.Count, isAutonegOnlyGroup ? "autoneg (mixed speeds)" : $"{groupPorts[0].CurrentSpeed}Mbps");

                                var fallbackSeverity = groupPorts.Count >= 5
                                    ? PortProfileSuggestionSeverity.Recommendation
                                    : PortProfileSuggestionSeverity.Info;

                                // Only add "(PoE)" suffix if ports actually have PoE enabled
                                var hasPoEPorts = groupPorts.Any(p => p.HasPoEEnabled);
                                var profileNameSuffix = hasPoEPorts ? " (PoE)" : "";

                                var fallbackSuggestion = new PortProfileSuggestion
                                {
                                    Type = PortProfileSuggestionType.CreateNew,
                                    Severity = fallbackSeverity,
                                    SuggestedProfileName = GenerateProfileName(signature, networksById) + profileNameSuffix,
                                    Configuration = signature,
                                    AffectedPorts = groupPorts.Select(p => p.Reference).ToList(),
                                    PortsWithoutProfile = groupPorts.Count,
                                    PortsAlreadyUsingProfile = 0,
                                    Recommendation = GenerateCreateRecommendation(
                                        groupPorts.Count,
                                        signature,
                                        networksById)
                                };
                                suggestions.Add(fallbackSuggestion);
                            }
                        }
                    }

                    continue; // Move to next group after processing this one
                }
                else
                {
                    // All ports already use a profile - no suggestion needed
                    continue;
                }
            }
            else if (ports.Count >= 2 && portsWithoutProfile.Count > 0)
            {
                // No matching profile and enough ports to warrant creating one
                // Recommendation level if 5+ ports need the profile
                var severity = portsWithoutProfile.Count >= 5
                    ? PortProfileSuggestionSeverity.Recommendation
                    : PortProfileSuggestionSeverity.Info;

                suggestion = new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.CreateNew,
                    Severity = severity,
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

    private List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)> CollectTrunkPorts(
        IEnumerable<UniFiDeviceResponse> devices,
        Dictionary<string, UniFiPortProfile> profilesById,
        Dictionary<string, UniFiNetworkConfig> networksById,
        HashSet<string> allNetworkIds)
    {
        var trunkPorts = new List<(PortReference, PortConfigSignature, bool, int, bool)>();

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

                // Capture port's PoE state, current speed, and autoneg setting
                // PortPoe = port has PoE capability (false for SFP ports)
                // PoeEnable = PoE is enabled on this port
                // Only consider PoE "enabled" if the port supports it AND has it turned on
                var hasPoEEnabled = port.PortPoe && port.PoeEnable;
                var currentSpeed = port.Speed;
                var portAutoneg = port.Autoneg;

                _logger?.LogDebug("Port {Device} port {Port}: PortPoe={PortPoe}, PoeEnable={PoeEnable}, HasPoEEnabled={HasPoEEnabled}, Speed={Speed}, Autoneg={Autoneg}, Media={Media}, NativeNetwork={Native}, AllowedVlans=[{Vlans}]",
                    device.Name, port.PortIdx, port.PortPoe, port.PoeEnable, hasPoEEnabled, port.Speed, port.Autoneg, port.Media,
                    signature.NativeNetworkName ?? signature.NativeNetworkId ?? "(none)",
                    string.Join(", ", signature.AllowedVlanNames));

                trunkPorts.Add((reference, signature, hasPoEEnabled, currentSpeed, portAutoneg));
            }
        }

        return trunkPorts;
    }

    private Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed)> BuildProfileSignatures(
        List<UniFiPortProfile> profiles,
        Dictionary<string, UniFiNetworkConfig> networksById,
        HashSet<string> allNetworkIds)
    {
        var signatures = new Dictionary<string, (string, string, PortConfigSignature, bool, bool)>();

        foreach (var profile in profiles)
        {
            // Only consider trunk profiles
            if (profile.Forward != "customize" || profile.TaggedVlanMgmt != "custom")
                continue;

            var excludedSet = new HashSet<string>(profile.ExcludedNetworkConfIds ?? new List<string>());
            var allowedVlans = allNetworkIds.Where(id => !excludedSet.Contains(id)).ToHashSet();

            // Check if profile forces PoE off or forces specific speed
            var forcesPoEOff = profile.PoeMode == "off";
            var forcesSpeed = profile.Autoneg == false;

            _logger?.LogDebug("Profile '{Name}': PoeMode={PoeMode}, Autoneg={Autoneg}, ForcesPoEOff={ForcesPoEOff}, ForcesSpeed={ForcesSpeed}, AllowedVlans=[{Vlans}]",
                profile.Name, profile.PoeMode, profile.Autoneg, forcesPoEOff, forcesSpeed,
                string.Join(", ", allowedVlans.Select(id => GetNetworkName(id, networksById) ?? id).OrderBy(n => n)));

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

            signatures[profile.Id] = (profile.Id, profile.Name, signature, forcesPoEOff, forcesSpeed);
        }

        return signatures;
    }

    private static (string ProfileId, string ProfileName, bool ForcesPoEOff, bool ForcesSpeed)? FindMatchingProfile(
        PortConfigSignature portSignature,
        Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed)> profileSignatures)
    {
        foreach (var (id, name, profileSig, forcesPoEOff, forcesSpeed) in profileSignatures.Values)
        {
            if (portSignature.Equals(profileSig))
            {
                return (id, name, forcesPoEOff, forcesSpeed);
            }
        }

        return null;
    }

    /// <summary>
    /// Find an alternate profile that matches the VLAN signature AND is compatible with the given ports.
    /// Used when ports are excluded from one profile but might work with another.
    /// </summary>
    private static (string ProfileId, string ProfileName, bool ForcesPoEOff, bool ForcesSpeed)? FindCompatibleProfile(
        PortConfigSignature portSignature,
        List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)> ports,
        Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed)> profileSignatures,
        string excludeProfileId)
    {
        foreach (var (id, name, profileSig, forcesPoEOff, forcesSpeed) in profileSignatures.Values)
        {
            // Skip the profile we already matched
            if (id == excludeProfileId)
                continue;

            // Must have same VLAN signature
            if (!portSignature.Equals(profileSig))
                continue;

            // Check if profile is compatible with ALL ports in the group
            var isCompatible = true;

            // If profile forces PoE off, it's incompatible with PoE-enabled ports
            if (forcesPoEOff && ports.Any(p => p.HasPoEEnabled))
            {
                isCompatible = false;
            }

            // If profile forces speed, check port compatibility
            if (isCompatible && forcesSpeed)
            {
                // For forced speed profiles, ports need to be at a consistent speed
                var speeds = ports.Select(p => p.CurrentSpeed).Distinct().ToList();
                if (speeds.Count > 1)
                {
                    // Multiple speeds - only compatible if all ports use autoneg
                    isCompatible = ports.All(p => p.PortAutoneg);
                }
            }

            // If profile uses autoneg, it's incompatible with forced-speed ports
            if (isCompatible && !forcesSpeed)
            {
                if (ports.Any(p => !p.PortAutoneg))
                {
                    isCompatible = false;
                }
            }

            if (isCompatible)
            {
                return (id, name, forcesPoEOff, forcesSpeed);
            }
        }

        return null;
    }

    /// <summary>
    /// Filter ports that are compatible with a specific profile.
    /// Used to find which ports can extend an existing profile's usage.
    /// </summary>
    private static List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)> FilterCompatiblePortsForProfile(
        List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)> candidatePorts,
        (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed) profileInfo,
        List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)> portsAlreadyUsingProfile)
    {
        var compatiblePorts = candidatePorts.ToList();

        // If profile forces PoE off, exclude ports with PoE enabled
        if (profileInfo.ForcesPoEOff)
        {
            compatiblePorts = compatiblePorts.Where(p => !p.HasPoEEnabled).ToList();
        }

        // PoE consistency: if existing users all have PoE enabled/disabled, only extend to matching ports
        if (portsAlreadyUsingProfile.Count > 0 && !profileInfo.ForcesPoEOff)
        {
            var existingHavePoE = portsAlreadyUsingProfile.All(p => p.HasPoEEnabled);
            var existingNoPoE = portsAlreadyUsingProfile.All(p => !p.HasPoEEnabled);

            if (existingHavePoE)
            {
                // All existing users have PoE enabled - only extend to ports with PoE enabled
                compatiblePorts = compatiblePorts.Where(p => p.HasPoEEnabled).ToList();
            }
            else if (existingNoPoE)
            {
                // All existing users have PoE disabled - only extend to ports with PoE disabled
                compatiblePorts = compatiblePorts.Where(p => !p.HasPoEEnabled).ToList();
            }
            // Mixed PoE state among existing users - don't filter by PoE
        }

        // If profile forces speed, check speed compatibility
        if (profileInfo.ForcesSpeed && portsAlreadyUsingProfile.Count > 0)
        {
            // Match the speeds of ports already using the profile
            var profileUserSpeeds = portsAlreadyUsingProfile.Select(p => p.CurrentSpeed).Distinct().ToHashSet();
            compatiblePorts = compatiblePorts.Where(p => profileUserSpeeds.Contains(p.CurrentSpeed)).ToList();
        }
        else if (profileInfo.ForcesSpeed)
        {
            // No ports using profile yet - group by speed
            var speedGroups = compatiblePorts.GroupBy(p => p.CurrentSpeed).OrderByDescending(g => g.Count()).ToList();
            if (speedGroups.Count > 0 && speedGroups[0].Count() >= 1)
            {
                compatiblePorts = speedGroups[0].ToList();
            }
            else
            {
                compatiblePorts = new List<(PortReference, PortConfigSignature, bool, int, bool)>();
            }
        }
        else
        {
            // Profile uses autoneg - only include ports that also use autoneg
            compatiblePorts = compatiblePorts.Where(p => p.PortAutoneg).ToList();
        }

        return compatiblePorts;
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
            portsWithoutProfile.Take(5).Select(p => $"{p.DeviceName} port {p.PortIndex}"));

        if (portsWithoutProfile.Count > 5)
            portList += $" +{portsWithoutProfile.Count - 5} more";

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
