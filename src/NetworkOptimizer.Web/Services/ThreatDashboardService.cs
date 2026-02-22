using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Services;
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
    private readonly IThreatSettingsAccessor _settingsAccessor;
    private readonly ICredentialProtectionService _credentialService;
    private readonly ILogger<ThreatDashboardService> _logger;

    // Cached noise filters (loaded once per service scope, i.e., per request)
    private List<ThreatNoiseFilter>? _activeFilters;

    public ThreatDashboardService(
        IThreatRepository repository,
        ExposureValidator exposureValidator,
        CrowdSecEnrichmentService crowdSecService,
        IUniFiClientAccessor uniFiClientAccessor,
        IThreatSettingsAccessor settingsAccessor,
        ICredentialProtectionService credentialService,
        ILogger<ThreatDashboardService> logger)
    {
        _repository = repository;
        _exposureValidator = exposureValidator;
        _crowdSecService = crowdSecService;
        _uniFiClientAccessor = uniFiClientAccessor;
        _settingsAccessor = settingsAccessor;
        _credentialService = credentialService;
        _logger = logger;
    }

    public async Task<ThreatDashboardData> GetDashboardDataAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ApplyNoiseFiltersToRepository(cancellationToken);
            var summary = await _repository.GetThreatSummaryAsync(from, to, cancellationToken);
            var killChain = await _repository.GetKillChainDistributionAsync(from, to, cancellationToken);
            var topSources = await _repository.GetTopSourcesAsync(from, to, 10, cancellationToken);
            var topPorts = await _repository.GetTopTargetedPortsAsync(from, to, 10, cancellationToken);
            var patterns = await _repository.GetPatternsAsync(from, to, limit: 20, cancellationToken: cancellationToken);

            // Enrich top sources with CrowdSec CTI reputation (cached 24h, ~10 API calls max)
            await EnrichTopSourcesWithCtiAsync(topSources, cancellationToken);

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

    /// <summary>
    /// Auto-enrich top sources with CrowdSec CTI only if quota is generous (>= 100/day).
    /// With 24h cache, this typically costs 0-10 API calls per dashboard load.
    /// </summary>
    private async Task EnrichTopSourcesWithCtiAsync(List<SourceIpSummary> sources,
        CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = await GetDecryptedApiKeyAsync(cancellationToken);
            if (apiKey == null) return;

            var quotaStr = await _settingsAccessor.GetSettingAsync("crowdsec.daily_quota", cancellationToken);
            var quota = int.TryParse(quotaStr, out var q) ? q : 30; // free tier default
            if (quota < 100) return; // low quota - user must enrich manually

            await EnrichSourcesAsync(sources, apiKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrowdSec CTI auto-enrichment failed");
        }
    }

    /// <summary>
    /// Look up CrowdSec CTI reputation for a single IP. Called by dashboard for manual lookups.
    /// </summary>
    public async Task<SourceIpSummary?> EnrichSingleSourceAsync(SourceIpSummary source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var apiKey = await GetDecryptedApiKeyAsync(cancellationToken);
            if (apiKey == null) return null;

            await EnrichSourcesAsync([source], apiKey, cancellationToken);
            return source;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to look up reputation for {Ip}", source.SourceIp);
            return null;
        }
    }

    private async Task<string?> GetDecryptedApiKeyAsync(CancellationToken cancellationToken)
    {
        var stored = await _settingsAccessor.GetSettingAsync("crowdsec.api_key", cancellationToken);
        if (string.IsNullOrWhiteSpace(stored)) return null;
        return _credentialService.IsEncrypted(stored) ? _credentialService.Decrypt(stored) : stored;
    }

    private async Task EnrichSourcesAsync(List<SourceIpSummary> sources, string apiKey,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            if (NetworkUtilities.IsPrivateIpAddress(source.SourceIp)) continue;

            try
            {
                var info = await _crowdSecService.GetReputationAsync(
                    source.SourceIp, apiKey, _repository, cancellationToken: cancellationToken);

                source.CrowdSecReputation = CrowdSecEnrichmentService.GetReputationBadge(info);
                source.ThreatScore = CrowdSecEnrichmentService.GetThreatScore(info);
                source.TopBehaviors = info?.Behaviors.Count > 0
                    ? string.Join(", ", info.Behaviors.Take(3).Select(b => b.Label))
                    : null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enrich {Ip} with CrowdSec CTI", source.SourceIp);
            }
        }
    }


    public async Task<List<TimelineBucket>> GetTimelineDataAsync(DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ApplyNoiseFiltersToRepository(cancellationToken);
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
            await ApplyNoiseFiltersToRepository(cancellationToken);
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
            await ApplyNoiseFiltersToRepository(cancellationToken);
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
        int cacheTtlHours = 720, CancellationToken cancellationToken = default)
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
            await ApplyNoiseFiltersToRepository(cancellationToken);
            return await _repository.GetAttackSequencesAsync(from, to, 50, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get attack sequences");
            return [];
        }
    }

    public async Task<IpDrilldownData> GetIpDrilldownAsync(string ip, DateTime from, DateTime to,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await ApplyNoiseFiltersToRepository(cancellationToken);
            var events = await _repository.GetEventsByIpAsync(ip, from, to, cancellationToken: cancellationToken);

            var asSource = events.Where(e => e.SourceIp == ip).ToList();
            var asDest = events.Where(e => e.DestIp == ip).ToList();

            // Peer groups: destinations when IP is source
            var destinations = asSource
                .GroupBy(e => e.DestIp)
                .Select(g => BuildPeerGroup(g.Key, g.ToList()))
                .OrderByDescending(p => p.EventCount)
                .ToList();

            // Peer groups: sources when IP is destination
            var sources = asDest
                .GroupBy(e => e.SourceIp)
                .Select(g => BuildPeerGroup(g.Key, g.ToList()))
                .OrderByDescending(p => p.EventCount)
                .ToList();

            // Port range breakdown (all events involving this IP)
            var portGroups = events
                .GroupBy(e => e.DestPort)
                .OrderByDescending(g => g.Count())
                .Select(g => new PortRangeGroup
                {
                    Port = g.Key,
                    Service = GetServiceName(g.Key),
                    EventCount = g.Count(),
                    BlockedCount = g.Count(e => e.Action == ThreatAction.Blocked),
                    DetectedCount = g.Count(e => e.Action != ThreatAction.Blocked)
                })
                .ToList();

            // Collapse consecutive ports into ranges
            var portRanges = CollapsePortRanges(portGroups);

            // Top signatures
            var signatures = events
                .GroupBy(e => e.SignatureName)
                .Where(g => !string.IsNullOrEmpty(g.Key))
                .Select(g => new SignatureGroup
                {
                    Name = g.Key,
                    Category = g.First().Category,
                    EventCount = g.Count(),
                    MaxSeverity = g.Max(e => e.Severity)
                })
                .OrderByDescending(s => s.EventCount)
                .Take(20)
                .ToList();

            return new IpDrilldownData
            {
                Ip = ip,
                TotalEvents = events.Count,
                BlockedCount = events.Count(e => e.Action == ThreatAction.Blocked),
                DetectedCount = events.Count(e => e.Action != ThreatAction.Blocked),
                AsSourceCount = asSource.Count,
                AsDestCount = asDest.Count,
                FirstSeen = events.Count > 0 ? events.Min(e => e.Timestamp) : (DateTime?)null,
                LastSeen = events.Count > 0 ? events.Max(e => e.Timestamp) : (DateTime?)null,
                Destinations = destinations,
                Sources = sources,
                PortRanges = portRanges,
                TopSignatures = signatures
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get IP drilldown for {Ip}", ip);
            return new IpDrilldownData { Ip = ip };
        }
    }

    private IpPeerGroup BuildPeerGroup(string peerIp, List<ThreatEvent> events)
    {
        var ports = events.Select(e => e.DestPort).Distinct().OrderBy(p => p).ToList();
        var portRangesStr = FormatPortRanges(ports);
        var services = events
            .Select(e => e.Service)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        return new IpPeerGroup
        {
            Ip = peerIp,
            Domain = events.FirstOrDefault(e => !string.IsNullOrEmpty(e.Domain))?.Domain,
            PortRanges = portRangesStr,
            Services = services.Count > 0 ? string.Join(", ", services) : null,
            EventCount = events.Count,
            BlockedCount = events.Count(e => e.Action == ThreatAction.Blocked)
        };
    }

    private static string FormatPortRanges(List<int> sortedPorts)
    {
        if (sortedPorts.Count == 0) return "-";

        var ranges = new List<string>();
        var start = sortedPorts[0];
        var end = start;

        for (var i = 1; i < sortedPorts.Count; i++)
        {
            if (sortedPorts[i] == end + 1)
            {
                end = sortedPorts[i];
            }
            else
            {
                ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
                start = sortedPorts[i];
                end = start;
            }
        }
        ranges.Add(start == end ? start.ToString() : $"{start}-{end}");

        return string.Join(", ", ranges);
    }

    private static List<PortRangeGroup> CollapsePortRanges(List<PortRangeGroup> portGroups)
    {
        if (portGroups.Count == 0) return portGroups;

        var sorted = portGroups.OrderBy(p => p.Port).ToList();
        var result = new List<PortRangeGroup>();
        var current = sorted[0];

        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Port == current.Port + 1 && string.IsNullOrEmpty(current.Service) == string.IsNullOrEmpty(sorted[i].Service))
            {
                // Merge into range
                current = new PortRangeGroup
                {
                    Port = current.Port,
                    PortEnd = sorted[i].PortEnd > 0 ? sorted[i].PortEnd : sorted[i].Port,
                    Service = current.Service ?? sorted[i].Service,
                    EventCount = current.EventCount + sorted[i].EventCount,
                    BlockedCount = current.BlockedCount + sorted[i].BlockedCount,
                    DetectedCount = current.DetectedCount + sorted[i].DetectedCount
                };
            }
            else
            {
                result.Add(current);
                current = sorted[i];
            }
        }
        result.Add(current);
        return result.OrderByDescending(r => r.EventCount).ToList();
    }

    private async Task ApplyNoiseFiltersToRepository(CancellationToken cancellationToken)
    {
        var filters = await GetActiveFiltersAsync(cancellationToken);
        _repository.SetNoiseFilters(filters);
    }

    // --- Noise Filter Management ---

    public async Task<List<ThreatNoiseFilter>> GetNoiseFiltersAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.GetNoiseFiltersAsync(cancellationToken);
    }

    public async Task SaveNoiseFilterAsync(ThreatNoiseFilter filter, CancellationToken cancellationToken = default)
    {
        await _repository.SaveNoiseFilterAsync(filter, cancellationToken);
        _activeFilters = null; // Invalidate cache
    }

    public async Task DeleteNoiseFilterAsync(int filterId, CancellationToken cancellationToken = default)
    {
        await _repository.DeleteNoiseFilterAsync(filterId, cancellationToken);
        _activeFilters = null;
    }

    public async Task ToggleNoiseFilterAsync(int filterId, bool enabled, CancellationToken cancellationToken = default)
    {
        await _repository.ToggleNoiseFilterAsync(filterId, enabled, cancellationToken);
        _activeFilters = null;
    }

    private async Task<List<ThreatNoiseFilter>> GetActiveFiltersAsync(CancellationToken cancellationToken = default)
    {
        _activeFilters ??= (await _repository.GetNoiseFiltersAsync(cancellationToken))
            .Where(f => f.Enabled).ToList();
        return _activeFilters;
    }

    /// <summary>
    /// Apply noise filters to a list of events, removing matches.
    /// </summary>
    private List<ThreatEvent> ApplyNoiseFilters(List<ThreatEvent> events, List<ThreatNoiseFilter> filters)
    {
        if (filters.Count == 0) return events;
        return events.Where(e => !filters.Any(f => MatchesFilter(e, f))).ToList();
    }

    private static bool MatchesFilter(ThreatEvent evt, ThreatNoiseFilter filter)
    {
        if (filter.SourceIp != null && evt.SourceIp != filter.SourceIp) return false;
        if (filter.DestIp != null && evt.DestIp != filter.DestIp) return false;
        if (filter.DestPort != null && evt.DestPort != filter.DestPort) return false;
        return true; // All non-null fields matched (null = wildcard)
    }

    private static string GetServiceName(int port)
    {
        return port switch
        {
            21 => "FTP", 22 => "SSH", 23 => "Telnet", 25 => "SMTP", 53 => "DNS",
            80 => "HTTP", 443 => "HTTPS", 445 => "SMB", 993 => "IMAPS", 1433 => "MSSQL",
            1883 => "MQTT", 3306 => "MySQL", 3389 => "RDP", 5432 => "PostgreSQL",
            5900 => "VNC", 6379 => "Redis", 8080 => "HTTP-Alt", 8443 => "HTTPS-Alt",
            27017 => "MongoDB", _ => ""
        };
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

/// <summary>
/// All data for the IP drill-down view.
/// </summary>
public class IpDrilldownData
{
    public string Ip { get; set; } = string.Empty;
    public int TotalEvents { get; set; }
    public int BlockedCount { get; set; }
    public int DetectedCount { get; set; }
    public int AsSourceCount { get; set; }
    public int AsDestCount { get; set; }
    public DateTime? FirstSeen { get; set; }
    public DateTime? LastSeen { get; set; }
    public List<IpPeerGroup> Destinations { get; set; } = [];
    public List<IpPeerGroup> Sources { get; set; } = [];
    public List<PortRangeGroup> PortRanges { get; set; } = [];
    public List<SignatureGroup> TopSignatures { get; set; } = [];
}

/// <summary>
/// A peer IP group within drill-down (destination or source).
/// </summary>
public class IpPeerGroup
{
    public string Ip { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string PortRanges { get; set; } = "-";
    public string? Services { get; set; }
    public int EventCount { get; set; }
    public int BlockedCount { get; set; }
}

/// <summary>
/// Port or port range with event counts.
/// </summary>
public class PortRangeGroup
{
    public int Port { get; set; }
    public int PortEnd { get; set; }
    public string Service { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int BlockedCount { get; set; }
    public int DetectedCount { get; set; }

    public string RangeLabel => PortEnd > 0 && PortEnd != Port ? $"{Port}-{PortEnd}" : Port.ToString();
}

/// <summary>
/// Signature aggregation within drill-down.
/// </summary>
public class SignatureGroup
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int MaxSeverity { get; set; }
}
