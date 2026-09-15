using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Places the site's observers on the uplink tree for <see cref="RolloutDarkSet"/>: the UniFi
/// Console, the server's own probes, and each agent's.
/// </summary>
public interface IRolloutObserverLocator
{
    /// <summary>
    /// Where the observers sit, against the device list the rollout is watching. Cached: nothing
    /// here moves during a rollout, and the placement costs console calls.
    /// </summary>
    /// <param name="devices">The console's current device list.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<RolloutObserverPositions> LocateAsync(
        IReadOnlyCollection<RolloutDeviceObservation> devices, CancellationToken cancellationToken = default);
}

/// <summary>
/// The real locator. Every lookup rides an existing cache: the client list comes from the speed
/// test tracer's topology, the server's position from the tracer's own placement, and the agent
/// on-gateway verdicts from their detector. The console is found by its own addresses
/// (stat/sysinfo ip_addrs) in that client list, the way a self-hosted UniFi OS Server appears
/// there; addresses that match no client on a Cloud Gateway are the gateway's own, so the console
/// is the root. An agent is placed by its LAN address, or on the gateway when the detector says so.
/// </summary>
public class RolloutObserverLocator : IRolloutObserverLocator
{
    /// <summary>
    /// How long one placement is trusted. A rollout runs for an hour or more and nothing here
    /// moves during it, so this is the interval between the sysinfo calls the placement costs.
    /// </summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>Vantage key of this server's own probes, as probe results name it.</summary>
    public const string ServerVantage = "server";

    private readonly UniFiConnectionService _connection;
    private readonly IFirmwareCommandClient _commands;
    private readonly SpeedTestServiceRegistry _speedTests;
    private readonly AgentOnGatewayDetector _onGateway;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<RolloutObserverLocator> _logger;
    private readonly string _siteSlug;

    private RolloutObserverPositions? _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    // The console's kind never changes, so it is asked once for the locator's lifetime.
    private bool? _standaloneConsole;

    /// <param name="siteConnections">Per-site console connections.</param>
    /// <param name="commands">Firmware command surface, for the console's kind.</param>
    /// <param name="speedTests">Per-site speed test bundles: the tracer's topology and server position.</param>
    /// <param name="onGateway">Whether an agent runs on its site's gateway.</param>
    /// <param name="mainDbFactory">Main database, for the site's agents.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site to place observers for.</param>
    public RolloutObserverLocator(
        SiteConnectionRegistry siteConnections,
        IFirmwareCommandClient commands,
        SpeedTestServiceRegistry speedTests,
        AgentOnGatewayDetector onGateway,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        TimeProvider time,
        ILogger<RolloutObserverLocator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _connection = siteConnections.GetFor(_siteSlug);
        _commands = commands;
        _speedTests = speedTests;
        _onGateway = onGateway;
        _mainDbFactory = mainDbFactory;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RolloutObserverPositions> LocateAsync(
        IReadOnlyCollection<RolloutDeviceObservation> devices, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (_cached != null && now - _cachedAt < CacheDuration)
            return _cached;

        try
        {
            var gatewayMac = devices.FirstOrDefault(d => d.IsGateway)?.Mac;
            var analyzer = _speedTests.GetFor(_siteSlug).PathAnalyzer;
            var topology = await analyzer.GetTopologyAsync(cancellationToken);
            if (topology == null || _connection.Client == null)
            {
                // Nothing can be placed without the console. A placement from earlier in the
                // rollout beats a guess; with none, only the console's own position is unknown
                // in a way that matters, and it is reported as such.
                return _cached ?? Unplaced();
            }

            var attachByIp = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in topology.Clients)
            {
                if (string.IsNullOrEmpty(c.IpAddress)) continue;
                attachByIp.TryAdd(c.IpAddress,
                    string.IsNullOrEmpty(c.ConnectedToDeviceMac) ? null : MacNormalizer.Normalize(c.ConnectedToDeviceMac));
            }

            var (consoleAttach, consoleUnlocated) = await LocateConsoleAsync(_connection.Client, attachByIp, gatewayMac, cancellationToken);
            var vantages = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (_siteSlug == SiteManagementService.DefaultSiteSlug)
                vantages[ServerVantage] = await LocateServerAsync(analyzer, gatewayMac, cancellationToken);
            await LocateAgentsAsync(vantages, attachByIp, gatewayMac, cancellationToken);

            _cached = new RolloutObserverPositions(consoleAttach, consoleUnlocated, vantages);
            _cachedAt = now;
            _logger.LogDebug(
                "Rollout observers on site {Site}: console at {Console} (unlocated: {Unlocated}), vantages {Vantages}",
                _siteSlug, consoleAttach ?? "root", consoleUnlocated,
                string.Join(", ", vantages.Select(v => $"{v.Key}={v.Value ?? "?"}")));
            return _cached;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rollout observers on site {Site} could not be placed", _siteSlug);
            return _cached ?? Unplaced();
        }
    }

    private async Task<(string? Attach, bool Unlocated)> LocateConsoleAsync(
        UniFiApiClient client, IReadOnlyDictionary<string, string?> attachByIp, string? gatewayMac,
        CancellationToken cancellationToken)
    {
        var sysInfo = await client.GetSystemInfoAsync(cancellationToken);
        foreach (var ip in sysInfo?.IpAddrs ?? [])
        {
            if (attachByIp.TryGetValue(ip, out var attach))
                return (attach, attach == null);
        }

        // Addresses that match no client belong to the gateway on a Cloud Gateway console. A
        // self-hosted console that is not in the client list cannot be placed at all.
        _standaloneConsole ??= (await _commands.GetConsoleSystemInfoAsync(cancellationToken))?.IsStandaloneConsole == true;
        return _standaloneConsole == true || gatewayMac == null ? (null, true) : (null, false);
    }

    private static async Task<string?> LocateServerAsync(INetworkPathAnalyzer analyzer, string? gatewayMac, CancellationToken cancellationToken)
    {
        var position = await analyzer.DiscoverServerPositionAsync(cancellationToken: cancellationToken);
        if (position == null) return null;
        if (!string.IsNullOrEmpty(position.SwitchMac)) return MacNormalizer.Normalize(position.SwitchMac);
        // The tracer anchors a gateway-resident server at the gateway with no switch.
        return gatewayMac != null && string.Equals(MacNormalizer.Normalize(position.Mac), gatewayMac, StringComparison.OrdinalIgnoreCase)
            ? gatewayMac
            : null;
    }

    private async Task LocateAgentsAsync(
        Dictionary<string, string?> vantages, IReadOnlyDictionary<string, string?> attachByIp, string? gatewayMac,
        CancellationToken cancellationToken)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(cancellationToken);
        var siteId = await db.Sites.AsNoTracking()
            .Where(s => s.Slug == _siteSlug).Select(s => (int?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (siteId == null) return;

        var agents = await db.SiteAgents.AsNoTracking()
            .Where(a => a.SiteId == siteId && a.Enabled)
            .Select(a => new { a.Id, a.LanIp })
            .ToListAsync(cancellationToken);

        foreach (var agent in agents)
        {
            var key = $"agent-{agent.Id}";
            IReadOnlyList<string> candidates = string.IsNullOrEmpty(agent.LanIp) ? [] : [agent.LanIp];
            if (gatewayMac != null && await _onGateway.IsAgentOnGatewayAsync(_siteSlug, agent.Id, candidates, cancellationToken))
            {
                vantages[key] = gatewayMac;
                continue;
            }

            vantages[key] = !string.IsNullOrEmpty(agent.LanIp) && attachByIp.TryGetValue(agent.LanIp, out var attach)
                ? attach
                : null;
        }
    }

    private static RolloutObserverPositions Unplaced() =>
        new(null, true, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
}
