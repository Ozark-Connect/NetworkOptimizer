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
}

/// <summary>
/// Keeps an IPv4 and an IPv6 address group in a site's UniFi console holding its Trusted Sources,
/// for port forwards and policies to reference instead of hand-kept addresses. Network Optimizer
/// only writes the members: nothing here references the groups, and they are never emptied, since
/// an empty group on a port forward's source limit would shut it.
/// </summary>
public sealed class TrustedSourcesGroup(IUniFiClientAccessor clients, IThreatSettingsAccessor settings)
{
    public const string NameV4 = "Network Optimizer Trusted Sources (IPv4)";
    public const string NameV6 = "Network Optimizer Trusted Sources (IPv6)";
    public const string Description = "Managed by Network Optimizer: your sites' addresses and the extra addresses in Trusted Sources. Changes here are overwritten.";

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

    /// <summary>
    /// Creates (or adopts by name) the groups, then writes the members of each family that has any.
    /// A family with none keeps what it had.
    /// </summary>
    public async Task<BlockTargetResult> SyncAsync(string? site, IReadOnlyList<string> members, CancellationToken ct)
    {
        var client = clients.GetClient(site);
        if (client == null) return BlockTargetResult.Failure("The UniFi Console is not connected.");
        var (v4, v6) = GatewayBlocklist.SplitFamilies(members);
        try
        {
            var state = await LoadStateAsync(site, ct) ?? new TrustedSourcesGroupState();
            if (state.GroupV4Id == null || state.GroupV6Id == null)
            {
                var groups = await client.GetFirewallGroupsOrThrowAsync(ct);
                state.GroupV4Id ??= groups.FirstOrDefault(g => g.Name == NameV4 && g.GroupType == "address-group")?.Id;
                state.GroupV6Id ??= groups.FirstOrDefault(g => g.Name == NameV6 && g.GroupType == "ipv6-address-group")?.Id;
            }
            if (v4.Count > 0 || state.GroupV4Id == null)
                state.GroupV4Id = (await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(state.GroupV4Id, NameV4, "address-group", v4, Description), ct)).Id;
            if (v6.Count > 0 || state.GroupV6Id == null)
                state.GroupV6Id = (await client.SaveFirewallGroupAsync(new UniFiFirewallGroupWrite(state.GroupV6Id, NameV6, "ipv6-address-group", v6, Description), ct)).Id;
            await settings.SaveSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.UniFiGroupState, site), JsonSerializer.Serialize(state), ct);
            return BlockTargetResult.Success(v4.Count + v6.Count);
        }
        catch (UniFiApiErrorException ex) when (ex.Code == "api.err.IdInvalid")
        {
            // Deleted in UniFi Network: forget it, and the next sync creates it again.
            await settings.SaveSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.UniFiGroupState, site), "", ct);
            return BlockTargetResult.Failure("A Trusted Sources group was removed in UniFi Network. It is created again on the next sync; point your port forwards at the new one.");
        }
        catch (Exception ex) when (ex is UniFiApiErrorException or UniFiPermissionException or InvalidOperationException or HttpRequestException)
        {
            return BlockTargetResult.Failure(ex.Message);
        }
    }
}
