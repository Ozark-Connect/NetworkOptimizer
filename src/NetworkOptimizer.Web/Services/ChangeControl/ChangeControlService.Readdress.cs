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
public sealed record StaticClient(string Name, string Mac, string Ip, string NewIp, string Network, bool Reserved, DateTime? LastSeen = null)
{
    /// <summary>Reserved, no lease, and idle: most likely a lease that lapsed while it slept, so taken as DHCP.</summary>
    public bool Idle { get; init; }
}

/// <summary>Something to know before applying: a short title and one line of detail.</summary>
public sealed record ReaddressWarning(string Title, string Detail);

/// <summary>The dry run: every move, rewrite, and thing a person must handle.</summary>
public sealed class ReaddressView
{
    public List<SubnetMove> Moves { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public List<ReaddressWarning> Warnings { get; init; } = [];
    public List<AddressRewrite> Rewrites { get; init; } = [];
    public List<AddressMention> Mentions { get; init; } = [];
    /// <summary>Addresses in a moved subnet left alone because a WAN or a VPN's far side owns them.</summary>
    public List<AddressMention> Remote { get; init; } = [];
    public List<StaticClient> StaticClients { get; init; } = [];
    /// <summary>Known clients last seen in a moved subnet but offline now: no lease proves nothing for them.</summary>
    public List<StaticClient> OfflineClients { get; init; } = [];
    /// <summary>Reserved, connected, no lease, and idle: taken as DHCP whose lease lapsed while asleep.</summary>
    public List<StaticClient> IdleClients { get; init; } = [];
    /// <summary>Where the static clients came from: UniFi's IP Table, or DHCP ranges when it was not readable.</summary>
    public string StaticSource { get; init; } = "";
    public List<SettingRewrite> Settings { get; init; } = [];
    /// <summary>DHCP leases lowered ahead of the move, when they are.</summary>
    public LeasePrepare? Prepare { get; init; }
    /// <summary>The longest DHCP lease among the moving networks, in seconds; null when none moves with DHCP on.</summary>
    public int? LongestLeaseSeconds { get; init; }
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
            return new ReaddressView { Moves = moves, Errors = errors, Prepare = prepare, LastAppliedAt = lastApplied, LongestLeaseSeconds = LongestLease(live, active) };

        var result = Readdressing.Apply(live, active);
        var (statics, source) = await StaticClientsAsync(live, active, cancellationToken);
        return new ReaddressView
        {
            Moves = moves,
            Errors = errors,
            Warnings = await ReaddressWarningsAsync(live, active, prepare, cancellationToken),
            Rewrites = result.Rewrites,
            Mentions = result.Mentions,
            Remote = result.Remote,
            StaticClients = statics.Where(s => s.LastSeen == null && !s.Idle).ToList(),
            IdleClients = statics.Where(s => s.LastSeen == null && s.Idle).ToList(),
            OfflineClients = statics.Where(s => s.LastSeen != null).ToList(),
            StaticSource = source,
            Settings = await _registry.ReaddressSettingsAsync(_site.Slug, active, RemoteCidrs(live), apply: false, cancellationToken),
            Prepare = prepare,
            LastAppliedAt = lastApplied,
            LongestLeaseSeconds = LongestLease(live, active),
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
        var warnings = await ReaddressWarningsAsync(live, active, prepare, cancellationToken);

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
                foreach (var w in warnings) doc.Notes.Add($"{w.Title}: {w.Detail}");
                if (statics.Where(s => s.LastSeen == null && !s.Idle).ToList() is { Count: > 0 } set)
                    doc.Notes.Add($"No DHCP lease, so this plan cannot move them unless they ask DHCP again: {string.Join(", ", set.Select(s => $"{s.Name} ({s.Ip} to {s.NewIp}{(s.Reserved ? ", reserved" : "")})"))}.");
                if (statics.Count(s => s.LastSeen != null) is > 0 and var offline)
                    doc.Notes.Add($"{offline} offline clients were likely set on the device (no reservation, last address outside the DHCP range): change them when they are back. The dry run lists them.");
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

    /// <summary>The longest DHCP lease among the moving networks with DHCP on (UniFi's default is a day); null when none.</summary>
    private static int? LongestLease(ConfigState live, List<SubnetMove> active) =>
        live.Get("networkconf").Where(n => active.Any(m => m.NetworkId == ConfigState.IdOf(n))
                && n["dhcpd_enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on)
            .Select(n => int.TryParse(n["dhcpd_leasetime"]?.ToString(), out var s) && s > 0 ? s : 86400)
            .Select(s => (int?)s).DefaultIfEmpty(null).Max();

    private static List<string> RemoteCidrs(ConfigState live) =>
        Readdressing.RemoteRanges(live).Select(r => r.Range.ToString()).Distinct().ToList();

    /// <summary>What to know before applying: one short title and one line of detail each, the most disruptive first.</summary>
    private async Task<List<ReaddressWarning>> ReaddressWarningsAsync(ConfigState live, List<SubnetMove> active, LeasePrepare? prepare, CancellationToken ct)
    {
        var warnings = new List<ReaddressWarning>();
        var map = Readdressing.Map(active);

        // Network Optimizer's own host (or this site's On-Site Agent): it cannot watch or revert from an address that is gone.
        var own = Plans.ManagementAddresses().Where(a => Readdressing.ParseIp(a) is { } ip && Readdressing.MapIp(ip, map) != null).Distinct().ToList();
        if (own.Count > 0)
            warnings.Add(new("Network Optimizer's host moves",
                $"{own[0]}{(own.Count > 1 ? $" and {own.Count - 1} more" : "")}. Re-address it in the same window; no watch or revert runs until it is back."));

        var mgmt = ConfigDiffEngine.ManagementNetworkIds(live);
        var managed = active.Where(m => mgmt.Contains(m.NetworkId)).Select(m => m.NetworkName).ToList();
        if (managed.Count > 0)
            warnings.Add(new(managed.Count == 1 ? "A management network moves" : "Management networks move",
                $"{string.Join(", ", managed)}. Devices and the UniFi Console are reached at new addresses; Review Plan holds {(managed.Count == 1 ? "it" : "them")} at the Lockout tier."));

        // Devices find the console by its inform address; a console inside a moved subnet must be found at its new one.
        if (Uri.TryCreate(_registry.Connection(_site.Slug).CurrentConfig?.ControllerUrl, UriKind.Absolute, out var uri))
        {
            var byIp = Readdressing.ParseIp(uri.Host) is { } ip ? ip : (uint?)null;
            var addresses = byIp is { } direct ? [direct]
                : (await SafeResolveAsync(uri.Host, ct)).Select(a => Readdressing.ParseIp(a.ToString())).OfType<uint>().ToList();
            if (addresses.FirstOrDefault(a => Readdressing.MapIp(a, map) != null) is var a && a != 0
                && (byIp != null || !MovedByLocalDns(live, uri.Host, map)))
            {
                var moved = Readdressing.Format(Readdressing.MapIp(a, map)!.Value);
                warnings.Add(new("The console moves", byIp != null
                    ? $"{uri.Host} to {moved}. Network Optimizer reconnects at the new address."
                    : $"{uri.Host} resolves to {Readdressing.Format(a)}: point its DNS record at {moved}."));
            }
        }

        if (await InformWarningAsync(live, map, ct) is { } inform) warnings.Add(inform);

        var lease = live.Get("networkconf").Where(n => active.Any(m => m.NetworkId == ConfigState.IdOf(n)))
            .Select(n => int.TryParse(n["dhcpd_leasetime"]?.ToString(), out var s) ? s : 86400).DefaultIfEmpty(86400).Max();
        warnings.Add(prepare switch
        {
            null when lease <= PrepareLeaseSeconds => new("Leases are short",
                $"{CcHours(TimeSpan.FromSeconds(lease))} or less on every moving network: clients move within minutes."),
            null => new("Clients move as leases renew",
                $"Within {CcHours(TimeSpan.FromSeconds(lease / 2))} of the move. Lower Lease Times first, or Reconnect Clients after, makes it minutes."),
            { } p when p.ReadyAt > DateTime.UtcNow => new("Leases lowered, not everywhere yet",
                $"Some clients keep their old lease until {Local(p.ReadyAt):MMM d, h:mm tt}. The plan puts each lease time back."),
            _ => new("Leases are short", "Clients move within minutes. The plan puts each network's lease time back."),
        });
        return warnings;
    }

    /// <summary>
    /// Devices whose inform address is in a moved subnet, read from each device's own inform_url, so it holds
    /// whatever set it (the Inform Host override, a self-hosted server's system_ip, or adoption). A name counts
    /// when it resolves into a moved subnet and no Local DNS record in the config moves it.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "stat/device inform_url")]
    private async Task<ReaddressWarning?> InformWarningAsync(ConfigState live, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> map, CancellationToken ct)
    {
        var devices = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/device", null, ct);
        if (!devices.IsSuccess || ConfigSnapshotReader.ExtractList(devices.Body) is not { } list) return null;

        var resolved = new Dictionary<string, uint?>(StringComparer.OrdinalIgnoreCase);
        var affected = new List<(string Device, string Host, uint Old)>();
        foreach (var d in list.OfType<JsonObject>())
        {
            if (!Uri.TryCreate(d["inform_url"]?.ToString(), UriKind.Absolute, out var inform)) continue;
            var host = inform.Host;
            if (!resolved.TryGetValue(host, out var ip))
            {
                ip = Readdressing.ParseIp(host)
                    ?? (MovedByLocalDns(live, host, map) ? null
                        : (await SafeResolveAsync(host, ct)).Select(a => Readdressing.ParseIp(a.ToString())).OfType<uint>().Cast<uint?>().FirstOrDefault());
                resolved[host] = ip;
            }
            if (ip is { } old && Readdressing.MapIp(old, map) != null)
                affected.Add((d["name"]?.ToString() is { Length: > 0 } n ? n : d["mac"]?.ToString() ?? "", host, old));
        }
        if (affected.Count == 0) return null;

        var first = affected[0];
        var moved = Readdressing.Format(Readdressing.MapIp(first.Old, map)!.Value);
        var hosts = string.Join(", ", affected.Select(x => x.Host).Distinct());
        return new(affected.Count == 1 ? "A device needs a new inform address" : $"{affected.Count} devices need a new inform address",
            $"{string.Join(", ", affected.Take(3).Select(x => x.Device))}{(affected.Count > 3 ? $" and {affected.Count - 3} more" : "")} inform to {hosts}. "
            + $"Point the Inform Host at {moved} first (UniFi Network's Inform Host override, or system_ip on a self-hosted UniFi OS Server), or they disconnect until re-pointed.");
    }

    /// <summary>
    /// A Local DNS record for the name, followed through CNAMEs, points into a moved subnet, so the plan
    /// rewrites it with the rest. A client's own DNS record (a short name UniFi extends with the domain)
    /// counts through its fixed IP.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "static_dns key/value; rest/user local_dns_record with fixed_ip")]
    internal static bool MovedByLocalDns(ConfigState live, string host, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> map)
    {
        var records = live.Get("static_dns").Select(r => (Name: r["key"]?.ToString(), Value: r["value"]?.ToString()))
            .Concat(live.Get("user").Where(u => u["local_dns_record_enabled"]?.ToString() == "true" && u["use_fixedip"]?.ToString() == "true")
                .Select(u => (Name: u["local_dns_record"]?.ToString(), Value: u["fixed_ip"]?.ToString())))
            .Where(r => !string.IsNullOrEmpty(r.Name)).ToList();
        static bool Names(string record, string name) =>
            string.Equals(record, name, StringComparison.OrdinalIgnoreCase)
            || (!record.Contains('.') && name.StartsWith(record + ".", StringComparison.OrdinalIgnoreCase));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var name = host.TrimEnd('.'); names.Add(name);)
        {
            var targets = records.Where(r => Names(r.Name!.TrimEnd('.'), name)).Select(r => r.Value ?? "").ToList();
            if (targets.Any(v => Readdressing.ParseIp(v) is { } ip && Readdressing.MapIp(ip, map) != null)) return true;
            if (targets.FirstOrDefault(v => v.Length > 0 && Readdressing.ParseIp(v) == null) is not { } cname) return false;
            name = cname.TrimEnd('.');
        }
        return false;
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
            // A reserved client with no lease that is idle most likely let its lease lapse while asleep (a TV in
            // standby); one passing traffic would have renewed, so its address is set on the device (a NAS).
            var idle = reserved && gateway != null && Rate(c) < ActiveBytesPerSecond;
            result.Add(new StaticClient(name, mac, ipText, Readdressing.Format(Readdressing.MapIp(ip, map)!.Value), network?["name"]?.ToString() ?? "", reserved)
            { Idle = idle });
        }
        result.AddRange(await OfflineClientsAsync(list, networks, map, ct));
        var source = gateway != null ? "from the gateway's DHCP leases"
            : ipTable != null ? "from UniFi's IP Table, reserved clients not checked"
            : "from DHCP ranges";
        return (result.OrderBy(r => Readdressing.ParseIp(r.Ip)).ToList(), source);
    }

    /// <summary>
    /// Known clients not connected now that were likely set on the device: last seen in a moved subnet,
    /// with no reservation, on an address outside their network's DHCP range.
    /// </summary>
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
            // A reservation means DHCP: it follows. So does an address DHCP's own range handed out.
            // Left: no reservation and an address outside the range, the one sign of an address set on the device.
            if (u["use_fixedip"] is JsonValue f && f.TryGetValue<bool>(out var r) && r) continue;
            var ipText = u["last_ip"]?.ToString();
            if (Readdressing.ParseIp(ipText) is not { } ip || Readdressing.MapIp(ip, map) is not { } moved) continue;
            var lastNetwork = u["last_connection_network_id"]?.ToString() is { } lid && networks.TryGetValue(lid, out var ln) ? ln : null;
            if (lastNetwork == null || InDhcpRange(lastNetwork, ip)) continue;
            var reserved = false;
            DateTime? seen = long.TryParse(u["last_seen"]?.ToString(), out var s) && s > 0 ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : DateTime.MinValue;
            var network = u["last_connection_network_id"]?.ToString() is { } nid && networks.TryGetValue(nid, out var n) ? n["name"]?.ToString() ?? "" : "";
            var name = u["name"]?.ToString() is { Length: > 0 } nm ? nm : u["hostname"]?.ToString() is { Length: > 0 } hn ? hn : mac;
            result.Add(new StaticClient(name, mac, ipText!, Readdressing.Format(moved), network, reserved, seen));
        }
        return result.OrderByDescending(c => c.LastSeen).ToList();
    }

    /// <summary>Traffic under this (about 8 kbit/s) counts as idle: a sleeping TV's multicast chatter, not a working host.</summary>
    private const double ActiveBytesPerSecond = 1000;

    /// <summary>A connected client's current traffic, both directions, bytes per second (stat/sta rates).</summary>
    private static double Rate(JsonObject c)
    {
        static double Get(JsonObject o, string k) => double.TryParse(o[k]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        return Math.Max(Get(c, "tx_bytes-r") + Get(c, "rx_bytes-r"), Get(c, "wired-tx_bytes-r") + Get(c, "wired-rx_bytes-r"));
    }

    private static bool InDhcpRange(JsonObject network, uint ip) =>
        network["dhcpd_enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on
        && Readdressing.ParseIp(network["dhcpd_start"]?.ToString()) is { } start && Readdressing.ParseIp(network["dhcpd_stop"]?.ToString()) is { } stop
        && ip >= start && ip <= stop;
}
