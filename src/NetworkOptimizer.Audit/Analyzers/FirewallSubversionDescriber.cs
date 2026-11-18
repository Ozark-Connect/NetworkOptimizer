using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>How much of a later deny rule an earlier, overlapping allow rule takes over.</summary>
public enum SubversionExtent
{
    /// <summary>The allow matches everything the deny matches, so the deny never matches.</summary>
    Total,

    /// <summary>The allow takes only a slice of the deny's traffic; the slice is described.</summary>
    Partial,

    /// <summary>The allow narrows the deny in a way that cannot be described (apps, inverted lists, unresolved groups).</summary>
    Unknown
}

/// <summary>The extent of a subversion and, for <see cref="SubversionExtent.Partial"/>, the slice text.</summary>
/// <param name="Extent">How much of the deny the allow takes over.</param>
/// <param name="Slice">The traffic taken, e.g. "TCP/UDP port 53 traffic from 2 clients". Null unless partial.</param>
/// <param name="FewPorts">Partial, and limited to at most three destination ports: likely a deliberate exception.</param>
public sealed record SubversionDescription(SubversionExtent Extent, string? Slice, bool FewPorts = false);

/// <summary>
/// Describes the traffic an earlier allow rule takes away from a later deny rule it overlaps.
/// The slice is every constraint the allow has that the deny lacks; the deny's own constraints
/// are implied by naming the deny. Callers check overlap first (<see cref="FirewallRuleOverlapDetector"/>).
/// </summary>
public static class FirewallSubversionDescriber
{
    private const int MaxListedItems = 3;

    // Matches the "few specific ports" tier in FirewallRuleOverlapDetector's scope scoring.
    private const int FewPortsLimit = 3;

    /// <summary>
    /// Classify how much of <paramref name="deny"/> the earlier <paramref name="allow"/> covers.
    /// A dimension is "covered" only when the allow provably matches all of the deny there; anything
    /// unprovable is either described (when the allow's constraint can be rendered) or Unknown.
    /// </summary>
    public static SubversionDescription Describe(FirewallRule allow, FirewallRule deny, List<NetworkInfo>? networks = null)
    {
        var slice = new Slice();

        var describable =
            ZoneCovered(allow.SourceZoneId, deny.SourceZoneId) &&
            ZoneCovered(allow.DestinationZoneId, deny.DestinationZoneId) &&
            StatesCovered(allow, deny) &&
            DescribeIpVersion(allow, deny, slice) &&
            DescribeProtocol(allow, deny, slice) &&
            DescribeDestinationPort(allow, deny, slice) &&
            DescribeSourcePort(allow, deny, slice) &&
            DescribeSource(allow, deny, networks, slice) &&
            DescribeDestination(allow, deny, networks, slice);

        if (!describable)
            return new SubversionDescription(SubversionExtent.Unknown, null);

        if (!allow.IsAlwaysOn)
            slice.Schedule = true;

        return slice.IsEmpty
            ? new SubversionDescription(SubversionExtent.Total, null)
            : new SubversionDescription(SubversionExtent.Partial, slice.ToString(),
                slice.PortCount is > 0 and <= FewPortsLimit);
    }

    private sealed class Slice
    {
        public string? IpVersion;
        public string? Protocol;
        public string? Port;
        public int PortCount;
        public string? From;
        public string? To;
        public string? SourcePort;
        public bool Schedule;

        public bool IsEmpty => IpVersion == null && Protocol == null && Port == null &&
                               From == null && To == null && SourcePort == null && !Schedule;

        // "IPv6 TCP/UDP port 53 traffic from 2 clients to 1.1.1.1 with source port 1234, during the allow rule's schedule"
        public override string ToString()
        {
            var head = string.Join(" ", new[] { IpVersion, Protocol, Port, "traffic" }.Where(p => p != null));
            var tail = new[] { From == null ? null : $"from {From}", To == null ? null : $"to {To}",
                               SourcePort == null ? null : $"with source {SourcePort}" }.Where(p => p != null);
            var text = string.Join(" ", new[] { head }.Concat(tail));
            return Schedule ? $"{text}, during the allow rule's schedule" : text;
        }
    }

    // An empty zone matches every zone, so only "allow pinned, deny unpinned" leaves something uncovered.
    private static bool ZoneCovered(string? allowZone, string? denyZone) =>
        string.IsNullOrEmpty(allowZone) || string.Equals(allowZone, denyZone, StringComparison.OrdinalIgnoreCase);

    private static bool StatesCovered(FirewallRule allow, FirewallRule deny) =>
        deny.MatchedConnectionStates().IsSubsetOf(allow.MatchedConnectionStates());

    private static bool DescribeIpVersion(FirewallRule allow, FirewallRule deny, Slice slice)
    {
        if (deny.MatchesIpFamily(IpFamily.IPv4) && !allow.MatchesIpFamily(IpFamily.IPv4))
            slice.IpVersion = "IPv6";
        else if (deny.MatchesIpFamily(IpFamily.IPv6) && !allow.MatchesIpFamily(IpFamily.IPv6))
            slice.IpVersion = "IPv4";
        return true;
    }

    private static string NormalizedProtocol(FirewallRule rule) =>
        string.IsNullOrEmpty(rule.Protocol) ? "all" : rule.Protocol.ToLowerInvariant();

    // tcp_udp contains tcp and udp; otherwise a protocol contains only itself.
    private static bool ProtocolContains(string outer, string inner) =>
        outer == "all" || outer == inner || (outer == "tcp_udp" && inner is "tcp" or "udp");

    private static bool IsPortProtocol(string protocol) => protocol is "all" or "tcp" or "udp" or "tcp_udp";

    private static bool DescribeProtocol(FirewallRule allow, FirewallRule deny, Slice slice)
    {
        var allowProtocol = NormalizedProtocol(allow);
        var denyProtocol = NormalizedProtocol(deny);

        if (allow.MatchOppositeProtocol)
        {
            // "Everything but X" covers a deny on one protocol that is not X; anything else is not renderable.
            return !deny.MatchOppositeProtocol && denyProtocol != "all" && !ProtocolContains(allowProtocol, denyProtocol);
        }

        if (allowProtocol == "all")
            return true;

        var covered = !deny.MatchOppositeProtocol && ProtocolContains(allowProtocol, denyProtocol);
        if (!covered)
            slice.Protocol = ProtocolDisplay(allowProtocol);

        // A specific ICMP type is narrower than the same protocol with any type.
        if (allowProtocol is "icmp" or "icmpv6" && IsSpecificIcmpType(allow.IcmpTypename) &&
            !(covered && string.Equals(allow.IcmpTypename, deny.IcmpTypename, StringComparison.OrdinalIgnoreCase)))
        {
            slice.Protocol = $"{ProtocolDisplay(allowProtocol)} {allow.IcmpTypename!.ToLowerInvariant()}";
        }

        return true;
    }

    private static bool IsSpecificIcmpType(string? typename) =>
        !string.IsNullOrEmpty(typename) && !string.Equals(typename, "ANY", StringComparison.OrdinalIgnoreCase);

    private static string ProtocolDisplay(string protocol) => protocol switch
    {
        "tcp_udp" => "TCP/UDP",
        "icmpv6" => "ICMPv6",
        _ => protocol.ToUpperInvariant()
    };

    private static bool DescribeDestinationPort(FirewallRule allow, FirewallRule deny, Slice slice)
    {
        // Ports mean nothing on a non-port protocol (ICMP), whatever the field holds.
        if (!allow.MatchOppositeProtocol && !IsPortProtocol(NormalizedProtocol(allow)))
            return true;

        return DescribePorts(
            allow.DestinationPort, allow.DestinationMatchOppositePorts, allow.HasUnresolvedDestinationPortGroup,
            deny.DestinationPort, deny.DestinationMatchOppositePorts, deny.HasUnresolvedDestinationPortGroup,
            (text, count) => { slice.Port = text; slice.PortCount = count; });
    }

    private static bool DescribeSourcePort(FirewallRule allow, FirewallRule deny, Slice slice)
    {
        if (!allow.MatchOppositeProtocol && !IsPortProtocol(NormalizedProtocol(allow)))
            return true;

        return DescribePorts(
            allow.SourcePort, allow.SourceMatchOppositePorts, false,
            deny.SourcePort, deny.SourceMatchOppositePorts, false,
            (text, _) => slice.SourcePort = text);
    }

    private static bool DescribePorts(
        string? allowPorts, bool allowOpposite, bool allowUnresolved,
        string? denyPorts, bool denyOpposite, bool denyUnresolved,
        Action<string, int> setSlice)
    {
        if (allowUnresolved)
            return false;
        if (string.IsNullOrEmpty(allowPorts))
            return true;

        var allowSet = FirewallRuleOverlapDetector.ParsePortString(allowPorts);
        var denyKnown = !string.IsNullOrEmpty(denyPorts) && !denyUnresolved;
        var denySet = denyKnown ? FirewallRuleOverlapDetector.ParsePortString(denyPorts!) : new HashSet<int>();

        if (allowOpposite)
        {
            // "Every port but these" covers a deny on listed ports none of which are excluded.
            return denyKnown && !denyOpposite && !denySet.Overlaps(allowSet);
        }

        if (!denyKnown)
        {
            setSlice(FormatPorts(allowSet), allowSet.Count);
            return true;
        }

        var taken = denyOpposite
            ? allowSet.Where(p => !denySet.Contains(p)).ToHashSet()
            : allowSet.Where(denySet.Contains).ToHashSet();

        if (!denyOpposite && taken.SetEquals(denySet))
            return true;

        var named = taken.Count > 0 ? taken : allowSet;
        setSlice(FormatPorts(named), named.Count);
        return true;
    }

    /// <summary>"port 53", "ports 53, 853", "ports 8000-8080".</summary>
    internal static string FormatPorts(IEnumerable<int> ports)
    {
        var sorted = ports.Distinct().OrderBy(p => p).ToList();
        var ranges = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var start = sorted[i];
            var end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] == end + 1)
                end = sorted[++i];
            ranges.Add(start == end ? $"{start}" : $"{start}-{end}");
            i++;
        }
        return (sorted.Count == 1 ? "port " : "ports ") + string.Join(", ", ranges);
    }

    private static bool DescribeSource(FirewallRule allow, FirewallRule deny, List<NetworkInfo>? networks, Slice slice)
    {
        var allowTarget = allow.SourceMatchingTarget?.ToUpperInvariant() ?? "ANY";
        var denyTarget = deny.SourceMatchingTarget?.ToUpperInvariant() ?? "ANY";

        return DescribeEndpoint(
            allowTarget, denyTarget,
            allow.SourceNetworkIds, allow.SourceMatchOppositeNetworks, deny.SourceNetworkIds, deny.SourceMatchOppositeNetworks,
            allow.SourceIps, allow.SourceMatchOppositeIps, deny.SourceIps, deny.SourceMatchOppositeIps,
            allow.SourceClientMacs, deny.SourceClientMacs,
            networks, text => slice.From = text);
    }

    private static bool DescribeDestination(FirewallRule allow, FirewallRule deny, List<NetworkInfo>? networks, Slice slice)
    {
        var allowTarget = allow.DestinationMatchingTarget?.ToUpperInvariant() ?? "ANY";
        var denyTarget = deny.DestinationMatchingTarget?.ToUpperInvariant() ?? "ANY";

        var allowHasApps = allow.AppIds?.Count > 0 || allow.AppCategoryIds?.Count > 0;
        if (allowHasApps)
        {
            // DPI matches cannot be rendered; only the same-or-wider app list on both sides is provably covered.
            var denyApps = deny.AppIds ?? new List<int>();
            var denyCats = deny.AppCategoryIds ?? new List<int>();
            return denyApps.Count + denyCats.Count > 0 &&
                   denyApps.All(a => allow.AppIds?.Contains(a) == true) &&
                   denyCats.All(c => allow.AppCategoryIds?.Contains(c) == true);
        }

        if (allowTarget == "WEB" || allow.WebDomains?.Count > 0)
        {
            if (allow.HasUnresolvedDestinationDomainGroup)
                return false;
            var allowDomains = allow.WebDomains ?? new List<string>();
            if (denyTarget == "WEB" && deny.WebDomains?.Count > 0 && !deny.HasUnresolvedDestinationDomainGroup)
            {
                if (deny.WebDomains.All(d => allowDomains.Any(a => DomainWithin(d, a))))
                    return true;
                var taken = allowDomains.Where(a => deny.WebDomains.Any(d => DomainWithin(a, d) || DomainWithin(d, a))).ToList();
                slice.To = ListText(taken.Count > 0 ? taken : allowDomains, "domain", "domains");
                return true;
            }
            slice.To = ListText(allowDomains, "domain", "domains");
            return true;
        }

        return DescribeEndpoint(
            allowTarget, denyTarget,
            allow.DestinationNetworkIds, allow.DestinationMatchOppositeNetworks, deny.DestinationNetworkIds, deny.DestinationMatchOppositeNetworks,
            allow.DestinationIps, allow.DestinationMatchOppositeIps, deny.DestinationIps, deny.DestinationMatchOppositeIps,
            null, null,
            networks, text => slice.To = text);
    }

    private static bool DomainWithin(string domain, string parent) =>
        domain.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);

    /// <summary>Shared source/destination logic for ANY, NETWORK, IP, and CLIENT targets.</summary>
    private static bool DescribeEndpoint(
        string allowTarget, string denyTarget,
        List<string>? allowNets, bool allowNetsOpposite, List<string>? denyNets, bool denyNetsOpposite,
        List<string>? allowIps, bool allowIpsOpposite, List<string>? denyIps, bool denyIpsOpposite,
        List<string>? allowMacs, List<string>? denyMacs,
        List<NetworkInfo>? networks, Action<string> setSlice)
    {
        switch (allowTarget)
        {
            case "ANY":
                return true;

            case "NETWORK":
            {
                if (allowNetsOpposite)
                    return false;
                var nets = allowNets ?? new List<string>();
                if (denyTarget == "NETWORK" && !denyNetsOpposite)
                {
                    var denyList = denyNets ?? new List<string>();
                    if (denyList.All(d => nets.Contains(d, StringComparer.OrdinalIgnoreCase)))
                        return true;
                    var shared = nets.Where(n => denyList.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (shared.Count > 0)
                        nets = shared;
                }
                else if (denyTarget == "IP" && !denyIpsOpposite && denyIps?.Count > 0 &&
                         denyIps.All(ip => nets.Any(n => IpInNetwork(ip, n, networks))))
                {
                    return true;
                }
                setSlice(NetworkListText(nets, networks));
                return true;
            }

            case "IP":
            {
                if (allowIpsOpposite)
                    return false;
                var ips = allowIps ?? new List<string>();
                if (denyTarget == "IP" && !denyIpsOpposite && denyIps?.Count > 0)
                {
                    if (denyIps.All(d => ips.Any(a => IpWithin(d, a))))
                        return true;
                    var shared = ips.Where(a => denyIps.Any(d => IpWithin(a, d) || IpWithin(d, a))).ToList();
                    if (shared.Count > 0)
                        ips = shared;
                }
                setSlice(ListText(ips, "address", "addresses"));
                return true;
            }

            case "CLIENT":
            {
                var macs = allowMacs ?? new List<string>();
                if (denyTarget == "CLIENT")
                {
                    var denyList = denyMacs ?? new List<string>();
                    if (denyList.All(d => macs.Contains(d, StringComparer.OrdinalIgnoreCase)))
                        return true;
                    var shared = macs.Where(m => denyList.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList();
                    if (shared.Count > 0)
                        macs = shared;
                }
                setSlice(macs.Count == 1 ? "1 client" : $"{macs.Count} clients");
                return true;
            }

            default:
                // REGION, INTERNET_CATEGORY, and anything newer: not renderable.
                return false;
        }
    }

    // An entry is within another when equal, or when it is an address (or smaller CIDR) inside the other's CIDR.
    private static bool IpWithin(string inner, string outer) =>
        inner.Equals(outer, StringComparison.OrdinalIgnoreCase) ||
        (FirewallRuleOverlapDetector.IpMatchesCidr(inner, outer) && (!inner.Contains('/') || PrefixLength(inner) >= PrefixLength(outer)));

    private static int PrefixLength(string cidr) =>
        cidr.Contains('/') && int.TryParse(cidr.Split('/')[1], out var p) ? p : int.MaxValue;

    private static bool IpInNetwork(string ip, string networkId, List<NetworkInfo>? networks)
    {
        var subnet = networks?.FirstOrDefault(n => string.Equals(n.Id, networkId, StringComparison.OrdinalIgnoreCase))?.Subnet;
        return !string.IsNullOrEmpty(subnet) && IpWithin(ip, subnet);
    }

    private static string NetworkListText(List<string> networkIds, List<NetworkInfo>? networks)
    {
        var names = networkIds
            .Select(id => networks?.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))?.Name)
            .ToList();
        return names.All(n => n != null)
            ? ListText(names!, "network", "networks")
            : networkIds.Count == 1 ? "1 network" : $"{networkIds.Count} networks";
    }

    // Up to three items by name ("Home, IoT"), otherwise a count ("5 networks").
    private static string ListText(IReadOnlyCollection<string> items, string singular, string plural)
    {
        if (items.Count > 0 && items.Count <= MaxListedItems)
            return string.Join(", ", items);
        return items.Count == 1 ? $"1 {singular}" : $"{items.Count} {plural}";
    }
}
