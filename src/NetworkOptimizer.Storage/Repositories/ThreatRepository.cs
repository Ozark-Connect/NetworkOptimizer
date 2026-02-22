using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for threat events, patterns, and CrowdSec cache.
/// </summary>
public class ThreatRepository : IThreatRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<ThreatRepository> _logger;
    private List<ThreatNoiseFilter> _noiseFilters = [];

    public ThreatRepository(NetworkOptimizerDbContext context, ILogger<ThreatRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void SetNoiseFilters(List<ThreatNoiseFilter> filters)
    {
        _noiseFilters = filters;
    }

    /// <summary>
    /// Build a base query for the time range with noise filters applied.
    /// </summary>
    private IQueryable<ThreatEvent> BaseQuery(DateTime from, DateTime to)
    {
        var query = _context.ThreatEvents
            .AsNoTracking()
            .Where(e => e.Timestamp >= from && e.Timestamp <= to);

        return ApplyNoiseFilters(query);
    }

    /// <summary>
    /// Apply noise filter exclusions to an IQueryable. Each filter adds a WHERE clause
    /// that excludes events matching all non-null filter fields.
    /// Supports CIDR notation (e.g. "10.0.0.0/8") for /8, /16, /24 subnets.
    /// </summary>
    private IQueryable<ThreatEvent> ApplyNoiseFilters(IQueryable<ThreatEvent> query)
    {
        foreach (var f in _noiseFilters)
        {
            var srcIp = f.SourceIp;
            var dstIp = f.DestIp;
            var dstPort = f.DestPort;
            var srcPrefix = ToCidrPrefix(srcIp);
            var dstPrefix = ToCidrPrefix(dstIp);
            var srcIsExact = srcIp != null && srcPrefix == null;
            var dstIsExact = dstIp != null && dstPrefix == null;
            var srcIsCidr = srcPrefix != null;
            var dstIsCidr = dstPrefix != null;

            // Build the exclusion using De Morgan's: keep if ANY condition doesn't match.
            // For CIDR, use StartsWith (translates to LIKE in SQLite).
            if (srcIp != null && dstIp != null && dstPort != null)
            {
                if (srcIsCidr && dstIsCidr)
                    query = query.Where(e => !e.SourceIp.StartsWith(srcPrefix!) || !e.DestIp.StartsWith(dstPrefix!) || e.DestPort != dstPort);
                else if (srcIsCidr)
                    query = query.Where(e => !e.SourceIp.StartsWith(srcPrefix!) || e.DestIp != dstIp || e.DestPort != dstPort);
                else if (dstIsCidr)
                    query = query.Where(e => e.SourceIp != srcIp || !e.DestIp.StartsWith(dstPrefix!) || e.DestPort != dstPort);
                else
                    query = query.Where(e => e.SourceIp != srcIp || e.DestIp != dstIp || e.DestPort != dstPort);
            }
            else if (srcIp != null && dstIp != null)
            {
                if (srcIsCidr && dstIsCidr)
                    query = query.Where(e => !e.SourceIp.StartsWith(srcPrefix!) || !e.DestIp.StartsWith(dstPrefix!));
                else if (srcIsCidr)
                    query = query.Where(e => !e.SourceIp.StartsWith(srcPrefix!) || e.DestIp != dstIp);
                else if (dstIsCidr)
                    query = query.Where(e => e.SourceIp != srcIp || !e.DestIp.StartsWith(dstPrefix!));
                else
                    query = query.Where(e => e.SourceIp != srcIp || e.DestIp != dstIp);
            }
            else if (srcIp != null && dstPort != null)
            {
                if (srcIsCidr)
                    query = query.Where(e => !e.SourceIp.StartsWith(srcPrefix!) || e.DestPort != dstPort);
                else
                    query = query.Where(e => e.SourceIp != srcIp || e.DestPort != dstPort);
            }
            else if (dstIp != null && dstPort != null)
            {
                if (dstIsCidr)
                    query = query.Where(e => !e.DestIp.StartsWith(dstPrefix!) || e.DestPort != dstPort);
                else
                    query = query.Where(e => e.DestIp != dstIp || e.DestPort != dstPort);
            }
            else if (srcIp != null)
            {
                if (srcIsCidr)
                    query = query.Where(e => !e.SourceIp.StartsWith(srcPrefix!));
                else
                    query = query.Where(e => e.SourceIp != srcIp);
            }
            else if (dstIp != null)
            {
                if (dstIsCidr)
                    query = query.Where(e => !e.DestIp.StartsWith(dstPrefix!));
                else
                    query = query.Where(e => e.DestIp != dstIp);
            }
            else if (dstPort != null)
                query = query.Where(e => e.DestPort != dstPort);
        }

        return query;
    }

    /// <summary>
    /// Build a SQL noise filter WHERE clause for raw SQL queries.
    /// Returns empty string if no filters active. Supports CIDR notation.
    /// </summary>
    private string BuildNoiseFilterSql(out List<object> parameters)
    {
        parameters = [];
        if (_noiseFilters.Count == 0) return "";

        var clauses = new List<string>();
        foreach (var f in _noiseFilters)
        {
            var parts = new List<string>();
            if (f.SourceIp != null)
            {
                var prefix = ToCidrPrefix(f.SourceIp);
                if (prefix != null)
                {
                    // CIDR: use NOT LIKE 'prefix%' (manually constructed, prefix is validated)
                    parts.Add($"SourceIp NOT LIKE '{prefix}%'");
                }
                else
                {
                    parts.Add($"SourceIp != {{{parameters.Count}}}");
                    parameters.Add(f.SourceIp);
                }
            }
            if (f.DestIp != null)
            {
                var prefix = ToCidrPrefix(f.DestIp);
                if (prefix != null)
                {
                    parts.Add($"DestIp NOT LIKE '{prefix}%'");
                }
                else
                {
                    parts.Add($"DestIp != {{{parameters.Count}}}");
                    parameters.Add(f.DestIp);
                }
            }
            if (f.DestPort != null) { parts.Add($"DestPort != {{{parameters.Count}}}"); parameters.Add(f.DestPort); }

            if (parts.Count > 0)
                clauses.Add($"({string.Join(" OR ", parts)})");
        }

        return clauses.Count > 0 ? " AND " + string.Join(" AND ", clauses) : "";
    }

    private static string? ToCidrPrefix(string? value) => NetworkUtilities.GetCidrLikePrefix(value);

    #region Threat Events

    public async Task SaveEventsAsync(List<ThreatEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0) return;

        try
        {
            // Get existing InnerAlertIds to skip duplicates
            var newAlertIds = events.Select(e => e.InnerAlertId).ToHashSet();
            var existingIds = await _context.ThreatEvents
                .Where(e => newAlertIds.Contains(e.InnerAlertId))
                .Select(e => e.InnerAlertId)
                .ToHashSetAsync(cancellationToken);

            var newEvents = events.Where(e => !existingIds.Contains(e.InnerAlertId)).ToList();
            if (newEvents.Count == 0)
            {
                _logger.LogDebug("All {Count} events already exist, skipping", events.Count);
                return;
            }

            _context.ThreatEvents.AddRange(newEvents);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved {New} new threat events ({Skipped} duplicates skipped)",
                newEvents.Count, events.Count - newEvents.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save {Count} threat events", events.Count);
            throw;
        }
    }

    public async Task<List<ThreatEvent>> GetEventsAsync(DateTime from, DateTime to,
        string? sourceIp = null, int? destPort = null, KillChainStage? stage = null,
        int limit = 1000, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = BaseQuery(from, to);

            if (!string.IsNullOrEmpty(sourceIp))
                query = query.Where(e => e.SourceIp == sourceIp);
            if (destPort.HasValue)
                query = query.Where(e => e.DestPort == destPort.Value);
            if (stage.HasValue)
                query = query.Where(e => e.KillChainStage == stage.Value);

            return await query
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat events");
            throw;
        }
    }

    public async Task<ThreatSummary> GetThreatSummaryAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var events = BaseQuery(from, to);

            var total = await events.CountAsync(cancellationToken);
            var blocked = await events.CountAsync(e => e.Action == ThreatAction.Blocked, cancellationToken);
            var uniqueSources = await events.Select(e => e.SourceIp).Distinct().CountAsync(cancellationToken);
            var uniquePorts = await events.Select(e => e.DestPort).Distinct().CountAsync(cancellationToken);

            return new ThreatSummary
            {
                TotalEvents = total,
                BlockedCount = blocked,
                DetectedCount = total - blocked,
                UniqueSourceIps = uniqueSources,
                UniqueDestPorts = uniquePorts
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat summary");
            throw;
        }
    }

    public async Task<List<SourceIpSummary>> GetTopSourcesAsync(DateTime from, DateTime to,
        int count = 10, CancellationToken cancellationToken = default)
    {
        try
        {
            return await BaseQuery(from, to)
                .GroupBy(e => e.SourceIp)
                .Select(g => new SourceIpSummary
                {
                    SourceIp = g.Key,
                    EventCount = g.Count(),
                    CountryCode = g.First().CountryCode,
                    City = g.First().City,
                    Asn = g.First().Asn,
                    AsnOrg = g.First().AsnOrg,
                    MaxSeverity = g.Max(e => e.Severity)
                })
                .OrderByDescending(s => s.EventCount)
                .Take(count)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get top threat sources");
            throw;
        }
    }

    public async Task<List<TargetPortSummary>> GetTopTargetedPortsAsync(DateTime from, DateTime to,
        int count = 10, CancellationToken cancellationToken = default)
    {
        try
        {
            return await BaseQuery(from, to)
                .GroupBy(e => e.DestPort)
                .Select(g => new TargetPortSummary
                {
                    Port = g.Key,
                    EventCount = g.Count(),
                    UniqueSourceIps = g.Select(e => e.SourceIp).Distinct().Count(),
                    TopSignature = g.GroupBy(e => e.SignatureName)
                        .OrderByDescending(sg => sg.Count())
                        .Select(sg => sg.Key)
                        .FirstOrDefault() ?? ""
                })
                .OrderByDescending(s => s.EventCount)
                .Take(count)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get top targeted ports");
            throw;
        }
    }

    public async Task<Dictionary<string, int>> GetCountryDistributionAsync(DateTime from, DateTime to,
        ThreatAction? actionFilter = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = BaseQuery(from, to).Where(e => e.CountryCode != null);
            if (actionFilter.HasValue)
                query = query.Where(e => e.Action == actionFilter.Value);
            return await query
                .GroupBy(e => e.CountryCode!)
                .Select(g => new { Country = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Country, g => g.Count, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get country distribution");
            throw;
        }
    }

    public async Task<List<TimelineBucket>> GetTimelineAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Use raw SQL with strftime for server-side hour truncation (avoids loading all events into memory)
            _logger.LogWarning("[TIMELINE-DEBUG] GetTimelineAsync: noiseFilters.Count={Count}", _noiseFilters.Count);
            var noiseFilterSql = BuildNoiseFilterSql(out var extraParams);
            _logger.LogWarning("[TIMELINE-DEBUG] BuildNoiseFilterSql returned: '{Sql}', params={ParamCount}", noiseFilterSql, extraParams.Count);
            var allParams = new List<object> { from, to };
            // Offset parameter indices in the noise filter SQL by 2 (for from/to)
            // Iterate backwards so {1} doesn't match inside {10}, {11}, etc.
            var offsetFilterSql = noiseFilterSql;
            for (var i = extraParams.Count - 1; i >= 0; i--)
                offsetFilterSql = offsetFilterSql.Replace($"{{{i}}}", $"{{{i + 2}}}");
            allParams.AddRange(extraParams);

            // All dynamic values use parameterized {N} placeholders - safe from injection
#pragma warning disable EF1002
            var buckets = await _context.Database
                .SqlQueryRaw<TimelineBucketRaw>(
                    $$"""
                    SELECT strftime('%Y-%m-%d %H:00:00', Timestamp) AS HourStr,
                           SUM(CASE WHEN Severity = 1 THEN 1 ELSE 0 END) AS Severity1,
                           SUM(CASE WHEN Severity = 2 THEN 1 ELSE 0 END) AS Severity2,
                           SUM(CASE WHEN Severity = 3 THEN 1 ELSE 0 END) AS Severity3,
                           SUM(CASE WHEN Severity = 4 THEN 1 ELSE 0 END) AS Severity4,
                           SUM(CASE WHEN Severity = 5 THEN 1 ELSE 0 END) AS Severity5,
                           COUNT(*) AS Total
                    FROM ThreatEvents
                    WHERE Timestamp >= {0} AND Timestamp <= {1}{{offsetFilterSql}}
                    GROUP BY strftime('%Y-%m-%d %H:00:00', Timestamp)
                    ORDER BY HourStr
                    """, allParams.ToArray())
                .ToListAsync(cancellationToken);
#pragma warning restore EF1002

            return buckets.Select(b => new TimelineBucket
            {
                Hour = DateTime.TryParse(b.HourStr, out var h) ? DateTime.SpecifyKind(h, DateTimeKind.Utc) : DateTime.MinValue,
                Severity1 = b.Severity1,
                Severity2 = b.Severity2,
                Severity3 = b.Severity3,
                Severity4 = b.Severity4,
                Severity5 = b.Severity5,
                Total = b.Total
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat timeline");
            throw;
        }
    }

    public async Task<Dictionary<KillChainStage, int>> GetKillChainDistributionAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await BaseQuery(from, to)
                .GroupBy(e => e.KillChainStage)
                .Select(g => new { Stage = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            return results.ToDictionary(r => r.Stage, r => r.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get kill chain distribution");
            throw;
        }
    }

    public async Task<List<ThreatEvent>> GetEventsByIpAsync(string ip, DateTime from, DateTime to,
        int limit = 5000, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ApplyNoiseFilters(
                _context.ThreatEvents
                    .AsNoTracking()
                    .Where(e => (e.SourceIp == ip || e.DestIp == ip) && e.Timestamp >= from && e.Timestamp <= to))
                .OrderByDescending(e => e.Timestamp)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get events for IP {Ip}", ip);
            throw;
        }
    }

    public async Task<int> GetThreatCountByPortAsync(int port, DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ThreatEvents
                .CountAsync(e => e.DestPort == port && e.Timestamp >= from && e.Timestamp <= to, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat count for port {Port}", port);
            throw;
        }
    }

    public async Task<Dictionary<int, int>> GetThreatCountsByPortAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var results = await BaseQuery(from, to)
                .GroupBy(e => e.DestPort)
                .Select(g => new { Port = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);

            return results.ToDictionary(r => r.Port, r => r.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat counts by port");
            throw;
        }
    }

    public async Task PurgeOldEventsAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await _context.ThreatEvents
                .Where(e => e.Timestamp < before)
                .ExecuteDeleteAsync(cancellationToken);

            if (count > 0)
                _logger.LogInformation("Purged {Count} old threat events before {Before}", count, before);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge old threat events");
            throw;
        }
    }

    public async Task<int> BackfillGeoDataAsync(Action<List<ThreatEvent>> enrichAction,
        int batchSize = 1000, CancellationToken cancellationToken = default)
    {
        try
        {
            // Get events with null geo data (tracked so changes are saved)
            var events = await _context.ThreatEvents
                .Where(e => e.CountryCode == null)
                .OrderByDescending(e => e.Timestamp)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            if (events.Count == 0) return 0;

            enrichAction(events);
            await _context.SaveChangesAsync(cancellationToken);
            return events.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backfill geo data");
            return 0;
        }
    }

    #endregion

    #region Attack Sequences

    public async Task<List<AttackSequence>> GetAttackSequencesAsync(DateTime from, DateTime to,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            // Step 1: Find source IPs with 2+ distinct kill chain stages (SQL-side filtering)
            var candidateIps = await BaseQuery(from, to)
                .GroupBy(e => e.SourceIp)
                .Where(g => g.Select(e => e.KillChainStage).Distinct().Count() >= 2)
                .OrderByDescending(g => g.Max(e => e.Timestamp))
                .Take(limit)
                .Select(g => g.Key)
                .ToListAsync(cancellationToken);

            if (candidateIps.Count == 0) return [];

            // Step 2: Load only events for those IPs (bounded set)
            var events = await BaseQuery(from, to)
                .Where(e => candidateIps.Contains(e.SourceIp))
                .Select(e => new
                {
                    e.SourceIp,
                    e.KillChainStage,
                    e.Timestamp,
                    e.SignatureName,
                    e.CountryCode,
                    e.AsnOrg
                })
                .ToListAsync(cancellationToken);

            return events
                .GroupBy(e => e.SourceIp)
                .OrderByDescending(g => g.Max(e => e.Timestamp))
                .Select(g => new AttackSequence
                {
                    SourceIp = g.Key,
                    CountryCode = g.First().CountryCode,
                    AsnOrg = g.First().AsnOrg,
                    Stages = g
                        .GroupBy(e => e.KillChainStage)
                        .OrderBy(sg => (int)sg.Key)
                        .Select(sg => new SequenceStage
                        {
                            Stage = sg.Key,
                            FirstSeen = sg.Min(e => e.Timestamp),
                            LastSeen = sg.Max(e => e.Timestamp),
                            EventCount = sg.Count(),
                            TopSignature = sg.GroupBy(e => e.SignatureName)
                                .OrderByDescending(ng => ng.Count())
                                .Select(ng => ng.Key)
                                .FirstOrDefault() ?? ""
                        })
                        .ToList()
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get attack sequences");
            throw;
        }
    }

    #endregion

    #region Threat Patterns

    public async Task SavePatternAsync(ThreatPattern pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            // Dedup: if a pattern with the same type + source IPs was detected in the last hour, update it
            var cutoff = DateTime.UtcNow.AddHours(-1);
            var existing = await _context.ThreatPatterns
                .FirstOrDefaultAsync(p =>
                    p.PatternType == pattern.PatternType &&
                    p.SourceIpsJson == pattern.SourceIpsJson &&
                    p.DetectedAt >= cutoff, cancellationToken);

            if (existing != null)
            {
                existing.EventCount = pattern.EventCount;
                existing.DetectedAt = pattern.DetectedAt;
                existing.Description = pattern.Description;
                existing.LastSeen = pattern.LastSeen;
                _logger.LogDebug("Updated existing pattern {Id}: {Type}", existing.Id, existing.PatternType);
            }
            else
            {
                _context.ThreatPatterns.Add(pattern);
                _logger.LogInformation("Saved new threat pattern: {Type} with {Count} events",
                    pattern.PatternType, pattern.EventCount);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save threat pattern");
            throw;
        }
    }

    public async Task<List<ThreatPattern>> GetPatternsAsync(DateTime from, DateTime to,
        PatternType? type = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.ThreatPatterns
                .AsNoTracking()
                .Where(p => p.DetectedAt >= from && p.DetectedAt <= to);

            if (type.HasValue)
                query = query.Where(p => p.PatternType == type.Value);

            return await query
                .OrderByDescending(p => p.DetectedAt)
                .Take(limit)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat patterns");
            throw;
        }
    }

    #endregion

    #region CrowdSec Cache

    public async Task<CrowdSecReputation?> GetCrowdSecCacheAsync(string ip,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.CrowdSecReputations
                .FirstOrDefaultAsync(r => r.Ip == ip && r.ExpiresAt > DateTime.UtcNow, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get CrowdSec cache for {Ip}", ip);
            throw;
        }
    }

    public async Task SaveCrowdSecCacheAsync(CrowdSecReputation reputation,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.CrowdSecReputations
                .FirstOrDefaultAsync(r => r.Ip == reputation.Ip, cancellationToken);

            if (existing != null)
            {
                existing.ReputationJson = reputation.ReputationJson;
                existing.FetchedAt = reputation.FetchedAt;
                existing.ExpiresAt = reputation.ExpiresAt;
            }
            else
            {
                _context.CrowdSecReputations.Add(reputation);
            }

            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save CrowdSec cache for {Ip}", reputation.Ip);
            throw;
        }
    }

    public async Task PurgeCrowdSecCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await _context.CrowdSecReputations
                .Where(r => r.ExpiresAt < DateTime.UtcNow)
                .ExecuteDeleteAsync(cancellationToken);

            if (count > 0)
                _logger.LogDebug("Purged {Count} expired CrowdSec cache entries", count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge CrowdSec cache");
            throw;
        }
    }

    #endregion

    #region Noise Filters

    public async Task<List<ThreatNoiseFilter>> GetNoiseFiltersAsync(CancellationToken cancellationToken = default)
    {
        return await _context.ThreatNoiseFilters
            .AsNoTracking()
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task SaveNoiseFilterAsync(ThreatNoiseFilter filter, CancellationToken cancellationToken = default)
    {
        _context.ThreatNoiseFilters.Add(filter);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Saved noise filter: {Description}", filter.Description);
    }

    public async Task DeleteNoiseFilterAsync(int filterId, CancellationToken cancellationToken = default)
    {
        await _context.ThreatNoiseFilters
            .Where(f => f.Id == filterId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task ToggleNoiseFilterAsync(int filterId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _context.ThreatNoiseFilters
            .Where(f => f.Id == filterId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Enabled, enabled), cancellationToken);
    }

    #endregion
}

/// <summary>
/// Internal DTO for mapping raw SQL timeline query results.
/// </summary>
internal class TimelineBucketRaw
{
    public string HourStr { get; set; } = string.Empty;
    public int Severity1 { get; set; }
    public int Severity2 { get; set; }
    public int Severity3 { get; set; }
    public int Severity4 { get; set; }
    public int Severity5 { get; set; }
    public int Total { get; set; }
}
