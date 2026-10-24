using System.Net;
using System.Text.Json;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// One Allow policy an exception needs on the UniFi console. <see cref="Key"/> names which policy it is
/// (the exception, its role, zone, and IP version), so an edited exception updates its policies in place;
/// <see cref="Hash"/> is its content, so an unchanged one is never written.
/// </summary>
public sealed record ExceptionPolicySpec(string Key, string SourceZoneId, string DestinationZoneId, UniFiGroupBlockPolicyWrite Body)
{
    public string Hash { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(Body)))[..16];
}

/// <summary>A zone other than External, with the subnets of its networks (IP/prefix as UniFi stores them).</summary>
public sealed record ExceptionZone(string Id, bool IsGateway, IReadOnlyList<string> Subnets)
{
    public ExceptionZone(string id, bool isGateway) : this(id, isGateway, []) { }
}

/// <summary>What the console needs to match a site's exception policies to their specs.</summary>
public sealed record ExceptionPolicyPlan(
    IReadOnlyList<GatewayExceptionPolicyRef> Keep,
    IReadOnlyList<(GatewayExceptionPolicyRef Ref, ExceptionPolicySpec Spec)> Update,
    IReadOnlyList<ExceptionPolicySpec> Create,
    IReadOnlyList<GatewayExceptionPolicyRef> Delete);

/// <summary>
/// The console Allow policies for a site's exceptions. Each exception gets the policy that lets it
/// through, per zone and IP version, plus one for its replies: the public list Block policies match
/// every connection state, so without it the replies would be dropped. Each policy is scoped to the
/// public list Network Lists when no address is given, so it never opens what no list blocks.
/// </summary>
public static class GatewayExceptionPolicies
{
    public const string PolicyName = "Net. Opt. Exception";
    public const string Description = "Managed by Network Optimizer: an exception to the public block lists. Changes here are overwritten.";

    /// <param name="externalZoneId">The External zone.</param>
    /// <param name="zones">Every zone but External.</param>
    /// <param name="outbound">Whether the outbound public list policies exist.</param>
    public static List<ExceptionPolicySpec> Build(IEnumerable<BlockingException> exceptions, GatewayBlocklistState state,
        string externalZoneId, IReadOnlyList<ExceptionZone> zones, bool outbound)
    {
        var specs = new List<ExceptionPolicySpec>();
        foreach (var e in exceptions)
        {
            var protocol = e.Protocol switch
            {
                ExceptionProtocol.Tcp => "tcp",
                ExceptionProtocol.Udp => "udp",
                ExceptionProtocol.TcpUdp => "tcp_udp",
                _ => "all",
            };
            var port = e.PortText.Length > 0 ? e.PortText : null;
            IReadOnlyList<string>? device = e.Device == null ? null : [e.Device];
            IReadOnlyList<string>? address = e.Address == null ? null : [e.Address];
            var deviceZones = ZonesOf(e.Device, zones);
            foreach (var v6 in e.IsV6 is { } only ? [only] : new[] { false, true })
            {
                var version = v6 ? "IPV6" : "IPV4";
                var feeds = v6 ? state.FeedsV6Id : state.FeedsV4Id;
                var feedsOut = v6 ? state.FeedsOutV6Id : state.FeedsOutV4Id;
                // Without the list's Network List, "every listed address" would become every address.
                if (address == null && feeds == null) continue;
                void Add(string role, string zone, string sourceZone, string destinationZone,
                    Dictionary<string, object> source, Dictionary<string, object> destination, IReadOnlyList<string>? states) =>
                    specs.Add(new ExceptionPolicySpec($"{e.Id}:{role}:{zone}:{version}", sourceZone, destinationZone,
                        UniFiGroupBlockPolicyWrite.CreateAllow(PolicyName, Description, version, protocol, source, destination, states)));

                if (e.Direction == ExceptionDirection.Outbound)
                {
                    foreach (var zone in deviceZones)
                    {
                        if (zone.IsGateway) continue;
                        if (outbound && feedsOut != null)
                            Add("out", zone.Id, zone.Id, externalZoneId,
                                UniFiGroupBlockPolicyWrite.Endpoint(zone.Id, device, null, null, isSource: true),
                                UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feedsOut, port, isSource: false), null);
                        Add("out-reply", zone.Id, externalZoneId, zone.Id,
                            UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feeds, port, isSource: true),
                            UniFiGroupBlockPolicyWrite.Endpoint(zone.Id, device, null, null, isSource: false),
                            UniFiGroupBlockPolicyWrite.RepliesOnly);
                    }
                }
                else
                {
                    // The Gateway zone always stays: a device on a LAN subnet may be the gateway itself,
                    // whose own services are reached in that zone.
                    foreach (var zone in zones.Where(z => z.IsGateway || deviceZones.Contains(z)))
                    {
                        Add("in", zone.Id, externalZoneId, zone.Id,
                            UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feeds, null, isSource: true),
                            UniFiGroupBlockPolicyWrite.Endpoint(zone.Id, device, null, port, isSource: false), null);
                        // The outbound policies leave the Gateway zone out, so its replies need nothing.
                        if (outbound && feedsOut != null && !zone.IsGateway)
                            Add("in-reply", zone.Id, zone.Id, externalZoneId,
                                UniFiGroupBlockPolicyWrite.Endpoint(zone.Id, device, null, port, isSource: true),
                                UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feedsOut, null, isSource: false),
                                UniFiGroupBlockPolicyWrite.RepliesOnly);
                    }
                }
            }
        }
        return specs;
    }

    /// <summary>
    /// The zones whose networks hold <paramref name="device"/> (a VPN server's client range places its
    /// clients in the VPN zone); <paramref name="zones"/> itself when it is not set or no network holds
    /// it (an IPv6 prefix UniFi delegates), so a device the subnets do not place is never left out.
    /// </summary>
    public static IReadOnlyList<ExceptionZone> ZonesOf(string? device, IReadOnlyList<ExceptionZone> zones)
    {
        if (device == null || ToNetwork(device) is not { } range) return zones;
        var holding = zones.Where(z => z.Subnets.Any(s => ToNetwork(s) is { } n && Overlaps(n, range))).ToList();
        return holding.Count > 0 ? holding : zones;
    }

    /// <summary>
    /// How the console's exception policies change to match <paramref name="specs"/>. A policy keeps its
    /// id while its key is wanted: unchanged it is kept, changed it is updated in place. Only a key no
    /// longer wanted is deleted, and only a key not on the console is created.
    /// </summary>
    /// <param name="current">The policies recorded last time.</param>
    /// <param name="present">The policy ids the console holds now; a recorded policy not among them is gone.</param>
    public static ExceptionPolicyPlan Plan(IReadOnlyList<GatewayExceptionPolicyRef> current, IReadOnlySet<string> present,
        IReadOnlyList<ExceptionPolicySpec> specs)
    {
        var available = current.Where(r => present.Contains(r.Id)).ToList();
        var keep = new List<GatewayExceptionPolicyRef>();
        var update = new List<(GatewayExceptionPolicyRef, ExceptionPolicySpec)>();
        var create = new List<ExceptionPolicySpec>();
        foreach (var spec in specs)
        {
            var existing = available.FirstOrDefault(r => r.Key == spec.Key);
            if (existing == null)
            {
                create.Add(spec);
                continue;
            }
            available.Remove(existing);
            if (existing.Hash == spec.Hash) keep.Add(existing);
            else update.Add((existing, spec));
        }
        return new ExceptionPolicyPlan(keep, update, create, available);
    }

    /// <summary>
    /// The batch-reorder lists for one zone pair: the policies in <paramref name="tiers"/> first, tier by
    /// tier, each in its current order; then the other custom policies before UniFi's predefined ones, as
    /// they were. Null when that is already the order, so a steady console is never written.
    /// Port-forward policies (custom, index 30000-39999) are UniFi's to place and are left out.
    /// </summary>
    public static (List<string> Before, List<string> After)? Order(IEnumerable<UniFiFirewallPolicy> policies,
        string sourceZoneId, string destinationZoneId, params IReadOnlySet<string>[] tiers)
    {
        var custom = policies
            .Where(p => !p.Predefined && p.Source.ZoneId == sourceZoneId && p.Destination.ZoneId == destinationZoneId
                        && (p.Index < 30000 || p.Index >= 40000))
            .OrderBy(p => p.Index)
            .ToList();
        var before = custom.Where(p => p.Index < 30000).Select(p => p.Id).ToList();
        var after = custom.Where(p => p.Index >= 40000).Select(p => p.Id).ToList();
        var ordered = new List<string>();
        foreach (var tier in tiers)
            ordered.AddRange(before.Where(id => tier.Contains(id) && !ordered.Contains(id)));
        ordered.AddRange(before.Where(id => !ordered.Contains(id)));
        return ordered.SequenceEqual(before) ? null : (ordered, after);
    }

    // UniFi stores a network's subnet as its gateway address with the prefix (192.168.2.1/24).
    private static IPNetwork? ToNetwork(string value) =>
        PublicBlockLists.Normalize(value) is { } n && IPNetwork.TryParse(n.Contains('/') ? n : n + (n.Contains(':') ? "/128" : "/32"), out var net)
            ? net : null;

    private static bool Overlaps(IPNetwork a, IPNetwork b) =>
        a.BaseAddress.AddressFamily == b.BaseAddress.AddressFamily
        && (a.Contains(b.BaseAddress) || b.Contains(a.BaseAddress));
}
