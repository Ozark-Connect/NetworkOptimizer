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
    /// </summary>
    public async Task<CrowdSecIpInfo?> GetReputationAsync(
        string ipAddress,
        string apiKey,
        IThreatRepository repository,
        int cacheTtlHours = 24,
        CancellationToken cancellationToken = default)
    {
        // Check cache first
        var cached = await repository.GetCrowdSecCacheAsync(ipAddress, cancellationToken);
        if (cached != null)
        {
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
        if (result == null) return null;

        // Cache result
        try
        {
            var reputation = new CrowdSecReputation
            {
                Ip = ipAddress,
                ReputationJson = JsonSerializer.Serialize(result),
                FetchedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(cacheTtlHours)
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
