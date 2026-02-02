using Microsoft.Extensions.Logging;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes ports to find groups with identical configurations that could
/// benefit from using a shared port profile. Covers:
/// - Trunk ports (existing analysis)
/// - Disabled ports (new: suggest creating a common disabled port profile)
/// - Unrestricted access ports (new: suggest creating a common access profile)
/// </summary>
public class PortProfileSuggestionAnalyzer
{
    private readonly ILogger<PortProfileSuggestionAnalyzer>? _logger;

    /// <summary>
    /// Minimum number of ports before suggesting a profile for disabled/access ports.
    /// </summary>
    private const int MinPortsForDisabledProfileSuggestion = 5;
    private const int MinPortsForAccessProfileSuggestion = 5;

    public PortProfileSuggestionAnalyzer(ILogger<PortProfileSuggestionAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze ports for port profile simplification opportunities.
    /// Includes trunk ports, disabled ports, and unrestricted access ports.
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

        // TODO: Re-enable disabled port profile suggestions when UniFi improves their UX.
        // Currently it's easier to just disable ports directly rather than use a profile
        // (too many clicks in the UniFi UI to assign a port profile).
        // var disabledPortSuggestions = AnalyzeDisabledPorts(devices, profileList, networksById);
        // suggestions.AddRange(disabledPortSuggestions);

        // Analyze unrestricted access ports for profile suggestions
        var accessPortSuggestions = AnalyzeUnrestrictedAccessPorts(devices, profileList, networksById);
        suggestions.AddRange(accessPortSuggestions);

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

                    // FIRST: Filter by speed if profile forces a specific speed
                    // This must happen before PoE filtering so we don't lose valid candidates
                    if (matchingProfile.Value.ForcesSpeed && portsWithProfile.Count > 0)
                    {
                        // Profile forces speed (autoneg=false) - match existing users' speed
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
                    else if (matchingProfile.Value.ForcesSpeed && matchingProfile.Value.ForcedSpeedMbps.HasValue)
                    {
                        // Profile forces speed but no ports currently use it - use profile's speed
                        var targetSpeed = matchingProfile.Value.ForcedSpeedMbps.Value;
                        var incompatibleSpeedPorts = compatiblePorts.Where(p => p.CurrentSpeed != targetSpeed).ToList();
                        if (incompatibleSpeedPorts.Count > 0)
                        {
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' forces {Speed}Mbps - excluding {Count} ports at different speeds: {Ports}",
                                matchingProfile.Value.ProfileName,
                                targetSpeed,
                                incompatibleSpeedPorts.Count,
                                string.Join(", ", incompatibleSpeedPorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex} ({p.CurrentSpeed}Mbps)")));
                        }
                        compatiblePorts = compatiblePorts.Where(p => p.CurrentSpeed == targetSpeed).ToList();
                    }
                    else if (matchingProfile.Value.ForcesSpeed)
                    {
                        // ForcesSpeed but no speed value - can't determine compatibility
                        _logger?.LogDebug(
                            "Profile '{ProfileName}' forces speed but speed value unknown - skipping",
                            matchingProfile.Value.ProfileName);
                        compatiblePorts = new List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)>();
                    }
                    else
                    {
                        // Profile uses autoneg - only suggest to ports that also use autoneg
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

                    // SECOND: Filter by PoE
                    if (matchingProfile.Value.ForcesPoEOff)
                    {
                        // Don't suggest applying to ports with PoE enabled
                        var incompatiblePorts = compatiblePorts.Where(p => p.HasPoEEnabled).ToList();
                        if (incompatiblePorts.Count > 0)
                        {
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' forces PoE off - excluding {Count} ports with PoE enabled: {Ports}",
                                matchingProfile.Value.ProfileName,
                                incompatiblePorts.Count,
                                string.Join(", ", incompatiblePorts.Select(p => $"{p.Reference.DeviceName} port {p.Reference.PortIndex}")));
                        }
                        compatiblePorts = compatiblePorts.Where(p => !p.HasPoEEnabled).ToList();
                    }
                    else if (compatiblePorts.Count > 0)
                    {
                        // Profile allows PoE (PoeMode=auto) - prefer PoE-enabled ports
                        // PoE-enabled profiles are typically for devices that need PoE (APs, cameras)
                        // PoE-disabled ports (SFP+ trunks) should get a separate/fallback suggestion
                        var poeEnabledPorts = compatiblePorts.Where(p => p.HasPoEEnabled).ToList();
                        var poeDisabledPorts = compatiblePorts.Where(p => !p.HasPoEEnabled).ToList();

                        if (poeEnabledPorts.Count > 0 && poeDisabledPorts.Count > 0)
                        {
                            // Mixed PoE states - prefer PoE-enabled ports for PoeMode=auto profiles
                            // PoE-disabled ports will get a fallback suggestion
                            _logger?.LogDebug(
                                "Profile '{ProfileName}' allows PoE - selecting {Count} PoE-enabled ports, excluding {ExcludedCount} PoE-disabled ports for fallback",
                                matchingProfile.Value.ProfileName,
                                poeEnabledPorts.Count,
                                poeDisabledPorts.Count);
                            compatiblePorts = poeEnabledPorts;
                        }
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
                    // 2. If not, fall back to speed/PoE-based grouping
                    if (excludedPorts.Count >= 2)
                    {
                        var compatibilityGroups = new List<List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)>>();
                        var handledPorts = new HashSet<(string Mac, int Port)>();

                        // Check if there are any forced-speed ports
                        var hasForcedSpeedPorts = excludedPorts.Any(p => !p.PortAutoneg);

                        if (hasForcedSpeedPorts)
                        {
                            // Mixed autoneg/forced or all forced: group by speed first
                            // Forced-speed ports can't adapt, so same-speed is required
                            // Autoneg ports at same speed can join (they'll link at that speed)
                            var speedGroups = excludedPorts
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

                            // Leftover autoneg ports at unique speeds can form their own group
                            var leftoverAutonegPorts = excludedPorts
                                .Where(p => p.PortAutoneg && !handledPorts.Contains((p.Reference.DeviceMac, p.Reference.PortIndex)))
                                .ToList();

                            if (leftoverAutonegPorts.Count >= 2)
                            {
                                compatibilityGroups.Add(leftoverAutonegPorts);
                                foreach (var p in leftoverAutonegPorts)
                                    handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));
                            }
                        }
                        else
                        {
                            // All autoneg: group by PoE state (can adapt to any speed)
                            var allAutonegPorts = excludedPorts.ToList();

                            // Try to find an autoneg profile for all autoneg ports
                            var autonegProfile = FindCompatibleProfile(
                                signature, allAutonegPorts, profileSignatures,
                                matchingProfile.Value.ProfileId);

                            if (autonegProfile != null && !autonegProfile.Value.ForcesSpeed)
                            {
                                // Found an autoneg profile - all autoneg ports can use it
                                compatibilityGroups.Add(allAutonegPorts);
                                foreach (var p in allAutonegPorts)
                                    handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));

                                _logger?.LogDebug(
                                    "All {Count} autoneg ports can use alternate profile '{ProfileName}'",
                                    allAutonegPorts.Count, autonegProfile.Value.ProfileName);
                            }
                            else
                            {
                                // No autoneg profile - group by PoE state for CreateNew
                                var poeEnabledPorts = allAutonegPorts.Where(p => p.HasPoEEnabled).ToList();
                                var poeDisabledPorts = allAutonegPorts.Where(p => !p.HasPoEEnabled).ToList();

                                // Split by PoE only if both groups would be viable
                                if (poeEnabledPorts.Count >= 2 && poeDisabledPorts.Count >= 2)
                                {
                                    compatibilityGroups.Add(poeEnabledPorts);
                                    foreach (var p in poeEnabledPorts)
                                        handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));

                                    compatibilityGroups.Add(poeDisabledPorts);
                                    foreach (var p in poeDisabledPorts)
                                        handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));
                                }
                                else if (allAutonegPorts.Count >= 2)
                                {
                                    // Keep together if splitting would create groups <2
                                    compatibilityGroups.Add(allAutonegPorts);
                                    foreach (var p in allAutonegPorts)
                                        handledPorts.Add((p.Reference.DeviceMac, p.Reference.PortIndex));
                                }
                            }
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
                // No matching profile - suggest creating new profile(s)
                // Split by PoE state only if BOTH groups would be viable (2+ each)
                // Otherwise keep together - PoeMode=Auto works for both PoE and non-PoE ports
                var poeEnabledPorts = portsWithoutProfile.Where(p => p.HasPoEEnabled).ToList();
                var poeDisabledPorts = portsWithoutProfile.Where(p => !p.HasPoEEnabled).ToList();

                // Only split if both groups would have 2+ ports
                var shouldSplitByPoe = poeEnabledPorts.Count >= 2 && poeDisabledPorts.Count >= 2;

                if (shouldSplitByPoe)
                {
                    // Create separate suggestions for each PoE group
                    var poeSeverity = poeEnabledPorts.Count >= 5
                        ? PortProfileSuggestionSeverity.Recommendation
                        : PortProfileSuggestionSeverity.Info;

                    var poeSuggestion = new PortProfileSuggestion
                    {
                        Type = PortProfileSuggestionType.CreateNew,
                        Severity = poeSeverity,
                        SuggestedProfileName = GenerateProfileName(signature, networksById) + " (PoE)",
                        Configuration = signature,
                        AffectedPorts = poeEnabledPorts.Select(p => p.Reference).ToList(),
                        PortsWithoutProfile = poeEnabledPorts.Count,
                        PortsAlreadyUsingProfile = 0,
                        Recommendation = GenerateCreateRecommendation(
                            poeEnabledPorts.Count,
                            signature,
                            networksById)
                    };
                    suggestions.Add(poeSuggestion);

                    _logger?.LogDebug(
                        "Port profile suggestion: CreateNew (PoE) - {Count} ports, {ProfileName}",
                        poeSuggestion.AffectedPorts.Count,
                        poeSuggestion.SuggestedProfileName);

                    var noPoeSeverity = poeDisabledPorts.Count >= 5
                        ? PortProfileSuggestionSeverity.Recommendation
                        : PortProfileSuggestionSeverity.Info;

                    var noPoeSuggestion = new PortProfileSuggestion
                    {
                        Type = PortProfileSuggestionType.CreateNew,
                        Severity = noPoeSeverity,
                        SuggestedProfileName = GenerateProfileName(signature, networksById),
                        Configuration = signature,
                        AffectedPorts = poeDisabledPorts.Select(p => p.Reference).ToList(),
                        PortsWithoutProfile = poeDisabledPorts.Count,
                        PortsAlreadyUsingProfile = 0,
                        Recommendation = GenerateCreateRecommendation(
                            poeDisabledPorts.Count,
                            signature,
                            networksById)
                    };
                    suggestions.Add(noPoeSuggestion);

                    _logger?.LogDebug(
                        "Port profile suggestion: CreateNew - {Count} ports, {ProfileName}",
                        noPoeSuggestion.AffectedPorts.Count,
                        noPoeSuggestion.SuggestedProfileName);
                }
                else if (portsWithoutProfile.Count >= 2)
                {
                    // Keep all ports together - PoeMode=Auto works for mixed PoE states
                    var severity = portsWithoutProfile.Count >= 5
                        ? PortProfileSuggestionSeverity.Recommendation
                        : PortProfileSuggestionSeverity.Info;

                    // Use "(PoE)" suffix if any ports have PoE enabled
                    var hasAnyPoE = poeEnabledPorts.Count > 0;
                    var profileName = GenerateProfileName(signature, networksById) + (hasAnyPoE ? " (PoE)" : "");

                    var combinedSuggestion = new PortProfileSuggestion
                    {
                        Type = PortProfileSuggestionType.CreateNew,
                        Severity = severity,
                        SuggestedProfileName = profileName,
                        Configuration = signature,
                        AffectedPorts = portsWithoutProfile.Select(p => p.Reference).ToList(),
                        PortsWithoutProfile = portsWithoutProfile.Count,
                        PortsAlreadyUsingProfile = 0,
                        Recommendation = GenerateCreateRecommendation(
                            portsWithoutProfile.Count,
                            signature,
                            networksById)
                    };
                    suggestions.Add(combinedSuggestion);

                    _logger?.LogDebug(
                        "Port profile suggestion: CreateNew (mixed PoE) - {Count} ports, {ProfileName}",
                        combinedSuggestion.AffectedPorts.Count,
                        combinedSuggestion.SuggestedProfileName);
                }

                continue;
            }
            else
            {
                // Not enough ports or all already have profiles
                continue;
            }
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

    private Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed, int? ForcedSpeedMbps)> BuildProfileSignatures(
        List<UniFiPortProfile> profiles,
        Dictionary<string, UniFiNetworkConfig> networksById,
        HashSet<string> allNetworkIds)
    {
        var signatures = new Dictionary<string, (string, string, PortConfigSignature, bool, bool, int?)>();

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
            var forcedSpeedMbps = forcesSpeed ? profile.Speed : null;

            _logger?.LogDebug("Profile '{Name}': PoeMode={PoeMode}, Autoneg={Autoneg}, ForcesPoEOff={ForcesPoEOff}, ForcesSpeed={ForcesSpeed}, ForcedSpeedMbps={ForcedSpeedMbps}, AllowedVlans=[{Vlans}]",
                profile.Name, profile.PoeMode, profile.Autoneg, forcesPoEOff, forcesSpeed, forcedSpeedMbps,
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

            signatures[profile.Id] = (profile.Id, profile.Name, signature, forcesPoEOff, forcesSpeed, forcedSpeedMbps);
        }

        return signatures;
    }

    private static (string ProfileId, string ProfileName, bool ForcesPoEOff, bool ForcesSpeed, int? ForcedSpeedMbps)? FindMatchingProfile(
        PortConfigSignature portSignature,
        Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed, int? ForcedSpeedMbps)> profileSignatures)
    {
        foreach (var (id, name, profileSig, forcesPoEOff, forcesSpeed, forcedSpeedMbps) in profileSignatures.Values)
        {
            if (portSignature.Equals(profileSig))
            {
                return (id, name, forcesPoEOff, forcesSpeed, forcedSpeedMbps);
            }
        }

        return null;
    }

    /// <summary>
    /// Find an alternate profile that matches the VLAN signature AND is compatible with the given ports.
    /// Used when ports are excluded from one profile but might work with another.
    /// </summary>
    private static (string ProfileId, string ProfileName, bool ForcesPoEOff, bool ForcesSpeed, int? ForcedSpeedMbps)? FindCompatibleProfile(
        PortConfigSignature portSignature,
        List<(PortReference Reference, PortConfigSignature Signature, bool HasPoEEnabled, int CurrentSpeed, bool PortAutoneg)> ports,
        Dictionary<string, (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed, int? ForcedSpeedMbps)> profileSignatures,
        string excludeProfileId)
    {
        foreach (var (id, name, profileSig, forcesPoEOff, forcesSpeed, forcedSpeedMbps) in profileSignatures.Values)
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

            // If profile forces speed, ports must match that speed
            if (isCompatible && forcesSpeed && forcedSpeedMbps.HasValue)
            {
                // All ports must be running at the profile's forced speed
                isCompatible = ports.All(p => p.CurrentSpeed == forcedSpeedMbps.Value);
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
                return (id, name, forcesPoEOff, forcesSpeed, forcedSpeedMbps);
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
        (string ProfileId, string ProfileName, PortConfigSignature Signature, bool ForcesPoEOff, bool ForcesSpeed, int? ForcedSpeedMbps) profileInfo,
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
        if (profileInfo.ForcesSpeed && profileInfo.ForcedSpeedMbps.HasValue)
        {
            // Use the profile's actual forced speed - only match ports at that speed
            compatiblePorts = compatiblePorts.Where(p => p.CurrentSpeed == profileInfo.ForcedSpeedMbps.Value).ToList();
        }
        else if (!profileInfo.ForcesSpeed)
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

    /// <summary>
    /// Analyze disabled ports that don't use a shared profile.
    /// Suggests creating a common "Disabled" profile if enough ports share similar configuration.
    /// </summary>
    private List<PortProfileSuggestion> AnalyzeDisabledPorts(
        IEnumerable<UniFiDeviceResponse> devices,
        List<UniFiPortProfile> profiles,
        Dictionary<string, UniFiNetworkConfig> networksById)
    {
        var suggestions = new List<PortProfileSuggestion>();

        // Find existing disabled port profiles
        var disabledProfiles = profiles
            .Where(p => p.Forward == "disabled")
            .ToList();

        _logger?.LogDebug("Found {Count} existing disabled port profiles", disabledProfiles.Count);
        foreach (var dp in disabledProfiles)
        {
            _logger?.LogDebug("Disabled profile '{Name}': Forward={Forward}, PoeMode={PoeMode}",
                dp.Name, dp.Forward, dp.PoeMode ?? "(null)");
        }

        // Collect all disabled ports without a profile
        var disabledPortsWithoutProfile = new List<(PortReference Reference, string? PoeMode, bool SupportsPoe)>();

        foreach (var device in devices)
        {
            if (device.PortTable == null)
                continue;

            foreach (var port in device.PortTable)
            {
                // Skip ports that already have a profile
                if (!string.IsNullOrEmpty(port.PortConfId))
                    continue;

                // Only include disabled ports
                if (port.Forward != "disabled")
                    continue;

                // Skip uplink ports (shouldn't normally be disabled, but just in case)
                if (port.IsUplink)
                    continue;

                var reference = new PortReference
                {
                    DeviceMac = device.Mac,
                    DeviceName = device.Name,
                    PortIndex = port.PortIdx,
                    PortName = port.Name
                };

                disabledPortsWithoutProfile.Add((reference, port.PoeMode, port.PortPoe));
            }
        }

        _logger?.LogDebug("Found {Count} disabled ports without profiles", disabledPortsWithoutProfile.Count);

        if (disabledPortsWithoutProfile.Count < MinPortsForDisabledProfileSuggestion)
            return suggestions;

        // Group by PoE capability - PoE-capable ports should get PoE-off profile,
        // non-PoE ports can use a simpler profile
        var poeCapablePorts = disabledPortsWithoutProfile.Where(p => p.SupportsPoe).ToList();
        var nonPoePorts = disabledPortsWithoutProfile.Where(p => !p.SupportsPoe).ToList();

        _logger?.LogDebug("Disabled ports: {PoECapable} PoE-capable, {NonPoE} non-PoE",
            poeCapablePorts.Count, nonPoePorts.Count);

        // Check if there's an existing disabled profile with PoE off
        var existingDisabledPoeOff = disabledProfiles
            .FirstOrDefault(p => p.PoeMode == "off");

        _logger?.LogDebug("Existing disabled profile with PoE off: {Name}",
            existingDisabledPoeOff?.Name ?? "(none found)");

        // Create suggestion for PoE-capable disabled ports
        if (poeCapablePorts.Count >= MinPortsForDisabledProfileSuggestion)
        {
            var severity = PortProfileSuggestionSeverity.Recommendation;

            if (existingDisabledPoeOff != null)
            {
                // Suggest applying the existing profile
                suggestions.Add(new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.ApplyExisting,
                    Severity = severity,
                    MatchingProfileId = existingDisabledPoeOff.Id,
                    MatchingProfileName = existingDisabledPoeOff.Name,
                    Configuration = new PortConfigSignature { PoeMode = "off" },
                    AffectedPorts = poeCapablePorts.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = poeCapablePorts.Count,
                    PortsAlreadyUsingProfile = 0,
                    Recommendation = $"{poeCapablePorts.Count} disabled PoE-capable ports could use the existing " +
                        $"\"{existingDisabledPoeOff.Name}\" profile for consistent configuration and easier management."
                });
            }
            else
            {
                // Suggest creating a new disabled profile
                suggestions.Add(new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.CreateNew,
                    Severity = severity,
                    SuggestedProfileName = "Disabled (PoE Off)",
                    Configuration = new PortConfigSignature { PoeMode = "off" },
                    AffectedPorts = poeCapablePorts.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = poeCapablePorts.Count,
                    PortsAlreadyUsingProfile = 0,
                    Recommendation = $"{poeCapablePorts.Count} disabled PoE-capable ports share the same configuration. " +
                        "Create a \"Disabled\" port profile with PoE off for consistent configuration, " +
                        "easier maintenance, and to quickly disable/enable ports in the future."
                });
            }

            _logger?.LogDebug("Created disabled port profile suggestion for {Count} PoE-capable ports", poeCapablePorts.Count);
        }

        // Create suggestion for non-PoE disabled ports (less common, but still useful)
        if (nonPoePorts.Count >= MinPortsForDisabledProfileSuggestion)
        {
            // For non-PoE ports, any disabled profile works (PoE setting is ignored)
            // Prefer the PoE-off profile if we already found one, otherwise use any disabled profile
            var existingDisabledAny = existingDisabledPoeOff ?? disabledProfiles.FirstOrDefault();

            if (existingDisabledAny != null)
            {
                suggestions.Add(new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.ApplyExisting,
                    Severity = PortProfileSuggestionSeverity.Info,
                    MatchingProfileId = existingDisabledAny.Id,
                    MatchingProfileName = existingDisabledAny.Name,
                    Configuration = new PortConfigSignature(),
                    AffectedPorts = nonPoePorts.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = nonPoePorts.Count,
                    PortsAlreadyUsingProfile = 0,
                    Recommendation = $"{nonPoePorts.Count} disabled non-PoE ports could use the existing " +
                        $"\"{existingDisabledAny.Name}\" profile."
                });
            }
            else if (poeCapablePorts.Count < MinPortsForDisabledProfileSuggestion)
            {
                // Only suggest a simple disabled profile if we didn't already suggest a PoE-off one
                suggestions.Add(new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.CreateNew,
                    Severity = PortProfileSuggestionSeverity.Info,
                    SuggestedProfileName = "Disabled",
                    Configuration = new PortConfigSignature(),
                    AffectedPorts = nonPoePorts.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = nonPoePorts.Count,
                    PortsAlreadyUsingProfile = 0,
                    Recommendation = $"{nonPoePorts.Count} disabled ports share the same configuration. " +
                        "Create a \"Disabled\" port profile for consistent configuration and easier maintenance."
                });
            }
        }

        return suggestions;
    }

    /// <summary>
    /// Analyze unrestricted access ports (no MAC restriction) that don't use a shared profile.
    /// These are access ports configured to accept any device - useful for hotel RJ45 jacks,
    /// conference rooms, or guest areas.
    /// </summary>
    private List<PortProfileSuggestion> AnalyzeUnrestrictedAccessPorts(
        IEnumerable<UniFiDeviceResponse> devices,
        List<UniFiPortProfile> profiles,
        Dictionary<string, UniFiNetworkConfig> networksById)
    {
        var suggestions = new List<PortProfileSuggestion>();
        var profilesById = profiles.ToDictionary(p => p.Id);

        // Find existing unrestricted access profiles
        // Unrestricted = access port (forward=native) + no MAC restriction + blocked tagged VLANs
        var unrestrictedAccessProfiles = profiles
            .Where(p => p.Forward == "native" &&
                       !p.PortSecurityEnabled &&
                       p.TaggedVlanMgmt == "block_all")
            .ToList();

        _logger?.LogDebug("Found {Count} existing unrestricted access profiles", unrestrictedAccessProfiles.Count);

        // Collect access ports without MAC restriction and without a profile
        // Group by native VLAN since different VLANs need different profiles
        var accessPortsByVlan = new Dictionary<string, List<(PortReference Reference, bool HasPoEEnabled)>>();

        foreach (var device in devices)
        {
            if (device.PortTable == null)
                continue;

            foreach (var port in device.PortTable)
            {
                // Skip uplink and disabled ports
                if (port.IsUplink || port.Forward == "disabled")
                    continue;

                // Check if this is an access port (native mode or block_all tagged VLANs)
                var isAccessPort = port.Forward == "native" ||
                    (port.TaggedVlanMgmt == "block_all" && port.Forward != "customize");

                if (!isAccessPort)
                    continue;

                // Check if port has a profile
                if (!string.IsNullOrEmpty(port.PortConfId))
                {
                    // Check if the profile is already an unrestricted access profile
                    if (profilesById.TryGetValue(port.PortConfId, out var profile))
                    {
                        if (IsUnrestrictedAccessProfile(profile))
                            continue; // Already using an appropriate profile
                    }
                    continue; // Has a profile, skip
                }

                // Check if port has MAC restriction configured directly (not via profile)
                // If port has security enabled or has allowed MAC addresses, it's not unrestricted
                if (port.PortSecurityEnabled || (port.PortSecurityMacAddresses?.Count > 0))
                {
                    continue; // Port has MAC restriction, not unrestricted
                }

                // Get the native VLAN ID
                var nativeVlanId = port.NativeNetworkConfId ?? "default";

                if (!accessPortsByVlan.TryGetValue(nativeVlanId, out var portList))
                {
                    portList = new List<(PortReference, bool)>();
                    accessPortsByVlan[nativeVlanId] = portList;
                }

                var reference = new PortReference
                {
                    DeviceMac = device.Mac,
                    DeviceName = device.Name,
                    PortIndex = port.PortIdx,
                    PortName = port.Name
                };

                var hasPoEEnabled = port.PortPoe && port.PoeEnable;
                portList.Add((reference, hasPoEEnabled));
            }
        }

        _logger?.LogDebug("Found access ports without unrestricted profiles across {VlanCount} VLANs",
            accessPortsByVlan.Count);

        // Generate suggestions for each VLAN group that has enough ports
        foreach (var (vlanId, ports) in accessPortsByVlan)
        {
            if (ports.Count < MinPortsForAccessProfileSuggestion)
                continue;

            var vlanName = GetNetworkName(vlanId, networksById) ?? vlanId;

            // Check if there's an existing unrestricted profile for this VLAN
            var existingProfile = unrestrictedAccessProfiles
                .FirstOrDefault(p => p.NativeNetworkId == vlanId);

            var signature = new PortConfigSignature
            {
                NativeNetworkId = vlanId,
                NativeNetworkName = vlanName
            };

            if (existingProfile != null)
            {
                // Suggest applying the existing profile
                suggestions.Add(new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.ApplyExisting,
                    Severity = PortProfileSuggestionSeverity.Recommendation,
                    MatchingProfileId = existingProfile.Id,
                    MatchingProfileName = existingProfile.Name,
                    Configuration = signature,
                    AffectedPorts = ports.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = ports.Count,
                    PortsAlreadyUsingProfile = 0,
                    Recommendation = $"{ports.Count} unrestricted access ports on the \"{vlanName}\" network " +
                        $"could use the existing \"{existingProfile.Name}\" profile for consistent configuration."
                });
            }
            else
            {
                // Suggest creating a new unrestricted access profile
                var profileName = $"[Access] {vlanName} - Unrestricted";

                suggestions.Add(new PortProfileSuggestion
                {
                    Type = PortProfileSuggestionType.CreateNew,
                    Severity = PortProfileSuggestionSeverity.Recommendation,
                    SuggestedProfileName = profileName,
                    Configuration = signature,
                    AffectedPorts = ports.Select(p => p.Reference).ToList(),
                    PortsWithoutProfile = ports.Count,
                    PortsAlreadyUsingProfile = 0,
                    Recommendation = $"{ports.Count} access ports on the \"{vlanName}\" network have no MAC restriction " +
                        "and no profile assigned. Create an unrestricted access port profile to standardize " +
                        "configuration for ports that need to accept any device (e.g., conference rooms, guest areas)."
                });
            }

            _logger?.LogDebug("Created unrestricted access profile suggestion for {Count} ports on VLAN {Vlan}",
                ports.Count, vlanName);
        }

        return suggestions;
    }

    /// <summary>
    /// Check if a port profile is configured as an unrestricted access profile.
    /// Unrestricted = access mode + no MAC restriction + blocked tagged VLANs.
    /// </summary>
    private static bool IsUnrestrictedAccessProfile(UniFiPortProfile profile)
    {
        return profile.Forward == "native" &&
               !profile.PortSecurityEnabled &&
               profile.TaggedVlanMgmt == "block_all";
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
