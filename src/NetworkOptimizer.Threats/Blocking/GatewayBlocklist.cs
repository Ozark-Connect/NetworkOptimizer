using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

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

/// <summary>
/// What Set Up found or created. <see cref="Repaired"/> counts the policies replaced and the groups retyped: a retyped
/// group is written back empty, so its members have to be sent again.
/// </summary>
public sealed record GatewaySetUpResult(int PoliciesCreated, int PoliciesAdopted, int GroupsCreated, int Repaired = 0);

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
    private readonly IThreatSystemAudit? _audit;

    public GatewayBlocklist(IUniFiClientAccessor clients, IThreatSettingsAccessor settings, ILogger<GatewayBlocklist> logger,
        IThreatSystemAudit? audit = null)
    {
        _clients = clients;
        _settings = settings;
        _logger = logger;
        _audit = audit;
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
    /// External to every other zone for each group. Safe to run again: anything already there is reused, a
    /// retyped group gets its type back, and a policy on a missing group is replaced.
    /// </summary>
    public Task<GatewaySetUpResult> SetUpAsync(string? site, CancellationToken ct) => SetUpCoreAsync(site, automatic: false, ct);

    // automatic: Network Optimizer runs it on its own, so its repairs get a system audit entry. The user's Set Up is
    // audited by its gate.
    private async Task<GatewaySetUpResult> SetUpCoreAsync(string? site, bool automatic, CancellationToken ct)
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
        var retyped = 0;
        async Task<string> EnsureGroup(string? id, string name, string type)
        {
            var (groupId, isNew, wasRetyped) = await EnsureGroupAsync(client, groups, id, name, type, Description, ct);
            if (isNew) groupsCreated++;
            if (wasRetyped) retyped++;
            return groupId;
        }
        state.GroupV4Id = await EnsureGroup(state.GroupV4Id, GroupNameV4, "address-group");
        state.GroupV6Id = await EnsureGroup(state.GroupV6Id, GroupNameV6, "ipv6-address-group");
        state.FeedsV4Id = await EnsureGroup(state.FeedsV4Id, FeedsNameV4, "address-group");
        state.FeedsV6Id = await EnsureGroup(state.FeedsV6Id, FeedsNameV6, "ipv6-address-group");

        // Only the inbound side: the outbound policies are rewritten by WriteFeedsOutboundAsync.
        var (policies, replaced) = await DeleteBrokenAsync(client, site, state, await client.GetFirewallPoliciesAsync(ct),
            p => p.Source.ZoneId == external.Id, ct);
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
                var existing = ExistingBlock(policies, external.Id, zone.Id, groupId!, groupIsSource: true);
                if (existing != null)
                {
                    adopted++;
                    refs.Add(new GatewayBlockPolicyRef(existing.Id, zone.Id, version));
                    continue;
                }
                var policy = await client.CreateFirewallPolicyAsync(UniFiGroupBlockPolicyWrite.Create(
                    name, description, version, external.Id, groupId!, zone.Id, newConnectionsOnly: true), ct);
                created++;
                refs.Add(new GatewayBlockPolicyRef(policy.Id, zone.Id, version));
            }
        }
        state.Policies = refs;
        await SaveStateAsync(site, state, ct);

        // Adopted policies may predate new-connections-only matching.
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
        if (automatic) AuditRepair(site, replaced, retyped);
        return new GatewaySetUpResult(created, adopted, groupsCreated, replaced + retyped);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A source block exists to stop unsolicited inbound traffic. Matching every state also dropped
    /// replies to connections LAN devices opened (our policies sit ahead of UniFi's Allow Return
    /// Traffic), so a listed address that also serves legitimate content broke the apps using it.
    /// The public list policies are narrowed too, as the gateway agent's rules are: only the
    /// outbound policies stop a device reaching a listed address.
    /// </remarks>
    public async Task<int> EnsureSourcePoliciesNewOnlyAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.GroupV4Id == null || state.GroupV6Id == null) return 0;
        var client = Client(site);
        // The stored ids can predate a Network app upgrade that re-keyed the groups, so the groups found by
        // their managed names count too. Best effort: without them, the stored ids still apply.
        IReadOnlyCollection<string> current = [];
        IReadOnlyDictionary<string, UniFiFirewallGroup>? byId = null;
        try
        {
            var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
            byId = GroupsById(groups);
            current = ManagedSourceGroupIds(groups);
            var stored = new[] { state.GroupV4Id, state.GroupV6Id, state.FeedsV4Id, state.FeedsV6Id }.OfType<string>().ToHashSet();
            if (current.Any(id => !stored.Contains(id)))
                _logger.LogInformation("Gateway blocking on {Site}: stored Network List ids are stale; also matching the ones UniFi Network names {Ids}",
                    site ?? "default", string.Join(", ", current.Where(id => !stored.Contains(id))));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Gateway blocking on {Site}: Network Lists not read ({Error}); matching the stored ids only",
                site ?? "default", ex.Message);
        }
        var narrowed = 0;
        foreach (var policy in SourcePoliciesToNarrow(await client.GetFirewallPoliciesAsync(ct), state, current))
        {
            // UniFi refuses every write to a policy whose group is gone or of the other family; RepairAsync replaces it.
            if (byId != null && HasBrokenGroup(policy, byId))
            {
                _logger.LogWarning("Gateway blocking on {Site}: policy {Id} names a Network List UniFi Network lacks for {Version}; skipped until it is replaced",
                    site ?? SiteSlugs.Default, policy.Id, policy.IpVersion);
                continue;
            }
            try
            {
                await client.SetFirewallPolicyConnectionStatesAsync(policy.Id, UniFiGroupBlockPolicyWrite.NewConnectionsOnly, ct);
                narrowed++;
            }
            catch (UniFiApiErrorException ex)
            {
                _logger.LogWarning("Gateway blocking on {Site}: policy {Id} not narrowed ({Error})", site ?? SiteSlugs.Default, policy.Id, ex.Code);
            }
        }
        if (narrowed > 0)
            _logger.LogInformation("Gateway blocking: {Count} blocklist policies now match new connections only", narrowed);
        return narrowed;
    }

    /// <summary>
    /// The site's Network List by id; when it is missing creates it, and when UniFi Network shows it with another
    /// type (it lets a referenced group be retyped) puts the type back, empty: the next sync refills it.
    /// </summary>
    private static async Task<(string Id, bool Created, bool Retyped)> EnsureGroupAsync(UniFiApiClient client,
        IReadOnlyList<UniFiFirewallGroup> groups, string? id, string name, string type, string description, CancellationToken ct)
    {
        var existing = groups.FirstOrDefault(g => g.Id == id) ?? groups.FirstOrDefault(g => g.Name == name && g.GroupType == type);
        if (existing == null)
            return ((await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(null, name, type, [], description), ct)).Id, true, false);
        if (existing.GroupType == type) return (existing.Id, false, false);
        await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(existing.Id, name, type, [], description), ct);
        return (existing.Id, false, true);
    }

    /// <summary>
    /// Deletes the <see cref="OwnBrokenPolicies"/> that <paramref name="only"/> picks; returns <paramref name="policies"/>
    /// without them, and how many it deleted.
    /// </summary>
    private async Task<(List<UniFiFirewallPolicy> Policies, int Deleted)> DeleteBrokenAsync(UniFiApiClient client, string? site,
        GatewayBlocklistState state, List<UniFiFirewallPolicy> policies, Func<UniFiFirewallPolicy, bool> only, CancellationToken ct)
    {
        var broken = OwnBrokenPolicies(policies, state, GroupsById(await client.GetFirewallGroupsOrThrowAsync(ct))).Where(only).ToList();
        if (broken.Count == 0) return (policies, 0);
        foreach (var policy in broken)
            await client.DeleteFirewallPolicyAsync(policy.Id, ct);
        _logger.LogInformation("Gateway blocking on {Site}: {Count} policies named a Network List UniFi Network lacks; replacing them",
            site ?? SiteSlugs.Default, broken.Count);
        return (policies.Except(broken).ToList(), broken.Count);
    }

    private void AuditRepair(string? site, int policies, int groups)
    {
        if (policies + groups > 0) _audit?.GatewayPoliciesRepaired(site, policies, groups);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Repairs as Set Up does, and only when there is something to repair: a stored group UniFi Network shows retyped,
    /// or one of Network Optimizer's policies on a missing or retyped group. Outbound only where it is set up.
    /// </remarks>
    public async Task<bool> RepairAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.GroupV4Id == null || state.GroupV6Id == null) return false;
        var client = Client(site);
        var groups = GroupsById(await client.GetFirewallGroupsOrThrowAsync(ct));
        var outboundOn = state.OutboundPolicies.Count > 0;
        static bool Retyped(IReadOnlyDictionary<string, UniFiFirewallGroup> groups, params (string? Id, string Type)[] stored) =>
            stored.Any(s => s.Id != null && groups.TryGetValue(s.Id, out var g) && g.GroupType != s.Type);
        var inbound = Retyped(groups, (state.GroupV4Id, "address-group"), (state.GroupV6Id, "ipv6-address-group"),
            (state.FeedsV4Id, "address-group"), (state.FeedsV6Id, "ipv6-address-group"));
        var outbound = outboundOn && Retyped(groups, (state.FeedsOutV4Id, "address-group"), (state.FeedsOutV6Id, "ipv6-address-group"));
        var broken = OwnBrokenPolicies(await client.GetFirewallPoliciesAsync(ct), state, groups);
        if (broken.Count > 0 && (await client.GetFirewallZonesAsync(ct)).FirstOrDefault(z => z.ZoneKey == FirewallZoneKeys.External) is { } external)
        {
            inbound |= broken.Any(p => p.Source.ZoneId == external.Id);
            outbound |= outboundOn && broken.Any(p => p.Destination.ZoneId == external.Id);
        }
        if (!inbound && !outbound) return false;
        _logger.LogInformation("Gateway blocking on {Site}: repairing policies on a missing or retyped Network List ({Inbound}{Outbound})",
            site ?? SiteSlugs.Default, inbound ? "inbound" : "", outbound ? (inbound ? ", outbound" : "outbound") : "");
        if (inbound) await SetUpCoreAsync(site, automatic: true, ct);
        if (outbound) await WriteFeedsOutboundAsync(site, true, automatic: true, ct);
        return true;
    }

    private static readonly HashSet<string> ManagedPolicyNames =
        [PolicyName, FeedsPolicyName, FeedsOutboundPolicyName, GatewayExceptionPolicies.PolicyName];

    private static readonly HashSet<string> ManagedGroupNames =
        [GroupNameV4, GroupNameV6, FeedsNameV4, FeedsNameV6, FeedsOutNameV4, FeedsOutNameV6];

    /// <summary>
    /// Network Optimizer's policies (recorded, or by a reserved name) that name a Network List UniFi Network lacks for
    /// their IP version. They block nothing, and UniFi refuses every write to them.
    /// </summary>
    internal static List<UniFiFirewallPolicy> OwnBrokenPolicies(IEnumerable<UniFiFirewallPolicy> policies, GatewayBlocklistState state,
        IReadOnlyDictionary<string, UniFiFirewallGroup> groups)
    {
        var ids = state.Policies.Concat(state.OutboundPolicies).Select(r => r.Id).Concat(state.ExceptionPolicies.Select(r => r.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return policies.Where(p => !p.Predefined && (ids.Contains(p.Id) || ManagedPolicyNames.Contains(p.Name)) && HasBrokenGroup(p, groups))
            .ToList();
    }

    /// <summary>
    /// Whether a policy names a Network List that is gone, or of the other family: UniFi Network answers any write to
    /// it with api.err.FirewallIpGroupDoesNotExist.
    /// </summary>
    internal static bool HasBrokenGroup(UniFiFirewallPolicy policy, IReadOnlyDictionary<string, UniFiFirewallGroup> groups)
    {
        var type = policy.IpVersion.ToUpperInvariant() switch { "IPV4" => "address-group", "IPV6" => "ipv6-address-group", _ => null };
        foreach (var side in new[] { policy.Source, policy.Destination })
        {
            // Only a side that matches by Network List uses its group; another target type ignores a leftover id.
            if (string.IsNullOrEmpty(side.IpGroupId)
                || (side.MatchingTargetType != null && !string.Equals(side.MatchingTargetType, "OBJECT", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!groups.TryGetValue(side.IpGroupId, out var group) || (type != null && group.GroupType != type)) return true;
        }
        return false;
    }

    /// <summary>Groups by id, keeping the first of any id the console lists twice.</summary>
    internal static IReadOnlyDictionary<string, UniFiFirewallGroup> GroupsById(IEnumerable<UniFiFirewallGroup> groups)
    {
        var byId = new Dictionary<string, UniFiFirewallGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
            if (!string.IsNullOrEmpty(g.Id)) byId.TryAdd(g.Id, g);
        return byId;
    }

    /// <summary>
    /// The Block policy already on the console for one zone pair and group, for Set Up to adopt. Never an
    /// exception's Allow policy: one for every listed address has the same zones and group.
    /// </summary>
    public static UniFiFirewallPolicy? ExistingBlock(IEnumerable<UniFiFirewallPolicy> policies, string sourceZoneId,
        string destinationZoneId, string groupId, bool groupIsSource) =>
        policies.FirstOrDefault(p => !p.Predefined && !IsAllow(p)
            && p.Source.ZoneId == sourceZoneId && p.Destination.ZoneId == destinationZoneId
            && (groupIsSource ? p.Source.IpGroupId : p.Destination.IpGroupId) == groupId);

    private static bool IsAllow(UniFiFirewallPolicy policy) =>
        string.Equals(policy.Action, "ALLOW", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Block policies on the site's blocklist and public list groups that still match more than new
    /// connections. Never the exceptions' Allow policies: they share the public list groups as their
    /// source, and their reply-only ones must keep their states.
    /// </summary>
    public static List<UniFiFirewallPolicy> SourcePoliciesToNarrow(IEnumerable<UniFiFirewallPolicy> policies, GatewayBlocklistState state,
        IEnumerable<string>? currentGroupIds = null)
    {
        var groups = new[] { state.GroupV4Id, state.GroupV6Id, state.FeedsV4Id, state.FeedsV6Id }.OfType<string>()
            .Concat(currentGroupIds ?? []).ToHashSet();
        return policies
            .Where(p => !p.Predefined && !IsAllow(p) && groups.Contains(p.Source.IpGroupId ?? "") && !IsNewConnectionsOnly(p))
            .ToList();
    }

    /// <summary>The ids of the blocklist and public list groups as the console names them, whatever ids are stored.</summary>
    public static IReadOnlyCollection<string> ManagedSourceGroupIds(IEnumerable<UniFiFirewallGroup> groups) =>
        groups.Where(g => (g.Name, g.GroupType) is (GroupNameV4, "address-group") or (GroupNameV6, "ipv6-address-group")
                              or (FeedsNameV4, "address-group") or (FeedsNameV6, "ipv6-address-group"))
            .Select(g => g.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();

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
        (await WriteFeedsOutboundAsync(site, enabled, automatic: false, ct)).Total;

    /// <summary>
    /// As <see cref="SetFeedsOutboundAsync"/>, also returning how many policies it created. <paramref name="automatic"/>
    /// as for <see cref="SetUpCoreAsync"/>.
    /// </summary>
    private async Task<(int Total, int Created, int Repaired)> WriteFeedsOutboundAsync(string? site, bool enabled, bool automatic,
        CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state?.FeedsV4Id == null || state.FeedsV6Id == null)
        {
            if (!enabled) return (0, 0, 0);
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
            return (0, 0, 0);
        }

        var zones = await client.GetFirewallZonesAsync(ct);
        var external = zones.FirstOrDefault(z => z.ZoneKey == FirewallZoneKeys.External)
            ?? throw new InvalidOperationException("Could not read the firewall zones from the UniFi Console.");

        var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
        var out4 = await EnsureGroupAsync(client, groups, state.FeedsOutV4Id, FeedsOutNameV4, "address-group", FeedsOutDescription, ct);
        var out6 = await EnsureGroupAsync(client, groups, state.FeedsOutV6Id, FeedsOutNameV6, "ipv6-address-group", FeedsOutDescription, ct);
        (state.FeedsOutV4Id, state.FeedsOutV6Id) = (out4.Id, out6.Id);
        var stalePolicies = policies.Where(p => !p.Predefined && p.Destination.ZoneId == external.Id
            && inboundGroups.Contains(p.Destination.IpGroupId ?? "")).ToList();
        foreach (var stale in stalePolicies)
            await client.DeleteFirewallPolicyAsync(stale.Id, ct);
        var (remaining, replaced) = await DeleteBrokenAsync(client, site, state, policies.Except(stalePolicies).ToList(),
            p => p.Destination.ZoneId == external.Id, ct);
        policies = remaining;

        var refs = new List<GatewayBlockPolicyRef>();
        var created = 0;
        foreach (var zone in zones.Where(z => z.Id != external.Id && z.ZoneKey != FirewallZoneKeys.Gateway))
        {
            foreach (var (version, groupId) in new[] { ("IPV4", state.FeedsOutV4Id), ("IPV6", state.FeedsOutV6Id) })
            {
                var existing = ExistingBlock(policies, zone.Id, external.Id, groupId, groupIsSource: false);
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
        var retypedOut = (out4.Retyped ? 1 : 0) + (out6.Retyped ? 1 : 0);
        if (automatic) AuditRepair(site, replaced, retypedOut);
        return (refs.Count, created, replaced + retypedOut);
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
    /// Removes Network Optimizer's policies, then its groups. Policies go first: Network 11 refuses to delete a
    /// referenced group (api.err.ObjectReferredBy), and 10.x deleted it and left the policy pointing at nothing.
    /// </summary>
    public async Task RemoveAsync(string? site, CancellationToken ct)
    {
        var state = await LoadStateAsync(site, ct);
        if (state == null) return;
        var client = Client(site);
        var policies = await client.GetFirewallPoliciesAsync(ct);
        // The stored ids can predate a Network app upgrade that re-keyed everything, so ours by name count too.
        var groupIds = new[] { state.GroupV4Id, state.GroupV6Id, state.FeedsV4Id, state.FeedsV6Id, state.FeedsOutV4Id, state.FeedsOutV6Id }
            .Concat((await client.GetFirewallGroupsOrThrowAsync(ct)).Where(g => ManagedGroupNames.Contains(g.Name)).Select(g => g.Id))
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
        var policyIds = state.Policies.Concat(state.OutboundPolicies).Select(p => p.Id)
            .Concat(state.ExceptionPolicies.Select(p => p.Id))
            .Concat(policies.Where(p => !p.Predefined && (ManagedPolicyNames.Contains(p.Name)
                || groupIds.Contains(p.Source.IpGroupId ?? "") || groupIds.Contains(p.Destination.IpGroupId ?? ""))).Select(p => p.Id))
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
            && current.Name == group.Name && current.Description == group.Description && current.GroupType == group.GroupType
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
        var (created, repaired) = (0, 0);
        if (inbound && await SetUpCoreAsync(site, automatic: true, ct) is var setUp)
            (created, repaired) = (created + setUp.PoliciesCreated, repaired + setUp.Repaired);
        if (outbound && await WriteFeedsOutboundAsync(site, true, automatic: true, ct) is var written)
            (created, repaired) = (created + written.Created, repaired + written.Repaired);
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
        return created > 0 || removed || repaired > 0;
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
                await WriteFeedsOutboundAsync(site, true, automatic: true, ct);
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
        var client = Client(site);
        var policies = await client.GetFirewallPoliciesAsync(ct);
        // Best effort: without the groups, the policies are still checked.
        List<UniFiFirewallGroup>? groups = null;
        try
        {
            groups = await client.GetFirewallGroupsOrThrowAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("Gateway blocking on {Site}: Network Lists not read for the check ({Error})", site ?? SiteSlugs.Default, ex.Message);
        }
        return Verify(state, policies, groups);
    }

    /// <summary>
    /// What <see cref="VerifyAsync"/> reports for <paramref name="state"/> against the console's policies, and its
    /// groups when given.
    /// </summary>
    internal static string? Verify(GatewayBlocklistState state, IEnumerable<UniFiFirewallPolicy> policies,
        IEnumerable<UniFiFirewallGroup>? groups = null)
    {
        // The console's list is not always unique by id: one has been seen listing an id twice (issue #1333).
        // Ours are ids the console gave our own policies, so keep the first of any repeat.
        var byId = new Dictionary<string, UniFiFirewallPolicy>();
        foreach (var p in policies)
            byId.TryAdd(p.Id, p);
        var all = state.Policies.Concat(state.OutboundPolicies).ToList();
        var missing = all.Count(r => !byId.ContainsKey(r.Id));
        var disabled = all.Count(r => byId.TryGetValue(r.Id, out var p) && !p.Enabled);
        if (missing > 0) return $"{missing} of Network Optimizer's Block policies were removed in UniFi Network. Run Set Up again.";
        var groupsById = groups == null ? null : GroupsById(groups);
        var broken = groupsById == null ? 0 : all.Count(r => byId.TryGetValue(r.Id, out var p) && HasBrokenGroup(p, groupsById));
        if (broken > 0) return $"{broken} of Network Optimizer's Block policies name a Network List UniFi Network no longer has. Run Set Up again.";
        if (state.FeedsV4Id == null) return "Run Set Up again to add the public block lists to the gateway.";
        if (disabled > 0) return $"{disabled} of Network Optimizer's Block policies are disabled in UniFi Network.";
        return null;
    }
}
