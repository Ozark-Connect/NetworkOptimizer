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
        if (runChecks && !board.LegacyFirewall && Engine.Evaluator != null)
        {
            var impact = await Engine.Evaluator.RunImpactAsync(_site.Slug, live, ZoneSetup.Desired(live, board), cancellationToken);
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
        var desired = ZoneSetup.Desired(live, board);
        var impact = Engine.Evaluator == null ? null : await Engine.Evaluator.RunImpactAsync(_site.Slug, live, desired, cancellationToken);
        var (checks, pass) = ZoneSetup.Checks(board, networks, impact);
        var plan = await Plans.CreateAsync(ConfigPlanKind.Zones, $"Set up zones on {await SiteNameAsync(cancellationToken)}",
            choices.Mode == ZoneSetup.PurposeMode ? "Zones by purpose" : "Zones by trust level", null, null, desired, ZoneSetup.Options(), Actor, cancellationToken,
            doc =>
            {
                foreach (var e in board.Exceptions.Where(e => e.Enabled)) doc.Notes.Add($"Exception: {e.Name} ({e.FromTo}). {e.Why}.");
                foreach (var c in checks.Where(c => c.Level != "ok")) doc.Notes.Add($"Security Audit check: {c.Label}: {c.Status}.");
                doc.Context["zone_checks"] = pass ? "pass" : "fail";
            });
        return plan.Id;
    }
}
