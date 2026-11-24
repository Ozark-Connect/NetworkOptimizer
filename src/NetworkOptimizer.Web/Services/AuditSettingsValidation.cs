using System.Net;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Validation for the Security Audit settings, by setting key. It accepts everything the audit
/// parses as meant (<see cref="AuditService"/> reads these keys) and rejects what it would drop.
/// </summary>
public static class AuditSettingsValidation
{
    public const string DnatExcludedVlansKey = "audit:dnatExcludedVlans";
    public const string TrustedDnsRedirectTargetsKey = "audit:trustedDnsRedirectTargets";
    public const string DnsManagementEndpointKey = "audit:piholeManagementPort";
    public const string UnusedPortDaysKey = "audit:unusedPortInactivityDays";
    public const string NamedPortDaysKey = "audit:namedPortInactivityDays";

    /// <summary>Longest grace period the port fields allow, matching their inputs' max.</summary>
    public const int MaxGraceDays = 365;

    /// <summary>Separators between entries of the trusted DNS redirect target list.</summary>
    public static readonly char[] IpListSeparators = [',', ';', '\n', '\r', ' ', '\t'];

    /// <summary>One VLAN ID entry, as the excluded-VLAN list reads it: 1 to 4094.</summary>
    public static bool TryParseVlanId(string entry, out int vlanId) =>
        int.TryParse(entry, out vlanId) && vlanId is > 0 and <= 4094;

    /// <summary>
    /// One IP address in full form: dotted-quad IPv4 or IPv6. <see cref="IPAddress.TryParse(string?, out IPAddress?)"/>
    /// alone also takes "10" as 0.0.0.10, which no one means.
    /// </summary>
    public static bool IsFullIpAddress(string entry) =>
        IPAddress.TryParse(entry, out var ip)
        && (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || entry.Count(c => c == '.') == 3);

    /// <summary>The error for one setting's value, or null when it is valid or not validated here.</summary>
    public static string? ErrorFor(string key, string? value) => key switch
    {
        DnatExcludedVlansKey => ExcludedVlans(value),
        TrustedDnsRedirectTargetsKey => TrustedDnsRedirectTargets(value),
        DnsManagementEndpointKey => DnsManagementEndpoint(value),
        UnusedPortDaysKey => GraceDays(value, "Unused port grace period"),
        NamedPortDaysKey => GraceDays(value, "Named port grace period"),
        _ => null,
    };

    /// <summary>Comma-separated VLAN IDs; empty means none excluded.</summary>
    public static string? ExcludedVlans(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.All(e => TryParseVlanId(e, out _)) ? null : "VLAN ID must be between 1 and 4094.";
    }

    /// <summary>IP addresses separated by commas, semicolons, spaces, or new lines; empty means none.</summary>
    public static string? TrustedDnsRedirectTargets(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var entries = value.Split(IpListSeparators, StringSplitOptions.RemoveEmptyEntries);
        return entries.All(IsFullIpAddress) ? null : "Trusted DNS redirect targets must be valid IP addresses.";
    }

    /// <summary>A port number or an http(s) URL for the DNS admin interface; empty means auto-probe.</summary>
    public static string? DnsManagementEndpoint(string? value)
    {
        var endpoint = value?.Trim();
        if (string.IsNullOrEmpty(endpoint)) return null;
        if (int.TryParse(endpoint, out var port) && port is > 0 and <= 65535) return null;
        return UrlSafety.IsSafeHttpUrl(endpoint)
            ? null
            : "Invalid DNS endpoint. Enter a port number (e.g., 8080) or full URL (e.g., https://pihole.local).";
    }

    private static string? GraceDays(string? value, string name) =>
        int.TryParse(value, out var days) && days is > 0 and <= MaxGraceDays
            ? null
            : $"{name} must be between 1 and {MaxGraceDays} days.";
}
