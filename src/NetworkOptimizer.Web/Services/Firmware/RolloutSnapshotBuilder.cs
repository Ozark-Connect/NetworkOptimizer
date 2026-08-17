using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Freezes discovered topology into <see cref="PlannerDevice"/> snapshots. Depth and mesh
/// relationships are captured here because mesh reparenting makes the live values dynamic.
/// </summary>
public static class RolloutSnapshotBuilder
{
    private const string MeshStaPrefix = "vwiresta";

    public static List<PlannerDevice> FromDevices(IEnumerable<DiscoveredDevice> devices)
    {
        var result = new List<PlannerDevice>();
        foreach (var d in devices)
        {
            if (string.IsNullOrEmpty(d.Mac) || !d.Adopted) continue;
            var wireless = string.Equals(d.UplinkType, "wireless", StringComparison.OrdinalIgnoreCase);
            result.Add(new PlannerDevice
            {
                Mac = MacNormalizer.Normalize(d.Mac),
                Name = string.IsNullOrEmpty(d.Name) ? d.FriendlyModelName : d.Name,
                Model = d.Model,
                DisplayModel = d.FriendlyModelName,
                Type = d.Type,
                Upgradable = d.Upgradable,
                FromVersion = string.IsNullOrEmpty(d.Firmware) ? null : d.Firmware,
                ToVersion = d.UpgradeToFirmware,
                UplinkMac = string.IsNullOrEmpty(d.UplinkMac) ? null : MacNormalizer.Normalize(d.UplinkMac),
                UplinkLocalPort = d.LocalUplinkPort,
                UplinkRemotePort = d.UplinkPort,
                WirelessUplink = wireless,
                MeshUplinkInterface = wireless && d.UplinkInterface?.StartsWith(MeshStaPrefix, StringComparison.OrdinalIgnoreCase) == true
                    ? d.UplinkInterface
                    : null,
                IpAddress = string.IsNullOrEmpty(d.DisplayIpAddress) ? null : d.DisplayIpAddress,
            });
        }
        return result;
    }
}

/// <summary>
/// Set-backed <see cref="IApNeighborOracle"/>. Callers precompute the neighbor pairs from
/// AP placements (propagation interference) corroborated by UniFi roaming edges with real
/// attempts; the planner only asks membership questions.
/// </summary>
public class ApNeighborOracle : IApNeighborOracle
{
    private readonly HashSet<(string, string)> _pairs = [];

    public ApNeighborOracle(bool hasPlacementData, int placedApCount = 0)
    {
        HasPlacementData = hasPlacementData;
        PlacedApCount = placedApCount;
    }

    public bool HasPlacementData { get; }

    /// <inheritdoc />
    public int PlacedApCount { get; }

    public void AddNeighbors(string macA, string macB)
    {
        var key = Key(macA, macB);
        if (key != null) _pairs.Add(key.Value);
    }

    public bool AreNeighbors(string macA, string macB)
    {
        var key = Key(macA, macB);
        return key != null && _pairs.Contains(key.Value);
    }

    private static (string, string)? Key(string macA, string macB)
    {
        var a = MacNormalizer.Normalize(macA);
        var b = MacNormalizer.Normalize(macB);
        if (a.Length == 0 || b.Length == 0 || a == b) return null;
        return string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
    }
}
