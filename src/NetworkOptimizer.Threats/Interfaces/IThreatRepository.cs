using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// Repository for threat events, patterns, and CrowdSec cache.
/// </summary>
public interface IThreatRepository
{
    // --- Noise Filtering ---
    /// <summary>
    /// Set active noise filters for this repository scope. All subsequent query methods
    /// will automatically exclude events matching these filters.
    /// </summary>
    void SetNoiseFilters(List<ThreatNoiseFilter> filters);

    // --- Severity Filtering ---
    /// <summary>
    /// Set active severity filter. When non-null, only events with matching severity levels
    /// are included in query results. Null means all severities.
    /// </summary>
    void SetSeverityFilter(int[]? severities);

    // --- Threat Events ---
    Task SaveEventsAsync(List<ThreatEvent> events, CancellationToken cancellationToken = default);
    Task<List<ThreatEvent>> GetEventsAsync(DateTime from, DateTime to, string? sourceIp = null, int? destPort = null, KillChainStage? stage = null, int limit = 1000, CancellationToken cancellationToken = default);
    Task<ThreatSummary> GetThreatSummaryAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
    Task<List<SourceIpSummary>> GetTopSourcesAsync(DateTime from, DateTime to, int count = 10, CancellationToken cancellationToken = default);
    Task<List<TargetPortSummary>> GetTopTargetedPortsAsync(DateTime from, DateTime to, int count = 10, CancellationToken cancellationToken = default);
    Task<Dictionary<string, int>> GetCountryDistributionAsync(DateTime from, DateTime to, ThreatAction? actionFilter = null, CancellationToken cancellationToken = default);
    Task<List<TimelineBucket>> GetTimelineAsync(DateTime from, DateTime to, int bucketMinutes = 60, CancellationToken cancellationToken = default);
    Task<Dictionary<KillChainStage, int>> GetKillChainDistributionAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
    Task<List<ThreatEvent>> GetEventsByIpAsync(string ip, DateTime from, DateTime to, int limit = 5000, CancellationToken cancellationToken = default);
    Task<List<ThreatEvent>> GetEventsByPortAsync(int port, DateTime from, DateTime to, int limit = 5000, CancellationToken cancellationToken = default);
    Task<List<ThreatEvent>> GetEventsByProtocolAsync(string protocol, DateTime from, DateTime to, int limit = 5000, CancellationToken cancellationToken = default);
    Task<int> GetThreatCountByPortAsync(int port, DateTime from, DateTime to, CancellationToken cancellationToken = default);
    Task<Dictionary<int, int>> GetThreatCountsByPortAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);
    Task PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetch events with null geo data (tracked, not AsNoTracking), enrich via callback, then save.
    /// Returns the count of events enriched. Call in a loop until 0 is returned.
    /// </summary>
    Task<int> BackfillGeoDataAsync(Action<List<ThreatEvent>> enrichAction, int batchSize = 1000, CancellationToken cancellationToken = default);

    // --- Threat Patterns ---
    Task SavePatternAsync(ThreatPattern pattern, CancellationToken cancellationToken = default);
    Task<List<ThreatPattern>> GetPatternsAsync(DateTime from, DateTime to, PatternType? type = null, int limit = 50, CancellationToken cancellationToken = default);

    // --- Attack Sequences ---
    Task<List<AttackSequence>> GetAttackSequencesAsync(DateTime from, DateTime to, int limit = 50, CancellationToken cancellationToken = default);

    // --- CrowdSec Cache ---
    Task<CrowdSecReputation?> GetCrowdSecCacheAsync(string ip, CancellationToken cancellationToken = default);
    Task SaveCrowdSecCacheAsync(CrowdSecReputation reputation, CancellationToken cancellationToken = default);
    Task PurgeCrowdSecCacheAsync(CancellationToken cancellationToken = default);

    // --- Noise Filters ---
    Task<List<ThreatNoiseFilter>> GetNoiseFiltersAsync(CancellationToken cancellationToken = default);
    Task SaveNoiseFilterAsync(ThreatNoiseFilter filter, CancellationToken cancellationToken = default);
    Task DeleteNoiseFilterAsync(int filterId, CancellationToken cancellationToken = default);
    Task ToggleNoiseFilterAsync(int filterId, bool enabled, CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregated threat stats for a time range.
/// </summary>
public record ThreatSummary
{
    public int TotalEvents { get; init; }
    public int BlockedCount { get; init; }
    public int DetectedCount { get; init; }
    public int UniqueSourceIps { get; init; }
    public int UniqueDestPorts { get; init; }
}

/// <summary>
/// Top source IP summary with event count and geo data.
/// </summary>
public record SourceIpSummary
{
    public string SourceIp { get; init; } = string.Empty;
    public int EventCount { get; init; }
    public string? CountryCode { get; init; }
    public string? City { get; init; }
    public int? Asn { get; init; }
    public string? AsnOrg { get; init; }
    public int MaxSeverity { get; init; }

    // CrowdSec CTI enrichment (populated post-query by dashboard service)
    public string? CrowdSecReputation { get; set; }
    public int? ThreatScore { get; set; }
    public string? TopBehaviors { get; set; }
    public List<(string Name, string Label, string? Description)>? MitreTechniques { get; set; }
}

/// <summary>
/// Top targeted port summary.
/// </summary>
public record TargetPortSummary
{
    public int Port { get; init; }
    public int EventCount { get; init; }
    public int UniqueSourceIps { get; init; }
    public string TopSignature { get; init; } = string.Empty;
}

/// <summary>
/// Hourly bucket for timeline chart.
/// </summary>
public record TimelineBucket
{
    public DateTime Hour { get; init; }
    public int Severity1 { get; init; }
    public int Severity2 { get; init; }
    public int Severity3 { get; init; }
    public int Severity4 { get; init; }
    public int Severity5 { get; init; }
    public int Total { get; init; }
}

/// <summary>
/// Multi-stage attack sequence detected for a single source IP.
/// </summary>
public record AttackSequence
{
    public string SourceIp { get; init; } = string.Empty;
    public string? CountryCode { get; init; }
    public string? AsnOrg { get; init; }
    public List<SequenceStage> Stages { get; init; } = [];
}

/// <summary>
/// A single stage within an attack sequence.
/// </summary>
public record SequenceStage
{
    public KillChainStage Stage { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }
    public int EventCount { get; init; }
    public string TopSignature { get; init; } = string.Empty;
}
