using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>The user's choices on Re-address Networks: a pattern, and per-network new subnets that win over it.</summary>
public sealed class ReaddressChoices
{
    public string? Pattern { get; init; }
    public Dictionary<string, string> NewSubnets { get; init; } = [];
}

/// <summary>A client whose address may be set on the device; <c>LastSeen</c> is set for an offline one, which cannot be told apart.</summary>
public sealed record StaticClient(string Name, string Mac, string Ip, string NewIp, string Network, bool Reserved, DateTime? LastSeen = null);

/// <summary>The dry run: every move, rewrite, and thing a person must handle.</summary>
public sealed class ReaddressView
{
    public List<SubnetMove> Moves { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<AddressRewrite> Rewrites { get; init; } = [];
    public List<AddressMention> Mentions { get; init; } = [];
    /// <summary>Addresses in a moved subnet left alone because a WAN or a VPN's far side owns them.</summary>
    public List<AddressMention> Remote { get; init; } = [];
    public List<StaticClient> StaticClients { get; init; } = [];
    /// <summary>Known clients last seen in a moved subnet but offline now: no lease proves nothing for them.</summary>
    public List<StaticClient> OfflineClients { get; init; } = [];
    /// <summary>Where the static clients came from: UniFi's IP Table, or DHCP ranges when it was not readable.</summary>
    public string StaticSource { get; init; } = "";
    public List<SettingRewrite> Settings { get; init; } = [];
    /// <summary>DHCP leases lowered ahead of the move, when they are.</summary>
    public LeasePrepare? Prepare { get; init; }
    /// <summary>When the last re-address applied, in the last week: Reconnect Clients is for after it.</summary>
    public DateTime? LastAppliedAt { get; init; }
    public bool Ready => Errors.Count == 0 && Moves.Any(m => m.OldCidr != m.NewCidr);
}

public sealed partial class ChangeControlService
{
    private static List<SubnetMove> Moves(ConfigState live, ReaddressChoices choices) =>
        Readdressing.Proposal(live, choices.Pattern)
            .Select(m => choices.NewSubnets.TryGetValue(m.NetworkId, out var chosen) && !string.IsNullOrWhiteSpace(chosen)
                ? m with { NewCidr = Readdressing.ParseSubnet(chosen)?.ToString() ?? chosen.Trim() }
                : m)
            .ToList();

    public async Task<ReaddressView> GetReaddressAsync(ReaddressChoices choices, CancellationToken cancellationToken = default)
    {
        var live = await Engine.ReadLiveAsync(cancellationToken);
        var moves = Moves(live, choices);
        var errors = Readdressing.Validate(live, moves);
        var active = moves.Where(m => m.OldCidr != m.NewCidr).ToList();
        var prepare = await LeasePrepareAsync(cancellationToken);
        var lastApplied = (await Repo.UseAsync((r, c) => r.GetPlansAsync([ConfigPlanStatus.Applied, ConfigPlanStatus.Watching], 200, c), cancellationToken))
            .Where(p => p.Kind == ConfigPlanKind.Readdress && p.CompletedAt > DateTime.UtcNow.AddDays(-7)).Max(p => p.CompletedAt);
        if (active.Count == 0 || errors.Count > 0)
            return new ReaddressView { Moves = moves, Errors = errors, Prepare = prepare, LastAppliedAt = lastApplied };

        var result = Readdressing.Apply(live, active);
        var (statics, source) = await StaticClientsAsync(live, active, cancellationToken);
        return new ReaddressView
        {
            Moves = moves,
            Errors = errors,
            Warnings = await ReaddressWarningsAsync(live, active, result, cancellationToken),
            Rewrites = result.Rewrites,
            Mentions = result.Mentions,
            Remote = result.Remote,
            StaticClients = statics.Where(s => s.LastSeen == null).ToList(),
            OfflineClients = statics.Where(s => s.LastSeen != null).ToList(),
            StaticSource = source,
            Settings = await _registry.ReaddressSettingsAsync(_site.Slug, active, RemoteCidrs(live), apply: false, cancellationToken),
            Prepare = prepare,
            LastAppliedAt = lastApplied,
        };
    }

    public async Task<int> BuildReaddressPlanAsync(ReaddressChoices choices, CancellationToken cancellationToken = default)
    {
        var live = await Engine.ReadLiveAsync(cancellationToken);
        var moves = Moves(live, choices);
        if (Readdressing.Validate(live, moves) is { Count: > 0 } errors) throw new InvalidOperationException(errors[0]);
        var active = moves.Where(m => m.OldCidr != m.NewCidr).ToList();
        if (active.Count == 0) throw new InvalidOperationException("No network moves: give at least one a new subnet.");

        var result = Readdressing.Apply(live, active);
        var prepare = await LeasePrepareAsync(cancellationToken);
        RestoreLeases(result, prepare);
        var warnings = await ReaddressWarningsAsync(live, active, result, cancellationToken);
        if (prepare == null)
            warnings.Add("DHCP leases were not lowered first: clients move as their current leases renew. Reconnect Clients after the apply speeds that up.");
        else if (prepare.ReadyAt > DateTime.UtcNow)
            warnings.Add($"Leases were lowered {Local(prepare.LoweredAt):MMM d, h:mm tt}; some clients are on their old lease until {Local(prepare.ReadyAt):MMM d, h:mm tt}.");
        else
            warnings.Add("DHCP leases are short on every moving network; this plan puts each one's lease time back.");
        var (statics, _) = await StaticClientsAsync(live, active, cancellationToken);
        var remote = RemoteCidrs(live);
        var settings = await _registry.ReaddressSettingsAsync(_site.Slug, active, remote, apply: false, cancellationToken);
        var summary = string.Join(", ", active.Select(m => $"{m.NetworkName} {m.OldCidr} to {m.NewCidr}"));
        var pathNetworks = await PathNetworksAsync(active, cancellationToken);
        var plan = await Plans.CreateAsync(ConfigPlanKind.Readdress, $"Re-address {await SiteNameAsync(cancellationToken)}", summary, null, null,
            result.Desired, new PlanOptions { Collections = result.Collections, DeleteMissing = false }, Actor, cancellationToken,
            doc =>
            {
                // The network holding the console, or Network Optimizer's path to it, is the last write:
                // once it moves, the old address every later write goes to is gone.
                foreach (var op in doc.Operations.Where(o => o.Collection == "networkconf" && o.ObjectId != null && pathNetworks.Contains(o.ObjectId)))
                {
                    op.Order = 100;
                    op.ManagementPath = true;
                }
                PlanBuilder.Reorder(doc);
                doc.Context[PlanCoordinator.ReaddressPathContext] = string.Join(",", doc.Operations
                    .Where(o => o.Collection == "networkconf" && o.ObjectId != null && pathNetworks.Contains(o.ObjectId)).Select(o => o.Key));
                doc.Context[PlanCoordinator.ReaddressContext] = JsonSerializer.Serialize(active);
                doc.Context[PlanCoordinator.ReaddressRemoteContext] = JsonSerializer.Serialize(remote);
                foreach (var m in result.Remote)
                    doc.Notes.Add($"Left alone, owned by a WAN or a VPN's far side: {ConfigCollections.Get(m.Collection)?.Noun ?? m.Collection} {m.ObjectName}, {m.Path}: {m.Value}");
                foreach (var w in warnings) doc.Notes.Add(w);
                if (statics.Where(s => s.LastSeen == null).ToList() is { Count: > 0 } set)
                    doc.Notes.Add($"Set on the device itself, so this plan cannot move them: {string.Join(", ", set.Select(s => $"{s.Name} ({s.Ip} to {s.NewIp})"))}.");
                if (statics.Count(s => s.LastSeen != null) is > 0 and var offline)
                    doc.Notes.Add($"{offline} offline clients were last seen in a moved subnet. Any set on the device need changing when they are back; the dry run lists them.");
                foreach (var m in result.Mentions)
                    doc.Notes.Add($"Not rewritten, holds an old address: {ConfigCollections.Get(m.Collection)?.Noun ?? m.Collection} {m.ObjectName}, {m.Path}: {m.Value}");
                if (settings.Count > 0)
                    doc.Notes.Add($"Network Optimizer moves its own settings with the site once this applies (and back on a revert): "
                        + string.Join(", ", settings.Select(s => $"{s.Setting} {s.Name} {s.Old} to {s.New}")) + ".");
            });
        return plan.Id;
    }

    /// <summary>Moved networks that hold the console's address or the address Network Optimizer reaches the site from.</summary>
    private async Task<HashSet<string>> PathNetworksAsync(List<SubnetMove> active, CancellationToken ct)
    {
        var addresses = Plans.ManagementAddresses().Select(Readdressing.ParseIp).OfType<uint>().ToList();
        if (Uri.TryCreate(_registry.Connection(_site.Slug).CurrentConfig?.ControllerUrl, UriKind.Absolute, out var uri))
            addresses.AddRange(Readdressing.ParseIp(uri.Host) is { } ip ? [ip]
                : (await SafeResolveAsync(uri.Host, ct)).Select(a => Readdressing.ParseIp(a.ToString())).OfType<uint>());
        return active.Where(m => Readdressing.ParseSubnet(m.OldCidr) is { } old && addresses.Any(old.Contains))
            .Select(m => m.NetworkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> RemoteCidrs(ConfigState live) =>
        Readdressing.RemoteRanges(live).Select(r => r.Range.ToString()).Distinct().ToList();

    private async Task<List<string>> ReaddressWarningsAsync(ConfigState live, List<SubnetMove> active, ReaddressResult result, CancellationToken ct)
    {
        var warnings = new List<string>();
        var map = Readdressing.Map(active);
        // Network Optimizer's own host (or this site's On-Site Agent): it cannot watch or revert from an address that is gone.
        var own = Plans.ManagementAddresses().Where(a => Readdressing.ParseIp(a) is { } ip && Readdressing.MapIp(ip, map) != null).Distinct().ToList();
        if (own.Count > 0)
            warnings.Add($"Network Optimizer reaches this site from {string.Join(", ", own)}, which {(own.Count == 1 ? "is" : "are")} in a moved subnet. "
                + "Re-address that host in the same window: until then it is off the network, and no watch or revert can run.");
        var mgmt = ConfigDiffEngine.ManagementNetworkIds(live);
        foreach (var m in active.Where(m => mgmt.Contains(m.NetworkId)))
            warnings.Add($"{m.NetworkName} is a management network: devices and the UniFi Console are reached at new addresses after the move. Review Plan holds it at the Lockout tier.");

        // Devices find the console by its inform address; a console inside a moved subnet must be found at its new one.
        var url = _registry.Connection(_site.Slug).CurrentConfig?.ControllerUrl;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var addresses = Readdressing.ParseIp(uri.Host) is { } ip ? [ip]
                : (await SafeResolveAsync(uri.Host, ct)).Select(a => Readdressing.ParseIp(a.ToString())).OfType<uint>().ToList();
            foreach (var a in addresses.Where(a => Readdressing.MapIp(a, map) != null).Distinct())
            {
                var moved = Readdressing.Format(Readdressing.MapIp(a, map)!.Value);
                var inform = live.Get("setting").FirstOrDefault(s => s["key"]?.ToString() == "super_mgmt");
                var overridden = inform?["override_inform_host"] is JsonValue o && o.TryGetValue<bool>(out var on) && on;
                warnings.Add(Readdressing.ParseIp(uri.Host) != null
                    ? $"The UniFi Console is at {uri.Host}, which moves to {moved}. Network Optimizer's console connection is moved with the plan."
                    : $"{uri.Host} resolves to {Readdressing.Format(a)}, which moves to {moved}. Update its DNS record with the move.");
                warnings.Add(overridden
                    ? "Devices inform to the Inform Host override, which this plan moves if it is an address."
                    : $"Devices inform to the console's current address. Set UniFi Network's Inform Host override to {moved} or a name before applying, or devices disconnect until they are re-pointed.");
            }
        }

        var leases = live.Get("networkconf").Where(n => active.Any(m => m.NetworkId == ConfigState.IdOf(n)))
            .Select(n => int.TryParse(n["dhcpd_leasetime"]?.ToString(), out var s) ? s : 86400).DefaultIfEmpty(86400).Max();
        warnings.Add($"Clients take their new address when their DHCP lease renews, within {TimeSpan.FromSeconds(leases).TotalHours:0.#} hours, or at once when they reconnect.");
        if (result.Collections.Contains("device"))
            warnings.Add("Devices with a static management address are moved in the plan; devices on DHCP follow their lease.");
        return warnings;
    }

    private static async Task<IPAddress[]> SafeResolveAsync(string host, CancellationToken ct)
    {
        try { return await Dns.GetHostAddressesAsync(host, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }

    /// <summary>
    /// Clients in a moved subnet whose address is set on the device. The gateway's own DHCP lease file
    /// (over Gateway SSH) is the record: a connected client with no lease there is static, reserved or
    /// not, since a DHCP client renews well before its lease ends. Without Gateway SSH, UniFi's IP Table
    /// can only clear clients with no reservation (it lists every reservation the same way), and without
    /// that, an address outside its network's DHCP range counts.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "stat/sta clients, the gateway's dnsmasq lease file, v2 active-leases (the IP Table)")]
    private async Task<(List<StaticClient> Clients, string Source)> StaticClientsAsync(ConfigState live, List<SubnetMove> active, CancellationToken ct)
    {
        var map = Readdressing.Map(active);
        var clients = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/sta", null, ct);
        if (!clients.IsSuccess || ConfigSnapshotReader.ExtractList(clients.Body) is not { } list) return ([], "");

        var gateway = await _registry.GatewayLeaseMacsAsync(_site.Slug, ct);
        HashSet<string>? ipTable = null;
        if (gateway == null)
        {
            // {"dhcp_lease_info":[{mac, ip, use_fixedip, lease_expiration_time, status}]}: leases and reservations.
            var leases = await Engine.Api.SendAsync(UniFiApiScope.SiteV2, HttpMethod.Get, "active-leases", null, ct);
            try
            {
                if (leases.IsSuccess && JsonNode.Parse(leases.Body)?["dhcp_lease_info"] is JsonArray rows)
                    ipTable = rows.OfType<JsonObject>().Select(r => r["mac"]?.ToString()?.ToLowerInvariant()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (JsonException) { }
        }

        var networks = live.Get("networkconf").ToDictionary(ConfigState.IdOf, n => n, StringComparer.OrdinalIgnoreCase);
        var result = new List<StaticClient>();
        foreach (var c in list)
        {
            if (c["ip"]?.ToString() is not { } ipText || Readdressing.ParseIp(ipText) is not { } ip || Readdressing.MapIp(ip, map) == null) continue;
            var reserved = c["use_fixedip"] is JsonValue f && f.TryGetValue<bool>(out var fixedIp) && fixedIp;
            var mac = c["mac"]?.ToString()?.ToLowerInvariant() ?? "";
            var network = c["network_id"]?.ToString() is { } nid && networks.TryGetValue(nid, out var n) ? n : null;
            var isStatic = gateway != null ? !gateway.Contains(mac)
                : !reserved && (ipTable != null ? !ipTable.Contains(mac) : network != null && !InDhcpRange(network, ip));
            if (!isStatic) continue;
            var name = c["name"]?.ToString() is { Length: > 0 } nm ? nm : c["hostname"]?.ToString() is { Length: > 0 } hn ? hn : mac;
            result.Add(new StaticClient(name, mac, ipText, Readdressing.Format(Readdressing.MapIp(ip, map)!.Value), network?["name"]?.ToString() ?? "", reserved));
        }
        result.AddRange(await OfflineClientsAsync(list, networks, map, ct));
        var source = gateway != null ? "the gateway's DHCP leases"
            : ipTable != null ? "UniFi's IP Table (reserved clients cannot be told apart without Gateway SSH)"
            : "DHCP ranges";
        return (result.OrderBy(r => Readdressing.ParseIp(r.Ip)).ToList(), source);
    }

    /// <summary>Known clients not connected now whose last (or reserved) address is in a moved subnet.</summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "rest/user last_ip, last_seen, fixed_ip")]
    private async Task<List<StaticClient>> OfflineClientsAsync(List<JsonObject> connected, Dictionary<string, JsonObject> networks,
        List<(Readdressing.Subnet Old, Readdressing.Subnet New)> map, CancellationToken ct)
    {
        var online = connected.Select(c => c["mac"]?.ToString()?.ToLowerInvariant()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var users = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "rest/user", null, ct);
        if (!users.IsSuccess || ConfigSnapshotReader.ExtractList(users.Body) is not { } list) return [];
        var result = new List<StaticClient>();
        foreach (var u in list)
        {
            var mac = u["mac"]?.ToString()?.ToLowerInvariant() ?? "";
            if (mac.Length == 0 || online.Contains(mac)) continue;
            var reserved = u["use_fixedip"] is JsonValue f && f.TryGetValue<bool>(out var r) && r;
            var ipText = (reserved ? u["fixed_ip"]?.ToString() : null) ?? u["last_ip"]?.ToString();
            if (Readdressing.ParseIp(ipText) is not { } ip || Readdressing.MapIp(ip, map) is not { } moved) continue;
            DateTime? seen = long.TryParse(u["last_seen"]?.ToString(), out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : DateTime.MinValue;
            var network = u["last_connection_network_id"]?.ToString() is { } nid && networks.TryGetValue(nid, out var n) ? n["name"]?.ToString() ?? "" : "";
            var name = u["name"]?.ToString() is { Length: > 0 } nm ? nm : u["hostname"]?.ToString() is { Length: > 0 } hn ? hn : mac;
            result.Add(new StaticClient(name, mac, ipText!, Readdressing.Format(moved), network, reserved, seen));
        }
        return result.OrderByDescending(c => c.LastSeen).ToList();
    }

    private static bool InDhcpRange(JsonObject network, uint ip) =>
        network["dhcpd_enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on
        && Readdressing.ParseIp(network["dhcpd_start"]?.ToString()) is { } start && Readdressing.ParseIp(network["dhcpd_stop"]?.ToString()) is { } stop
        && ip >= start && ip <= stop;
}
