using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.CrowdSec;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Scoped service providing aggregated data for the Threat Intelligence Dashboard.
/// </summary>
public class ThreatDashboardService
{
    private readonly IThreatRepository _repository;
    private readonly ExposureValidator _exposureValidator;
    private readonly CrowdSecEnrichmentService _crowdSecService;
    private readonly IUniFiClientAccessor _uniFiClientAccessor;
    private readonly ILogger<ThreatDashboardService> _logger;

    public ThreatDashboardService(
        IThreatRepository repository,
        ExposureValidator exposureValidator,
        CrowdSecEnrichmentService crowdSecService,
        IUniFiClientAccessor uniFiClientAccessor,
        ILogger<ThreatDashboardService> logger)
    {
        _repository = repository;
        _exposureValidator = exposureValidator;
        _crowdSecService = crowdSecService;
        _uniFiClientAccessor = uniFiClientAccessor;
        _logger = logger;
    }

    public async Task<ThreatDashboardData> GetDashboardDataAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await _repository.GetThreatSummaryAsync(from, to, cancellationToken);
            var killChain = await _repository.GetKillChainDistributionAsync(from, to, cancellationToken);
            var topSources = await _repository.GetTopSourcesAsync(from, to, 10, cancellationToken);
            var topPorts = await _repository.GetTopTargetedPortsAsync(from, to, 10, cancellationToken);
            var patterns = await _repository.GetPatternsAsync(from, to, limit: 20, cancellationToken: cancellationToken);

            return new ThreatDashboardData
            {
                Summary = summary,
                KillChainDistribution = killChain,
                TopSources = topSources,
                TopTargetedPorts = topPorts,
                RecentPatterns = patterns
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat dashboard data");
            return new ThreatDashboardData();
        }
    }

    public async Task<List<TimelineBucket>> GetTimelineDataAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.GetTimelineAsync(from, to, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat timeline");
            return [];
        }
    }

    public async Task<Dictionary<string, int>> GetGeoDistributionAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.GetCountryDistributionAsync(from, to, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get geo distribution");
            return new();
        }
    }

    public async Task<ExposureReport> GetExposureReportAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Auto-fetch port forward rules from UniFi API
            List<UniFiPortForwardRule>? portForwardRules = null;
            var apiClient = _uniFiClientAccessor.Client;
            if (apiClient != null)
            {
                try
                {
                    portForwardRules = await apiClient.GetPortForwardRulesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch port forward rules for exposure report");
                }
            }

            var from = DateTime.UtcNow.AddDays(-30);
            var to = DateTime.UtcNow;
            return await _exposureValidator.ValidateAsync(portForwardRules, _repository, from, to, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get exposure report");
            return new ExposureReport();
        }
    }

    public async Task<List<ThreatEvent>> GetRecentEventsAsync(int limit = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.GetEventsAsync(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow,
                limit: limit, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent events");
            return [];
        }
    }

    public async Task<CrowdSecIpInfo?> GetCrowdSecReputationAsync(string ip, string apiKey,
        int cacheTtlHours = 24, CancellationToken cancellationToken = default)
    {
        return await _crowdSecService.GetReputationAsync(ip, apiKey, _repository, cacheTtlHours, cancellationToken);
    }

    /// <summary>
    /// Lightweight hourly totals for sparkline display on the main dashboard.
    /// </summary>
    public async Task<(int TotalCount, List<ThreatTrendPoint> Points)> GetThreatTrendAsync(
        int hours = 24, CancellationToken cancellationToken = default)
    {
        try
        {
            var from = DateTime.UtcNow.AddHours(-hours);
            var to = DateTime.UtcNow;
            var timeline = await _repository.GetTimelineAsync(from, to, cancellationToken);
            var total = timeline.Sum(b => b.Total);
            var points = timeline.Select(b => new ThreatTrendPoint
            {
                Hour = DateTime.SpecifyKind(b.Hour, DateTimeKind.Utc),
                Count = b.Total
            }).ToList();
            return (total, points);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get threat trend");
            return (0, []);
        }
    }

    public async Task<List<AttackSequence>> GetAttackSequencesAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.GetAttackSequencesAsync(from, to, 50, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get attack sequences");
            return [];
        }
    }
}

/// <summary>
/// Aggregated dashboard data DTO.
/// </summary>
public class ThreatDashboardData
{
    public ThreatSummary Summary { get; set; } = new();
    public Dictionary<KillChainStage, int> KillChainDistribution { get; set; } = new();
    public List<SourceIpSummary> TopSources { get; set; } = [];
    public List<TargetPortSummary> TopTargetedPorts { get; set; } = [];
    public List<ThreatPattern> RecentPatterns { get; set; } = [];
}

/// <summary>
/// Single data point for the threat trend sparkline.
/// </summary>
public record ThreatTrendPoint
{
    public DateTime Hour { get; init; }
    public int Count { get; init; }
}
