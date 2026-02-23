using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.CrowdSec;

/// <summary>
/// Orchestrates CrowdSec enrichment with caching and rate-limit awareness.
/// </summary>
public class CrowdSecEnrichmentService
{
    private readonly CrowdSecClient _client;
    private readonly ILogger<CrowdSecEnrichmentService> _logger;

    public CrowdSecEnrichmentService(
        CrowdSecClient client,
        ILogger<CrowdSecEnrichmentService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Get reputation for an IP, checking cache first.
    /// Positive hits are cached for <paramref name="cacheTtlHours"/> (default 720 = 30 days).
    /// Negative hits (IP not in CrowdSec DB) are cached for 24 hours to avoid wasting API calls.
    /// </summary>
    public async Task<CrowdSecIpInfo?> GetReputationAsync(
        string ipAddress,
        string apiKey,
        IThreatRepository repository,
        int cacheTtlHours = 720,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cached = await repository.GetCrowdSecCacheAsync(ipAddress, cancellationToken);
        if (cached != null)
        {
            // Negative cache entry - API previously returned null for this IP
            if (cached.ReputationJson == "null")
                return null;

            try
            {
                return JsonSerializer.Deserialize<CrowdSecIpInfo>(cached.ReputationJson);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Failed to deserialize cached CrowdSec data for {Ip}", ipAddress);
            }
        }

        // Query API
        var result = await _client.GetIpReputationAsync(ipAddress, apiKey, cancellationToken);

        // Cache result (positive or negative)
        try
        {
            var reputation = new CrowdSecReputation
            {
                Ip = ipAddress,
                ReputationJson = result != null ? JsonSerializer.Serialize(result) : "null",
                FetchedAt = DateTime.UtcNow,
                ExpiresAt = result != null
                    ? DateTime.UtcNow.AddHours(cacheTtlHours)   // positive: 30 days
                    : DateTime.UtcNow.AddHours(24)               // negative: 24 hours
            };
            await repository.SaveCrowdSecCacheAsync(reputation, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache CrowdSec data for {Ip}", ipAddress);
        }

        return result;
    }

    /// <summary>
    /// Get a summary badge for display in the dashboard.
    /// </summary>
    public static string GetReputationBadge(CrowdSecIpInfo? info)
    {
        if (info == null) return "unknown";

        return info.Reputation?.ToLowerInvariant() switch
        {
            "malicious" => "malicious",
            "suspicious" => "suspicious",
            "known" => "known",
            "safe" => "safe",
            _ => "unknown"
        };
    }

    /// <summary>
    /// Get the overall threat score (0-5).
    /// </summary>
    public static int GetThreatScore(CrowdSecIpInfo? info)
    {
        if (info?.Scores?.Overall == null) return 0;
        return info.Scores.Overall.Total switch
        {
            >= 4 => 5,
            3 => 4,
            2 => 3,
            1 => 2,
            _ => 1
        };
    }
}
