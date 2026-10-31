using System.Text.Json.Nodes;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>The user's choices on the Set Up Zones board.</summary>
public sealed class ZoneChoices
{
    public string Mode { get; init; } = ZoneSetup.TrustMode;
    public Dictionary<string, string> Moves { get; init; } = [];
    public List<string> AddedZones { get; init; } = [];
    public Dictionary<string, bool> Exceptions { get; init; } = [];
}

public sealed partial class ChangeControlService
{
    private async Task<(ZoneSetupView Board, ConfigState Live, List<NetworkOptimizer.Audit.Models.NetworkInfo> Networks)> ZoneBoardAsync(ZoneChoices choices, CancellationToken ct)
    {
        var live = await Engine.ReadLiveAsync(ct);
        var networks = Engine.Evaluator == null ? null : await Engine.Evaluator.GetNetworksAsync(_site.Slug, live, ct);
        if (networks == null) throw new InvalidOperationException("The Security Audit could not read this site's networks. Check the UniFi Console connection.");
        return (ZoneSetup.Build(live, networks, choices.Mode, choices.Moves, choices.AddedZones, choices.Exceptions), live, networks);
    }

    public async Task<ZoneSetupView> GetZoneSetupAsync(ZoneChoices choices, bool runChecks, CancellationToken cancellationToken = default)
    {
        var (board, live, networks) = await ZoneBoardAsync(choices, cancellationToken);
        if (!board.LegacyFirewall
            && await Engine.Repositories.UseAsync((r, c) => r.GetLatestSnapshotWithAsync("firewallrule", c), cancellationToken) is { } legacy)
        {
            board.LegacySnapshotId = legacy.Id;
            board.LegacySnapshotLabel = Local(legacy.TakenAt).ToString("MMM d, yyyy h:mm tt");
        }
        if (runChecks && !board.LegacyFirewall && Engine.Evaluator != null)
        {
            var impact = await Engine.Evaluator.RunImpactAsync(_site.Slug, live, ZoneSetup.Desired(live, board, await ClientNetworksAsync(cancellationToken)), cancellationToken);
            (board.Checks, var pass) = ZoneSetup.Checks(board, networks, impact);
            board.ChecksPass = pass;
        }
        return board;
    }

    public async Task<int> BuildZonePlanAsync(ZoneChoices choices, CancellationToken cancellationToken = default)
    {
        var (board, live, networks) = await ZoneBoardAsync(choices, cancellationToken);
        if (board.LegacyFirewall)
            throw new InvalidOperationException("This site still runs the legacy firewall. Zones need the zone-based firewall first.");
        var zonePlan = ZoneSetup.Plan(live, board, await Plans.InformAddressesAsync(cancellationToken), await ClientNetworksAsync(cancellationToken));
        var desired = zonePlan.Desired;
        var impact = Engine.Evaluator == null ? null : await Engine.Evaluator.RunImpactAsync(_site.Slug, live, desired, cancellationToken);
        var (checks, pass) = ZoneSetup.Checks(board, networks, impact);
        var plan = await Plans.CreateAsync(ConfigPlanKind.Zones, $"Set up zones on {await SiteNameAsync(cancellationToken)}",
            choices.Mode == ZoneSetup.PurposeMode ? "Zones by purpose" : "Zones by trust level", null, null, desired, ZoneSetup.Options(), Actor, cancellationToken,
            doc =>
            {
                AfterZoneWrites(doc, zonePlan.Rehomed);
                foreach (var e in board.Exceptions.Where(e => e.Enabled)) doc.Notes.Add($"Exception: {e.Name} ({e.FromTo}). {e.Why}.");
                doc.Notes.AddRange(zonePlan.Notes);
                foreach (var c in checks.Where(c => c.Level != "ok")) doc.Notes.Add($"Security Audit check: {c.Label}: {c.Status}.");
                doc.Context["zone_checks"] = pass ? "pass" : "fail";
            });
        return plan.Id;
    }

    /// <summary>
    /// Each client's last network, which the config copy of clients leaves out (it is telemetry): a client policy's source
    /// follows its clients' network when it moves. Empty when the read fails; the plan then notes those policies instead.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "rest/user last_connection_network_id")]
    private async Task<Dictionary<string, string>> ClientNetworksAsync(CancellationToken ct)
    {
        var users = await Engine.Api.SendAsync(NetworkOptimizer.UniFi.UniFiApiScope.SiteV1, HttpMethod.Get, "rest/user", null, ct);
        if (!users.IsSuccess || ConfigSnapshotReader.ExtractList(users.Body) is not { } list) return [];
        return list.Where(u => u["mac"]?.ToString() is { Length: > 0 } && u["last_connection_network_id"]?.ToString() is { Length: > 0 })
            .GroupBy(u => u["mac"]!.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()["last_connection_network_id"]!.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every policy write follows every zone write. A zone write pauses each policy naming a network it moves and
    /// strips that network from it (UDR7 on Network 11, 2026-10-30), so a re-homed policy written first would be undone.
    /// A re-homed update also verifies its enabled flag and both sides, which the zone write is what changes.
    /// </summary>
    internal static void AfterZoneWrites(PlanDocument doc, IReadOnlySet<string> rehomed)
    {
        var zoneKeys = doc.Operations.Where(o => o.Collection == "firewall_zone" && o.Action != PlanAction.Delete).Select(o => o.Key).ToList();
        foreach (var op in doc.Operations.Where(o => o.Collection == "firewall_policy" && o.Action is PlanAction.Create or PlanAction.Update))
        {
            op.After.AddRange(zoneKeys.Except(op.After));
            if (op.Action == PlanAction.Update && op.ObjectId != null && rehomed.Contains(op.ObjectId))
                foreach (var field in new[] { "enabled", "source", "destination" })
                    if (!op.WriteFields.Contains(field) && op.Body is JsonObject body && body.ContainsKey(field)) op.WriteFields.Add(field);
        }
        PlanBuilder.Reorder(doc);
    }

    public async Task<int> BuildLegacyTranslationAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        var state = await Engine.LoadSnapshotStateAsync(snapshotId, cancellationToken)
            ?? throw new InvalidOperationException("That snapshot is not available.");
        var snap = await Engine.Repositories.UseAsync((r, c) => r.GetSnapshotAsync(snapshotId, c), cancellationToken);
        var live = await Engine.ReadLiveAsync(cancellationToken);
        if (!LegacyFirewallTranslator.Applies(state, live))
            throw new InvalidOperationException("Translation needs a snapshot with legacy rules and a site on the zone-based firewall.");

        // Firewall only, nothing deleted: policies UniFi's own move created stay, and same-named ones are updated.
        var translation = LegacyFirewallTranslator.Translate(state, live, deleteMissing: false);
        var label = Local(snap!.TakenAt).ToString("MMM d, h:mm tt");
        var plan = await Plans.CreateAsync(ConfigPlanKind.Zones, $"Translate legacy firewall rules ({label}) on {await SiteNameAsync(cancellationToken)}",
            $"Snapshot, {label}", snapshotId, null, translation.Desired,
            new PlanOptions { Collections = new HashSet<string> { "firewall_zone", "firewall_policy", "firewallgroup" }, DeleteMissing = false },
            Actor, cancellationToken, doc => AddTranslation(doc, translation), impactBefore: state);
        return plan.Id;
    }
}
