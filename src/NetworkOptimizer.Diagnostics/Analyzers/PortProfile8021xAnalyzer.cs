using Microsoft.Extensions.Logging;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.UniFi.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Diagnostics.Analyzers;

/// <summary>
/// Analyzes port profiles to identify trunk/AP profiles with 802.1X Control set to "Auto".
/// These profiles should use "Force Authorized" to prevent network fabric connectivity loss
/// when 802.1X is enabled on the network.
/// </summary>
public class PortProfile8021xAnalyzer
{
    private readonly ILogger<PortProfile8021xAnalyzer>? _logger;

    /// <summary>
    /// Minimum number of tagged VLANs to consider a profile a "trunk/AP" profile.
    /// Profiles with more than this threshold (or "Allow All") are considered trunk profiles.
    /// </summary>
    private const int TrunkVlanThreshold = 2;

    public PortProfile8021xAnalyzer(ILogger<PortProfile8021xAnalyzer>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze port profiles for 802.1X configuration issues.
    /// </summary>
    /// <param name="portProfiles">All port profiles</param>
    /// <param name="networks">All network configurations (for VLAN counting)</param>
    /// <returns>List of profiles with 802.1X issues</returns>
    public List<PortProfile8021xIssue> Analyze(
        IEnumerable<UniFiPortProfile> portProfiles,
        IEnumerable<UniFiNetworkConfig> networks)
    {
        var issues = new List<PortProfile8021xIssue>();
        var profileList = portProfiles.ToList();
        var networkList = networks.ToList();

        // Get all VLAN network IDs for calculating allowed VLANs
        var vlanNetworks = networkList.Where(VlanAnalysisHelper.IsVlanNetwork).ToList();
        var allVlanNetworkIds = vlanNetworks.Select(n => n.Id).ToHashSet();

        _logger?.LogDebug("Analyzing {Count} port profiles for 802.1X issues", profileList.Count);

        foreach (var profile in profileList)
        {
            // Only analyze trunk profiles (Forward=customize, TaggedVlanMgmt=custom)
            if (!IsTrunkProfile(profile))
            {
                _logger?.LogDebug("Skipping profile '{Name}' - not a trunk profile", profile.Name);
                continue;
            }

            // Calculate the effective tagged VLANs
            var (taggedVlanCount, allowsAllVlans) = GetTaggedVlanInfo(profile, allVlanNetworkIds);

            // Check if this is a trunk/AP profile (>2 VLANs or Allow All)
            if (!IsTrunkOrApProfile(taggedVlanCount, allowsAllVlans))
            {
                _logger?.LogDebug(
                    "Skipping profile '{Name}' - only {Count} tagged VLANs (threshold: >{Threshold})",
                    profile.Name, taggedVlanCount, TrunkVlanThreshold);
                continue;
            }

            // Check 802.1X control setting
            var dot1xCtrl = profile.Dot1xCtrl ?? "auto"; // Default is "auto" if not set

            if (dot1xCtrl.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogInformation(
                    "Found 802.1X issue: profile '{Name}' has {VlanCount} VLANs (AllowAll={AllowAll}) with dot1x_ctrl=auto",
                    profile.Name, taggedVlanCount, allowsAllVlans);

                issues.Add(new PortProfile8021xIssue
                {
                    ProfileId = profile.Id,
                    ProfileName = profile.Name,
                    CurrentDot1xCtrl = dot1xCtrl,
                    TaggedVlanCount = taggedVlanCount,
                    AllowsAllVlans = allowsAllVlans,
                    Recommendation = GenerateRecommendation(profile.Name, taggedVlanCount, allowsAllVlans)
                });
            }
            else
            {
                _logger?.LogDebug(
                    "Profile '{Name}' has dot1x_ctrl={Ctrl} - no issue",
                    profile.Name, dot1xCtrl);
            }
        }

        return issues;
    }

    /// <summary>
    /// Check if a profile is a trunk profile (allows tagged VLANs).
    /// </summary>
    private static bool IsTrunkProfile(UniFiPortProfile profile)
    {
        return profile.Forward == "customize" && profile.TaggedVlanMgmt == "custom";
    }

    /// <summary>
    /// Get the tagged VLAN count and whether the profile allows all VLANs.
    /// </summary>
    /// <param name="profile">The port profile to analyze</param>
    /// <param name="allVlanNetworkIds">All VLAN network IDs in the system</param>
    /// <returns>Tuple of (tagged VLAN count, allows all VLANs flag)</returns>
    private static (int TaggedVlanCount, bool AllowsAllVlans) GetTaggedVlanInfo(
        UniFiPortProfile profile,
        HashSet<string> allVlanNetworkIds)
    {
        // If excluded list is null or empty, it means "Allow All"
        var excludedIds = profile.ExcludedNetworkConfIds ?? new List<string>();

        if (excludedIds.Count == 0)
        {
            // Allow All VLANs
            return (allVlanNetworkIds.Count, true);
        }

        // Calculate allowed VLANs = All - Excluded
        var allowedCount = allVlanNetworkIds.Count(id => !excludedIds.Contains(id));
        return (allowedCount, false);
    }

    /// <summary>
    /// Check if the profile is a trunk/AP profile based on VLAN count.
    /// </summary>
    private static bool IsTrunkOrApProfile(int taggedVlanCount, bool allowsAllVlans)
    {
        // Allow All means it's definitely a trunk profile
        if (allowsAllVlans)
            return true;

        // More than threshold VLANs suggests trunk/AP usage
        return taggedVlanCount > TrunkVlanThreshold;
    }

    /// <summary>
    /// Generate a human-readable recommendation.
    /// </summary>
    private static string GenerateRecommendation(string profileName, int vlanCount, bool allowsAllVlans)
    {
        var vlanDesc = allowsAllVlans ? "all VLANs" : $"{vlanCount} VLANs";

        return $"Profile \"{profileName}\" allows {vlanDesc} (trunk/AP profile) but has 802.1X Control " +
               "set to Auto. Set to \"Force Authorized\" to prevent losing network fabric connectivity " +
               "when 802.1X is enabled.";
    }
}
