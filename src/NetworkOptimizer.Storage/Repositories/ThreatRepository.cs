using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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

    public ThreatRepository(NetworkOptimizerDbContext context, ILogger<ThreatRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            var query = _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to);

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
            var events = _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to);

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
            return await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
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
            return await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
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
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to && e.CountryCode != null)
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
            var buckets = await _context.Database
                .SqlQueryRaw<TimelineBucketRaw>(
                    """
                    SELECT strftime('%Y-%m-%d %H:00:00', Timestamp) AS HourStr,
                           SUM(CASE WHEN Severity = 1 THEN 1 ELSE 0 END) AS Severity1,
                           SUM(CASE WHEN Severity = 2 THEN 1 ELSE 0 END) AS Severity2,
                           SUM(CASE WHEN Severity = 3 THEN 1 ELSE 0 END) AS Severity3,
                           SUM(CASE WHEN Severity = 4 THEN 1 ELSE 0 END) AS Severity4,
                           SUM(CASE WHEN Severity = 5 THEN 1 ELSE 0 END) AS Severity5,
                           COUNT(*) AS Total
                    FROM ThreatEvents
                    WHERE Timestamp >= {0} AND Timestamp <= {1}
                    GROUP BY strftime('%Y-%m-%d %H:00:00', Timestamp)
                    ORDER BY HourStr
                    """, from, to)
                .ToListAsync(cancellationToken);

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
            var results = await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
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
            var results = await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
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

    #endregion

    #region Attack Sequences

    public async Task<List<AttackSequence>> GetAttackSequencesAsync(DateTime from, DateTime to,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            // Step 1: Find source IPs with 2+ distinct kill chain stages (SQL-side filtering)
            var candidateIps = await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
                .GroupBy(e => e.SourceIp)
                .Where(g => g.Select(e => e.KillChainStage).Distinct().Count() >= 2)
                .OrderByDescending(g => g.Max(e => e.Timestamp))
                .Take(limit)
                .Select(g => g.Key)
                .ToListAsync(cancellationToken);

            if (candidateIps.Count == 0) return [];

            // Step 2: Load only events for those IPs (bounded set)
            var events = await _context.ThreatEvents
                .AsNoTracking()
                .Where(e => e.Timestamp >= from && e.Timestamp <= to && candidateIps.Contains(e.SourceIp))
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
