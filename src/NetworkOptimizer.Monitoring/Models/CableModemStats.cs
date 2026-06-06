namespace NetworkOptimizer.Monitoring.Models;

/// <summary>
/// Comprehensive cable modem statistics from DOCSIS status page scraping.
/// Supports downstream/upstream channel data with per-channel detail in cache
/// and aggregated metrics written to InfluxDB.
/// </summary>
public class CableModemStats
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string DeviceHost { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string DeviceModel { get; set; } = "";

    /// <summary>Per-channel downstream data (kept in cache, not written to InfluxDB)</summary>
    public List<DsChannel> DownstreamChannels { get; set; } = new();

    /// <summary>Per-channel upstream data (kept in cache, not written to InfluxDB)</summary>
    public List<UsChannel> UpstreamChannels { get; set; } = new();

    // Computed aggregates - these get written to InfluxDB

    public int LockedDsChannels => DownstreamChannels.Count(c =>
        c.LockStatus.Equals("Locked", StringComparison.OrdinalIgnoreCase));

    public int LockedUsChannels => UpstreamChannels.Count(c =>
        c.LockStatus.Equals("Locked", StringComparison.OrdinalIgnoreCase));

    public double? DownstreamPowerAvgDbmv
    {
        get
        {
            var locked = DownstreamChannels
                .Where(c => c.LockStatus.Equals("Locked", StringComparison.OrdinalIgnoreCase) && c.Power.HasValue)
                .ToList();
            return locked.Count > 0 ? locked.Average(c => c.Power!.Value) : null;
        }
    }

    public double? DownstreamSnrAvgDb
    {
        get
        {
            var locked = DownstreamChannels
                .Where(c => c.LockStatus.Equals("Locked", StringComparison.OrdinalIgnoreCase) && c.Snr.HasValue)
                .ToList();
            return locked.Count > 0 ? locked.Average(c => c.Snr!.Value) : null;
        }
    }

    public double? UpstreamPowerAvgDbmv
    {
        get
        {
            var locked = UpstreamChannels
                .Where(c => c.LockStatus.Equals("Locked", StringComparison.OrdinalIgnoreCase) && c.Power.HasValue)
                .ToList();
            return locked.Count > 0 ? locked.Average(c => c.Power!.Value) : null;
        }
    }

    public long TotalCorrectables => DownstreamChannels.Sum(c => c.Correctables);

    public long TotalUncorrectables => DownstreamChannels.Sum(c => c.Uncorrectables);

    public int ChannelsWithUncorrectables => DownstreamChannels.Count(c => c.Uncorrectables > 0);
}

/// <summary>
/// Downstream DOCSIS channel metrics
/// </summary>
public class DsChannel
{
    public int ChannelId { get; set; }
    public string LockStatus { get; set; } = "";
    public string Modulation { get; set; } = "";
    public long Frequency { get; set; }
    public double? Power { get; set; }
    public double? Snr { get; set; }
    public long Correctables { get; set; }
    public long Uncorrectables { get; set; }
}

/// <summary>
/// Upstream DOCSIS channel metrics
/// </summary>
public class UsChannel
{
    public int ChannelId { get; set; }
    public string LockStatus { get; set; } = "";
    public string ChannelType { get; set; } = "";
    public long Frequency { get; set; }
    public double? Power { get; set; }
    public long SymbolRate { get; set; }
}
