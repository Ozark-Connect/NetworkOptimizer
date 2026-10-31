using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Core;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>The UniFi console objects Network Optimizer owns for gateway blocking.</summary>
public sealed class GatewayBlocklistState
{
    public string? GroupV4Id { get; set; }
    public string? GroupV6Id { get; set; }

    /// <summary>The public block lists' groups, kept apart so the minute-by-minute blocklist never rewrites them.</summary>
    public string? FeedsV4Id { get; set; }
    public string? FeedsV6Id { get; set; }

    /// <summary>
    /// The outbound policies' own groups: only the lists that are blocked outbound. Consoles set up before
    /// these existed point their outbound policies at the inbound groups until the next public list write.
    /// </summary>
    public string? FeedsOutV4Id { get; set; }
    public string? FeedsOutV6Id { get; set; }

    public List<GatewayBlockPolicyRef> Policies { get; set; } = [];

    /// <summary>The optional outbound policies: each internal zone to the outbound public lists in External.</summary>
    public List<GatewayBlockPolicyRef> OutboundPolicies { get; set; } = [];

    /// <summary>The Allow policies of the site's exceptions, by <see cref="ExceptionPolicySpec.Key"/>.</summary>
    public List<GatewayExceptionPolicyRef> ExceptionPolicies { get; set; } = [];
}

/// <summary>One exception Allow policy on the console, with the content hash it was last written with.</summary>
public sealed record GatewayExceptionPolicyRef(string Id, string Key, string? Hash = null);

/// <summary>One Block policy: External to <see cref="ZoneId"/> for one IP version (outbound: <see cref="ZoneId"/> to External).</summary>
public sealed record GatewayBlockPolicyRef(string Id, string ZoneId, string IpVersion);

/// <summary>What Set Up found or created.</summary>
public sealed record GatewaySetUpResult(int PoliciesCreated, int PoliciesAdopted, int GroupsCreated);

/// <summary>
/// Gateway blocking through a site's UniFi console: an IPv4 and an IPv6 address group, each
/// referenced by a Block policy from External to every other zone. Set Up creates them once; every
/// sync after that only replaces the group members. Every method takes the site (null = default).
/// </summary>
public sealed class GatewayBlocklist : IGatewayBlocklistWriter
{
    public const string GroupNameV4 = "Net. Opt. Blocklist (IPv4)";
    public const string GroupNameV6 = "Net. Opt. Blocklist (IPv6)";
    public const string PolicyName = ManagedFirewallPolicies.Blocklist;
    public const string Description = "Managed by Network Optimizer: sources it has blocked. Changes here are overwritten.";
    public const string FeedsNameV4 = "Net. Opt. Public Lists (IPv4)";
    public const string FeedsNameV6 = "Net. Opt. Public Lists (IPv6)";
    public const string FeedsPolicyName = ManagedFirewallPolicies.PublicLists;
    public const string FeedsDescription = "Managed by Network Optimizer: the public block lists chosen in Automatic Blocking. Changes here are overwritten.";
    public const string FeedsOutboundPolicyName = ManagedFirewallPolicies.PublicListsOutbound;
    public const string FeedsOutboundDescription = "Managed by Network Optimizer: stops devices reaching the public block lists' addresses. Changes here are overwritten.";
    public const string FeedsOutNameV4 = "Net. Opt. Public Lists Outbound (IPv4)";
    public const string FeedsOutNameV6 = "Net. Opt. Public Lists Outbound (IPv6)";
    public const string FeedsOutDescription = "Managed by Network Optimizer: the public block lists' command servers and criminal networks, blocked outbound. Changes here are overwritten.";

    private readonly IUniFiClientAccessor _clients;
    private readonly IThreatSettingsAccessor _settings;
    private readonly ILogger _logger;

    public GatewayBlocklist(IUniFiClientAccessor clients, IThreatSettingsAccessor settings, ILogger<GatewayBlocklist> logger)
    {
        _clients = clients;
        _settings = settings;
        _logger = logger;
    }

    public async Task<GatewayBlocklistState?> LoadStateAsync(string? site, CancellationToken ct)
    {
        var json = await _settings.GetSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.GatewayState, site), ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<GatewayBlocklistState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task SaveStateAsync(string? site, GatewayBlocklistState? state, CancellationToken ct) =>
        _settings.SaveSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.GatewayState, site),
            state == null ? "" : JsonSerializer.Serialize(state), ct);

    private UniFiApiClient Client(string? site) =>
        _clients.GetClient(site) ?? throw new InvalidOperationException("Connect the UniFi Console in Settings - Connection first.");

    /// <summary>
    /// Creates (or adopts) the blocklist and public block list groups, and a Block policy from
    /// External to every other zone for each group. Safe to run again: anything already there is reused.
    /// </summary>
    public async Task<GatewaySetUpResult> SetUpAsync(string? site, CancellationToken ct)
    {
        var client = Client(site);
        var state = await LoadStateAsync(site, ct) ?? new GatewayBlocklistState();

        // An empty zone list is a failed read (the client returns [] on error), not a console without zones.
        var zones = await client.GetFirewallZonesAsync(ct);
        if (zones.Count == 0)
            throw new InvalidOperationException("Could not read the firewall zones from the UniFi Console.");
        var external = zones.FirstOrDefault(z => z.ZoneKey == FirewallZoneKeys.External)
            ?? throw new InvalidOperationException("This UniFi Console has no zone-based firewall. Gateway blocking needs UniFi Network 9.0 or later with zones enabled.");

        var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
        var groupsCreated = 0;
        async Task<string> EnsureGroup(string? id, string name, string type)
        {
            var existing = groups.FirstOrDefault(g => g.Id == id) ?? groups.FirstOrDefault(g => g.Name == name && g.GroupType == type);
            if (existing != null) return existing.Id;
            groupsCreated++;
            var created = await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(null, name, type, [], Description), ct);
            return created.Id;
        }
        state.GroupV4Id = await EnsureGroup(state.GroupV4Id, GroupNameV4, "address-group");
        state.GroupV6Id = await EnsureGroup(state.GroupV6Id, GroupNameV6, "ipv6-address-group");
        state.FeedsV4Id = await EnsureGroup(state.FeedsV4Id, FeedsNameV4, "address-group");
        state.FeedsV6Id = await EnsureGroup(state.FeedsV6Id, FeedsNameV6, "ipv6-address-group");

        var policies = await client.GetFirewallPoliciesAsync(ct);
        var created = 0;
        var adopted = 0;
        var refs = new List<GatewayBlockPolicyRef>();
        foreach (var zone in zones.Where(z => z.Id != external.Id))
        {
            foreach (var (version, groupId, name, description) in new[]
                     {
                         ("IPV4", state.GroupV4Id, PolicyName, Description), ("IPV6", state.GroupV6Id, PolicyName, Description),
                         ("IPV4", state.FeedsV4Id, FeedsPolicyName, FeedsDescription), ("IPV6", state.FeedsV6Id, FeedsPolicyName, FeedsDescription),
                     })
            {
                var existing = policies.FirstOrDefault(p => !p.Predefined
                    && p.Source.ZoneId == external.Id && p.Destination.ZoneId == zone.Id
                    && p.Source.IpGroupId == groupId);
                if (existing != null)
                {
                    adopted++;
                    refs.Add(new GatewayBlockPolicyRef(existing.Id, zone.Id, version));
                    continue;
                }
                var isSourceBlock = groupId == state.GroupV4Id || groupId == state.GroupV6Id;
                var policy = await client.CreateFirewallPolicyAsync(UniFiGroupBlockPolicyWrite.Create(
                    name, description, version, external.Id, groupId!, zone.Id, newConnectionsOnly: isSourceBlock), ct);
                created++;
                refs.Add(new GatewayBlockPolicyRef(policy.Id, zone.Id, version));
            }
        }
        state.Policies = refs;
        await SaveStateAsync(site, state, ct);

        // Adopted blocklist policies predate new-connections-only matching.
        await EnsureSourcePoliciesNewOnlyAsync(site, ct);

        // New custom policies land after the ones already there, so a user ALLOW from External
        // would run first. Move ours to the front of each zone pair once; later ones land after.
        // Exceptions sit between the site's own blocks and the public lists, as SyncExceptionsAsync orders them.
        var current = await client.GetFirewallPoliciesAsync(ct);
        var exceptionIds = ExceptionIds(state, current);
        foreach (var pair in refs.GroupBy(r => r.ZoneId))
        {
            var ids = pair.Select(r => r.Id).ToHashSet();
            var own = current.Where(p => ids.Contains(p.Id) && (p.Source.IpGroupId == state.GroupV4Id || p.Source.IpGroupId == state.GroupV6Id))
                .Select(p => p.Id).ToHashSet();
            if (GatewayExceptionPolicies.Order(current, external.Id, pair.Key, own, exceptionIds, ids) is { } order)
                await client.ReorderFirewallPoliciesAsync(external.Id, pair.Key, order.Before, order.After, ct);
        }
        _logger.LogInformation("Gateway blocking set up: {Created} policies created, {Adopted} adopted, {Groups} groups created",
            created, adopted, groupsCreated);
        return new GatewaySetUpResult(created, adopted, groupsCreated);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A source block exists to stop unsolicited inbound traffic. Matching every state also dropped
    /// replies to connections LAN devices opened (our policies sit ahead of UniFi's Allow Return
    /// Traffic), so a wrongly blocked CDN broke the apps using it. The public list policies keep
    /// matching every state, paired with the optional outbound block.
    /// </remarks>
    public async Task<int> EnsureSourcePoliciesNewOnlyAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.GroupV4Id == null || state.GroupV6Id == null) return 0;
        var client = Client(site);
        var groups = new HashSet<string> { state.GroupV4Id, state.GroupV6Id };
        var stale = (await client.GetFirewallPoliciesAsync(ct))
            .Where(p => !p.Predefined && groups.Contains(p.Source.IpGroupId ?? "") && !IsNewConnectionsOnly(p))
            .ToList();
        foreach (var policy in stale)
            await client.SetFirewallPolicyConnectionStatesAsync(policy.Id, UniFiGroupBlockPolicyWrite.NewConnectionsOnly, ct);
        if (stale.Count > 0)
            _logger.LogInformation("Gateway blocking: {Count} blocklist policies now match new connections only", stale.Count);
        return stale.Count;
    }

    /// <summary>Whether a policy matches exactly <see cref="UniFiGroupBlockPolicyWrite.NewConnectionsOnly"/>.</summary>
    public static bool IsNewConnectionsOnly(UniFiFirewallPolicy policy) =>
        string.Equals(policy.ConnectionStateType, "CUSTOM", StringComparison.OrdinalIgnoreCase)
        && policy.ConnectionStates is { Count: 1 } states
        && string.Equals(states[0], "NEW", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds or removes the outbound policies that stop devices on every internal zone (not the
    /// gateway itself) from reaching the public lists' addresses. Needs Set Up first. Returns how
    /// many policies now exist.
    /// </summary>
    /// <remarks>
    /// The policies match every state, unlike the source policies, so a session opened before the
    /// list loaded is cut too. Only the narrow <see cref="PublicBlockList.Outbound"/> lists feed them.
    /// </remarks>
    public async Task<int> SetFeedsOutboundAsync(string? site, bool enabled, CancellationToken ct) =>
        (await WriteFeedsOutboundAsync(site, enabled, ct)).Total;

    /// <summary>As <see cref="SetFeedsOutboundAsync"/>, also returning how many policies it created.</summary>
    private async Task<(int Total, int Created)> WriteFeedsOutboundAsync(string? site, bool enabled, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.FeedsV4Id == null || state.FeedsV6Id == null)
        {
            if (!enabled) return (0, 0);
            throw new InvalidOperationException("Set up gateway blocking first.");
        }
        var client = Client(site);
        var policies = await client.GetFirewallPoliciesAsync(ct);
        // Outbound policies on the inbound groups are the shape before outbound groups existed.
        var inboundGroups = new HashSet<string> { state.FeedsV4Id, state.FeedsV6Id };

        if (!enabled)
        {
            var feeds = new HashSet<string>(inboundGroups.Concat(new[] { state.FeedsOutV4Id, state.FeedsOutV6Id }.OfType<string>()));
            var ids = state.OutboundPolicies.Select(p => p.Id)
                .Concat(policies.Where(p => !p.Predefined && feeds.Contains(p.Destination.IpGroupId ?? "")).Select(p => p.Id))
                .Distinct().ToList();
            foreach (var id in ids)
                await client.DeleteFirewallPolicyAsync(id, ct);
            state.OutboundPolicies = [];
            await SaveStateAsync(site, state, ct);
            return (0, 0);
        }

        var zones = await client.GetFirewallZonesAsync(ct);
        var external = zones.FirstOrDefault(z => z.ZoneKey == FirewallZoneKeys.External)
            ?? throw new InvalidOperationException("Could not read the firewall zones from the UniFi Console.");

        var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
        async Task<string> EnsureOutGroup(string? id, string name, string type)
        {
            var existing = groups.FirstOrDefault(g => g.Id == id) ?? groups.FirstOrDefault(g => g.Name == name && g.GroupType == type);
            return existing?.Id ?? (await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(null, name, type, [], FeedsOutDescription), ct)).Id;
        }
        state.FeedsOutV4Id = await EnsureOutGroup(state.FeedsOutV4Id, FeedsOutNameV4, "address-group");
        state.FeedsOutV6Id = await EnsureOutGroup(state.FeedsOutV6Id, FeedsOutNameV6, "ipv6-address-group");
        foreach (var stale in policies.Where(p => !p.Predefined && p.Destination.ZoneId == external.Id
                     && inboundGroups.Contains(p.Destination.IpGroupId ?? "")).ToList())
            await client.DeleteFirewallPolicyAsync(stale.Id, ct);

        var refs = new List<GatewayBlockPolicyRef>();
        var created = 0;
        foreach (var zone in zones.Where(z => z.Id != external.Id && z.ZoneKey != FirewallZoneKeys.Gateway))
        {
            foreach (var (version, groupId) in new[] { ("IPV4", state.FeedsOutV4Id), ("IPV6", state.FeedsOutV6Id) })
            {
                var existing = policies.FirstOrDefault(p => !p.Predefined
                    && p.Source.ZoneId == zone.Id && p.Destination.ZoneId == external.Id && p.Destination.IpGroupId == groupId);
                var id = existing?.Id;
                if (id == null)
                {
                    id = (await client.CreateFirewallPolicyAsync(UniFiGroupBlockPolicyWrite.CreateToGroup(
                        FeedsOutboundPolicyName, FeedsOutboundDescription, version, zone.Id, external.Id, groupId), ct)).Id;
                    created++;
                }
                refs.Add(new GatewayBlockPolicyRef(id, zone.Id, version));
            }
        }
        state.OutboundPolicies = refs;
        await SaveStateAsync(site, state, ct);

        // Ahead of any custom ALLOW to External in each zone pair, as with the inbound ones.
        var current = await client.GetFirewallPoliciesAsync(ct);
        var exceptionIds = ExceptionIds(state, current);
        foreach (var pair in refs.GroupBy(r => r.ZoneId))
        {
            if (GatewayExceptionPolicies.Order(current, pair.Key, external.Id, exceptionIds, pair.Select(r => r.Id).ToHashSet()) is { } order)
                await client.ReorderFirewallPoliciesAsync(pair.Key, external.Id, order.Before, order.After, ct);
        }
        _logger.LogInformation("Public lists outbound blocking set up: {Count} policies ({Created} created)", refs.Count, created);
        return (refs.Count, created);
    }

    /// <summary>
    /// The batch-reorder lists for one zone pair: custom policies that run before UniFi's predefined
    /// ones with ours first and the rest in their current order, and those that run after, untouched.
    /// Port-forward policies (custom, index 30000-39999) are UniFi's to place and are left out.
    /// </summary>
    public static (List<string> Before, List<string> After) OrderWithOursFirst(
        IEnumerable<UniFiFirewallPolicy> policies, string sourceZoneId, string destinationZoneId, IReadOnlySet<string> ours)
    {
        var custom = policies
            .Where(p => !p.Predefined && p.Source.ZoneId == sourceZoneId && p.Destination.ZoneId == destinationZoneId
                        && (p.Index < 30000 || p.Index >= 40000))
            .OrderBy(p => p.Index)
            .ToList();
        var before = custom.Where(p => p.Index < 30000).ToList();
        var after = custom.Where(p => p.Index >= 40000).Select(p => p.Id).ToList();
        var ordered = before.Where(p => ours.Contains(p.Id)).Concat(before.Where(p => !ours.Contains(p.Id)))
            .Select(p => p.Id).ToList();
        return (ordered, after);
    }

    /// <summary>
    /// Removes Network Optimizer's policies, then its groups. Policies go first: UniFi lets a
    /// referenced group be deleted and leaves the policy pointing at nothing.
    /// </summary>
    public async Task RemoveAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state == null) return;
        var client = Client(site);
        var policies = await client.GetFirewallPoliciesAsync(ct);
        var groupIds = new[] { state.GroupV4Id, state.GroupV6Id, state.FeedsV4Id, state.FeedsV6Id, state.FeedsOutV4Id, state.FeedsOutV6Id }
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
        var policyIds = state.Policies.Concat(state.OutboundPolicies).Select(p => p.Id)
            .Concat(state.ExceptionPolicies.Select(p => p.Id))
            .Concat(policies.Where(p => !p.Predefined
                && (groupIds.Contains(p.Source.IpGroupId ?? "") || groupIds.Contains(p.Destination.IpGroupId ?? ""))).Select(p => p.Id))
            .Distinct();
        foreach (var id in policyIds)
            await client.DeleteFirewallPolicyAsync(id, ct);
        foreach (var id in groupIds)
            await client.DeleteFirewallGroupAsync(id!, ct);
        await SaveStateAsync(site, null, ct);
    }

    /// <summary>
    /// Writes a group only when it differs from what the console holds. Every group write can
    /// reprovision the gateway, so a write that changes nothing is skipped.
    /// </summary>
    internal static async Task<string> SaveIfChangedAsync(UniFiApiClient client, IReadOnlyList<UniFiFirewallGroup> groups,
        UniFiFirewallGroupWrite group, CancellationToken ct)
    {
        // Network 11 re-keyed every group to a UUID on upgrade and still accepts writes to the old id, so a stale id
        // would skip this check and rewrite (reprovisioning the gateway) on every sync. Adopt our group by name instead.
        if (group.Id != null && groups.All(g => g.Id != group.Id)
            && groups.FirstOrDefault(g => g.Name == group.Name && g.GroupType == group.GroupType) is { } renamed)
            group = group with { Id = renamed.Id };
        if (group.Id != null && groups.FirstOrDefault(g => g.Id == group.Id) is { } current
            && current.Name == group.Name && current.Description == group.Description
            && SameMembers(current.GroupMembers, group.GroupMembers))
            return current.Id;
        return (await client.SaveFirewallGroupAsync(group, ct)).Id;
    }

    /// <summary>Whether two member lists hold the same addresses and ranges, however each is spelled.</summary>
    internal static bool SameMembers(IEnumerable<string> a, IEnumerable<string> b)
    {
        static HashSet<string> Canonical(IEnumerable<string> members) => members
            .Select(m => PublicBlockLists.Normalize(m) is { } n ? StripHostPrefix(n) : m.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        static string StripHostPrefix(string n) =>
            n.EndsWith("/32", StringComparison.Ordinal) && !n.Contains(':') ? n[..^3]
            : n.EndsWith("/128", StringComparison.Ordinal) ? n[..^4]
            : n;
        return Canonical(a).SetEquals(Canonical(b));
    }

    /// <inheritdoc />
    public async Task<BlockTargetResult> SyncMembersAsync(string? site, IReadOnlyList<string> ips, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.GroupV4Id == null || state.GroupV6Id == null)
        {
            // Nothing set up means nothing to clear; only an actual send needs the setup.
            return ips.Count == 0 ? BlockTargetResult.Success(0) : BlockTargetResult.Failure("Set up gateway blocking first.");
        }

        var (v4, v6) = SplitFamilies(ips);
        try
        {
            var client = Client(site);
            var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
            await SaveIfChangedAsync(client, groups, new UniFiFirewallGroupWrite(state.GroupV4Id, GroupNameV4, "address-group", v4, Description), ct);
            await SaveIfChangedAsync(client, groups, new UniFiFirewallGroupWrite(state.GroupV6Id, GroupNameV6, "ipv6-address-group", v6, Description), ct);
            return BlockTargetResult.Success(v4.Count + v6.Count);
        }
        catch (UniFiApiErrorException ex) when (ex.Code == "api.err.IdInvalid")
        {
            return BlockTargetResult.Failure("Network Optimizer's Network List was removed in UniFi Network. Run Set Up again.");
        }
        catch (UniFiPermissionException ex)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is UniFiApiErrorException or InvalidOperationException or HttpRequestException)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureZonePoliciesAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state == null || state.Policies.Count == 0) return false;
        // An empty zone list is a failed read (the client returns [] on error), never a console without zones.
        var zones = await Client(site).GetFirewallZonesAsync(ct);
        var external = zones.FirstOrDefault(z => z.ZoneKey == FirewallZoneKeys.External);
        if (zones.Count == 0 || external == null) return false;

        var (inbound, outbound) = Uncovered(state, zones, external.Id);
        if (!inbound && !outbound) return false;
        var (added, gone) = ZoneChanges(state, zones, external.Id);
        var created = 0;
        if (inbound) created += (await SetUpAsync(site, ct)).PoliciesCreated;
        if (outbound) created += (await WriteFeedsOutboundAsync(site, true, ct)).Created;
        var where = site ?? "default";
        // A zone deleted leaves only gone ones. Nothing created with a zone "added" is the record drifting (a zone re-keyed,
        // its policies already there), not a change on the site.
        var removed = gone.Count > 0 && added.Count == 0;
        if (created > 0)
            _logger.LogInformation("Gateway blocking on {Site}: {Created} per-zone policies created ({Zones} added)", where, created,
                added.Count > 0 ? string.Join(", ", added) : "a zone lacked them");
        else if (removed)
            _logger.LogInformation("Gateway blocking on {Site}: {Count} zone(s) gone; their policies left the record with them", where, gone.Count);
        else
            _logger.LogDebug("Gateway blocking on {Site}: per-zone policy record refreshed; nothing created", where);
        return created > 0 || removed;
    }

    /// <summary>
    /// The exception Allow policies, which order between the site's own blocks and the public lists: the recorded ones
    /// and any by their reserved name. The record alone can be stale: a Network app upgrade re-keys every policy (10.6 to
    /// 11, grounding.md phase 4), and only an exception write, held while a gateway agent enforces, rewrites it. A Set Up
    /// that missed one ordered it behind the public lists, where it never matched.
    /// </summary>
    internal static HashSet<string> ExceptionIds(GatewayBlocklistState state, IEnumerable<UniFiFirewallPolicy> current) =>
        state.ExceptionPolicies.Select(p => p.Id)
            .Concat(current.Where(p => !p.Predefined && p.Name == GatewayExceptionPolicies.PolicyName).Select(p => p.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exception Allow policies on the console that the record does not hold, by their reserved name. Only Network
    /// Optimizer writes that name, so each is its own leftover: still allowing traffic nothing tracks.
    /// </summary>
    internal static List<string> UntrackedExceptions(IEnumerable<UniFiFirewallPolicy> live, IEnumerable<GatewayExceptionPolicyRef> tracked)
    {
        var ids = tracked.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return live.Where(p => !p.Predefined && p.Name == GatewayExceptionPolicies.PolicyName && !ids.Contains(p.Id)).Select(p => p.Id).ToList();
    }

    /// <summary>
    /// Zones with no recorded policy (by name), and recorded zones the site no longer has (by ID: their names are gone
    /// with them). Neither when only the record drifted, such as a zone ID re-keyed by a Network app upgrade.
    /// </summary>
    internal static (List<string> Added, List<string> Gone) ZoneChanges(GatewayBlocklistState state, IReadOnlyList<UniFiFirewallZone> zones, string externalId)
    {
        var recorded = state.Policies.Concat(state.OutboundPolicies).Select(r => r.ZoneId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = zones.Where(z => z.Id != externalId).ToList();
        var added = current.Where(z => !recorded.Contains(z.Id)).Select(z => z.Name).ToList();
        var ids = current.Select(z => z.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = recorded.Where(id => !ids.Contains(id)).ToList();
        return (added, gone);
    }

    /// <summary>
    /// Whether the inbound policies (External to every other zone) and the outbound ones (every zone but External and
    /// Gateway to External, when set up) no longer cover exactly the site's zones: one was added or removed.
    /// </summary>
    internal static (bool Inbound, bool Outbound) Uncovered(GatewayBlocklistState state, IReadOnlyList<UniFiFirewallZone> zones, string externalId)
    {
        static bool Covers(IEnumerable<GatewayBlockPolicyRef> refs, IEnumerable<string> zoneIds) =>
            refs.Select(r => r.ZoneId).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(zoneIds);
        var inbound = !Covers(state.Policies, zones.Where(z => z.Id != externalId).Select(z => z.Id));
        var outbound = state.OutboundPolicies.Count > 0
            && !Covers(state.OutboundPolicies, zones.Where(z => z.Id != externalId && z.ZoneKey != FirewallZoneKeys.Gateway).Select(z => z.Id));
        return (inbound, outbound);
    }

    /// <inheritdoc />
    public async Task<BlockTargetResult> SyncFeedsAsync(string? site, IReadOnlyList<string> entries, IReadOnlyList<string> outbound,
        CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.FeedsV4Id == null || state.FeedsV6Id == null)
        {
            return entries.Count == 0 ? BlockTargetResult.Success(0)
                : BlockTargetResult.Failure(state == null ? "Set up gateway blocking first." : "Run Set Up again to add the public block lists to the gateway.");
        }
        var (v4, v6) = SplitFamilies(entries);
        try
        {
            // Outbound policies still on the inbound groups move to their own, which hold only the outbound lists.
            if (state.OutboundPolicies.Count > 0 && (state.FeedsOutV4Id == null || state.FeedsOutV6Id == null))
            {
                await SetFeedsOutboundAsync(site, true, ct);
                state = await LoadStateAsync(site, ct) ?? state;
            }
            var client = Client(site);
            var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
            await SaveIfChangedAsync(client, groups, new UniFiFirewallGroupWrite(state.FeedsV4Id, FeedsNameV4, "address-group", v4, FeedsDescription), ct);
            await SaveIfChangedAsync(client, groups, new UniFiFirewallGroupWrite(state.FeedsV6Id, FeedsNameV6, "ipv6-address-group", v6, FeedsDescription), ct);
            if (state.OutboundPolicies.Count > 0 && state.FeedsOutV4Id != null && state.FeedsOutV6Id != null)
            {
                var (out4, out6) = SplitFamilies(outbound);
                await SaveIfChangedAsync(client, groups, new UniFiFirewallGroupWrite(state.FeedsOutV4Id, FeedsOutNameV4, "address-group", out4, FeedsOutDescription), ct);
                await SaveIfChangedAsync(client, groups, new UniFiFirewallGroupWrite(state.FeedsOutV6Id, FeedsOutNameV6, "ipv6-address-group", out6, FeedsOutDescription), ct);
            }
            return BlockTargetResult.Success(v4.Count + v6.Count);
        }
        catch (UniFiApiErrorException ex) when (ex.Code == "api.err.IdInvalid")
        {
            return BlockTargetResult.Failure("Network Optimizer's public block list Network List was removed in UniFi Network. Run Set Up again.");
        }
        catch (UniFiPermissionException ex)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is UniFiApiErrorException or InvalidOperationException or HttpRequestException)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Each exception's policies are scoped to the zones whose networks hold its device, and matched to
    /// the console by key: unchanged ones are left alone, changed ones updated in place, and only keys
    /// that come or go are created or deleted, since every write can reprovision the gateway. Every zone
    /// pair touched is then ordered Automatic Blocking's own Block policy, the exceptions, the public
    /// list Block policies, so an exception never lets through a source the site blocked itself.
    /// </remarks>
    public async Task<BlockTargetResult> SyncExceptionsAsync(string? site, IReadOnlyList<BlockingException> exceptions,
        bool outbound, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        // With no exceptions and none recorded, the console is still read: a stale record can leave an untracked one there.
        if (state == null)
            return exceptions.Count == 0 ? BlockTargetResult.Success(0) : BlockTargetResult.Failure("Set up gateway blocking first.");
        if (exceptions.Count > 0 && (state.FeedsV4Id == null || state.FeedsV6Id == null))
            return BlockTargetResult.Failure("Run Set Up again to add the public block lists to the gateway.");
        try
        {
            var client = Client(site);
            var zones = await client.GetFirewallZonesAsync(ct);
            var external = zones.FirstOrDefault(z => z.ZoneKey == FirewallZoneKeys.External)
                ?? throw new InvalidOperationException("Could not read the firewall zones from the UniFi Console.");
            // An unreadable network list leaves every zone without subnets, which scopes to every zone.
            var networks = exceptions.Any(e => e.Device != null) ? await client.GetNetworkConfigsAsync(ct) : [];
            // A WireGuard server's site-to-site peers route their own subnets into the server's zone.
            var peerSubnets = new Dictionary<string, List<string>>();
            foreach (var server in networks.Where(n => n.VpnType == "wireguard-server"))
                peerSubnets[server.Id] = (await client.GetWireGuardPeersAsync(server.Id, ct)).SelectMany(p => p.AllowedIps ?? []).ToList();
            // Teleport's range is in no network and no zone lists it, but the gateway takes tlprt+ traffic
            // into the VPN zone's chains, so only the VPN zone's copy of an exception ever matches it.
            var teleport = exceptions.Any(e => e.Device != null) ? await client.GetTeleportSettingsAsync(ct) : null;
            string[] teleportSubnet = teleport is { Enabled: true, SubnetCidr: { Length: > 0 } cidr } ? [cidr] : [];
            var others = zones.Where(z => z.Id != external.Id)
                .Select(z => new ExceptionZone(z.Id, z.ZoneKey == FirewallZoneKeys.Gateway, networks
                    .Where(n => n.FirewallZoneId == z.Id || z.NetworkIds.Contains(n.Id))
                    .SelectMany(n => new[] { n.IpSubnet, n.Ipv6Subnet }.Concat(peerSubnets.GetValueOrDefault(n.Id) ?? []))
                    .Concat(z.ZoneKey == FirewallZoneKeys.Vpn ? teleportSubnet : [])
                    .OfType<string>().Where(s => s.Length > 0).ToList()))
                .ToList();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var names = zones.ToDictionary(z => z.Id, z => z.Name);
                foreach (var e in exceptions.Where(e => e.Device != null))
                {
                    var held = GatewayExceptionPolicies.ZonesOf(e.Device, others);
                    _logger.LogDebug("Gateway exceptions on {Site}: exception {Id} device {Device} is in {Zones}",
                        site ?? SiteSlugs.Default, e.Id, e.Device,
                        ReferenceEquals(held, others) ? "no zone's networks, so every zone" : string.Join(", ", held.Select(z => names[z.Id])));
                }
            }
            var specs = GatewayExceptionPolicies.Build(exceptions, state, external.Id, others,
                outbound && state.OutboundPolicies.Count > 0);
            var livePolicies = await client.GetFirewallPoliciesAsync(ct);
            var present = livePolicies.Select(p => p.Id).ToHashSet();
            var plan = GatewayExceptionPolicies.Plan(state.ExceptionPolicies, present, specs);

            // Every policy still on the console stays tracked until its own write succeeds, so a failure
            // part way leaves nothing behind that the next sync cannot find.
            var refs = state.ExceptionPolicies.Where(r => present.Contains(r.Id)).ToList();
            var (updated, created, deleted) = (0, 0, 0);
            try
            {
                foreach (var stale in plan.Delete)
                {
                    await client.DeleteFirewallPolicyAsync(stale.Id, ct);
                    refs.Remove(stale);
                    deleted++;
                }
                foreach (var (existing, spec) in plan.Update)
                {
                    await client.UpdateFirewallPolicyAsync(existing.Id, spec.Body, ct);
                    refs[refs.IndexOf(existing)] = existing with { Hash = spec.Hash };
                    updated++;
                }
                foreach (var spec in plan.Create)
                {
                    var policy = await client.CreateFirewallPolicyAsync(spec.Body, ct);
                    refs.Add(new GatewayExceptionPolicyRef(policy.Id, spec.Key, spec.Hash));
                    created++;
                }
                // Last, once every wanted policy is written: an exception policy the record does not hold is a stale
                // record's leftover (an upgrade re-keyed it, and this sync wrote it again), or one whose exception is gone.
                foreach (var orphan in UntrackedExceptions(livePolicies, refs))
                {
                    await client.DeleteFirewallPolicyAsync(orphan, ct);
                    deleted++;
                }
            }
            finally
            {
                state.ExceptionPolicies = refs;
                await SaveStateAsync(site, state, ct);
            }

            if (specs.Count > 0)
            {
                var current = await client.GetFirewallPoliciesAsync(ct);
                var ours = refs.Select(r => r.Id).ToHashSet();
                var own = current.Where(p => p.Source.IpGroupId is { } g && (g == state.GroupV4Id || g == state.GroupV6Id))
                    .Select(p => p.Id).ToHashSet();
                var lists = state.Policies.Concat(state.OutboundPolicies).Select(p => p.Id).Where(id => !own.Contains(id)).ToHashSet();
                foreach (var pair in specs.Select(s => (s.SourceZoneId, s.DestinationZoneId)).Distinct())
                {
                    if (GatewayExceptionPolicies.Order(current, pair.SourceZoneId, pair.DestinationZoneId, own, ours, lists) is { } order)
                        await client.ReorderFirewallPoliciesAsync(pair.SourceZoneId, pair.DestinationZoneId, order.Before, order.After, ct);
                }
            }
            if (updated + created + deleted > 0)
                _logger.LogInformation("Gateway exceptions on {Site}: {Count} Allow policies ({Updated} updated, {Created} created, {Deleted} deleted)",
                    site ?? SiteSlugs.Default, refs.Count, updated, created, deleted);
            return BlockTargetResult.Success(refs.Count);
        }
        catch (UniFiPermissionException ex)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
        catch (Exception ex) when (ex is UniFiApiErrorException or InvalidOperationException or HttpRequestException)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }

    /// <summary>Splits IPs and CIDR ranges into IPv4 and IPv6, unwrapping IPv4-mapped addresses. Others are dropped.</summary>
    public static (List<string> V4, List<string> V6) SplitFamilies(IEnumerable<string> entries)
    {
        var v4 = new List<string>();
        var v6 = new List<string>();
        foreach (var entry in entries)
        {
            if (PublicBlockLists.Normalize(entry) is not { } n) continue;
            var host = n.Split('/')[0];
            if (!IPAddress.TryParse(host, out var addr)) continue;
            (addr.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : v4).Add(n);
        }
        return (v4, v6);
    }

    /// <summary>
    /// Checks the policies Set Up created are still there and enabled. Null when all is well,
    /// otherwise a message for the Settings card.
    /// </summary>
    public async Task<string?> VerifyAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state == null) return null;
        return Verify(state, await Client(site).GetFirewallPoliciesAsync(ct));
    }

    /// <summary>What <see cref="VerifyAsync"/> reports for <paramref name="state"/> against the console's policies.</summary>
    internal static string? Verify(GatewayBlocklistState state, IEnumerable<UniFiFirewallPolicy> policies)
    {
        // The console's list is not always unique by id: some consoles repeat a synthetic obj_<uuid>_<n> id
        // (issue #1333). Ours are ids the console gave our own policies, so keep the first of any repeat.
        var byId = new Dictionary<string, UniFiFirewallPolicy>();
        foreach (var p in policies)
            byId.TryAdd(p.Id, p);
        var all = state.Policies.Concat(state.OutboundPolicies).ToList();
        var missing = all.Count(r => !byId.ContainsKey(r.Id));
        var disabled = all.Count(r => byId.TryGetValue(r.Id, out var p) && !p.Enabled);
        if (missing > 0) return $"{missing} of Network Optimizer's Block policies were removed in UniFi Network. Run Set Up again.";
        if (state.FeedsV4Id == null) return "Run Set Up again to add the public block lists to the gateway.";
        if (disabled > 0) return $"{disabled} of Network Optimizer's Block policies are disabled in UniFi Network.";
        return null;
    }
}
