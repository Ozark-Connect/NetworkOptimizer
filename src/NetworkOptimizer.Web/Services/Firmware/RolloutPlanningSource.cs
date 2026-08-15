using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>The site data a plan is built from, frozen at plan time.</summary>
public class RolloutPlanningContext
{
    /// <summary>Every adopted device, upgradable or not.</summary>
    public IReadOnlyList<PlannerDevice> Devices { get; init; } = [];

    /// <summary>Null when neither AP placements nor roaming history exist (uniform-density fallback).</summary>
    public IApNeighborOracle? Neighbors { get; init; }

    /// <summary>Wireless clients, for the home/business heuristic when there is no usage history.</summary>
    public int ClientCount { get; init; }

    /// <summary>Whether the console answered. Nothing can be planned against a dark console.</summary>
    public bool ConsoleConnected { get; init; }

    /// <summary>The site's own timezone, IANA form, as the console reports it. Null falls back to this server's.</summary>
    public string? TimeZoneId { get; init; }
}

/// <summary>
/// Everything a plan needs from the live site, behind one seam: the device snapshot, the AP
/// neighbor oracle, the quiet-window proposal, and the prior-version image URLs a rollback needs.
/// Kept separate from the service so plan composition can be tested without a console, a floor
/// plan, or the release feed.
/// </summary>
public interface IRolloutPlanningSource
{
    /// <summary>Freezes the site's topology and client load for planning.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RolloutPlanningContext> GetContextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Composes the downtime estimator for this site: its own learned timings, filled in from the
    /// other sites' where this one has too little history of a model to estimate from.
    /// </summary>
    /// <param name="siteTimings">This site's learned model timings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FirmwareTimingEstimator> GetEstimatorAsync(
        IReadOnlyList<FirmwareModelTiming> siteTimings, CancellationToken cancellationToken = default);

    /// <summary>Proposes the start window for a rollout of the given estimated length.</summary>
    /// <param name="context">The planning context the estimate was made against.</param>
    /// <param name="estimatedSeconds">How long the rollout is expected to take.</param>
    /// <param name="settings">Settings the plan was built from (pinned windows live here).</param>
    /// <param name="minLead">Least notice the window must leave.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<QuietWindowProposal> ProposeWindowAsync(
        RolloutPlanningContext context,
        int estimatedSeconds,
        FirmwareRolloutSettings settings,
        TimeSpan minLead,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves each step's current firmware to a direct image URL and records it on the plan, so a
    /// rollback has somewhere to read it from after the device has moved on.
    /// </summary>
    /// <param name="document">Plan document to fill.</param>
    /// <param name="steps">The plan's steps.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PopulatePriorVersionsAsync(
        RolloutPlanDocument document,
        IEnumerable<FirmwareRolloutStep> steps,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The real planning source: the site's console for topology, the AP map and propagation model
/// (corroborated by UniFi roaming edges) for the neighbor oracle, monitoring history for the quiet
/// window, and Ubiquiti's public feed for rollback images.
/// </summary>
public class RolloutPlanningSource : IRolloutPlanningSource
{
    /// <summary>Band the coverage question is asked on: 5 GHz is where roaming decisions are made.</summary>
    private const string NeighborBand = "5";

    private readonly SiteConnectionRegistry _siteConnections;
    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly HeatmapDataCache _heatmapCache;
    private readonly FloorPlanService _floorPlans;
    private readonly ApMapService _apMap;
    private readonly PlannedApService _plannedAps;
    private readonly PropagationService _propagation;
    private readonly WiFiOptimizerService _wifi;
    private readonly UbiquitiReleaseFeedClient _feed;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RolloutPlanningSource> _logger;
    private readonly string _siteSlug;

    /// <param name="siteConnections">Per-site console connections.</param>
    /// <param name="influxRegistry">Per-site monitoring history.</param>
    /// <param name="siteContext">Site in context.</param>
    /// <param name="heatmapCache">Cached AP markers, walls and buildings.</param>
    /// <param name="floorPlans">Floor plans behind the cache.</param>
    /// <param name="apMap">AP placements behind the cache.</param>
    /// <param name="plannedAps">Planned APs behind the cache.</param>
    /// <param name="propagation">Propagation model for AP overlap.</param>
    /// <param name="wifi">Roaming topology and wireless client counts.</param>
    /// <param name="feed">Ubiquiti public release feed.</param>
    /// <param name="mainDbFactory">Main database, for the site registry behind cross-site timings.</param>
    /// <param name="siteDbFactory">Per-site databases, for the other sites' learned timings.</param>
    /// <param name="loggerFactory">Logger factory (the quiet-window service takes its own).</param>
    /// <param name="logger">Logger.</param>
    public RolloutPlanningSource(
        SiteConnectionRegistry siteConnections,
        MonitoringInfluxRegistry influxRegistry,
        SiteContextService siteContext,
        HeatmapDataCache heatmapCache,
        FloorPlanService floorPlans,
        ApMapService apMap,
        PlannedApService plannedAps,
        PropagationService propagation,
        WiFiOptimizerService wifi,
        UbiquitiReleaseFeedClient feed,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        SiteDbContextFactory siteDbFactory,
        ILoggerFactory loggerFactory,
        ILogger<RolloutPlanningSource> logger)
    {
        _siteConnections = siteConnections;
        _influxRegistry = influxRegistry;
        _heatmapCache = heatmapCache;
        _floorPlans = floorPlans;
        _apMap = apMap;
        _plannedAps = plannedAps;
        _propagation = propagation;
        _wifi = wifi;
        _feed = feed;
        _mainDbFactory = mainDbFactory;
        _siteDbFactory = siteDbFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteContext.Slug) ? SiteManagementService.DefaultSiteSlug : siteContext.Slug;
    }

    /// <inheritdoc />
    public async Task<RolloutPlanningContext> GetContextAsync(CancellationToken cancellationToken = default)
    {
        var connection = _siteConnections.GetFor(_siteSlug);
        var discovered = await connection.GetDiscoveredDevicesAsync(cancellationToken);
        var devices = RolloutSnapshotBuilder.FromDevices(discovered);

        return new RolloutPlanningContext
        {
            Devices = devices,
            Neighbors = await BuildNeighborOracleAsync(devices, cancellationToken),
            ClientCount = await CountWirelessClientsAsync(),
            ConsoleConnected = connection.IsConnected,
            TimeZoneId = await ReadConsoleTimeZoneAsync(connection, cancellationToken),
        };
    }

    /// <summary>
    /// The site's timezone as the console reports it. Hours of the week are meaningless in the
    /// server's timezone when the site is somewhere else.
    /// </summary>
    private async Task<string?> ReadConsoleTimeZoneAsync(
        UniFiConnectionService connection, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.Client == null) return null;
            var console = await connection.Client.GetConsoleSystemInfoAsync(cancellationToken);
            return console?.TimeZone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the console timezone for site {Site}", _siteSlug);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<FirmwareTimingEstimator> GetEstimatorAsync(
        IReadOnlyList<FirmwareModelTiming> siteTimings, CancellationToken cancellationToken = default)
    {
        var crossSite = new CrossSiteTimingSource(_mainDbFactory, _siteDbFactory, _logger, _siteSlug);
        return new FirmwareTimingEstimator(await crossSite.MergeAsync(siteTimings ?? [], cancellationToken));
    }

    /// <inheritdoc />
    public Task<QuietWindowProposal> ProposeWindowAsync(
        RolloutPlanningContext context,
        int estimatedSeconds,
        FirmwareRolloutSettings settings,
        TimeSpan minLead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var quietWindows = new QuietWindowService(
            _influxRegistry,
            _loggerFactory.CreateLogger<QuietWindowService>(),
            _siteSlug,
            consoleTimeZoneId: context.TimeZoneId);

        return quietWindows.ProposeAsync(
            context.Devices, estimatedSeconds, settings, context.ClientCount, minLead, cancellationToken);
    }

    /// <inheritdoc />
    public Task PopulatePriorVersionsAsync(
        RolloutPlanDocument document,
        IEnumerable<FirmwareRolloutStep> steps,
        CancellationToken cancellationToken = default)
        => RollbackUrlCache.PopulateAsync(document, steps, _feed, cancellationToken);

    /// <summary>
    /// Pairs of APs that must not go down together. Placements answer it properly (propagation
    /// overlap); a roaming edge with real attempts forces the pair regardless, because clients have
    /// demonstrably moved between them. With neither, the oracle is null and the planner falls back
    /// to its uniform-density assumption.
    /// </summary>
    private async Task<IApNeighborOracle?> BuildNeighborOracleAsync(
        IReadOnlyList<PlannerDevice> devices, CancellationToken cancellationToken)
    {
        var apMacs = devices
            .Where(d => d.Type == DeviceType.AccessPoint)
            .Select(d => d.Mac)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (apMacs.Count < 2) return null;

        var placed = await LoadPlacementsAsync(apMacs);
        var oracle = new ApNeighborOracle(hasPlacementData: placed.Count >= 2);
        var anyData = false;

        if (placed.Count >= 2)
        {
            anyData = true;
            var walls = placed[0].Walls;
            var buildings = placed[0].Buildings;
            for (var i = 0; i < placed.Count; i++)
            {
                for (var j = i + 1; j < placed.Count; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_propagation.DoApsInterfere(placed[i].Ap, placed[j].Ap, NeighborBand, walls, buildings))
                        oracle.AddNeighbors(placed[i].Ap.Mac, placed[j].Ap.Mac);
                }
            }
        }

        try
        {
            var roaming = await _wifi.GetRoamingTopologyAsync();
            foreach (var edge in roaming?.Edges ?? [])
            {
                if (edge.TotalRoamAttempts <= 0) continue;
                var a = MacNormalizer.Normalize(edge.Endpoint1Mac);
                var b = MacNormalizer.Normalize(edge.Endpoint2Mac);
                if (!apMacs.Contains(a) || !apMacs.Contains(b)) continue;
                oracle.AddNeighbors(a, b);
                anyData = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "No roaming topology for the rollout neighbor oracle on site {Site}", _siteSlug);
        }

        return anyData ? oracle : null;
    }

    private async Task<List<(PropagationAp Ap, Dictionary<int, List<PropagationWall>> Walls, List<BuildingFloorInfo> Buildings)>>
        LoadPlacementsAsync(HashSet<string> apMacs)
    {
        try
        {
            var cached = await _heatmapCache.GetOrLoadAsync(_floorPlans, _apMap, _plannedAps);
            return cached.ApMarkers
                .Where(m => m.Latitude.HasValue && m.Longitude.HasValue)
                .Where(m => apMacs.Contains(MacNormalizer.Normalize(m.Mac)))
                .Select(m => (
                    Ap: new PropagationAp
                    {
                        Mac = MacNormalizer.Normalize(m.Mac),
                        Model = m.Model,
                        Latitude = m.Latitude!.Value,
                        Longitude = m.Longitude!.Value,
                        Floor = m.Floor ?? 1,
                        OrientationDeg = m.OrientationDeg,
                        MountType = m.MountType,
                    },
                    cached.WallsByFloor,
                    cached.BuildingFloorInfos))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No AP placements for the rollout neighbor oracle on site {Site}", _siteSlug);
            return [];
        }
    }

    private async Task<int> CountWirelessClientsAsync()
    {
        try
        {
            return (await _wifi.GetWirelessClientsAsync()).Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "No client count for the rollout site profile on site {Site}", _siteSlug);
            return 0;
        }
    }
}
