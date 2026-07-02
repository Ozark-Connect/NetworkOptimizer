using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// One attributed metrics sample for an AP radio: the utilization / interference / TX retry
/// the radio measured while the given (channel, width) was live.
/// </summary>
/// <param name="ApMac">AP MAC (lowercase, colon-separated)</param>
/// <param name="Band">Radio band code - "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz)</param>
/// <param name="Channel">Control channel the sample is attributed to</param>
/// <param name="WidthMhz">Channel width in MHz; 0 when unknown</param>
/// <param name="TimestampUtc">When the sample was measured (UTC)</param>
/// <param name="Utilization">Channel utilization percent (0-100)</param>
/// <param name="Interference">Interference percent (0-100)</param>
/// <param name="TxRetryPct">TX retry percent (0-100)</param>
public record ChannelOutcomeSample(
    string ApMac,
    string Band,
    int Channel,
    int WidthMhz,
    DateTime TimestampUtc,
    double Utilization,
    double Interference,
    double TxRetryPct);

/// <summary>
/// One observation of a neighbor network by an AP radio, for the long-term neighbor memory.
/// </summary>
/// <param name="ApMac">Observing AP MAC (lowercase, colon-separated)</param>
/// <param name="Band">Radio band code - "ng" (2.4 GHz), "na" (5 GHz), "6e" (6 GHz)</param>
/// <param name="Bssid">Neighbor BSSID (lowercase, colon-separated)</param>
/// <param name="Channel">Control channel the neighbor was seen on</param>
/// <param name="WidthMhz">Neighbor channel width in MHz; 0 when unknown</param>
/// <param name="SignalDbm">Observed signal strength in dBm</param>
/// <param name="SeenAtUtc">When the neighbor was seen (UTC)</param>
/// <param name="Ssid">Neighbor SSID, if any</param>
public record NeighborSightingSample(
    string ApMac,
    string Band,
    string Bssid,
    int Channel,
    int WidthMhz,
    int SignalDbm,
    DateTime SeenAtUtc,
    string? Ssid);

/// <summary>
/// Persistence for the Channel Recommendation engine's outcome memory: long-term
/// per-(AP, band, channel, width) measured outcomes and the channel-change log used for
/// metric attribution and soak-period suppression.
/// </summary>
public interface IChannelMemoryRepository
{
    /// <summary>
    /// Aggregate the given samples into their daily (AP, band, channel, width) buckets,
    /// creating buckets as needed.
    /// </summary>
    Task AddOutcomeSamplesAsync(IReadOnlyCollection<ChannelOutcomeSample> samples, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist one collection cycle atomically: outcome samples, change records, and the new
    /// watermark commit together or not at all - so a partial failure can never advance the
    /// watermark past unsaved data, nor leave saved samples that a retry would double-count.
    /// </summary>
    Task CommitCollectionAsync(
        IReadOnlyCollection<ChannelOutcomeSample> samples,
        IReadOnlyCollection<ApChannelChange> changes,
        DateTime watermarkUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Get all outcome buckets on or after the given UTC date.</summary>
    Task<List<ApChannelOutcome>> GetOutcomesSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>Get all channel-change records at or after the given UTC time, ordered chronologically.</summary>
    Task<List<ApChannelChange>> GetChangesSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken = default);

    /// <summary>Get the most recent change record per (AP, band) - the last known live config.</summary>
    Task<List<ApChannelChange>> GetLatestConfigsAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert channel-change records. The caller is responsible for de-duplication.</summary>
    Task AddChangesAsync(IReadOnlyCollection<ApChannelChange> changes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upsert neighbor sightings into their (AP, band, BSSID, channel) rows: LastSeen advances,
    /// FirstSeen holds, signal keeps the strongest observed, width and SSID follow the newest
    /// sighting that knows them.
    /// </summary>
    Task UpsertNeighborSightingsAsync(IReadOnlyCollection<NeighborSightingSample> sightings, CancellationToken cancellationToken = default);

    /// <summary>Get neighbor sightings last seen at or after the given UTC time.</summary>
    Task<List<ApNeighborSighting>> GetNeighborSightingsSinceAsync(DateTime lastSeenSinceUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete outcome buckets and change records older than the retention window, and neighbor
    /// sightings unseen for longer than their (shorter) retention. The most recent change record
    /// per (AP, band) is always kept - it carries the last known config.
    /// </summary>
    Task PruneAsync(int retentionDays, int neighborRetentionDays, CancellationToken cancellationToken = default);

    /// <summary>End of the window the collector last aggregated, or null if it has never run.</summary>
    Task<DateTime?> GetCollectionWatermarkAsync(CancellationToken cancellationToken = default);

    /// <summary>Persist the end of the window the collector just aggregated.</summary>
    Task SetCollectionWatermarkAsync(DateTime watermarkUtc, CancellationToken cancellationToken = default);
}
