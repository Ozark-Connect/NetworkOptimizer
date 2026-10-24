using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>One Allow policy an exception needs on the UniFi console, keyed so an unchanged one is kept.</summary>
/// <param name="Key">The exception, its content, the policy's role, zone, and IP version.</param>
public sealed record ExceptionPolicySpec(string Key, string SourceZoneId, string DestinationZoneId, UniFiGroupBlockPolicyWrite Body);

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
    /// <param name="zones">Every zone but External: (id, whether it is the Gateway zone).</param>
    /// <param name="outbound">Whether the outbound public list policies exist.</param>
    public static List<ExceptionPolicySpec> Build(IEnumerable<BlockingException> exceptions, GatewayBlocklistState state,
        string externalZoneId, IReadOnlyList<(string Id, bool IsGateway)> zones, bool outbound)
    {
        var specs = new List<ExceptionPolicySpec>();
        foreach (var e in exceptions)
        {
            var hash = Hash(e);
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
            foreach (var v6 in e.IsV6 is { } only ? [only] : new[] { false, true })
            {
                var version = v6 ? "IPV6" : "IPV4";
                var feeds = v6 ? state.FeedsV6Id : state.FeedsV4Id;
                var feedsOut = v6 ? state.FeedsOutV6Id : state.FeedsOutV4Id;
                // Without the list's Network List, "every listed address" would become every address.
                if (address == null && feeds == null) continue;
                void Add(string role, string zone, string sourceZone, string destinationZone,
                    Dictionary<string, object> source, Dictionary<string, object> destination, IReadOnlyList<string>? states) =>
                    specs.Add(new ExceptionPolicySpec($"{e.Id}:{hash}:{role}:{zone}:{version}", sourceZone, destinationZone,
                        UniFiGroupBlockPolicyWrite.CreateAllow(PolicyName, Description, version, protocol, source, destination, states)));

                if (e.Direction == ExceptionDirection.Outbound)
                {
                    foreach (var (zone, isGateway) in zones)
                    {
                        if (isGateway) continue;
                        if (outbound && feedsOut != null)
                            Add("out", zone, zone, externalZoneId,
                                UniFiGroupBlockPolicyWrite.Endpoint(zone, device, null, null, isSource: true),
                                UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feedsOut, port, isSource: false), null);
                        Add("out-reply", zone, externalZoneId, zone,
                            UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feeds, port, isSource: true),
                            UniFiGroupBlockPolicyWrite.Endpoint(zone, device, null, null, isSource: false),
                            UniFiGroupBlockPolicyWrite.RepliesOnly);
                    }
                }
                else
                {
                    foreach (var (zone, isGateway) in zones)
                    {
                        Add("in", zone, externalZoneId, zone,
                            UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feeds, null, isSource: true),
                            UniFiGroupBlockPolicyWrite.Endpoint(zone, device, null, port, isSource: false), null);
                        // The outbound policies leave the Gateway zone out, so its replies need nothing.
                        if (outbound && feedsOut != null && !isGateway)
                            Add("in-reply", zone, zone, externalZoneId,
                                UniFiGroupBlockPolicyWrite.Endpoint(zone, device, null, port, isSource: true),
                                UniFiGroupBlockPolicyWrite.Endpoint(externalZoneId, address, feedsOut, null, isSource: false),
                                UniFiGroupBlockPolicyWrite.RepliesOnly);
                    }
                }
            }
        }
        return specs;
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

    // Short and stable: an edited exception gets new policies, an unchanged one keeps its own.
    private static string Hash(BlockingException e) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{e.Direction}|{e.Device}|{e.Address}|{e.Protocol}|{e.PortText}")))[..8];
}
