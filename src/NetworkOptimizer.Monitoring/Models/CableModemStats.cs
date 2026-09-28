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

    /// <summary>
    /// The modem's own DOCSIS event log, newest last, as it reported it on this poll. Empty for
    /// providers that do not read one. New entries are also written to InfluxDB as events.
    /// </summary>
    public List<CmEvent> Events { get; set; } = new();

    /// <summary>DOCSIS registration state, for providers that report it; null otherwise.</summary>
    public CmDocsisState? DocsisState { get; set; }

    /// <summary>Modem firmware version, for providers that report it.</summary>
    public string? FirmwareVersion { get; set; }

    /// <summary>Modem uptime in seconds, for providers that report it.</summary>
    public long? UptimeSeconds { get; set; }

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

    /// <summary>Channel capacity the modem reports, in Mbps, for providers that report it.</summary>
    public double? ReportedSpeedMbps { get; set; }
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

    /// <summary>Upstream modulation (e.g. "QAM64"), for providers that report it.</summary>
    public string? Modulation { get; set; }

    /// <summary>Channel capacity the modem reports, in Mbps, for providers that report it.</summary>
    public double? ReportedSpeedMbps { get; set; }
}

/// <summary>
/// One entry from a modem's DOCSIS event log.
/// </summary>
public class CmEvent
{
    /// <summary>
    /// When the modem logged it, in UTC. Null when the modem had not synced its clock yet
    /// ("Time Not Established"), or logged a time from before it synced: such entries are shown
    /// but never written to InfluxDB or alerted on.
    /// </summary>
    public DateTime? Time { get; set; }

    /// <summary>Log level as the modem wrote it (Critical, Error, Warning, Notice, ...).</summary>
    public string Level { get; set; } = "";

    /// <summary>The event text, without the time and level.</summary>
    public string Text { get; set; } = "";

    /// <summary>The full line as the modem reported it; the identity used to tell entries apart.</summary>
    public string Raw { get; set; } = "";

    /// <summary>What kind of event this is, when recognised (see <see cref="CmEventKinds"/>); null otherwise.</summary>
    public string? Kind { get; set; }
}

/// <summary>Recognised DOCSIS event kinds, also the suffix of their alert event types.</summary>
public static class CmEventKinds
{
    public const string T3Timeout = "t3_timeout";
    public const string T4Timeout = "t4_timeout";
    public const string RangingFailure = "ranging_failure";
}

/// <summary>
/// DOCSIS registration state reported by the modem.
/// </summary>
/// <param name="Mode">DOCSIS mode, e.g. "D3.1".</param>
/// <param name="State">Registration state, e.g. "Operational".</param>
/// <param name="ReinitReason">Why the modem last reinitialized, e.g. "POWER_ON".</param>
public sealed record CmDocsisState(string? Mode, string? State, string? ReinitReason);
