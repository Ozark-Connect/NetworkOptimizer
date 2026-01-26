namespace NetworkOptimizer.Diagnostics.Models;

/// <summary>
/// Type of port profile suggestion.
/// </summary>
public enum PortProfileSuggestionType
{
    /// <summary>
    /// No matching profile exists - suggest creating one
    /// </summary>
    CreateNew,

    /// <summary>
    /// An existing profile matches - suggest applying it to ports
    /// </summary>
    ApplyExisting,

    /// <summary>
    /// Some ports use a profile, others with same config don't - extend usage
    /// </summary>
    ExtendUsage
}

/// <summary>
/// Reference to a specific switch port.
/// </summary>
public class PortReference
{
    /// <summary>
    /// MAC address of the switch device
    /// </summary>
    public string DeviceMac { get; set; } = string.Empty;

    /// <summary>
    /// Display name of the switch device
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Port index number
    /// </summary>
    public int PortIndex { get; set; }

    /// <summary>
    /// Port name (if configured)
    /// </summary>
    public string? PortName { get; set; }

    /// <summary>
    /// Currently assigned port profile ID (null if no profile)
    /// </summary>
    public string? CurrentProfileId { get; set; }

    /// <summary>
    /// Name of the currently assigned profile (null if no profile)
    /// </summary>
    public string? CurrentProfileName { get; set; }
}

/// <summary>
/// Represents the comparable configuration signature of a port or profile.
/// Used for grouping ports with identical configurations.
/// </summary>
public class PortConfigSignature : IEquatable<PortConfigSignature>
{
    // VLAN settings
    /// <summary>
    /// Native VLAN network config ID
    /// </summary>
    public string? NativeNetworkId { get; set; }

    /// <summary>
    /// Display name of native network
    /// </summary>
    public string? NativeNetworkName { get; set; }

    /// <summary>
    /// Set of allowed VLAN network IDs (for order-independent comparison)
    /// </summary>
    public HashSet<string> AllowedVlanIds { get; set; } = new();

    /// <summary>
    /// Display names of allowed VLANs
    /// </summary>
    public List<string> AllowedVlanNames { get; set; } = new();

    // Link settings (null = auto-negotiation, don't compare)
    /// <summary>
    /// Speed in Mbps (null if auto-negotiation)
    /// </summary>
    public int? Speed { get; set; }

    /// <summary>
    /// Duplex mode (null if auto-negotiation)
    /// </summary>
    public string? Duplex { get; set; }

    // Other port settings (null = default/auto, don't compare)
    /// <summary>
    /// PoE mode (null if "auto")
    /// </summary>
    public string? PoeMode { get; set; }

    /// <summary>
    /// Port isolation enabled (null if false/default)
    /// </summary>
    public bool? Isolation { get; set; }

    /// <summary>
    /// Storm control enabled (null if all disabled)
    /// </summary>
    public bool? StormControlEnabled { get; set; }

    /// <inheritdoc/>
    public bool Equals(PortConfigSignature? other)
    {
        if (other is null) return false;

        // VLAN comparison (order-independent)
        if (NativeNetworkId != other.NativeNetworkId) return false;
        if (!AllowedVlanIds.SetEquals(other.AllowedVlanIds)) return false;

        // Link settings (only compare if both have explicit values)
        if (Speed.HasValue && other.Speed.HasValue && Speed != other.Speed) return false;
        if (Duplex != null && other.Duplex != null && Duplex != other.Duplex) return false;

        // Other settings (only compare if both have explicit values)
        if (PoeMode != null && other.PoeMode != null && PoeMode != other.PoeMode) return false;
        if (Isolation == true && other.Isolation != true) return false;
        if (other.Isolation == true && Isolation != true) return false;

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PortConfigSignature);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Hash based on VLAN config (primary grouping key)
        var hash = new HashCode();
        hash.Add(NativeNetworkId);
        foreach (var vlan in AllowedVlanIds.OrderBy(v => v))
            hash.Add(vlan);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Suggestion to simplify port configuration using port profiles.
/// </summary>
public class PortProfileSuggestion
{
    /// <summary>
    /// Type of suggestion (create new, apply existing, or extend usage)
    /// </summary>
    public PortProfileSuggestionType Type { get; set; }

    /// <summary>
    /// Suggested name for a new profile (if Type is CreateNew)
    /// </summary>
    public string SuggestedProfileName { get; set; } = string.Empty;

    /// <summary>
    /// ID of existing profile that matches (if Type is ApplyExisting or ExtendUsage)
    /// </summary>
    public string? MatchingProfileId { get; set; }

    /// <summary>
    /// Name of existing profile that matches
    /// </summary>
    public string? MatchingProfileName { get; set; }

    /// <summary>
    /// The configuration signature being matched
    /// </summary>
    public PortConfigSignature Configuration { get; set; } = new();

    /// <summary>
    /// Ports affected by this suggestion
    /// </summary>
    public List<PortReference> AffectedPorts { get; set; } = new();

    /// <summary>
    /// Number of ports that don't currently use a profile
    /// </summary>
    public int PortsWithoutProfile { get; set; }

    /// <summary>
    /// Number of ports already using the matching profile
    /// </summary>
    public int PortsAlreadyUsingProfile { get; set; }

    /// <summary>
    /// Human-readable recommendation
    /// </summary>
    public string Recommendation { get; set; } = string.Empty;
}
