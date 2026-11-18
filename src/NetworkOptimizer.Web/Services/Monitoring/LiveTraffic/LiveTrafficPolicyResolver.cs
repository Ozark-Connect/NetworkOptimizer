using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Web.Services.ChangeControl;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// Which firewall policy lets a live flow through, for flows UniFi Network has not reported yet (it writes a
/// flow's record when the flow ends). Reads the site's networks, zones, policies, and groups, and decides
/// with the Audit (<see cref="FirewallRuleParser"/> through <see cref="ZonePaths"/>, then
/// <see cref="FirewallRuleEvaluator.DecideFlow"/>). A rule it cannot judge ahead of the match leaves the
/// answer unknown rather than naming a later rule.
/// </summary>
[VendorSpecific("UniFi", "v2 zone-based firewall policies, evaluated for one live connection")]
public sealed class LiveTrafficPolicyResolver : ISiteScopedRegistry
{
    private static readonly TimeSpan ConfigKept = TimeSpan.FromMinutes(5);
    private static readonly string[] Collections = ["networkconf", "firewall_zone", "firewall_policy", "firewallgroup"];

    private readonly SiteConnectionRegistry _connections;
    private readonly GeoEnrichmentService _geo;
    private readonly ILogger<LiveTrafficPolicyResolver> _logger;
    private readonly ConcurrentDictionary<string, SiteRules> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loading = new(StringComparer.OrdinalIgnoreCase);

    public LiveTrafficPolicyResolver(SiteConnectionRegistry connections, GeoEnrichmentService geo, ILogger<LiveTrafficPolicyResolver> logger)
    {
        _connections = connections;
        _geo = geo;
        _logger = logger;
    }

    /// <summary>The address's country from GeoLite2; null without the database or an answer.</summary>
    private string? CountryOf(string address) => _geo.IsCityAvailable ? _geo.Enrich(address).CountryCode : null;

    /// <summary>
    /// One connection to decide: the LAN end as the source unless the flow came in from outside. The
    /// destination name is the one the client looked it up by, when the gateway saw that lookup.
    /// </summary>
    public sealed record FlowQuery(string SourceIp, string? SourceMac, int SourcePort, string DestinationIp, int DestinationPort, string Protocol,
        string? DestinationName = null);

    /// <summary>
    /// A domain rule's domains, resolved: kept 5 minutes, or the answer's own TTL when that is longer
    /// (up to an hour), as their answers move.
    /// </summary>
    private static readonly TimeSpan DomainKept = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DomainKeptMax = TimeSpan.FromHours(1);
    private const int MaxDomains = 300;
    private readonly ConcurrentDictionary<string, (HashSet<string> Addresses, DateTime Expires)> _domains = new(StringComparer.OrdinalIgnoreCase);
    private static readonly DnsClient.LookupClient Dns = new(new DnsClient.LookupClientOptions
    {
        UseCache = false,
        Timeout = TimeSpan.FromSeconds(2),
        Retries = 0,
    });

    /// <summary>The policy's name and whether it allows; null when no rule could be named.</summary>
    public sealed record PolicyVerdict(string Name, bool Allows);

    private sealed record SiteRules(DateTime LoadedAt, ConfigState State, Dictionary<FirewallRule, JsonObject> Rules, Dictionary<string, string> ZoneOf);

    /// <summary>
    /// Decides each flow. Flows that cannot be decided (no zone, an IPv6 address, a rule ahead that cannot be
    /// judged) are left out of the result. Never throws: a failed config read leaves every flow out.
    /// </summary>
    public async Task<IReadOnlyDictionary<FlowQuery, PolicyVerdict>> DecideAsync(string slug, IReadOnlyCollection<FlowQuery> flows, CancellationToken ct)
    {
        var result = new Dictionary<FlowQuery, PolicyVerdict>();
        if (flows.Count == 0) return result;
        var rules = await RulesAsync(slug, ct);
        if (rules == null)
        {
            _logger.LogDebug("Live Traffic policies: no firewall config for site {Slug}; {Count} flows left unknown", slug, flows.Count);
            return result;
        }
        await ResolveDomainsAsync(rules, ct);
        var outcomes = new Dictionary<string, int>();
        var stoppedAt = new Dictionary<string, int>();
        var blockedBy = new List<string>();
        var decided = new List<(FlowQuery Flow, PolicyVerdict? Verdict, string Outcome)>(flows.Count);
        foreach (var flow in flows)
        {
            var (verdict, outcome, undecided) = Decide(rules, flow, ResolvesTo, CountryOf);
            if (verdict != null) result[flow] = verdict;
            decided.Add((flow, verdict, outcome));
            if (undecided != null) stoppedAt[undecided] = stoppedAt.GetValueOrDefault(undecided) + 1;
        }
        var allowed = result.Where(r => r.Value.Allows)
            .Select(r => (r.Key.SourceIp, r.Key.SourcePort, r.Key.DestinationIp, r.Key.DestinationPort, r.Key.Protocol)).ToHashSet();
        foreach (var (flow, verdict, decidedOutcome) in decided)
        {
            var outcome = decidedOutcome;
            // A LAN flow is asked both ways; a block on the side whose mirror is allowed is the reply direction,
            // not a mismatch. Any other block is our model of the config being off: name the rule and the flow.
            if (outcome == "block" && allowed.Contains((flow.DestinationIp, flow.DestinationPort, flow.SourceIp, flow.SourcePort, flow.Protocol)))
                outcome = "reply side blocked";
            else if (outcome == "block" && blockedBy.Count < 5)
                blockedBy.Add($"\"{verdict!.Name}\" for {flow.SourceIp} to {flow.DestinationIp}:{flow.DestinationPort}/{flow.Protocol}");
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
        }
        _logger.LogDebug("Live Traffic policies for site {Slug}: {Count} flows against {Rules} rules: {Outcomes}; stopped at {StoppedAt}; blocked by {BlockedBy}",
            slug, flows.Count, rules.Rules.Count, string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}")),
            stoppedAt.Count == 0 ? "none" : string.Join(", ", stoppedAt.OrderByDescending(s => s.Value).Take(5).Select(s => $"\"{s.Key}\" x{s.Value}")),
            blockedBy.Count == 0 ? "none" : string.Join("; ", blockedBy));
        return result;
    }

    /// <summary>
    /// The rule to show for a live flow. The flow got through, so only an allowing rule can be the one: a block
    /// verdict means our model of the config is off, and names nothing. With a reverse (a LAN-to-LAN flow whose
    /// opener is unknown), the one direction that is allowed names it, or both when they name the same rule.
    /// </summary>
    public static string? Name(IReadOnlyDictionary<FlowQuery, PolicyVerdict> verdicts, FlowQuery query, FlowQuery? reverse)
    {
        var forward = verdicts.TryGetValue(query, out var f) && f.Allows ? f.Name : null;
        if (reverse == null) return forward;
        var back = verdicts.TryGetValue(reverse, out var b) && b.Allows ? b.Name : null;
        if (forward == null) return back;
        if (back == null || back == forward) return forward;
        return null;
    }

    /// <summary>Whether a listed domain resolves to an address; null while it has no fresh answer.</summary>
    private bool? ResolvesTo(string domain, string address) =>
        _domains.TryGetValue(domain, out var e) && DateTime.UtcNow < e.Expires ? e.Addresses.Contains(address) : null;

    /// <summary>
    /// Resolves the domains of the site's allow rules that match on domains, those with no fresh answer, a few
    /// at a time. Only allow rules: a live flow got through, so a block rule never needs judging.
    /// </summary>
    private async Task ResolveDomainsAsync(SiteRules site, CancellationToken ct)
    {
        var due = site.Rules.Keys
            .Where(r => r.Enabled && r.ActionType.IsAllowAction()
                && string.Equals(r.DestinationMatchingTarget, "WEB", StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => r.WebDomains ?? [])
            .Select(d => d.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(d => d.StartsWith("*.", StringComparison.Ordinal) ? d[2..] : d)
            .Where(d => d.Length > 0 && !(_domains.TryGetValue(d, out var e) && DateTime.UtcNow < e.Expires))
            .Distinct()
            .Take(MaxDomains)
            .ToList();
        if (due.Count == 0) return;
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(due.Select(async domain =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var ttl = TimeSpan.Zero;
                foreach (var type in new[] { DnsClient.QueryType.A, DnsClient.QueryType.AAAA })
                {
                    var answer = await Dns.QueryAsync(domain, type, cancellationToken: ct);
                    foreach (var a in answer.Answers.OfType<DnsClient.Protocol.AddressRecord>())
                    {
                        addresses.Add(a.Address.ToString());
                        var recordTtl = TimeSpan.FromSeconds(a.TimeToLive);
                        if (ttl == TimeSpan.Zero || recordTtl < ttl) ttl = recordTtl;
                    }
                }
                var kept = ttl > DomainKept ? (ttl < DomainKeptMax ? ttl : DomainKeptMax) : DomainKept;
                _domains[domain] = (addresses, DateTime.UtcNow + kept);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Unanswered: the domain stays unknown, and is tried again on the next refresh.
                _logger.LogDebug(ex, "Live Traffic policies: could not resolve {Domain}", domain);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    /// <summary>The verdict, a one-word outcome for the log, and the rule that stopped the decision, if one did.</summary>
    private static (PolicyVerdict? Verdict, string Outcome, string? StoppedAt) Decide(SiteRules site, FlowQuery flow, Func<string, string, bool?> resolvesTo,
        Func<string, string?> countryOf)
    {
        // The zone-to-address mapping is IPv4; an IPv6 flow is left undecided rather than misplaced.
        if (!IPAddress.TryParse(flow.SourceIp, out var src) || src.AddressFamily != AddressFamily.InterNetwork
            || !IPAddress.TryParse(flow.DestinationIp, out var dst) || dst.AddressFamily != AddressFamily.InterNetwork)
            return (null, "ipv6", null);
        var (srcZone, srcNetwork) = ZonePaths.ZoneOfAddress(site.State, site.ZoneOf, flow.SourceIp);
        var (dstZone, dstNetwork) = ZonePaths.ZoneOfAddress(site.State, site.ZoneOf, flow.DestinationIp);
        if (srcZone == null || dstZone == null) return (null, "no-zone", null);

        var pair = site.Rules.Keys.Where(r => string.Equals(r.SourceZoneId, srcZone, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.DestinationZoneId, dstZone, StringComparison.OrdinalIgnoreCase));
        var protocol = flow.Protocol.ToLowerInvariant();
        var port = flow.DestinationPort > 0 ? flow.DestinationPort.ToString(System.Globalization.CultureInfo.InvariantCulture) : "0";
        var sourcePort = flow.SourcePort.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var decision = FirewallRuleEvaluator.DecideFlow(pair, r =>
        {
            // A client rule lists UniFi clients by MAC. The MAC comes from the agent's neighbor table or the
            // console's client and device lists; an address none of them knows is not a listed client.
            var s = r.MatchesSourceHost(srcNetwork, flow.SourceIp, flow.SourceMac)
                ?? (string.Equals(r.SourceMatchingTarget, "CLIENT", StringComparison.OrdinalIgnoreCase) && flow.SourceMac == null ? false : null);
            // A region rule is judged on GeoLite2's country; without one it stays unknown, as before.
            var d = r.DestinationMatchingTarget?.ToUpperInvariant() switch
            {
                "WEB" => r.MatchesDestinationDomain(flow.DestinationName, domain => resolvesTo(domain, flow.DestinationIp)),
                "REGION" => r.MatchesDestinationRegion(countryOf(flow.DestinationIp)),
                _ => r.MatchesDestinationHost(dstNetwork, flow.DestinationIp),
            };
            if (s == false || d == false) return false;
            if (!string.IsNullOrEmpty(r.SourcePort)
                && FirewallGroupHelper.IncludesPort(r.SourcePort, sourcePort) == r.SourceMatchOppositePorts) return false;
            // An unresolved port group would read as all ports: it cannot be judged.
            if (r.HasUnresolvedDestinationPortGroup) return null;
            if (!FirewallGroupHelper.RuleMatchesPortAndProtocol(r, port, protocol)) return false;
            // A scheduled rule may be off right now; when it is, it does not decide.
            var known = r.IsAlwaysOn && s != null && d != null;
            if (known) return true;
            // These flows are live, so they got through: a block rule ahead of them cannot have matched them,
            // whatever it is matched on (a domain list, say). Only an allow rule that cannot be judged stops it.
            return r.ActionType.IsBlockAction() ? false : null;
        }, IpFamily.IPv4);

        if (decision.Undecided is { } stop)
            return (null, "stopped", $"{stop.Name ?? stop.Id} ({stop.SourceMatchingTarget ?? "ANY"} to {stop.DestinationMatchingTarget ?? "ANY"}{(stop.IsAlwaysOn ? "" : ", scheduled")})");
        if (decision.Rule is not { } rule) return (null, pair.Any() ? "no-match" : "empty-pair", null);
        if (!site.Rules.TryGetValue(rule, out var policy)) return (null, "unmapped", null);
        return policy["name"] is JsonValue v && v.TryGetValue<string>(out var name) && !string.IsNullOrWhiteSpace(name)
            ? (new PolicyVerdict(name, rule.ActionType.IsAllowAction()), rule.ActionType.IsAllowAction() ? "allow" : "block", null)
            : (null, "unnamed", null);
    }

    private async Task<SiteRules?> RulesAsync(string slug, CancellationToken ct)
    {
        if (_sites.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.LoadedAt < ConfigKept) return cached;
        var gate = _loading.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_sites.TryGetValue(slug, out cached) && DateTime.UtcNow - cached.LoadedAt < ConfigKept) return cached;
            var api = new SiteConfigApi(_connections.GetFor(slug));
            if (!api.IsConnected) return cached;
            var reader = new ConfigSnapshotReader(NullLogger.Instance);
            var state = new ConfigState { ReadAt = DateTime.UtcNow };
            foreach (var key in Collections)
            {
                if (ConfigCollections.Get(key) is not { } def) return cached;
                var (objects, error) = await reader.ReadCollectionAsync(api, def, ct);
                if (objects == null)
                {
                    _logger.LogDebug("Live Traffic policies: {Collection} unreadable for site {Slug}: {Error}", key, slug, error);
                    return cached;
                }
                state.Collections[key] = objects;
            }
            // UniFi's predefined policies for a custom zone are generated, so they are filled in as the Audit does.
            state = ZonePaths.WithCustomZoneDefaults(state);
            var loaded = new SiteRules(DateTime.UtcNow, state, ZonePaths.Parse(state, state.Get("firewall_policy")), ZonePaths.ZoneOfNetwork(state));
            _sites[slug] = loaded;
            return loaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Live Traffic policies: config read failed for site {Slug}", slug);
            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _sites.TryRemove(slug, out _);
        return null;
    }
}
