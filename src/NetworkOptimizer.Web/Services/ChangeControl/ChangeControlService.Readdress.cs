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

/// <summary>A client whose address is set on the device itself: the plan cannot move it.</summary>
public sealed record StaticClient(string Name, string Mac, string Ip, string NewIp, string Network);

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
    /// <summary>Where the static clients came from: UniFi's IP Table, or DHCP ranges when it was not readable.</summary>
    public string StaticSource { get; init; } = "";
    public List<SettingRewrite> Settings { get; init; } = [];
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
        if (active.Count == 0 || errors.Count > 0)
            return new ReaddressView { Moves = moves, Errors = errors };

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
            StaticClients = statics,
            StaticSource = source,
            Settings = await _registry.ReaddressSettingsAsync(_site.Slug, active, RemoteCidrs(live), apply: false, cancellationToken),
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
        var warnings = await ReaddressWarningsAsync(live, active, result, cancellationToken);
        var (statics, _) = await StaticClientsAsync(live, active, cancellationToken);
        var remote = RemoteCidrs(live);
        var settings = await _registry.ReaddressSettingsAsync(_site.Slug, active, remote, apply: false, cancellationToken);
        var summary = string.Join(", ", active.Select(m => $"{m.NetworkName} {m.OldCidr} to {m.NewCidr}"));
        var plan = await Plans.CreateAsync(ConfigPlanKind.Readdress, $"Re-address {await SiteNameAsync(cancellationToken)}", summary, null, null,
            result.Desired, new PlanOptions { Collections = result.Collections, DeleteMissing = false }, Actor, cancellationToken,
            doc =>
            {
                doc.Context[PlanCoordinator.ReaddressContext] = JsonSerializer.Serialize(active);
                doc.Context[PlanCoordinator.ReaddressRemoteContext] = JsonSerializer.Serialize(remote);
                foreach (var m in result.Remote)
                    doc.Notes.Add($"Left alone, owned by a WAN or a VPN's far side: {ConfigCollections.Get(m.Collection)?.Noun ?? m.Collection} {m.ObjectName}, {m.Path}: {m.Value}");
                foreach (var w in warnings) doc.Notes.Add(w);
                if (statics.Count > 0)
                    doc.Notes.Add($"Set on the device itself, so this plan cannot move them: {string.Join(", ", statics.Select(s => $"{s.Name} ({s.Ip})"))}.");
                foreach (var m in result.Mentions)
                    doc.Notes.Add($"Not rewritten, holds an old address: {ConfigCollections.Get(m.Collection)?.Noun ?? m.Collection} {m.ObjectName}, {m.Path}: {m.Value}");
                if (settings.Count > 0)
                    doc.Notes.Add($"Network Optimizer moves its own settings with the site once this applies (and back on a revert): "
                        + string.Join(", ", settings.Select(s => $"{s.Setting} {s.Name} {s.Old} to {s.New}")) + ".");
            });
        return plan.Id;
    }

    private static List<string> RemoteCidrs(ConfigState live) =>
        Readdressing.RemoteRanges(live).Select(r => r.Range.ToString()).Distinct().ToList();

    private async Task<List<string>> ReaddressWarningsAsync(ConfigState live, List<SubnetMove> active, ReaddressResult result, CancellationToken ct)
    {
        var warnings = new List<string>();
        var map = Readdressing.Map(active);
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
    /// Clients in a moved subnet whose address is set on the device. UniFi's IP Table (active-leases)
    /// marks them static; when it cannot be read, a client outside its network's DHCP range with no
    /// reservation counts.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "stat/sta clients and v2 active-leases (the IP Table)")]
    private async Task<(List<StaticClient> Clients, string Source)> StaticClientsAsync(ConfigState live, List<SubnetMove> active, CancellationToken ct)
    {
        var map = Readdressing.Map(active);
        var clients = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/sta", null, ct);
        if (!clients.IsSuccess || ConfigSnapshotReader.ExtractList(clients.Body) is not { } list) return ([], "");

        var leases = await Engine.Api.SendAsync(UniFiApiScope.SiteV2, HttpMethod.Get, "active-leases", null, ct);
        var leaseByMac = leases.IsSuccess && ConfigSnapshotReader.ExtractList(leases.Body) is { Count: > 0 } rows
            ? rows.Where(r => r["mac"] != null).GroupBy(r => r["mac"]!.ToString().ToLowerInvariant())
                .ToDictionary(g => g.Key, g => (g.First()["type"] ?? g.First()["lease_type"] ?? g.First()["leaseType"])?.ToString()?.ToLowerInvariant() ?? "dynamic")
            : null;

        var networks = live.Get("networkconf").ToDictionary(ConfigState.IdOf, n => n, StringComparer.OrdinalIgnoreCase);
        var result = new List<StaticClient>();
        foreach (var c in list)
        {
            if (c["ip"]?.ToString() is not { } ipText || Readdressing.ParseIp(ipText) is not { } ip || Readdressing.MapIp(ip, map) == null) continue;
            if (c["use_fixedip"] is JsonValue f && f.TryGetValue<bool>(out var fixedIp) && fixedIp) continue;
            var mac = c["mac"]?.ToString()?.ToLowerInvariant() ?? "";
            var network = c["network_id"]?.ToString() is { } nid && networks.TryGetValue(nid, out var n) ? n : null;
            bool isStatic;
            if (leaseByMac != null)
                isStatic = !leaseByMac.TryGetValue(mac, out var type) || type.Contains("static", StringComparison.Ordinal);
            else
                isStatic = network != null && !InDhcpRange(network, ip);
            if (!isStatic) continue;
            var name = c["name"]?.ToString() is { Length: > 0 } nm ? nm : c["hostname"]?.ToString() is { Length: > 0 } hn ? hn : mac;
            result.Add(new StaticClient(name, mac, ipText, Readdressing.Format(Readdressing.MapIp(ip, map)!.Value), network?["name"]?.ToString() ?? ""));
        }
        return (result.OrderBy(r => Readdressing.ParseIp(r.Ip)).ToList(), leaseByMac != null ? "UniFi's IP Table" : "DHCP ranges");
    }

    private static bool InDhcpRange(JsonObject network, uint ip) =>
        network["dhcpd_enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on
        && Readdressing.ParseIp(network["dhcpd_start"]?.ToString()) is { } start && Readdressing.ParseIp(network["dhcpd_stop"]?.ToString()) is { } stop
        && ip >= start && ip <= stop;
}
