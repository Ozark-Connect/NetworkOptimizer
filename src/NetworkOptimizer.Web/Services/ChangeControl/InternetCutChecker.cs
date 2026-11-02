using System.Text.Json.Nodes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// A plan that takes a LAN network off the internet: it reaches the internet on the live config and not on
/// the planned one. Acknowledged rather than watched: the commit-confirmed watch reads the console's own
/// internet health, which a block on the LAN side never fails.
/// </summary>
public static class InternetCutChecker
{
    // Any address outside every LAN subnet is External; this documentation address is in no real block list,
    // so only a policy that blocks the internet as a whole decides it (a DNS or DoH block on 1.1.1.1 does not).
    private const string InternetProbe = "203.0.113.10";
    private const int InternetPort = 443;

    /// <summary>Marks the writes that cut a network off the internet: Acknowledge tier, with the networks they cut.</summary>
    public static void Check(ConfigState live, ConfigState planned, PlanDocument doc)
    {
        if (!doc.Operations.Any(o => o.Collection is "firewall_zone" or "firewall_policy")) return;
        if (!planned.Collections.ContainsKey("firewall_zone") || planned.Get("firewall_zone").Count == 0) return;

        var names = planned.NamesById();
        var plannedZones = ZonePaths.ZoneOfNetwork(planned);
        var lanZones = planned.Get("firewall_zone").Where(z => ZonePaths.IsCustom(z) || Str(z["zone_key"]) is "internal" or "hotspot" or "dmz")
            .Select(ConfigState.IdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var external = planned.Get("firewall_zone").FirstOrDefault(z => Str(z["zone_key"]) == "external") is { } e ? ConfigState.IdOf(e) : null;
        // A console can repeat a policy ID; the first copy stands for it.
        var liveById = live.Get("firewall_policy").GroupBy(ConfigState.IdOf, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var cut = new Dictionary<PlanOperation, List<string>>();
        var scheduledCuts = new HashSet<PlanOperation>();
        foreach (var net in planned.Get("networkconf"))
        {
            var id = ConfigState.IdOf(net);
            if (!plannedZones.TryGetValue(id, out var zone) || !lanZones.Contains(zone)) continue;
            if (ZonePaths.Allows(live, id, InternetProbe, "tcp", InternetPort) != true
                || ZonePaths.Allows(planned, id, InternetProbe, "tcp", InternetPort) != false) continue;
            var scheduled = ZonePaths.Decides(planned, id, InternetProbe, "tcp", InternetPort) is { } policy
                && Str(policy["schedule"]?["mode"]) is { Length: > 0 } mode && mode != "ALWAYS";
            foreach (var op in Causes(doc, id, zone, external, liveById))
            {
                if (!cut.TryGetValue(op, out var networks)) cut[op] = networks = [];
                networks.Add(names.GetValueOrDefault(id, id));
                if (scheduled) scheduledCuts.Add(op);
            }
        }

        foreach (var (op, networks) in cut)
        {
            var reason = $"Blocks {ChangeDescriber.JoinAnd(networks)} from the internet{(scheduledCuts.Contains(op) ? " on its schedule" : "")}. "
                         + "The commit-confirmed watch does not catch this";
            if (!op.Reasons.Contains(reason)) op.Reasons.Add(reason);
            op.Tier = Math.Max(op.Tier, PlanTiers.Acknowledge);
        }
    }

    /// <summary>The writes behind a network's cut: the zone move that takes it, else the policies on its zone's path to External.</summary>
    private static IEnumerable<PlanOperation> Causes(PlanDocument doc, string network, string zone, string? external,
        Dictionary<string, JsonObject> liveById)
    {
        var moves = doc.Operations.Where(o => o.Collection == "firewall_zone"
            && (o.Body?["network_ids"] as JsonArray ?? []).Any(n => string.Equals(Str(n), network, StringComparison.OrdinalIgnoreCase))).ToList();
        if (moves.Count > 0) return moves;
        // A delete, or a write that leaves the sides alone, names no zone pair: it is the live policy's.
        return doc.Operations.Where(o => o.Collection == "firewall_policy"
            && (o.Body is JsonObject { } b && b["source"] != null ? b : o.ObjectId != null ? liveById.GetValueOrDefault(o.ObjectId) : null) is { } p
            && string.Equals(Str(p["source"]?["zone_id"]), zone, StringComparison.OrdinalIgnoreCase)
            && (external == null || string.Equals(Str(p["destination"]?["zone_id"]), external, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();
}
