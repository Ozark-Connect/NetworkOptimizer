using System.Text.Json;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>The Trusted Sources address groups in a site's UniFi console.</summary>
public sealed class TrustedSourcesGroupState
{
    public string? GroupV4Id { get; set; }
    public string? GroupV6Id { get; set; }

    /// <summary>The "+ Cloudflare" pair, kept only while that option is on.</summary>
    public string? CloudflareV4Id { get; set; }
    public string? CloudflareV6Id { get; set; }

    /// <summary>
    /// 2 once the pairs are separate. Before that, one pair held Cloudflare's ranges when the option
    /// was on; those groups are adopted as the "+ Cloudflare" pair, so rules using them keep working.
    /// </summary>
    public int Version { get; set; }
}

/// <summary>
/// Keeps Trusted Sources as address groups in a site's UniFi console, for port forwards and
/// policies to reference instead of hand-kept addresses: a plain pair (sites and extra trusted
/// addresses) and, by choice, a "+ Cloudflare" pair that adds Cloudflare's ranges. Network
/// Optimizer only writes the members: nothing here references the groups, and they are never
/// emptied, since an empty group on a port forward's source limit would shut it.
/// </summary>
public sealed class TrustedSourcesGroup(IUniFiClientAccessor clients, IThreatSettingsAccessor settings)
{
    public const string NameV4 = "Net. Opt. Trusted Sources (IPv4)";
    public const string NameV6 = "Net. Opt. Trusted Sources (IPv6)";
    public const string CloudflareNameV4 = "Net. Opt. Trusted Sources + Cloudflare (IPv4)";
    public const string CloudflareNameV6 = "Net. Opt. Trusted Sources + Cloudflare (IPv6)";
    public const string Description = "Managed by Network Optimizer: your sites' addresses and the extra trusted addresses in Trusted Sources. Changes here are overwritten.";
    public const string CloudflareDescription = "Managed by Network Optimizer: Trusted Sources plus Cloudflare's ranges, for port forwards to hosts Cloudflare proxies. Changes here are overwritten.";

    private const int CurrentVersion = 2;

    public async Task<TrustedSourcesGroupState?> LoadStateAsync(string? site, CancellationToken ct)
    {
        var json = await settings.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.UniFiGroupState, site), ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<TrustedSourcesGroupState>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task SaveStateAsync(string? site, TrustedSourcesGroupState state, CancellationToken ct) =>
        settings.SaveSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.UniFiGroupState, site), JsonSerializer.Serialize(state), ct);

    /// <summary>
    /// Writes the plain pair, and the "+ Cloudflare" pair when <paramref name="cloudflareRanges"/> is
    /// given. Groups are created or adopted by name; a family with no members keeps what it had.
    /// </summary>
    public async Task<BlockTargetResult> SyncAsync(string? site, IReadOnlyList<string> members,
        IReadOnlyList<string>? cloudflareRanges, CancellationToken ct)
    {
        var client = clients.GetClient(site);
        if (client == null) return BlockTargetResult.Failure("The UniFi Console is not connected.");
        try
        {
            var state = await LoadStateAsync(site, ct) ?? new TrustedSourcesGroupState { Version = CurrentVersion };
            if (state.Version < CurrentVersion)
            {
                if (cloudflareRanges != null && state.GroupV4Id != null)
                {
                    // The old single pair carried Cloudflare: it becomes the "+ Cloudflare" pair.
                    (state.CloudflareV4Id, state.CloudflareV6Id) = (state.GroupV4Id, state.GroupV6Id);
                    (state.GroupV4Id, state.GroupV6Id) = (null, null);
                }
                state.Version = CurrentVersion;
            }

            List<UniFiFirewallGroup>? groups = null;
            async Task<string?> Find(string? id, string name, string type)
            {
                if (id != null) return id;
                groups ??= await client.GetFirewallGroupsOrThrowAsync(ct);
                return groups.FirstOrDefault(g => g.Name == name && g.GroupType == type)?.Id;
            }
            async Task<string> Write(string? id, string name, string type, List<string> entries, string description) =>
                (await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(id, name, type, entries, description), ct)).Id;

            var (v4, v6) = GatewayBlocklist.SplitFamilies(members);
            state.GroupV4Id = await Find(state.GroupV4Id, NameV4, "address-group");
            state.GroupV6Id = await Find(state.GroupV6Id, NameV6, "ipv6-address-group");
            if (v4.Count > 0 || state.GroupV4Id == null)
                state.GroupV4Id = await Write(state.GroupV4Id, NameV4, "address-group", v4, Description);
            if (v6.Count > 0 || state.GroupV6Id == null)
                state.GroupV6Id = await Write(state.GroupV6Id, NameV6, "ipv6-address-group", v6, Description);
            var count = v4.Count + v6.Count;

            if (cloudflareRanges != null)
            {
                var (c4, c6) = GatewayBlocklist.SplitFamilies(members.Concat(cloudflareRanges));
                state.CloudflareV4Id = await Find(state.CloudflareV4Id, CloudflareNameV4, "address-group");
                state.CloudflareV6Id = await Find(state.CloudflareV6Id, CloudflareNameV6, "ipv6-address-group");
                state.CloudflareV4Id = await Write(state.CloudflareV4Id, CloudflareNameV4, "address-group", c4, CloudflareDescription);
                state.CloudflareV6Id = await Write(state.CloudflareV6Id, CloudflareNameV6, "ipv6-address-group", c6, CloudflareDescription);
            }

            await SaveStateAsync(site, state, ct);
            return BlockTargetResult.Success(count);
        }
        catch (UniFiApiErrorException ex) when (ex.Code == "api.err.IdInvalid")
        {
            // Deleted in UniFi Network: forget the ids, and the next sync creates the groups again.
            await SaveStateAsync(site, new TrustedSourcesGroupState { Version = CurrentVersion }, ct);
            return BlockTargetResult.Failure("A Trusted Sources group was removed in UniFi Network. It is created again on the next sync; point your rules at the new one.");
        }
        catch (Exception ex) when (ex is UniFiApiErrorException or UniFiPermissionException or InvalidOperationException or HttpRequestException)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }
}
