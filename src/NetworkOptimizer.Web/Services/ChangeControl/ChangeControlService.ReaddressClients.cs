using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// DHCP leases lowered ahead of a re-address: when, the original lease time of each network (seconds),
/// and when every DHCP client is on the short lease (one full original lease later).
/// </summary>
public sealed record LeasePrepare(int PlanId, DateTime LoweredAt, DateTime ReadyAt, Dictionary<string, int> Originals);

/// <summary>Lower Lease Times: the plan it ran, and why it stopped (null when it applied).</summary>
public sealed record LeaseLowerResult(int PlanId, string? Error);

/// <summary>What Reconnect Clients did after a re-address.</summary>
public sealed class ReconnectResult
{
    public List<string> Reconnected { get; } = [];
    public List<string> PowerCycled { get; } = [];
    /// <summary>Wired clients with no PoE to cycle: they move at their next DHCP renewal.</summary>
    public List<string> Waiting { get; } = [];
    public List<string> Failed { get; } = [];
    /// <summary>Connected clients already on a new subnet.</summary>
    public int Moved { get; set; }
}

public sealed partial class ChangeControlService
{
    /// <summary>The short lease a re-address prepares with: clients renew every few minutes, a trivial load.</summary>
    public const int PrepareLeaseSeconds = 600;

    private const string PrepareContext = "readdress_prepare";

    /// <summary>The latest lease preparation that applied in the last two weeks, if any.</summary>
    private async Task<LeasePrepare?> LeasePrepareAsync(CancellationToken ct)
    {
        var plans = await Repo.UseAsync((r, c) => r.GetPlansAsync([ConfigPlanStatus.Applied, ConfigPlanStatus.Watching], 200, c), ct);
        var latest = plans.Where(p => p.Kind == ConfigPlanKind.ReaddressPrepare && p.CompletedAt > DateTime.UtcNow.AddDays(-14))
            .OrderByDescending(p => p.CompletedAt).FirstOrDefault();
        if (latest == null || await Plans.LoadAsync(latest.Id, ct) is not { } loaded
            || !loaded.Doc.Context.TryGetValue(PrepareContext, out var json)
            || JsonSerializer.Deserialize<Dictionary<string, int>>(json) is not { Count: > 0 } originals) return null;
        // A re-address applied after it has already put the lease times back.
        if (plans.Any(p => p.Kind == ConfigPlanKind.Readdress && p.CompletedAt > latest.CompletedAt)) return null;
        var at = latest.CompletedAt!.Value;
        return new LeasePrepare(latest.Id, at, at.AddSeconds(originals.Values.Max()), originals);
    }

    private async Task<int> BuildReaddressPreparePlanAsync(ReaddressChoices choices, CancellationToken cancellationToken = default)
    {
        var live = await Engine.ReadLiveAsync(cancellationToken);
        var moving = Moves(live, choices).Where(m => m.OldCidr != m.NewCidr).Select(m => m.NetworkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (moving.Count == 0) throw new InvalidOperationException("No network moves: give at least one a new subnet first.");

        var desired = live.Clone();
        var originals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in desired.Get("networkconf").Where(n => moving.Contains(ConfigState.IdOf(n))))
        {
            if (!(n["dhcpd_enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on)) continue;
            var lease = int.TryParse(n["dhcpd_leasetime"]?.ToString(), out var s) && s > 0 ? s : 86400;
            if (lease <= PrepareLeaseSeconds) continue;
            originals[ConfigState.IdOf(n)] = lease;
            n["dhcpd_leasetime"] = PrepareLeaseSeconds;
        }
        if (originals.Count == 0) throw new InvalidOperationException("Every moving network already has a short lease, or no DHCP: nothing to prepare.");

        var longest = TimeSpan.FromSeconds(originals.Values.Max());
        var plan = await Plans.CreateAsync(ConfigPlanKind.ReaddressPrepare, $"Prepare re-address of {await SiteNameAsync(cancellationToken)}",
            $"DHCP leases to {PrepareLeaseSeconds / 60} minutes", null, null, desired,
            new PlanOptions { Collections = new HashSet<string> { "networkconf" }, DeleteMissing = false }, Actor, cancellationToken,
            doc =>
            {
                doc.Context[PrepareContext] = JsonSerializer.Serialize(originals);
                doc.Notes.Add($"Every DHCP client is on the short lease {CcHours(longest)} after this applies, the longest current lease. "
                    + "Apply the re-address after that, and it puts each network's lease time back.");
            });
        return plan.Id;
    }

    /// <summary>
    /// Lowers the moving networks' DHCP leases now: a lease time is harmless and the re-address puts it
    /// back, so it skips Review Plan. It still runs as a plan, for the pinned snapshot, attribution, and
    /// audit. Anything that would need a person (a lockout, an approval) stops it with the plan to review.
    /// </summary>
    public async Task<LeaseLowerResult> LowerLeaseTimesAsync(ReaddressChoices choices, CancellationToken cancellationToken = default)
    {
        var planId = await BuildReaddressPreparePlanAsync(choices, cancellationToken);
        if (await Plans.LoadAsync(planId, cancellationToken) is not { } loaded) return new(planId, "The plan could not be loaded.");
        if (loaded.Doc.HighestTier >= PlanTiers.Lockout || loaded.Doc.Errors.Count > 0)
            return new(planId, "This change needs review: open the plan.");
        await Plans.SaveAcknowledgementsAsync(planId, new PlanAcknowledgements
        {
            Items = loaded.Doc.Operations.Select(o => o.Key).ToHashSet(),
            TypedConfirmation = PlanReadiness.ConfirmationText(loaded.Doc),
        }, cancellationToken);
        return new(planId, await Plans.ApplyAsync(planId, new PlanActor(Actor, IsSiteAdmin: true), requireApproval: false, automatic: false, cancellationToken));
    }

    private static string CcHours(TimeSpan t) => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hours" : $"{t.TotalMinutes:0} minutes";

    /// <summary>Puts back the lease times a preparation lowered, in the re-address plan itself.</summary>
    private static void RestoreLeases(ReaddressResult result, LeasePrepare? prepare)
    {
        if (prepare == null) return;
        foreach (var n in result.Desired.Get("networkconf"))
            if (prepare.Originals.TryGetValue(ConfigState.IdOf(n), out var lease))
            {
                n["dhcpd_leasetime"] = lease;
                result.Collections.Add("networkconf");
            }
    }

    /// <summary>
    /// After a re-address: reconnects the connected clients still on an old address so they ask DHCP again.
    /// Wireless clients rejoin (UniFi's Reconnect); wired clients powered over PoE have their port
    /// power-cycled when <paramref name="powerCycle"/>; other wired clients move at their next renewal.
    /// Clients set on the device, and Network Optimizer's own host, are left alone.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "cmd/stamgr kick-sta, cmd/devmgr power-cycle, stat/sta sw_mac/sw_port, stat/device port_table poe")]
    public async Task<ReconnectResult> ReconnectClientsAsync(bool powerCycle, CancellationToken cancellationToken = default)
    {
        var result = new ReconnectResult();
        var plans = await Repo.UseAsync((r, c) => r.GetPlansAsync([ConfigPlanStatus.Applied, ConfigPlanStatus.Watching], 200, c), cancellationToken);
        var last = plans.Where(p => p.Kind == ConfigPlanKind.Readdress && p.CompletedAt > DateTime.UtcNow.AddDays(-7)).OrderByDescending(p => p.CompletedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("No re-address applied in the last week.");
        if (await Plans.LoadAsync(last.Id, cancellationToken) is not { } loaded
            || !loaded.Doc.Context.TryGetValue(PlanCoordinator.ReaddressContext, out var json)
            || JsonSerializer.Deserialize<List<SubnetMove>>(json) is not { Count: > 0 } moves)
            throw new InvalidOperationException("That re-address plan has no moves recorded.");
        var map = Readdressing.Map(moves);
        var moved = moves.Select(m => Readdressing.ParseSubnet(m.NewCidr)).OfType<Readdressing.Subnet>().ToList();

        var clients = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/sta", null, cancellationToken);
        if (!clients.IsSuccess || ConfigSnapshotReader.ExtractList(clients.Body) is not { } list)
            throw new InvalidOperationException("Could not read the connected clients from the UniFi Console.");
        var leased = await _registry.GatewayLeaseMacsAsync(_site.Slug, cancellationToken);
        var own = Plans.ManagementAddresses().ToHashSet(StringComparer.Ordinal);
        var poePorts = powerCycle ? await PoePortsAsync(cancellationToken) : [];

        foreach (var c in list)
        {
            if (c["ip"]?.ToString() is not { } ip || Readdressing.ParseIp(ip) is not { } addr) continue;
            if (Readdressing.MapIp(addr, map) == null)
            {
                if (moved.Any(s => s.Contains(addr))) result.Moved++;
                continue;
            }
            var mac = c["mac"]?.ToString()?.ToLowerInvariant() ?? "";
            var name = c["name"]?.ToString() is { Length: > 0 } nm ? nm : c["hostname"]?.ToString() is { Length: > 0 } hn ? hn : mac;
            // Network Optimizer's own host: never cut. No lease and no reservation: set on the device, nothing to gain.
            // No lease with a reservation is either static or a lease that lapsed while the client slept (a TV in
            // standby): a wireless rejoin settles it, but a power-cycle is only for clients known to be on DHCP.
            var reserved = c["use_fixedip"] is JsonValue fx && fx.TryGetValue<bool>(out var isReserved) && isReserved;
            var noLease = leased != null && !leased.Contains(mac);
            if (own.Contains(ip) || (noLease && !reserved)) continue;

            var wired = c["is_wired"] is JsonValue w && w.TryGetValue<bool>(out var isWired) && isWired;
            if (wired && noLease) { result.Waiting.Add($"{name} ({ip})"); continue; }
            if (!wired)
            {
                var kick = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Post, "cmd/stamgr",
                    new JsonObject { ["cmd"] = "kick-sta", ["mac"] = mac }.ToJsonString(), cancellationToken);
                (kick.IsSuccess ? result.Reconnected : result.Failed).Add($"{name} ({ip})");
                continue;
            }
            var sw = c["sw_mac"]?.ToString()?.ToLowerInvariant();
            var port = int.TryParse(c["sw_port"]?.ToString(), out var p) ? p : 0;
            if (sw != null && port > 0 && poePorts.Contains((sw, port)))
            {
                var cycle = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Post, "cmd/devmgr",
                    new JsonObject { ["cmd"] = "power-cycle", ["mac"] = sw, ["port_idx"] = port }.ToJsonString(), cancellationToken);
                (cycle.IsSuccess ? result.PowerCycled : result.Failed).Add($"{name} ({ip})");
            }
            else result.Waiting.Add($"{name} ({ip})");
        }
        return result;
    }

    /// <summary>Switch ports supplying PoE power now, by switch MAC and port index.</summary>
    private async Task<HashSet<(string Mac, int Port)>> PoePortsAsync(CancellationToken ct)
    {
        var ports = new HashSet<(string, int)>();
        var devices = await Engine.Api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/device", null, ct);
        if (!devices.IsSuccess || ConfigSnapshotReader.ExtractList(devices.Body) is not { } list) return ports;
        foreach (var d in list)
            if (d["mac"]?.ToString()?.ToLowerInvariant() is { } mac && d["port_table"] is JsonArray table)
                foreach (var port in table.OfType<JsonObject>())
                    if (int.TryParse(port["port_idx"]?.ToString(), out var idx)
                        && double.TryParse(port["poe_power"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var watts) && watts > 0)
                        ports.Add((mac, idx));
        return ports;
    }
}
