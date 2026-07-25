using System.Net;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// One address configured on the interface, with its DHCP lifetimes when the address is
/// dynamic. On a WAN this is where the ISP lease shows up: <c>valid_lft</c> counts down the
/// seconds left before the lease expires, which is the only place a UniFi gateway exposes
/// that (issue #1054).
/// </summary>
public class GatewayInterfaceAddress
{
    /// <summary>True for an IPv6 address.</summary>
    public bool IsIpv6 { get; init; }

    /// <summary>Address with prefix length, as reported ("203.0.113.5/24").</summary>
    public required string Cidr { get; init; }

    /// <summary>Address without the prefix length.</summary>
    public required string Address { get; init; }

    /// <summary>Prefix length in bits, or null when the address had no prefix.</summary>
    public int? PrefixLength { get; init; }

    /// <summary>Dotted subnet mask derived from the prefix length. IPv4 only.</summary>
    public string? SubnetMask { get; init; }

    /// <summary>Broadcast address, when reported.</summary>
    public string? Broadcast { get; init; }

    /// <summary>Address scope as reported: "global", "link", "host".</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// True when the kernel flagged the address <c>dynamic</c> - it came from DHCP (or IPv6
    /// autoconfiguration) and carries a real lease. A statically configured address never does.
    /// </summary>
    public bool IsDynamic { get; init; }

    /// <summary>
    /// Seconds left on the lease at collection time, or null when the address never expires
    /// (reported as <c>forever</c>, which is what a static address shows).
    /// </summary>
    public long? ValidLifetimeSeconds { get; init; }

    /// <summary>Seconds until the address stops being preferred for new connections; null for forever.</summary>
    public long? PreferredLifetimeSeconds { get; init; }

    /// <summary>Remaining lease as a span, or null when the address never expires.</summary>
    public TimeSpan? ValidLifetime =>
        ValidLifetimeSeconds.HasValue ? TimeSpan.FromSeconds(ValidLifetimeSeconds.Value) : null;
}

/// <summary>Parsed <c>ip -d addr show dev &lt;iface&gt;</c> output for one interface.</summary>
public class GatewayInterfaceInfo
{
    /// <summary>OS interface name the diagnostics ran against.</summary>
    public required string Name { get; init; }

    /// <summary>Operational state as reported ("UP", "DOWN", "UNKNOWN").</summary>
    public string? State { get; init; }

    /// <summary>Configured MTU.</summary>
    public int? Mtu { get; init; }

    /// <summary>The interface's own MAC address.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Every address configured on the interface, IPv4 first.</summary>
    public List<GatewayInterfaceAddress> Addresses { get; init; } = new();

    /// <summary>Next hop of the default route leaving this interface, when it has one.</summary>
    public string? DefaultGateway { get; set; }
}

/// <summary>One neighbor (ARP / NDP) entry seen on the interface.</summary>
public class GatewayNeighbor
{
    public required string IpAddress { get; init; }

    /// <summary>Link-layer address, or null for an INCOMPLETE/FAILED entry that never resolved.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Neighbor state: REACHABLE, STALE, DELAY, PROBE, FAILED, INCOMPLETE, PERMANENT.</summary>
    public string? State { get; init; }

    /// <summary>True when the kernel flagged the neighbor as a router.</summary>
    public bool IsRouter { get; init; }

    /// <summary>True for an IPv6 neighbor.</summary>
    public bool IsIpv6 { get; init; }

    /// <summary>OUI vendor for <see cref="MacAddress"/>, filled in by the service.</summary>
    public string? Vendor { get; set; }
}

/// <summary>One <c>ethtool -m</c> line: the field name and its reported value.</summary>
public record SfpModuleField(string Name, string Value);

/// <summary>Parsed <c>ethtool -m &lt;iface&gt;</c> transceiver / DDM readout.</summary>
public class SfpModuleInfo
{
    /// <summary>Every reported field, in the order ethtool printed them.</summary>
    public List<SfpModuleField> Fields { get; init; } = new();

    /// <summary>Field lookup by exact ethtool name.</summary>
    public string? Get(string name) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>
    /// The handful of fields worth leading with: what the module is, and the four
    /// digital-diagnostic readings that actually move when an optic is going bad.
    /// </summary>
    public IEnumerable<SfpModuleField> Highlights()
    {
        foreach (var name in HighlightOrder)
        {
            var value = Get(name);
            if (!string.IsNullOrWhiteSpace(value))
                yield return new SfpModuleField(FriendlyName(name), value);
        }
    }

    private static readonly string[] HighlightOrder =
    {
        "Vendor name",
        "Vendor PN",
        "Vendor SN",
        "Identifier",
        "Module temperature",
        "Module voltage",
        "Laser output power",
        "Receiver signal average optical power"
    };

    /// <summary>Shortens the two longest DDM labels so the summary table stays readable.</summary>
    private static string FriendlyName(string ethtoolName) => ethtoolName switch
    {
        "Vendor PN" => "Part number",
        "Vendor SN" => "Serial number",
        "Vendor name" => "Vendor",
        "Receiver signal average optical power" => "RX power",
        "Laser output power" => "TX power",
        _ => ethtoolName
    };
}

/// <summary>
/// Everything one gateway diagnostics run collected for a single interface. Each section
/// carries its own error text, because a command can be unavailable (no SFP in the port,
/// no ethtool on the box) without invalidating the rest of the run.
/// </summary>
public class GatewayDiagnosticsResult
{
    /// <summary>Interface the run targeted.</summary>
    public required string Interface { get; init; }

    /// <summary>When the commands ran (UTC). Lease countdowns are relative to this.</summary>
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;

    public GatewayInterfaceInfo? InterfaceInfo { get; set; }
    public string? InterfaceError { get; set; }

    public SfpModuleInfo? SfpModule { get; set; }
    public string? SfpError { get; set; }

    /// <summary>
    /// The port the transceiver was actually read from. Differs from <see cref="Interface"/>
    /// when a VLAN sub-interface was requested, since the module lives in its parent port.
    /// </summary>
    public string? SfpInterface { get; set; }

    public List<GatewayNeighbor> Neighbors { get; set; } = new();
    public string? NeighborError { get; set; }

    /// <summary>Set when the SSH round trip itself failed; the sections are then empty.</summary>
    public string? RunError { get; set; }

    /// <summary>Raw command output per section, for the "show raw output" disclosure.</summary>
    public Dictionary<string, string> RawOutput { get; } = new();
}

/// <summary>
/// Pure parsers for the read-only interface diagnostics a UniFi gateway can report. Kept
/// separate from <see cref="GatewayDiagnosticsService"/> (which owns the SSH round trip) so
/// the output shapes can be tested against real captures.
/// </summary>
public static class GatewayDiagnosticsParser
{
    // Interface names come from a dropdown but are also free-text editable, and they are
    // interpolated into a shell command - so anything outside the set Linux actually allows
    // for an interface name is rejected before it can reach the gateway.
    private static readonly Regex InterfaceNamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:@-]{0,30}$", RegexOptions.Compiled);

    /// <summary>True when the name is safe to interpolate into an SSH command.</summary>
    public static bool IsValidInterfaceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && InterfaceNamePattern.IsMatch(name);

    /// <summary>
    /// The physical port behind an interface name. A VLAN sub-interface ("ethN.100" for a
    /// tagged WAN) is a logical device with no transceiver of its own - the module sits in
    /// the parent port, and asking ethtool about the sub-interface only ever answers
    /// "Operation not supported". Names with no VLAN tag come back unchanged.
    /// </summary>
    public static string PhysicalInterfaceName(string interfaceName)
    {
        if (string.IsNullOrEmpty(interfaceName)) return interfaceName;
        var dot = interfaceName.IndexOf('.');
        return dot > 0 ? interfaceName[..dot] : interfaceName;
    }

    private static readonly Regex HeaderPattern = new(
        @"^\d+:\s+(?<name>[^:@]+)(@[^:]+)?:\s+<(?<flags>[^>]*)>(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex AddressPattern = new(
        @"^\s*inet6?\s+(?<cidr>\S+)",
        RegexOptions.Compiled);

    private static readonly Regex LifetimePattern = new(
        @"valid_lft\s+(?<valid>forever|\d+)(sec)?\s+preferred_lft\s+(?<pref>forever|\d+)(sec)?",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses <c>ip -d addr show dev &lt;iface&gt;</c>. The lifetime line follows its address
    /// line, so addresses are accumulated and the pending one is completed when the
    /// <c>valid_lft</c> line arrives. An address with no lifetime line at all (busybox ip, or
    /// the plain non-detailed form) still lands, just without lease data.
    /// </summary>
    public static GatewayInterfaceInfo? ParseAddressOutput(string? output, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? name = null, state = null, mac = null;
        int? mtu = null;
        var addresses = new List<GatewayInterfaceAddress>();

        // Fields of the address line currently awaiting its valid_lft/preferred_lft line.
        string? pendingCidr = null, pendingBroadcast = null, pendingScope = null;
        bool pendingIsIpv6 = false, pendingIsDynamic = false;

        void FlushPending(long? validSec, long? preferredSec)
        {
            if (pendingCidr == null) return;
            var slash = pendingCidr.IndexOf('/');
            var address = slash > 0 ? pendingCidr[..slash] : pendingCidr;
            int? prefix = slash > 0 && int.TryParse(pendingCidr[(slash + 1)..], out var p) ? p : null;
            addresses.Add(new GatewayInterfaceAddress
            {
                IsIpv6 = pendingIsIpv6,
                Cidr = pendingCidr,
                Address = address,
                PrefixLength = prefix,
                SubnetMask = !pendingIsIpv6 && prefix.HasValue ? PrefixToMask(prefix.Value) : null,
                Broadcast = pendingBroadcast,
                Scope = pendingScope,
                IsDynamic = pendingIsDynamic,
                ValidLifetimeSeconds = validSec,
                PreferredLifetimeSeconds = preferredSec
            });
            pendingCidr = null;
            pendingBroadcast = null;
            pendingScope = null;
            pendingIsIpv6 = false;
            pendingIsDynamic = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var header = HeaderPattern.Match(trimmed);
            if (header.Success)
            {
                FlushPending(null, null);
                name = header.Groups["name"].Value.Trim();
                var rest = header.Groups["rest"].Value;
                var mtuMatch = Regex.Match(rest, @"\bmtu\s+(\d+)");
                if (mtuMatch.Success && int.TryParse(mtuMatch.Groups[1].Value, out var m)) mtu = m;
                var stateMatch = Regex.Match(rest, @"\bstate\s+(\S+)");
                if (stateMatch.Success) state = stateMatch.Groups[1].Value;
                continue;
            }

            if (trimmed.StartsWith("link/", StringComparison.Ordinal))
            {
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && parts[1].Contains(':')) mac = parts[1];
                continue;
            }

            var lifetime = LifetimePattern.Match(trimmed);
            if (lifetime.Success)
            {
                FlushPending(ParseLifetime(lifetime.Groups["valid"].Value),
                             ParseLifetime(lifetime.Groups["pref"].Value));
                continue;
            }

            var addr = AddressPattern.Match(line);
            if (addr.Success)
            {
                // Previous address had no lifetime line of its own.
                FlushPending(null, null);
                pendingIsIpv6 = trimmed.StartsWith("inet6", StringComparison.Ordinal);
                pendingCidr = addr.Groups["cidr"].Value;
                var brd = Regex.Match(trimmed, @"\bbrd\s+(\S+)");
                if (brd.Success) pendingBroadcast = brd.Groups[1].Value;
                var scope = Regex.Match(trimmed, @"\bscope\s+(\S+)");
                if (scope.Success) pendingScope = scope.Groups[1].Value;
                pendingIsDynamic = Regex.IsMatch(trimmed, @"\bdynamic\b");
            }
        }

        FlushPending(null, null);

        if (name == null && addresses.Count == 0) return null;

        return new GatewayInterfaceInfo
        {
            Name = name ?? fallbackName,
            State = state,
            Mtu = mtu,
            MacAddress = mac,
            // IPv4 first: on a WAN the v4 lease is what the user came for.
            Addresses = addresses.OrderBy(a => a.IsIpv6).ToList()
        };
    }

    private static long? ParseLifetime(string value) =>
        long.TryParse(value, out var seconds) ? seconds : null;

    /// <summary>Dotted-quad mask for an IPv4 prefix length (24 -&gt; 255.255.255.0).</summary>
    public static string? PrefixToMask(int prefixLength)
    {
        if (prefixLength < 0 || prefixLength > 32) return null;
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        return new IPAddress(new[]
        {
            (byte)(mask >> 24), (byte)(mask >> 16), (byte)(mask >> 8), (byte)mask
        }).ToString();
    }

    /// <summary>
    /// Parses <c>ethtool -m &lt;iface&gt;</c>. Every reported line is "Field : value" with the
    /// field padded out to a fixed width; anything without a colon (the leading blank line, a
    /// hex dump when the module has no DDM page) is skipped rather than guessed at.
    ///
    /// Returns null unless the result actually looks like a transceiver readout, because
    /// ethtool's failure messages are themselves colon-separated ("Cannot get module EEPROM
    /// information: Operation not supported") and would otherwise render as a module field on
    /// every copper port. A real readout always leads with Identifier and runs to dozens of
    /// fields; the failure messages are one line.
    /// </summary>
    public static SfpModuleInfo? ParseEthtoolModuleOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var info = new SfpModuleInfo();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon <= 0 || colon == line.Length - 1) continue;

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0) continue;

            // "Identifier : 0x03 (SFP)" is a field; an EEPROM hex dump line
            // ("0x0010: 00 00 ...") is not.
            if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;

            info.Fields.Add(new SfpModuleField(name, value));
        }

        var looksLikeModule = info.Get("Identifier") != null || info.Fields.Count >= 3;
        return looksLikeModule ? info : null;
    }

    private static readonly Regex NeighborPattern = new(
        @"^(?<ip>\S+)(?:\s+dev\s+\S+)?(?:\s+lladdr\s+(?<mac>[0-9a-fA-F:]+))?(?<flags>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses <c>ip neigh show dev &lt;iface&gt;</c>. Entries that never resolved (FAILED,
    /// INCOMPLETE) carry no lladdr and are kept - an unresolved WAN gateway is itself the
    /// answer to "why is the link up but nothing works".
    /// </summary>
    public static List<GatewayNeighbor> ParseNeighborOutput(string? output)
    {
        var neighbors = new List<GatewayNeighbor>();
        if (string.IsNullOrWhiteSpace(output)) return neighbors;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var match = NeighborPattern.Match(line);
            if (!match.Success) continue;

            var ip = match.Groups["ip"].Value;
            if (!IPAddress.TryParse(ip, out var parsed)) continue;

            var flags = match.Groups["flags"].Value;
            var state = flags
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(t => t.All(char.IsUpper) && t.Length > 2);

            neighbors.Add(new GatewayNeighbor
            {
                IpAddress = ip,
                MacAddress = match.Groups["mac"].Success ? match.Groups["mac"].Value : null,
                State = state,
                IsRouter = Regex.IsMatch(flags, @"\brouter\b"),
                IsIpv6 = parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            });
        }

        // Resolved neighbors first, then by address - a FAILED entry is worth seeing but
        // shouldn't push the real next hop off the top of the list.
        return neighbors
            .OrderBy(n => n.MacAddress == null)
            .ThenBy(n => n.IsIpv6)
            .ThenBy(n => n.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
