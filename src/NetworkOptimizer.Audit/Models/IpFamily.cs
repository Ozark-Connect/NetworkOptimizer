namespace NetworkOptimizer.Audit.Models;

/// <summary>
/// Address family a traffic claim is evaluated for.
/// </summary>
public enum IpFamily
{
    IPv4,
    IPv6
}

/// <summary>
/// Matching of UniFi's ip_version field (IPV4, IPV6, BOTH) against an address family.
/// </summary>
public static class IpVersionMatcher
{
    /// <summary>
    /// Whether a rule with this ip_version matches the family. BOTH, missing, or unknown values
    /// match either family.
    /// </summary>
    public static bool Matches(string? ipVersion, IpFamily family)
    {
        if (string.Equals(ipVersion, "IPV4", StringComparison.OrdinalIgnoreCase))
            return family == IpFamily.IPv4;
        if (string.Equals(ipVersion, "IPV6", StringComparison.OrdinalIgnoreCase))
            return family == IpFamily.IPv6;
        return true;
    }
}

/// <summary>
/// Shared wording for findings that apply to one address family only.
/// </summary>
public static class IpFamilyText
{
    /// <summary>
    /// Metadata key carried by a finding that applies to IPv6 only.
    /// </summary>
    public const string MetadataKey = "ip_family";

    /// <summary>
    /// Metadata value for <see cref="MetadataKey"/> on an IPv6-only finding.
    /// </summary>
    public const string Ipv6 = "IPv6";

    /// <summary>
    /// Suffix appended to an IPv6-only finding's message and title.
    /// </summary>
    public const string OverIpv6 = " over IPv6";

    /// <summary>
    /// Mark a finding's metadata as IPv6-only when <paramref name="ipv6Only"/> is set; returns the same dictionary.
    /// </summary>
    public static Dictionary<string, object> Tag(Dictionary<string, object> metadata, bool ipv6Only)
    {
        if (ipv6Only)
            metadata[MetadataKey] = Ipv6;
        return metadata;
    }

    /// <summary>
    /// The message suffix for a finding: <see cref="OverIpv6"/> when IPv6-only, otherwise empty.
    /// </summary>
    public static string Suffix(bool ipv6Only) => ipv6Only ? OverIpv6 : "";

    /// <summary>
    /// Whether an issue's metadata marks it as IPv6-only.
    /// </summary>
    public static bool IsIpv6Only(IReadOnlyDictionary<string, object>? metadata) =>
        metadata != null &&
        metadata.TryGetValue(MetadataKey, out var value) &&
        string.Equals(value?.ToString(), Ipv6, StringComparison.Ordinal);
}
