using System.Text.Json;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Pure rollout ordering: outer-to-inner by topology depth, channel-contiguous groups,
/// SKU canaries, mesh child-before-parent, and coverage-aware AP parallelism. No I/O -
/// everything arrives frozen in <see cref="RolloutPlanningInput"/>.
/// </summary>
public class RolloutPlanner
{
    /// <summary>Console channel change (and restore) allowance in the timeline.</summary>
    public const int ChannelChangeSeconds = 60;

    /// <summary>Per-wave allowance for command dispatch and catalog settling.</summary>
    public const int CommandOverheadSeconds = 30;

    /// <summary>UniFi Network application update allowance (console app restart).</summary>
    public const int UniFiNetworkUpdateSeconds = 300;

    public RolloutPlanResult Plan(RolloutPlanningInput input)
    {
        var settings = input.Settings;
        var spacing = ResolvedSpacing.For(settings.SpacingProfile, settings.AdvancedSpacingJson);
        var exclusions = RolloutExclusions.Parse(settings.ExclusionsJson);
        foreach (var mac in input.AdditionalExcludedMacs)
            exclusions.Macs.Add(MacNormalizer.Normalize(mac));
        var doc = new RolloutPlanDocument();
        var steps = new List<FirmwareRolloutStep>();

        var depths = ComputeDepths(input.Devices);
        var meshParents = input.Devices
            .Where(d => d.WirelessUplink && d.UplinkMac != null)
            .Select(d => d.UplinkMac!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var upgradable = input.Devices.Where(d => d.Upgradable).ToList();
        var excluded = upgradable.Where(exclusions.Excludes).ToList();
        var candidates = upgradable.Where(d => !exclusions.Excludes(d)).ToList();

        foreach (var d in excluded)
        {
            steps.Add(MakeStep(d, channel: ResolveChannel(d, settings), wave: 0, FirmwareRolloutStepState.SkippedExcluded));
        }

        // Channel is a pure function of SKU/type/global, so one SKU never spans two groups
        // and the per-group canary bookkeeping is safe.
        var groups = candidates
            .GroupBy(d => ResolveChannel(d, settings))
            .Select(g => new ChannelGroup(g.Key, g.ToList()))
            .ToList();

        var ordered = groups
            .OrderBy(g => g.Devices.Any(d => d.Type == DeviceType.Gateway) ? 1 : 0)
            .ThenBy(g => string.Equals(g.Channel, input.CurrentConsoleChannel, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(g => g.Devices.Count)
            .ThenBy(g => g.Channel, StringComparer.Ordinal)
            .ToList();

        ordered = ApplyMeshGroupOrdering(ordered, candidates, doc.Notes);

        // Family, not the raw code: the same AP in another shell has its own console code, and
        // counting them apart meant one of each was two models of one device - so neither earned
        // a canary and both went in normal waves with nothing gating them.
        var skuCounts = candidates.GroupBy(d => d.ModelFamily, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var canaried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceWave = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Not candidates: a Cloud Gateway reports upgradable=false while its UniFi OS build waits,
        // because that update belongs to the console. Its own device candidacy says nothing here.
        var cloudGateway = input.Devices
            .FirstOrDefault(d => FirmwareTimingEstimator.Classify(d) == FirmwareDeviceClass.CloudGatewayUniFiOs);
        doc.ConsoleMac = cloudGateway?.Mac;

        var includeOs = settings.IncludeUniFiOs
            && input.UniFiOsUpdateAvailable
            && cloudGateway != null;

        int waveNumber = 0;
        foreach (var group in ordered)
        {
            var requiresChange = !string.Equals(group.Channel, input.CurrentConsoleChannel, StringComparison.OrdinalIgnoreCase);
            var firstWave = waveNumber + 1;

            foreach (var levelDevices in Levels(group.Devices, depths))
            {
                foreach (var wave in BuildLevelWaves(levelDevices, meshParents, spacing, input.Neighbors, skuCounts, canaried))
                {
                    waveNumber++;
                    var planWave = new PlanWave { Number = waveNumber, Channel = group.Channel };
                    foreach (var (device, isCanary, held) in wave)
                    {
                        deviceWave[device.Mac] = waveNumber;
                        var cls = EffectiveClass(device, settings);
                        planWave.Steps.Add(new PlanWaveStep
                        {
                            Mac = device.Mac,
                            Name = device.Name,
                            Model = device.Model,
                            DisplayModel = device.DisplayModel,
                            DeviceType = FirmwareDeviceTypes.Code(device.Type),
                            FromVersion = device.FromVersion,
                            // Already the target for THIS device's channel: the gather stages each
                            // planned channel and leaves it null where it could not reach one.
                            ToVersion = device.ToVersion,
                            IsCanary = isCanary,
                            HeldForCanary = held,
                            IsMeshParticipant = IsMeshParticipant(device, meshParents),
                            EstimatedDowntimeSeconds = input.Estimator.EstimateDowntimeSeconds(device.Model, cls),
                            OfflineBudgetSeconds = FirmwareTimingEstimator.OfflineBudgetSeconds(cls),
                        });
                        steps.Add(MakeStep(device, group.Channel, waveNumber,
                            held ? FirmwareRolloutStepState.Held : FirmwareRolloutStepState.Pending));
                    }
                    doc.Waves.Add(planWave);
                }
            }

            doc.ChannelGroups.Add(new PlanChannelGroup
            {
                Channel = group.Channel,
                RequiresConsoleChange = requiresChange,
                FirstWave = firstWave,
                LastWave = waveNumber,
                DeviceCount = group.Devices.Count,
            });
        }

        BuildMeshRepairs(doc, candidates, deviceWave);

        var includeApp = settings.IncludeUniFiNetwork && input.NetworkAppUpdateAvailable;
        doc.IncludesUniFiNetworkUpdate = includeApp;
        doc.UniFiNetworkUpdateSeconds = includeApp ? UniFiNetworkUpdateSeconds : 0;
        doc.IncludesUniFiOsUpdate = includeOs;
        doc.UniFiOsUpdateSeconds = includeOs
            ? FirmwareTimingEstimator.SeedDowntimeSeconds(FirmwareDeviceClass.CloudGatewayUniFiOs)
            : 0;

        // Versions and waves for the two console phases, so a scheduled plan can show them before
        // it runs. The executor names its own target when it gets there; this is what was planned.
        doc.NetworkAppUpdate.FromVersion = input.NetworkAppFromVersion;
        doc.NetworkAppUpdate.TargetVersion = input.NetworkAppToVersion;
        doc.NetworkAppUpdate.Url = input.NetworkAppDownloadUrl;
        doc.UniFiOsUpdate.FromVersion = input.UniFiOsFromVersion;
        doc.UniFiOsUpdate.TargetVersion = input.UniFiOsToVersion;
        doc.UniFiOsUpdate.Url = input.UniFiOsDownloadUrl;
        doc.NetworkAppUpdate.Wave = 0;
        doc.UniFiOsUpdate.Wave = steps.Count > 0 ? steps.Max(s => s.Wave) + 1 : 1;
        ComputeTimeline(doc, spacing);
        AddNotes(doc, input, candidates);

        return new RolloutPlanResult { Document = doc, Steps = steps };
    }

    private sealed record ChannelGroup(string Channel, List<PlannerDevice> Devices);

    /// <summary>
    /// Mesh children must upgrade before their parents even when a channel override puts
    /// them in different groups: reorder groups so a child's group precedes its parent's.
    /// Gateway-last always wins (a child sharing the gateway's group cannot pull it
    /// earlier), and conflicting edges fall back to the channel order with a plan note.
    /// </summary>
    private static List<ChannelGroup> ApplyMeshGroupOrdering(
        List<ChannelGroup> ordered, List<PlannerDevice> candidates, List<string> notes)
    {
        if (ordered.Count < 2) return ordered;

        var groupOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ordered.Count; i++)
        {
            foreach (var d in ordered[i].Devices) groupOf[d.Mac] = i;
        }

        var gatewayIdx = ordered.FindIndex(g => g.Devices.Any(d => d.Type == DeviceType.Gateway));
        var n = ordered.Count;
        var edges = new HashSet<(int From, int To)>();
        foreach (var child in candidates.Where(d => d.WirelessUplink && d.UplinkMac != null))
        {
            if (!groupOf.TryGetValue(child.Mac, out var cg) ||
                !groupOf.TryGetValue(child.UplinkMac!, out var pg) || cg == pg)
            {
                continue;
            }
            if (cg == gatewayIdx)
            {
                notes.Add($"{child.Name} upgrades after its mesh parent because its channel group runs last with the gateway; its backhaul drops briefly during the parent's reboot.");
                continue;
            }
            edges.Add((cg, pg));
        }
        if (edges.Count == 0) return ordered;

        var indegree = new int[n];
        var adjacent = new List<int>[n];
        for (var i = 0; i < n; i++) adjacent[i] = [];
        foreach (var (from, to) in edges)
        {
            adjacent[from].Add(to);
            indegree[to]++;
        }

        var result = new List<ChannelGroup>(n);
        var placed = new bool[n];
        while (result.Count < n)
        {
            var next = -1;
            for (var i = 0; i < n; i++)
            {
                if (!placed[i] && indegree[i] == 0) { next = i; break; }
            }
            if (next == -1)
            {
                notes.Add("Some mesh pairs span channel groups in conflicting directions; their order follows the channel grouping instead.");
                for (var i = 0; i < n; i++)
                {
                    if (!placed[i]) result.Add(ordered[i]);
                }
                break;
            }
            placed[next] = true;
            result.Add(ordered[next]);
            foreach (var t in adjacent[next]) indegree[t]--;
        }
        return result;
    }

    /// <summary>Effective channel: per-SKU override, then per-type, then global.</summary>
    public static string ResolveChannel(PlannerDevice d, FirmwareRolloutSettings settings)
    {
        // Exact code first so a pin deliberately set on one color still wins, then the family, so a
        // pin covers the same hardware in another shell and a map written against a raw code resolves.
        var bySku = ParseMap(settings.PerSkuChannelsJson);
        if (bySku.TryGetValue(d.Model, out var skuChannel) && !string.IsNullOrWhiteSpace(skuChannel))
            return skuChannel;
        foreach (var (sku, channel) in bySku)
        {
            if (string.IsNullOrWhiteSpace(channel)) continue;
            if (string.Equals(UniFiProductDatabase.GetModelFamily(sku), d.ModelFamily, StringComparison.OrdinalIgnoreCase))
                return channel;
        }

        var byType = ParseMap(settings.PerDeviceTypeChannelsJson);
        if (byType.TryGetValue(FirmwareDeviceTypes.Code(d.Type), out var typeChannel) ||
            byType.TryGetValue(d.Type.ToString(), out typeChannel))
        {
            if (!string.IsNullOrWhiteSpace(typeChannel)) return typeChannel;
        }

        return string.IsNullOrWhiteSpace(settings.GlobalChannel) ? FirmwareChannels.Release : settings.GlobalChannel;
    }

    /// <summary>
    /// Depth from the root gateway via uplink chains, cycle-guarded. Devices with an
    /// unknown parent are treated as deep leaves: they have no known children, so
    /// upgrading them early is the safe default.
    /// </summary>
    public static Dictionary<string, int> ComputeDepths(IReadOnlyList<PlannerDevice> devices)
    {
        const int orphanBase = 1000;
        var byMac = devices
            .GroupBy(d => d.Mac, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices)
        {
            if (depths.ContainsKey(device.Mac)) continue;
            var chain = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = device;
            int baseDepth;
            while (true)
            {
                chain.Add(current.Mac);
                visited.Add(current.Mac);
                var parentMac = current.UplinkMac;
                if (string.IsNullOrEmpty(parentMac) || string.Equals(parentMac, current.Mac, StringComparison.OrdinalIgnoreCase))
                {
                    baseDepth = current.Type == DeviceType.Gateway ? 0 : orphanBase;
                    break;
                }
                if (depths.TryGetValue(parentMac, out var known))
                {
                    baseDepth = known + 1;
                    break;
                }
                if (!byMac.TryGetValue(parentMac, out var parent))
                {
                    baseDepth = current.Type == DeviceType.Gateway ? 0 : orphanBase;
                    break;
                }
                if (visited.Contains(parent.Mac))
                {
                    baseDepth = orphanBase;
                    break;
                }
                current = parent;
            }

            for (var i = chain.Count - 1; i >= 0; i--)
            {
                depths[chain[i]] = baseDepth + (chain.Count - 1 - i);
            }
        }

        return depths;
    }

    private static bool IsMeshParticipant(PlannerDevice d, HashSet<string> meshParents) =>
        d.WirelessUplink || meshParents.Contains(d.Mac);

    /// <summary>Deepest level first; within a level APs run before switches, gateway last.</summary>
    private static IEnumerable<List<PlannerDevice>> Levels(List<PlannerDevice> devices, Dictionary<string, int> depths)
    {
        return devices
            .GroupBy(d => depths.GetValueOrDefault(d.Mac, 1))
            .OrderByDescending(g => g.Key)
            .Select(g => g.OrderBy(TypeOrder).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(d => d.Mac, StringComparer.Ordinal).ToList());

        static int TypeOrder(PlannerDevice d) => d.Type switch
        {
            DeviceType.AccessPoint => 0,
            DeviceType.Switch => 2,
            DeviceType.Gateway => 3,
            _ => 1,
        };
    }

    /// <summary>
    /// Waves for one depth level: canaries solo first, non-mesh APs packed by
    /// coverage-compatibility, mesh participants and unknowns solo, switches packed
    /// (same-depth subtrees are disjoint in a tree), gateways solo and last.
    /// </summary>
    private static List<List<(PlannerDevice Device, bool IsCanary, bool Held)>> BuildLevelWaves(
        List<PlannerDevice> level,
        HashSet<string> meshParents,
        ResolvedSpacing spacing,
        IApNeighborOracle? neighbors,
        Dictionary<string, int> skuCounts,
        HashSet<string> canaried)
    {
        var waves = new List<List<(PlannerDevice, bool, bool)>>();
        var packableAps = new List<PlannerDevice>();
        var packableSwitches = new List<PlannerDevice>();
        var solo = new List<PlannerDevice>();
        var gateways = new List<PlannerDevice>();

        foreach (var d in level)
        {
            var isCanary = skuCounts.GetValueOrDefault(d.ModelFamily, 0) > 1 && canaried.Add(d.ModelFamily);
            if (isCanary)
            {
                waves.Add([(d, true, false)]);
                continue;
            }
            if (d.Type == DeviceType.Gateway) gateways.Add(d);
            else if (IsMeshParticipant(d, meshParents)) solo.Add(d);
            else if (d.Type == DeviceType.AccessPoint) packableAps.Add(d);
            else if (d.Type == DeviceType.Switch) packableSwitches.Add(d);
            else solo.Add(d);
        }

        bool Held(PlannerDevice d) => canaried.Contains(d.ModelFamily) && skuCounts.GetValueOrDefault(d.ModelFamily, 0) > 1;

        Pack(waves, packableAps, spacing.MaxApParallelism,
            (a, b) => neighbors == null || !neighbors.AreNeighbors(a.Mac, b.Mac), Held);
        foreach (var d in solo) waves.Add([(d, false, Held(d))]);
        Pack(waves, packableSwitches, spacing.MaxSwitchParallelism, (_, _) => true, Held);
        foreach (var d in gateways) waves.Add([(d, false, Held(d))]);

        return waves;
    }

    /// <summary>First-fit packing under a size cap and a pairwise compatibility predicate.</summary>
    private static void Pack(
        List<List<(PlannerDevice, bool, bool)>> waves,
        List<PlannerDevice> devices,
        int cap,
        Func<PlannerDevice, PlannerDevice, bool> compatible,
        Func<PlannerDevice, bool> held)
    {
        var open = new List<List<(PlannerDevice, bool, bool)>>();
        foreach (var d in devices)
        {
            var placed = false;
            foreach (var wave in open)
            {
                if (wave.Count >= cap) continue;
                if (wave.All(w => compatible(w.Item1, d)))
                {
                    wave.Add((d, false, held(d)));
                    placed = true;
                    break;
                }
            }
            if (!placed)
            {
                var wave = new List<(PlannerDevice, bool, bool)> { (d, false, held(d)) };
                open.Add(wave);
                waves.Add(wave);
            }
        }
    }

    private static void BuildMeshRepairs(RolloutPlanDocument doc, List<PlannerDevice> candidates, Dictionary<string, int> deviceWave)
    {
        foreach (var child in candidates.Where(d => d.WirelessUplink && !string.IsNullOrEmpty(d.MeshUplinkInterface)))
        {
            if (!deviceWave.TryGetValue(child.Mac, out var childWave)) continue;
            var parentWave = child.UplinkMac != null && deviceWave.TryGetValue(child.UplinkMac, out var pw) ? pw : 0;
            doc.MeshRepairs.Add(new PlanMeshRepair
            {
                ChildMac = child.Mac,
                ChildName = child.Name,
                ChildIp = child.IpAddress,
                ParentMac = child.UplinkMac,
                Iface = child.MeshUplinkInterface,
                AfterWave = Math.Max(childWave, parentWave),
            });
        }
    }

    /// <summary>Cumulative offsets: channel changes at group edges, gaps between waves.</summary>
    internal static void ComputeTimeline(RolloutPlanDocument doc, ResolvedSpacing spacing)
    {
        var t = doc.UniFiNetworkUpdateSeconds;
        foreach (var group in doc.ChannelGroups)
        {
            if (group.RequiresConsoleChange) t += ChannelChangeSeconds;
            var groupWaves = doc.Waves.Where(w => w.Number >= group.FirstWave && w.Number <= group.LastWave).ToList();
            for (var i = 0; i < groupWaves.Count; i++)
            {
                var wave = groupWaves[i];
                wave.StartOffsetSeconds = t;
                foreach (var s in wave.Steps) s.EtaOffsetSeconds = t;
                var hasGateway = wave.Steps.Any(s =>
                    FirmwareDeviceTypes.Parse(s.DeviceType) == DeviceType.Gateway);
                var cooldown = hasGateway
                    ? FirmwareRolloutOrchestrator.GatewayCoolDown
                    : FirmwareRolloutOrchestrator.CoolDown;
                t += (wave.Steps.Count == 0 ? 0 : wave.Steps.Max(s => s.EstimatedDowntimeSeconds))
                    + (int)cooldown.TotalSeconds
                    + CommandOverheadSeconds;
                if (i < groupWaves.Count - 1) t += GapAfter(wave, spacing);
            }
            if (group.RequiresConsoleChange) t += ChannelChangeSeconds;
        }
        doc.UniFiOsStartOffsetSeconds = doc.UniFiOsUpdateSeconds > 0 ? t : 0;
        t += doc.UniFiOsUpdateSeconds;
        doc.TotalEstimatedSeconds = t;
    }

    private static int GapAfter(PlanWave wave, ResolvedSpacing spacing)
    {
        var gap = 0;
        foreach (var s in wave.Steps)
        {
            gap = Math.Max(gap, s.DeviceType switch
            {
                "ugw" => spacing.GatewayGapSeconds,
                "usw" => spacing.SwitchGapSeconds,
                _ => spacing.ApGapSeconds,
            });
        }
        return gap;
    }

    private static void AddNotes(RolloutPlanDocument doc, RolloutPlanningInput input, List<PlannerDevice> candidates)
    {
        if (input.Neighbors == null)
        {
            doc.Notes.Add("No AP placement or roaming data - assumed uniform AP density, so APs run in parallel up to the cap.");
        }
        else if (!input.Neighbors.HasPlacementData)
        {
            doc.Notes.Add("AP placements are not set - parallel APs were chosen from UniFi roaming neighbors only.");
        }

        if (candidates.Any(d => d.Type == DeviceType.Gateway))
        {
            doc.Notes.Add(doc.IncludesUniFiOsUpdate
                ? "The gateway upgrades last. Its UniFi OS cycle can take up to 30 minutes, and the console (and any agent tunnel) is unreachable during it."
                : "The gateway upgrades last; the console is briefly unreachable during its reboot.");
        }

        if (doc.MeshRepairs.Count > 0)
        {
            doc.Notes.Add("Mesh children upgrade before their parents; each pair gets a backhaul re-scan once both are done, while later waves continue.");
        }
    }

    private static FirmwareDeviceClass EffectiveClass(PlannerDevice device, FirmwareRolloutSettings settings)
    {
        var cls = FirmwareTimingEstimator.Classify(device);
        // Without the UniFi OS cycle a Cloud Gateway only reboots its network firmware,
        // which measures like a UXG-class gateway.
        if (cls == FirmwareDeviceClass.CloudGatewayUniFiOs && !settings.IncludeUniFiOs)
            return FirmwareDeviceClass.GatewayNetworkOnly;
        return cls;
    }

    private static FirmwareRolloutStep MakeStep(PlannerDevice d, string channel, int wave, FirmwareRolloutStepState state) => new()
    {
        DeviceMac = d.Mac,
        DeviceName = string.IsNullOrEmpty(d.Name) ? d.DisplayModel : d.Name,
        Model = d.Model,
        DeviceType = FirmwareDeviceTypes.Code(d.Type),
        FromVersion = d.FromVersion,
        ToVersion = d.ToVersion,
        Channel = channel,
        Wave = wave,
        State = state,
    };

    private static Dictionary<string, string> ParseMap(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return map == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
