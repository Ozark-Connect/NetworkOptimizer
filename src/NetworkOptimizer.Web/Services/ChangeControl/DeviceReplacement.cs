using System.Globalization;
using System.Text.Json.Nodes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One old port, or a run of identical idle ports, and where its config goes on the new device.</summary>
public sealed class ReplacementPortRow
{
    public string Key { get; init; } = "";
    public List<int> OldPorts { get; init; } = [];
    public string OldLabel { get; init; } = "";
    public string Config { get; init; } = "";
    public string Connected { get; init; } = "";
    public int InUseCount { get; init; }

    /// <summary>New port indexes; null when unresolved, empty when the config is dropped.</summary>
    public List<int>? Target { get; set; }
    public string TargetLabel { get; set; } = "";
    public string Fit { get; set; } = "";

    /// <summary>ok, warn, error, or muted.</summary>
    public string FitLevel { get; set; } = "ok";
    public bool NeedsAck { get; set; }
    public bool Acknowledged { get; set; }
    public List<(string Value, string Label)> Options { get; init; } = [];
}

/// <summary>A device-level setting and what happens to it on the new model.</summary>
public sealed record ReplacementSettingRow(string Label, string Status, string Level);

/// <summary>One radio band, old config against the new model.</summary>
public sealed record ReplacementRadioRow(string Band, string Old, string New, string Note, string Level, string? AckKey = null, bool Acknowledged = false);

/// <summary>Everything Replace Device shows before Review Plan.</summary>
public sealed class ReplacementProposal
{
    public string Kind { get; init; } = "switch";
    public string OldMac { get; init; } = "";
    public string NewMac { get; init; } = "";
    public string OldName { get; init; } = "";
    public string OldModel { get; init; } = "";
    public string NewModel { get; init; } = "";
    public string? OldVersion { get; init; }
    public string? NewVersion { get; init; }
    public bool SameLayout { get; init; }
    public string OldSummary { get; init; } = "";
    public string NewSummary { get; init; } = "";
    public List<ReplacementPortRow> Ports { get; init; } = [];
    public int UnconfiguredPorts { get; init; }
    public List<ReplacementSettingRow> Settings { get; init; } = [];
    public List<ReplacementRadioRow> Radios { get; init; } = [];
    public List<RecablingRow> Recabling { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public int Unresolved => Ports.Count(p => p.Target == null);
    public int ToAcknowledge => Ports.Count(p => p.NeedsAck && !p.Acknowledged) + Radios.Count(r => r.AckKey != null && !r.Acknowledged);
    public bool Ready => Unresolved == 0 && ToAcknowledge == 0 && Ports.All(p => p.FitLevel != "error");
}

/// <summary>
/// Replace Device (hardware-replacement.md): adapts one device's config to new hardware, same model
/// or not. Works from the capability block the snapshot keeps for each device (<c>_no_meta</c>), so
/// an old device that is already offline or gone still has its ports, radios, and neighbors.
/// </summary>
public static class DeviceReplacement
{
    public const string Drop = "drop";

    private sealed record Port(int Idx, string Media, bool Sfp, int SpeedMbps, bool Poe, double PoeWatts, bool Powering, bool Up, bool Uplink, string? Name, string? Ifname);

    /// <summary>The proposal for moving <paramref name="oldDevice"/>'s config onto <paramref name="newDevice"/>.</summary>
    /// <param name="oldDevice">The old device's config object, with its <c>_no_meta</c>.</param>
    /// <param name="newDevice">The new device as read live, with its <c>_no_meta</c>.</param>
    /// <param name="context">Config the names resolve against (networks, port profiles, devices, WLANs).</param>
    /// <param name="choices">Row key to "drop" or comma-separated new port indexes, from the user.</param>
    /// <param name="acks">Row keys the user acknowledged.</param>
    public static ReplacementProposal Propose(JsonObject oldDevice, JsonObject newDevice, ConfigState context,
        IReadOnlyDictionary<string, string>? choices = null, IReadOnlySet<string>? acks = null)
    {
        choices ??= new Dictionary<string, string>();
        acks ??= new HashSet<string>();
        var oldMeta = oldDevice[ConfigCollections.DeviceMetaField] as JsonObject ?? new JsonObject();
        var newMeta = newDevice[ConfigCollections.DeviceMetaField] as JsonObject ?? new JsonObject();
        var kind = KindOf(oldDevice, oldMeta);
        var oldPorts = Ports(oldMeta);
        var newPorts = Ports(newMeta);
        var names = context.NamesById();
        var deviceNames = context.Get("device").Where(d => d["mac"] != null)
            .GroupBy(d => d["mac"]!.ToString().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => ConfigState.DisplayName(ConfigCollections.Get("device"), g.First()));

        var sameLayout = oldPorts.Count == newPorts.Count && oldPorts.Zip(newPorts).All(p => p.First.Idx == p.Second.Idx && p.First.Media == p.Second.Media);
        var rows = kind == "ap" ? [] : PortRows(oldDevice, oldMeta, oldPorts, names, deviceNames, kind);
        var configured = rows.SelectMany(r => r.OldPorts).ToHashSet();
        var unconfigured = oldPorts.Count(p => !configured.Contains(p.Idx));

        Assign(rows, oldPorts, newPorts, sameLayout, choices, acks);

        var proposal = new ReplacementProposal
        {
            Kind = kind,
            OldMac = Str(oldDevice, "mac")?.ToLowerInvariant() ?? "",
            NewMac = Str(newDevice, "mac")?.ToLowerInvariant() ?? "",
            OldName = ConfigState.DisplayName(ConfigCollections.Get("device"), oldDevice),
            OldModel = Str(oldMeta, "shortname") ?? Str(oldDevice, "model") ?? "",
            NewModel = Str(newMeta, "shortname") ?? Str(newDevice, "model") ?? "",
            OldVersion = Str(oldDevice, "version") ?? Str(oldMeta, "version"),
            NewVersion = Str(newDevice, "version") ?? Str(newMeta, "version"),
            SameLayout = sameLayout,
            OldSummary = Summary(oldPorts, oldMeta),
            NewSummary = Summary(newPorts, newMeta),
            Ports = rows,
            UnconfiguredPorts = kind == "ap" ? 0 : unconfigured,
        };

        if (kind == "ap") Radios(oldDevice, newMeta, context, proposal, acks);
        else Settings(oldDevice, oldMeta, newMeta, rows, names, proposal);
        if (kind == "gateway") WanRoles(oldDevice, newDevice, proposal);
        Recabling(rows, oldPorts, newPorts, proposal);
        return proposal;
    }

    /// <summary>
    /// The old device's config rewritten for the new hardware. It keeps the old MAC: the plan's
    /// device map renames it so it lands on the new device.
    /// </summary>
    public static JsonObject Adapt(ReplacementProposal proposal, JsonObject oldDevice, JsonObject newDevice)
    {
        var adapted = (JsonObject)oldDevice.DeepClone();
        adapted.Remove(ConfigCollections.DeviceMetaField);
        foreach (var k in new[] { "model", "type", "version", "adopted" })
        {
            if (newDevice[k] is { } v) adapted[k] = v.DeepClone();
            else adapted.Remove(k);
        }

        var portMap = PortMap(proposal);
        if (adapted["port_overrides"] is JsonArray overrides)
        {
            var moved = new JsonArray();
            foreach (var o in overrides.OfType<JsonObject>())
            {
                var idx = Int(o, "port_idx");
                if (idx == null || !portMap.TryGetValue(idx.Value, out var to)) continue;
                var copy = (JsonObject)o.DeepClone();
                copy["port_idx"] = to;
                // The LAG lead port lists its members (sample-device-w-lag.txt).
                if (copy["aggregate_members"] is JsonArray members)
                    copy["aggregate_members"] = new JsonArray(members.Select(IntOf).OfType<int>()
                        .Where(portMap.ContainsKey).Select(m => (JsonNode?)portMap[m]).ToArray());
                moved.Add(copy);
            }
            adapted["port_overrides"] = moved;
        }

        if (adapted["link_aggregation_groups"] is JsonArray lags)
        {
            var moved = new JsonArray();
            foreach (var lag in lags.OfType<JsonObject>())
            {
                var copy = (JsonObject)lag.DeepClone();
                if (copy["aggregate_members"] is JsonArray members)
                {
                    var mapped = members.Select(IntOf).OfType<int>().Where(portMap.ContainsKey).Select(m => portMap[m]).ToList();
                    if (mapped.Count < 2) continue;
                    copy["aggregate_members"] = new JsonArray(mapped.Select(m => (JsonNode?)m).ToArray());
                }
                moved.Add(copy);
            }
            adapted["link_aggregation_groups"] = moved;
        }

        if (proposal.Kind == "ap")
            adapted["radio_table"] = AdaptRadios(oldDevice, newDevice);

        if (proposal.Kind == "gateway" && newDevice["ethernet_overrides"] is JsonArray newEth)
            adapted["ethernet_overrides"] = AdaptEthernet(oldDevice, newEth);

        // Settings the new model cannot take are dropped rather than refused by the console.
        var newMeta = newDevice[ConfigCollections.DeviceMetaField] as JsonObject;
        if (newMeta?["switch_caps"] is JsonObject caps && Int(caps, "etherlight_caps") is 0 or null)
            adapted.Remove("ether_lighting");
        return adapted;
    }

    /// <summary>Old port to new port for every row that has a target.</summary>
    public static Dictionary<int, int> PortMap(ReplacementProposal proposal)
    {
        var map = new Dictionary<int, int>();
        foreach (var row in proposal.Ports)
        {
            if (row.Target is not { Count: > 0 } target) continue;
            // A run keeps its in-use ports when it is cut short: in-use ports come first in OldPorts order.
            for (var i = 0; i < Math.Min(row.OldPorts.Count, target.Count); i++)
                map[row.OldPorts[i]] = target[i];
        }
        return map;
    }

    private static string KindOf(JsonObject device, JsonObject meta)
    {
        var type = Str(device, "type") ?? Str(meta, "type") ?? "";
        if (type is "ugw" or "udm" or "uxg") return "gateway";
        if (type == "uap" || meta["radios"] is JsonArray { Count: > 0 } && meta["ports"] is not JsonArray { Count: > 2 }) return "ap";
        return "switch";
    }

    private static List<Port> Ports(JsonObject meta) =>
        (meta["ports"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(p =>
        {
            var media = Str(p, "media") ?? "GE";
            var watts = double.TryParse(Str(p, "poe_power"), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 0;
            var poeEnabled = p["poe_enable"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
            return new Port(Int(p, "port_idx") ?? 0, media, media.Contains("SFP", StringComparison.OrdinalIgnoreCase),
                SpeedOf(media, Int(p, "speed")), p["port_poe"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
                watts, poeEnabled && watts > 0, p["up"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
                p["is_uplink"]?.GetValueKind() == System.Text.Json.JsonValueKind.True, Str(p, "name"), Str(p, "ifname"));
        })
        .Where(p => p.Idx > 0).OrderBy(p => p.Idx).ToList();

    /// <summary>Line rate from the port's media string (stat/device port_table media).</summary>
    internal static int SpeedOf(string media, int? reported)
    {
        var m = media.ToUpperInvariant();
        var bySpec = m switch
        {
            "FE" => 100,
            "GE" => 1000,
            "2.5GE" or "2P5GE" => 2500,
            "5GE" => 5000,
            "10GE" => 10000,
            "SFP" => 1000,
            "SFP+" => 10000,
            "SFP28" => 25000,
            "QSFP28" => 100000,
            _ => 0,
        };
        return bySpec > 0 ? bySpec : reported ?? 1000;
    }

    private static string SpeedLabel(int mbps) => mbps >= 1000 ? $"{mbps / 1000.0:0.#}G" : $"{mbps}M";

    private static string PortLabel(Port p) => p.Sfp ? $"{p.Media} {p.Idx}" : $"Port {p.Idx}";

    private static string OptionLabel(Port p)
    {
        var extras = new List<string>();
        if (!p.Sfp && p.SpeedMbps != 1000) extras.Add(SpeedLabel(p.SpeedMbps));
        if (p.Poe) extras.Add("PoE");
        return PortLabel(p) + (extras.Count > 0 ? $" ({string.Join(" ", extras)})" : "");
    }

    private static string Summary(List<Port> ports, JsonObject meta)
    {
        var parts = new List<string>();
        var copper = ports.Count(p => !p.Sfp);
        var sfp = ports.Count(p => p.Sfp);
        if (copper > 0) parts.Add($"{copper} ports");
        if (sfp > 0) parts.Add($"{sfp} {ports.First(p => p.Sfp).Media}");
        if (meta["radios"] is JsonArray radios && radios.Count > 0)
            parts.Add(string.Join(", ", radios.OfType<JsonObject>().Select(r => BandName(Str(r, "radio")))));
        return string.Join(" and ", parts);
    }

    private static List<ReplacementPortRow> PortRows(JsonObject device, JsonObject meta, List<Port> ports, Dictionary<string, string> names,
        Dictionary<string, string> deviceNames, string kind)
    {
        var overrides = (device["port_overrides"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(o => Int(o, "port_idx") != null).GroupBy(o => Int(o, "port_idx")!.Value).ToDictionary(g => g.Key, g => g.First());
        var byIdx = ports.ToDictionary(p => p.Idx);
        var lldp = (meta["lldp"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(l => Int(l, "local_port_idx") != null).GroupBy(l => Int(l, "local_port_idx")!.Value)
            .ToDictionary(g => g.Key, g => Str(g.First(), "chassis_id")?.ToLowerInvariant());
        var uplink = meta["uplink"] as JsonObject;
        var lagOf = new Dictionary<int, int>();
        var lagNo = 0;
        foreach (var lag in (device["link_aggregation_groups"] as JsonArray ?? []).OfType<JsonObject>())
        {
            lagNo++;
            foreach (var m in (lag["aggregate_members"] as JsonArray ?? []).Select(IntOf).OfType<int>())
                lagOf[m] = lagNo;
        }

        string ConfigOf(int idx, JsonObject o)
        {
            var parts = new List<string>();
            if (Str(o, "portconf_id") is { } pc && names.TryGetValue(pc, out var profile)) parts.Add(profile);
            else if (Str(o, "native_networkconf_id") is { } nn && names.TryGetValue(nn, out var net)) parts.Add(net);
            if (Str(o, "forward") is "disabled") parts.Add("disabled");
            if (o["isolation"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) parts.Add("port isolation");
            if (byIdx.TryGetValue(idx, out var p) && p.Powering) parts.Add("PoE");
            return parts.Count > 0 ? string.Join(", ", parts) : "Custom";
        }

        string Connected(int idx)
        {
            var p = byIdx.GetValueOrDefault(idx);
            if (lldp.TryGetValue(idx, out var mac) && mac != null)
            {
                var name = deviceNames.GetValueOrDefault(mac) ?? mac;
                return p is { Powering: true } ? $"{name} ({p.PoeWatts:0} W)" : name;
            }
            if (p is { Uplink: true } && uplink != null && Str(uplink, "uplink_device_name") is { } up)
                return Int(uplink, "uplink_remote_port") is { } rp ? $"{up} port {rp}" : up;
            if (p is { Powering: true }) return $"PoE device ({p.PoeWatts:0} W)";
            return p is { Up: true } ? "In use" : "Idle";
        }

        var singles = new List<(int Idx, int Priority, ReplacementPortRow Row)>();
        var runCandidates = new List<(int Idx, string Config, bool Up)>();

        // A LAG moves as one row: its members need consecutive ports of the same kind.
        foreach (var group in lagOf.GroupBy(kv => kv.Value))
        {
            var members = group.Select(kv => kv.Key).OrderBy(i => i).ToList();
            var lead = members.FirstOrDefault(overrides.ContainsKey);
            var config = $"LAG {group.Key}" + (lead > 0 && ConfigOf(lead, overrides[lead]) is var c && c != "Custom" ? $", {c}" : "");
            var neighbor = members.Select(m => lldp.GetValueOrDefault(m)).FirstOrDefault(m => m != null);
            singles.Add((members[0], 1, new ReplacementPortRow
            {
                Key = $"l{group.Key}",
                OldPorts = members,
                OldLabel = $"Ports {Range(members)}",
                Config = config,
                Connected = neighbor != null ? $"{deviceNames.GetValueOrDefault(neighbor) ?? neighbor} ({members.Count} x {SpeedLabel(byIdx.GetValueOrDefault(members[0])?.SpeedMbps ?? 1000)})"
                    : $"{members.Count(m => byIdx.GetValueOrDefault(m)?.Up == true)} in use",
                InUseCount = members.Count(m => byIdx.GetValueOrDefault(m)?.Up == true),
            }));
        }

        foreach (var (idx, o) in overrides.OrderBy(k => k.Key))
        {
            if (lagOf.ContainsKey(idx)) continue;
            var p = byIdx.GetValueOrDefault(idx);
            var hasNeighbor = lldp.ContainsKey(idx);
            var isUplink = p?.Uplink == true;
            var config = ConfigOf(idx, o);
            if (lagOf.TryGetValue(idx, out var lag))
                config = $"LAG {lag}" + (config == "Custom" ? "" : $", {config}");
            if (isUplink) config = "Uplink" + (config == "Custom" ? "" : $", {config}");
            var special = isUplink || hasNeighbor || lagOf.ContainsKey(idx) || p?.Powering == true || p?.Sfp == true;
            if (!special && kind != "gateway")
            {
                runCandidates.Add((idx, config, p?.Up == true));
                continue;
            }
            var priority = isUplink ? 0 : lagOf.ContainsKey(idx) ? 1 : p?.Powering == true ? 2 : p?.Sfp == true ? 3 : hasNeighbor ? 4 : 5;
            singles.Add((idx, priority, new ReplacementPortRow
            {
                Key = $"p{idx}",
                OldPorts = [idx],
                OldLabel = p != null ? PortLabel(p) : $"Port {idx}",
                Config = config,
                Connected = Connected(idx),
                InUseCount = p?.Up == true ? 1 : 0,
            }));
        }

        // Runs: consecutive plain ports with the same config collapse into one row ("Ports 7-12").
        var runs = new List<List<(int Idx, string Config, bool Up)>>();
        foreach (var c in runCandidates)
        {
            if (runs.Count > 0 && runs[^1][^1].Idx == c.Idx - 1 && runs[^1][^1].Config == c.Config) runs[^1].Add(c);
            else runs.Add([c]);
        }
        var runRows = runs.Select(run =>
        {
            var inUse = run.Where(r => r.Up).Select(r => r.Idx).ToList();
            // In-use ports first, so a run cut short keeps the ports something is plugged into.
            var ordered = inUse.Concat(run.Where(r => !r.Up).Select(r => r.Idx)).ToList();
            var first = run[0].Idx;
            var last = run[^1].Idx;
            return (first, run.Count == 1 ? 5 : 6, new ReplacementPortRow
            {
                Key = run.Count == 1 ? $"p{first}" : $"r{first}-{last}",
                OldPorts = ordered,
                OldLabel = run.Count == 1 ? $"Port {first}" : $"Ports {first}-{last}",
                Config = run.Count == 1 ? run[0].Config : $"{run[0].Config}, {run.Count} ports",
                Connected = run.Count == 1 ? Connected(first) : inUse.Count == 0 ? "Idle" : $"{inUse.Count} in use",
                InUseCount = inUse.Count,
            });
        });

        return singles.Concat(runRows).OrderBy(r => r.Item2).ThenBy(r => r.Item1).Select(r => r.Item3).ToList();
    }

    private static void Assign(List<ReplacementPortRow> rows, List<Port> oldPorts, List<Port> newPorts, bool sameLayout,
        IReadOnlyDictionary<string, string> choices, IReadOnlySet<string> acks)
    {
        var oldByIdx = oldPorts.ToDictionary(p => p.Idx);
        var newByIdx = newPorts.ToDictionary(p => p.Idx);
        var used = new HashSet<int>();

        // The user's choices claim their ports first; suggestions fill around them.
        foreach (var row in rows)
        {
            if (!choices.TryGetValue(row.Key, out var choice)) continue;
            if (choice == Drop) row.Target = [];
            else
            {
                var picked = choice.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out var i) ? i : -1).Where(newByIdx.ContainsKey).ToList();
                if (picked.Count > 0)
                {
                    row.Target = picked;
                    used.UnionWith(picked);
                }
            }
        }

        // Short on ports: runs of plain ports claim only what is plugged in, so idle config drops before live ports do.
        var tight = !sameLayout && rows.Where(r => r.Target == null).Sum(r => r.OldPorts.Count) > newPorts.Count(p => !used.Contains(p.Idx));

        foreach (var row in rows.Where(r => r.Target == null))
        {
            if (sameLayout)
            {
                var same = row.OldPorts.Where(newByIdx.ContainsKey).OrderBy(i => i).ToList();
                if (same.Count == row.OldPorts.Count && !same.Any(used.Contains))
                {
                    row.Target = row.OldPorts.ToList();
                    used.UnionWith(same);
                    continue;
                }
            }

            var olds = row.OldPorts.Select(i => oldByIdx.GetValueOrDefault(i)).ToList();
            var template = olds.FirstOrDefault(o => o != null);
            var needSfp = template?.Sfp == true;
            var needPoe = olds.Any(o => o?.Powering == true);
            var minSpeed = template?.SpeedMbps ?? 1000;
            var wantUplink = template?.Uplink == true;

            var candidates = newPorts.Where(p => !used.Contains(p.Idx) && p.Sfp == needSfp && (!needPoe || p.Poe)).ToList();
            if (candidates.Count == 0 && !needSfp)
                candidates = newPorts.Where(p => !used.Contains(p.Idx) && (!needPoe || p.Poe)).ToList();

            // Plain runs take ports that waste nothing: no PoE or SFP when the run needs neither.
            IEnumerable<Port> Ranked(IEnumerable<Port> ps) => ps
                .OrderByDescending(p => wantUplink && p.Uplink)
                .ThenByDescending(p => p.SpeedMbps >= minSpeed)
                .ThenBy(p => !needPoe && p.Poe)
                .ThenBy(p => needPoe || wantUplink ? -p.SpeedMbps : p.SpeedMbps)
                .ThenBy(p => p.Idx);

            var want = tight && row.Key.StartsWith('r') ? row.InUseCount : row.OldPorts.Count;
            if (want == 0)
            {
                row.Target = [];
                continue;
            }
            var take = TakeRun(Ranked(candidates).ToList(), want);
            if (take.Count < want && row.InUseCount > 0 && take.Count < row.InUseCount)
                take = TakeRun(Ranked(candidates).ToList(), row.InUseCount);
            if (take.Count == 0)
            {
                if (row.InUseCount == 0)
                {
                    row.Target = [];
                    row.NeedsAck = true;
                }
                continue;
            }
            row.Target = take.Select(p => p.Idx).ToList();
            used.UnionWith(row.Target);
        }

        // Ports left over go back to runs that were cut short, nearest first, so no port sits empty while config drops.
        if (tight)
            foreach (var row in rows.Where(r => r.Key.StartsWith('r') && !choices.ContainsKey(r.Key) && r.Target != null && r.Target.Count < r.OldPorts.Count))
            {
                var anchor = row.Target!.Count > 0 ? row.Target.Average() : 0;
                var extra = newPorts.Where(p => !used.Contains(p.Idx) && !p.Sfp)
                    .OrderBy(p => Math.Abs(p.Idx - anchor)).Take(row.OldPorts.Count - row.Target.Count).Select(p => p.Idx).ToList();
                row.Target = row.Target.Concat(extra).OrderBy(i => i).ToList();
                used.UnionWith(extra);
            }

        foreach (var row in rows)
            Describe(row, oldByIdx, newByIdx, newPorts, used, acks);

        // A port claimed twice is an error on every row that claims it.
        var dupes = rows.Where(r => r.Target != null).SelectMany(r => r.Target!).GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var row in rows.Where(r => r.Target?.Any(dupes.Contains) == true))
        {
            row.Fit = $"Port {row.Target!.First(dupes.Contains)} used twice";
            row.FitLevel = "error";
        }
    }

    /// <summary>The best <paramref name="count"/> ports, consecutive when a run that long exists.</summary>
    private static List<Port> TakeRun(List<Port> ranked, int count)
    {
        if (count <= 1) return ranked.Take(count).ToList();
        var sorted = ranked.OrderBy(p => p.Idx).ToList();
        for (var i = 0; i + count <= sorted.Count; i++)
        {
            var window = sorted.Skip(i).Take(count).ToList();
            if (window[^1].Idx - window[0].Idx == count - 1) return window;
        }
        return ranked.Take(count).OrderBy(p => p.Idx).ToList();
    }

    private static void Describe(ReplacementPortRow row, Dictionary<int, Port> oldByIdx, Dictionary<int, Port> newByIdx, List<Port> newPorts,
        HashSet<int> used, IReadOnlySet<string> acks)
    {
        var olds = row.OldPorts.Select(i => oldByIdx.GetValueOrDefault(i)).ToList();
        var template = olds.FirstOrDefault(o => o != null);

        // Options: any single new port for a one-port row; consecutive windows for a run.
        var lengths = new[] { row.OldPorts.Count, row.InUseCount }.Where(n => n > 0).Distinct();
        foreach (var n in lengths)
        {
            if (n == 1)
                foreach (var p in newPorts) row.Options.Add((p.Idx.ToString(CultureInfo.InvariantCulture), OptionLabel(p)));
            else
                for (var i = 0; i + n <= newPorts.Count; i++)
                {
                    var w = newPorts.Skip(i).Take(n).ToList();
                    if (w[^1].Idx - w[0].Idx != n - 1) continue;
                    row.Options.Add((string.Join(",", w.Select(p => p.Idx)), $"Ports {w[0].Idx}-{w[^1].Idx}"));
                }
        }
        row.Options.Add((Drop, "Drop config"));

        if (row.Target == null)
        {
            row.TargetLabel = "";
            row.Fit = "No free port";
            row.FitLevel = "error";
            return;
        }
        if (row.Target.Count == 0)
        {
            row.TargetLabel = "Drop config";
            row.Fit = row.InUseCount > 0 ? "Dropped, in use" : "Dropped, idle";
            row.FitLevel = row.InUseCount > 0 ? "error" : "warn";
            row.NeedsAck = true;
            row.Acknowledged = acks.Contains(row.Key);
            if (row.InUseCount == 0 && row.Acknowledged) row.FitLevel = "muted";
            return;
        }

        var targets = row.Target.Select(i => newByIdx[i]).ToList();
        row.TargetLabel = targets.Count == 1 ? OptionLabel(targets[0])
            : string.Join(",", row.Target) == string.Join(",", Enumerable.Range(row.Target.Min(), row.Target.Count)) ? $"Ports {row.Target.Min()}-{row.Target.Max()}"
            : "Ports " + string.Join(", ", row.Target);

        var faster = template != null && targets.Min(t => t.SpeedMbps) > template.SpeedMbps;
        var slower = template != null && targets.Any(t => t.SpeedMbps < template.SpeedMbps);
        var powering = olds.Any(o => o?.Powering == true);
        var parts = new List<string>();
        if (template?.Uplink == true) parts.Add("Uplink");
        if (row.Config.StartsWith("LAG", StringComparison.Ordinal)) parts.Add("LAG");
        if (powering) parts.Add(targets.All(t => t.Poe) ? "PoE" : "No PoE");
        if (template?.Uplink == true) parts.Add(SpeedLabel(targets.Min(t => t.SpeedMbps)));
        else if (faster) parts.Add("faster");
        else if (slower) parts.Add("slower");
        row.Fit = parts.Count > 0 ? string.Join(", ", parts) : "Fits";
        row.FitLevel = powering && !targets.All(t => t.Poe) ? "error" : slower ? "warn" : "ok";

        if (row.Target.Count < row.OldPorts.Count)
        {
            var dropped = row.OldPorts.Count - row.Target.Count;
            row.Fit = row.InUseCount > row.Target.Count
                ? $"{row.InUseCount - row.Target.Count} in use without a port"
                : row.Target.Count == row.InUseCount ? $"Keeps {row.InUseCount} in use, drops {dropped} idle" : $"Drops {dropped} idle";
            row.FitLevel = row.InUseCount > row.Target.Count ? "error" : "warn";
            row.NeedsAck = true;
            row.Acknowledged = acks.Contains(row.Key);
        }
    }

    private static void Settings(JsonObject device, JsonObject oldMeta, JsonObject newMeta, List<ReplacementPortRow> rows,
        Dictionary<string, string> names, ReplacementProposal proposal)
    {
        var s = proposal.Settings;
        if (Str(device, "stp_priority") is { } stp) s.Add(new($"STP priority {stp}", "Copied", "ok"));

        var map = PortMap(proposal);
        var lagNo = 0;
        foreach (var lag in (device["link_aggregation_groups"] as JsonArray ?? []).OfType<JsonObject>())
        {
            lagNo++;
            var members = (lag["aggregate_members"] as JsonArray ?? []).Select(IntOf).OfType<int>().ToList();
            var mapped = members.Where(map.ContainsKey).Select(m => map[m]).OrderBy(m => m).ToList();
            s.Add(mapped.Count == members.Count
                ? new($"LAG {lagNo}, {members.Count} members", $"Remapped to {Range(mapped)}", "ok")
                : new($"LAG {lagNo}, {members.Count} members", mapped.Count >= 2 ? $"{members.Count - mapped.Count} members dropped" : "Dropped", "warn"));
        }
        var maxLags = (newMeta["switch_caps"] as JsonObject) is { } caps ? Int(caps, "max_aggregate_sessions") : null;
        if (maxLags != null && lagNo > maxLags)
            s.Add(new($"{lagNo} LAGs", $"New model supports {maxLags}", "warn"));

        var extras = new List<string>();
        if (device["jumboframe_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) extras.Add("jumbo frames");
        if (device["flowctrl_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) extras.Add("flow control");
        if (device["dot1x_portctrl_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True) extras.Add("802.1X");
        if ((device["port_overrides"] as JsonArray ?? []).OfType<JsonObject>().Any(o => o["stormctrl_bcast_enabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True))
            extras.Add("storm control");
        if (extras.Count > 0)
        {
            var label = string.Join(", ", extras);
            s.Add(new(char.ToUpperInvariant(label[0]) + label[1..], "Copied", "ok"));
        }

        var used = Dbl(oldMeta, "total_used_power");
        var budget = Dbl(newMeta, "total_max_power");
        if (used is > 0)
            s.Add(budget is > 0
                ? new($"PoE: {used:0} W used, new budget {budget:0} W", used <= budget ? "Fits" : "Over budget", used <= budget ? "ok" : "warn")
                : new($"PoE: {used:0} W used", "New model has no PoE budget", "warn"));

        var mgmt = Str(device, "mgmt_network_id") is { } m && names.TryGetValue(m, out var mn) ? mn : null;
        var staticIp = device["config_network"] is JsonObject cn && Str(cn, "type") == "static" ? Str(cn, "ip") : null;
        if (mgmt != null || staticIp != null)
            s.Add(new(string.Join(", ", new[] { mgmt != null ? $"Management network: {mgmt}" : null, staticIp != null ? $"static {staticIp}" : null }.OfType<string>()), "Copied", "ok"));

        var oldLight = (oldMeta["switch_caps"] as JsonObject) is { } oc ? Int(oc, "etherlight_caps") ?? 0 : 0;
        var newLight = (newMeta["switch_caps"] as JsonObject) is { } nc ? Int(nc, "etherlight_caps") ?? 0 : 0;
        if (newLight > 0 && oldLight == 0) s.Add(new("EtherLighting", "Not on old model", "muted"));
        else if (oldLight > 0 && newLight == 0 && device["ether_lighting"] != null) s.Add(new("EtherLighting", "Not on new model", "warn"));

        if (proposal.Kind == "gateway")
            proposal.Warnings.Add("Adaptive SQM, Performance Tweaks, and other Network Optimizer gateway features re-provision from their own pages after the swap.");
    }

    private static void WanRoles(JsonObject oldDevice, JsonObject newDevice, ReplacementProposal proposal)
    {
        var oldRoles = (oldDevice["ethernet_overrides"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => (Str(e, "networkgroup") ?? "").StartsWith("WAN", StringComparison.OrdinalIgnoreCase)).ToList();
        var newRoles = (newDevice["ethernet_overrides"] as JsonArray ?? []).OfType<JsonObject>()
            .GroupBy(e => Str(e, "networkgroup") ?? "").ToDictionary(g => g.Key, g => Str(g.First(), "ifname"), StringComparer.OrdinalIgnoreCase);
        foreach (var role in oldRoles)
        {
            var group = Str(role, "networkgroup")!;
            proposal.Settings.Add(newRoles.TryGetValue(group, out var ifname) && ifname != null
                ? new($"{group} on {Str(role, "ifname")}", $"{ifname} on the new model", "ok")
                : new($"{group} on {Str(role, "ifname")}", "No such port by default: assign it in UniFi after the swap", "warn"));
        }
    }

    private static JsonArray AdaptEthernet(JsonObject oldDevice, JsonArray newDefaults)
    {
        // Roles move by name (WAN to the new model's WAN port), not by ifname.
        var oldDisabled = (oldDevice["ethernet_overrides"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(e => e["disabled"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
            .Select(e => Str(e, "networkgroup")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new JsonArray();
        foreach (var e in newDefaults.OfType<JsonObject>())
        {
            var copy = (JsonObject)e.DeepClone();
            if (Str(e, "networkgroup") is { } g && g.StartsWith("WAN", StringComparison.OrdinalIgnoreCase) && oldDisabled.Contains(g))
                copy["disabled"] = true;
            result.Add(copy);
        }
        return result;
    }

    private static string BandName(string? radio) => radio switch
    {
        "ng" => "2.4 GHz",
        "na" => "5 GHz",
        "6e" => "6 GHz",
        _ => radio ?? "",
    };

    private static string WlanBand(string radio) => radio switch { "ng" => "2g", "na" => "5g", "6e" => "6g", _ => radio };

    private static int MaxWidth(JsonObject caps, string band) => band switch
    {
        "6e" => caps["has_eht320"]?.GetValueKind() == System.Text.Json.JsonValueKind.True ? 320 : caps["has_ht160"]?.GetValueKind() == System.Text.Json.JsonValueKind.True ? 160 : 80,
        "na" => caps["has_eht240"]?.GetValueKind() == System.Text.Json.JsonValueKind.True ? 240 : caps["has_ht160"]?.GetValueKind() == System.Text.Json.JsonValueKind.True ? 160 : 80,
        _ => 40,
    };

    private static void Radios(JsonObject oldDevice, JsonObject newMeta, ConfigState context, ReplacementProposal proposal, IReadOnlySet<string> acks)
    {
        var oldRadios = (oldDevice["radio_table"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var newCaps = (newMeta["radios"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(r => Str(r, "radio") ?? "", r => r);
        foreach (var old in oldRadios)
        {
            var band = Str(old, "radio") ?? "";
            var oldText = RadioText(old);
            if (!newCaps.TryGetValue(band, out var caps))
            {
                var wlans = context.Get("wlanconf").Where(w => (w["wlan_bands"] as JsonArray)?.Any(b => b?.ToString() == WlanBand(band)) == true)
                    .Select(w => Str(w, "name")).OfType<string>().ToList();
                proposal.Radios.Add(new(BandName(band), oldText, "Not on new model",
                    wlans.Count > 0 ? $"Stops broadcasting {string.Join(", ", wlans)} on {BandName(band)} from this AP" : "Config dropped", "warn"));
                continue;
            }
            var adapted = AdaptRadio(old, caps, out var notes);
            var newText = RadioText(adapted);
            string? ackKey = null;
            // Standard Power needs AFC; without a region the new AP runs Low Power Indoor (ap_pwr_type, sample captures).
            if (band == "6e" && Str(old, "ap_pwr_type") == "SP" && newMeta["supported_afc_regions"] is not JsonArray { Count: > 0 })
            {
                oldText += ", Standard Power";
                newText += ", Low Power Indoor";
                notes.Add("No AFC: lower power, smaller 6 GHz coverage");
                ackKey = "radio-6e-afc";
            }
            proposal.Radios.Add(new(BandName(band), oldText, newText, notes.Count > 0 ? string.Join("; ", notes) : "Copied", notes.Count > 0 ? "warn" : "ok",
                ackKey, ackKey != null && acks.Contains(ackKey)));
        }
        foreach (var (band, _) in newCaps.Where(n => oldRadios.All(o => Str(o, "radio") != n.Key)))
            proposal.Radios.Add(new(BandName(band), "-", "Site defaults", "New band", "muted"));

        if (Str(oldDevice, "mesh_uplink_1") is { Length: > 0 })
            proposal.Settings.Add(new("Mesh uplink", "Copied", "ok"));
        var oldMac = Str(oldDevice, "mac")?.ToLowerInvariant();
        foreach (var child in context.Get("device").Where(d => oldMac != null
            && (Str(d, "mesh_uplink_1")?.ToLowerInvariant() == oldMac || Str(d, "mesh_uplink_2")?.ToLowerInvariant() == oldMac)))
            proposal.Settings.Add(new($"{ConfigState.DisplayName(ConfigCollections.Get("device"), child)} meshes through this AP", "Uplink MAC rewritten", "ok"));
        if (oldDevice["outdoor_mode_override"] != null || oldDevice["led_override"] != null)
            proposal.Settings.Add(new("LED and outdoor mode", "Copied", "ok"));
    }

    private static string RadioText(JsonObject r)
    {
        var channel = Str(r, "channel") is { } c && c != "0" ? $"ch {c}" : "auto";
        var width = Int(r, "ht") is { } w ? $"{w} MHz" : "";
        var power = Str(r, "tx_power_mode") is "custom" ? $"{Str(r, "tx_power")} dBm" : Str(r, "tx_power_mode") ?? "";
        return string.Join(", ", new[] { channel, width, power }.Where(s => s.Length > 0));
    }

    private static JsonObject AdaptRadio(JsonObject old, JsonObject caps, out List<string> notes)
    {
        notes = [];
        var r = (JsonObject)old.DeepClone();
        r["name"] = Str(caps, "name") ?? Str(old, "name");
        var band = Str(old, "radio") ?? "";
        if (Int(r, "ht") is { } ht && ht > MaxWidth(caps, band))
        {
            r["ht"] = MaxWidth(caps, band);
            notes.Add($"width {ht} to {MaxWidth(caps, band)} MHz");
        }
        if (band == "na" && Int(r, "channel") is { } ch && ch is >= 52 and <= 144 && caps["has_dfs"]?.GetValueKind() != System.Text.Json.JsonValueKind.True)
        {
            r["channel"] = "auto";
            notes.Add("DFS channel to auto");
        }
        if (Str(r, "tx_power_mode") == "custom" && Int(r, "tx_power") is { } p)
        {
            var min = Int(caps, "min_txpower") ?? p;
            var max = Int(caps, "max_txpower") ?? p;
            var clamped = Math.Clamp(p, min, max);
            if (clamped != p)
            {
                r["tx_power"] = clamped;
                notes.Add($"power {p} to {clamped} dBm");
            }
        }
        if (r["antenna_id"] != null && caps["builtin_antenna"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)
        {
            r.Remove("antenna_id");
            r.Remove("antenna_gain");
        }
        return r;
    }

    private static JsonArray AdaptRadios(JsonObject oldDevice, JsonObject newDevice)
    {
        var caps = ((newDevice[ConfigCollections.DeviceMetaField] as JsonObject)?["radios"] as JsonArray ?? []).OfType<JsonObject>()
            .ToDictionary(r => Str(r, "radio") ?? "", r => r);
        var result = new JsonArray();
        foreach (var old in (oldDevice["radio_table"] as JsonArray ?? []).OfType<JsonObject>())
            if (caps.TryGetValue(Str(old, "radio") ?? "", out var c))
                result.Add(AdaptRadio(old, c, out _));
        return result;
    }

    private static void Recabling(List<ReplacementPortRow> rows, List<Port> oldPorts, List<Port> newPorts, ReplacementProposal proposal)
    {
        foreach (var row in rows)
        {
            var from = row.OldPorts.Count > 1 ? Range(row.OldPorts.Take(Math.Max(row.Target?.Count ?? 0, 1)).OrderBy(i => i).ToList()) : row.OldLabel.Replace("Port ", "");
            if (row.Target == null)
            {
                proposal.Recabling.Add(new RecablingRow { OldPort = from, NewPort = "?", Connected = $"{row.Connected}: choose a port or drop its config", Unresolved = true });
                continue;
            }
            if (row.Target.Count == 0) continue;
            var to = row.Target.Count == 1 ? newPorts.First(p => p.Idx == row.Target[0]) is var t && t.Sfp ? $"{t.Media} {t.Idx}" : row.Target[0].ToString(CultureInfo.InvariantCulture)
                : Range(row.Target);
            var what = row.Connected is "Idle" or "In use" || row.Connected.EndsWith(" in use", StringComparison.Ordinal)
                ? row.Config.Split(',')[0] + (row.Target.Count < row.OldPorts.Count ? $", the {row.Target.Count} in use" : "")
                : row.Connected;
            proposal.Recabling.Add(new RecablingRow { OldPort = from, NewPort = to, Connected = what });
        }
    }

    private static string Range(List<int> ports)
    {
        if (ports.Count == 0) return "-";
        var sorted = ports.OrderBy(p => p).ToList();
        return sorted[^1] - sorted[0] == sorted.Count - 1 && sorted.Count > 1 ? $"{sorted[0]}-{sorted[^1]}" : string.Join(", ", sorted);
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v ? v.ToString() : null;

    private static int? Int(JsonObject o, string key) => IntOf(o[key]);

    // UniFi sends some integers as strings (see FlexibleIntConverter), so accept both.
    private static int? IntOf(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<int>(out var i) ? i : int.TryParse(v.ToString(), out var s) ? s : null
        : null;

    private static double? Dbl(JsonObject o, string key) => o[key] is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : null
        : null;
}
