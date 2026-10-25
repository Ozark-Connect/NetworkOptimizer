using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Brings device firmware upgrades from before change detection saw them into the change history:
/// Firmware Rollout steps that upgraded a device, and firmware-upgrade boots from reboot-reason
/// history, which also catches upgrades started from the UniFi Console. Runs once per engine start.
/// An upgrade already recorded (same device, same version, within <see cref="SameUpgrade"/>) is
/// skipped, so a re-run adds nothing and live detection never doubles with it.
/// </summary>
public sealed class FirmwareHistoryImport
{
    /// <summary>How far back reboot-reason history is read.</summary>
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(400);

    /// <summary>Two records of one device reaching one version this close together are the same upgrade.</summary>
    internal static readonly TimeSpan SameUpgrade = TimeSpan.FromHours(12);

    // A boot is re-probed and rewritten when the classifier changes, at nearly the same timestamp.
    private static readonly TimeSpan SameBoot = TimeSpan.FromMinutes(10);

    private readonly FirmwareRolloutRepositoryAccessor _rollout;
    private readonly Func<MonitoringInfluxClient> _influx;
    private readonly ILogger _logger;

    public FirmwareHistoryImport(FirmwareRolloutRepositoryAccessor rollout, Func<MonitoringInfluxClient> influx, ILogger logger)
    {
        _rollout = rollout;
        _influx = influx;
        _logger = logger;
    }

    /// <summary>One upgrade found in history.</summary>
    internal sealed record Upgrade(DateTime At, string Mac, string Name, string? From, string? To, int? RolloutPlanId);

    /// <summary>Imports what is missing. <paramref name="namesByMac"/> names devices the history only knows by MAC.</summary>
    public async Task RunAsync(ChangeControlSiteEngine engine, IReadOnlyDictionary<string, string> namesByMac, CancellationToken ct)
    {
        var found = Merge(await RolloutUpgradesAsync(ct), await RebootUpgradesAsync(namesByMac, ct));
        if (found.Count == 0) return;

        var from = found.Min(u => u.At) - SameUpgrade;
        var existing = await engine.Repositories.UseAsync((r, c) => r.GetChangesAsync(from, DateTime.UtcNow, 5000, c), ct);
        var recorded = existing.Where(x => x.Kind == ConfigChangeKind.DeviceFirmware).ToList();
        var added = 0;
        foreach (var u in found.Where(u => !recorded.Any(x => IsSame(x, u))))
        {
            var change = new ConfigChange
            {
                Kind = ConfigChangeKind.DeviceFirmware,
                OccurredAt = u.At,
                DetectedAt = DateTime.UtcNow,
                ActorKind = u.RolloutPlanId != null ? ConfigChangeActorKind.Firmware : ConfigChangeActorKind.LikelyUniFi,
                ActorName = u.RolloutPlanId != null ? "Firmware Rollout" : null,
                FirmwareRolloutPlanId = u.RolloutPlanId,
                Summary = Summary(u),
                VersionFrom = u.From,
                VersionTo = u.To,
                Evaluated = true,
            };
            await engine.Repositories.UseAsync((r, c) => r.AddChangeAsync(change, [], c), ct);
            added++;
        }
        if (added > 0)
            _logger.LogInformation("Change Control imported {Count} past firmware upgrade(s) for site {Site}", added, engine.SiteSlug);
    }

    internal static string Summary(Upgrade u) => u.To == null
        ? $"{u.Name}: firmware upgrade"
        : u.From == null || u.From == u.To ? $"{u.Name}: firmware to {u.To}" : $"{u.Name}: firmware {u.From} to {u.To}";

    /// <summary>The same device reaching the same version close in time. Live rows carry the name, not the MAC.</summary>
    internal static bool IsSame(ConfigChange x, Upgrade u) =>
        Math.Abs((x.OccurredAt - u.At).TotalHours) <= SameUpgrade.TotalHours
        && x.Summary.StartsWith(u.Name + ":", StringComparison.Ordinal)
        && (u.To == null || x.VersionTo == null || SameVersion(x.VersionTo, u.To));

    /// <summary>Rollout steps carry the build ("7.6.7.17255"); reboot records may not, so compare on the shorter.</summary>
    internal static bool SameVersion(string a, string b) =>
        a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);

    /// <summary>Rollout and reboot history describe the same upgrade twice; the rollout's record wins.</summary>
    internal static List<Upgrade> Merge(List<Upgrade> rollout, List<Upgrade> reboots) =>
        rollout.Concat(reboots.Where(b => !rollout.Any(r =>
                string.Equals(r.Mac, b.Mac, StringComparison.OrdinalIgnoreCase)
                && Math.Abs((r.At - b.At).TotalHours) <= SameUpgrade.TotalHours
                && (b.To == null || r.To == null || SameVersion(r.To, b.To)))))
            .OrderBy(u => u.At).ToList();

    private async Task<List<Upgrade>> RolloutUpgradesAsync(CancellationToken ct)
    {
        try
        {
            return await _rollout.UseAsync(async (r, c) =>
            {
                var result = new List<Upgrade>();
                foreach (var plan in await r.GetPlanHistoryAsync(500, c))
                    foreach (var s in await r.GetStepsAsync(plan.Id, c))
                        if (s.State is FirmwareRolloutStepState.LitmusPassed or FirmwareRolloutStepState.RegressionFlagged && s.BackAt is { } at)
                            result.Add(new Upgrade(at, Mac(s.DeviceMac), s.DeviceName, s.FromVersion, s.ToVersion, plan.Id));
                return result;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Firmware Rollout history could not be read for the firmware import");
            return [];
        }
    }

    private async Task<List<Upgrade>> RebootUpgradesAsync(IReadOnlyDictionary<string, string> namesByMac, CancellationToken ct)
    {
        IReadOnlyList<MonitoringInfluxClient.DeviceRebootPoint> boots;
        try
        {
            boots = await _influx().QueryDeviceRebootsInRangeAsync(DateTime.UtcNow - Lookback, DateTime.UtcNow, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reboot history could not be read for the firmware import");
            return [];
        }
        return FromBoots(boots, namesByMac);
    }

    /// <summary>
    /// Firmware-upgrade boots, one per boot, with the version the device ran on its boot before.
    /// Devices no longer on the site keep their MAC as the name.
    /// </summary>
    internal static List<Upgrade> FromBoots(IReadOnlyList<MonitoringInfluxClient.DeviceRebootPoint> boots, IReadOnlyDictionary<string, string> namesByMac)
    {
        var result = new List<Upgrade>();
        foreach (var device in boots.GroupBy(b => Mac(b.DeviceMac)))
        {
            var perBoot = new List<MonitoringInfluxClient.DeviceRebootPoint>();
            foreach (var b in device.OrderBy(b => b.BootedAt))
            {
                if (perBoot.Count > 0 && b.BootedAt - perBoot[^1].BootedAt < SameBoot)
                {
                    if (b.ClassifierVersion >= perBoot[^1].ClassifierVersion) perBoot[^1] = b;
                    continue;
                }
                perBoot.Add(b);
            }
            var versions = perBoot.Select(b => string.IsNullOrWhiteSpace(b.FirmwareVersion) ? null : b.FirmwareVersion).ToList();
            string? previous = null;
            for (var i = 0; i < perBoot.Count; i++)
            {
                var version = versions[i];
                var next = perBoot.Skip(i + 1).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b.FirmwareVersion));
                if (IsUpgrade(perBoot[i]) && Changed(previous, version, next?.FirmwareVersion, next != null && IsUpgrade(next)))
                    result.Add(new Upgrade(perBoot[i].BootedAt, device.Key, namesByMac.TryGetValue(device.Key, out var n) ? n : device.Key,
                        previous, version, null));
                // Keep the fuller spelling of one version ("5.1.26.33914" over "5.1.26").
                if (version != null && !(previous != null && SameVersion(previous, version) && previous.Length >= version.Length))
                    previous = version;
            }
        }
        return result;
    }

    private static bool IsUpgrade(MonitoringInfluxClient.DeviceRebootPoint b) => b.Category == nameof(RebootCategory.FirmwareUpgrade);

    /// <summary>
    /// Whether a boot the reboot classifier calls an upgrade moved the firmware. The classifier also
    /// marks boots that kept their version (a pending-upgrade marker, a re-probe), so a boot on the
    /// version before it is not an upgrade. A boot with no recorded version is judged by its neighbours:
    /// the same version on both sides means it changed nothing, and a later upgrade boot that records
    /// the new version owns the change. Anything else cannot be ruled out.
    /// </summary>
    internal static bool Changed(string? previous, string? version, string? nextKnown, bool nextIsUpgrade)
    {
        if (previous == null) return true;
        if (version != null) return !SameVersion(previous, version);
        return nextKnown == null || (!SameVersion(previous, nextKnown) && !nextIsUpgrade);
    }

    /// <summary>Colon-separated lowercase; reboot records store the MAC without colons.</summary>
    internal static string Mac(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        return hex.Length == 12 ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))) : mac.ToLowerInvariant();
    }
}
