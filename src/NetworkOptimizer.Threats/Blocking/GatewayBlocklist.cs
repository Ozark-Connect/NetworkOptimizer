using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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

    public List<GatewayBlockPolicyRef> Policies { get; set; } = [];
}

/// <summary>One Block policy: External to <see cref="ZoneId"/> for one IP version.</summary>
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
    public const string GroupNameV4 = "Network Optimizer Blocklist (IPv4)";
    public const string GroupNameV6 = "Network Optimizer Blocklist (IPv6)";
    public const string PolicyName = "Network Optimizer Blocklist";
    public const string Description = "Managed by Network Optimizer: sources it has blocked. Changes here are overwritten.";
    public const string FeedsNameV4 = "Network Optimizer Public Block Lists (IPv4)";
    public const string FeedsNameV6 = "Network Optimizer Public Block Lists (IPv6)";
    public const string FeedsPolicyName = "Network Optimizer Public Block Lists";
    public const string FeedsDescription = "Managed by Network Optimizer: the public block lists chosen in Automatic Blocking. Changes here are overwritten.";

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
                var policy = await client.CreateFirewallPolicyAsync(UniFiGroupBlockPolicyWrite.Create(
                    name, description, version, external.Id, groupId!, zone.Id), ct);
                created++;
                refs.Add(new GatewayBlockPolicyRef(policy.Id, zone.Id, version));
            }
        }
        state.Policies = refs;
        await SaveStateAsync(site, state, ct);

        // New custom policies land after the ones already there, so a user ALLOW from External
        // would run first. Move ours to the front of each zone pair once; later ones land after.
        var current = await client.GetFirewallPoliciesAsync(ct);
        foreach (var pair in refs.GroupBy(r => r.ZoneId))
        {
            var (before, after) = OrderWithOursFirst(current, external.Id, pair.Key, pair.Select(r => r.Id).ToHashSet());
            if (before.Count > 0)
                await client.ReorderFirewallPoliciesAsync(external.Id, pair.Key, before, after, ct);
        }
        _logger.LogInformation("Gateway blocking set up: {Created} policies created, {Adopted} adopted, {Groups} groups created",
            created, adopted, groupsCreated);
        return new GatewaySetUpResult(created, adopted, groupsCreated);
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
        var groupIds = new[] { state.GroupV4Id, state.GroupV6Id, state.FeedsV4Id, state.FeedsV6Id }
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet();
        var policyIds = state.Policies.Select(p => p.Id)
            .Concat(policies.Where(p => !p.Predefined && groupIds.Contains(p.Source.IpGroupId ?? "")).Select(p => p.Id))
            .Distinct();
        foreach (var id in policyIds)
            await client.DeleteFirewallPolicyAsync(id, ct);
        foreach (var id in groupIds)
            await client.DeleteFirewallGroupAsync(id!, ct);
        await SaveStateAsync(site, null, ct);
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
            await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(state.GroupV4Id, GroupNameV4, "address-group", v4, Description), ct);
            await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(state.GroupV6Id, GroupNameV6, "ipv6-address-group", v6, Description), ct);
            return BlockTargetResult.Success(v4.Count + v6.Count);
        }
        catch (UniFiApiErrorException ex) when (ex.Code == "api.err.IdInvalid")
        {
            return BlockTargetResult.Failure("Network Optimizer's address group was removed in UniFi Network. Run Set Up again.");
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
    public async Task<BlockTargetResult> SyncFeedsAsync(string? site, IReadOnlyList<string> entries, CancellationToken ct)
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
            var client = Client(site);
            await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(state.FeedsV4Id, FeedsNameV4, "address-group", v4, FeedsDescription), ct);
            await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(state.FeedsV6Id, FeedsNameV6, "ipv6-address-group", v6, FeedsDescription), ct);
            return BlockTargetResult.Success(v4.Count + v6.Count);
        }
        catch (UniFiApiErrorException ex) when (ex.Code == "api.err.IdInvalid")
        {
            return BlockTargetResult.Failure("Network Optimizer's public block list group was removed in UniFi Network. Run Set Up again.");
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
        var policies = await Client(site).GetFirewallPoliciesAsync(ct);
        var byId = policies.ToDictionary(p => p.Id);
        var missing = state.Policies.Count(r => !byId.ContainsKey(r.Id));
        var disabled = state.Policies.Count(r => byId.TryGetValue(r.Id, out var p) && !p.Enabled);
        if (missing > 0) return $"{missing} of Network Optimizer's Block policies were removed in UniFi Network. Run Set Up again.";
        if (state.FeedsV4Id == null) return "Run Set Up again to add the public block lists to the gateway.";
        if (disabled > 0) return $"{disabled} of Network Optimizer's Block policies are disabled in UniFi Network.";
        return null;
    }
}
