using System.Collections.Concurrent;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Detects whether a site's on-site agent runs on the UniFi gateway itself
/// rather than a separate box. The agent's hello reports the IPv4 of its
/// default-route interface; on a gateway that is the WAN address, which is
/// exactly the "ip" UniFi Network reports for the gateway device (the LAN-side
/// gateway IP is matched too, in case the agent's detection lands there
/// instead). Detection is IP correlation only - no agent-side flag - so it
/// works with any agent version.
///
/// Repeat callers are never blocked: the answer comes from a cache (stale is
/// fine for UI gating), and expiry triggers a background refresh with its own
/// timeout. The FIRST query for a site seeds from the persisted last verdict
/// (surviving restarts, when the console - which reconnects through the agent
/// tunnel - may not be back yet to answer live), and only a never-detected
/// site awaits one bounded refresh. A made-up false would paint the full
/// speed-test surfaces (dead targets pointing at the gateway) on a
/// gateway-agent site, and pages without polling would hold that until a
/// reload. After the first answer the cache always has an entry, so UI paths
/// answer instantly.
///
/// Consumers gate the speed-test surfaces on this: today an on-gateway agent
/// never hosts the LAN speed-test listener or the WAN test binary. When
/// speed-test-capable gateway installs arrive, that gating should move to a
/// per-agent capability flag - this detector only answers "is it on the
/// gateway", not "what can it do".
/// </summary>
public class AgentOnGatewayDetector
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);

    private readonly AgentEnrollmentService _enrollment;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<AgentOnGatewayDetector> _logger;
    private readonly ConcurrentDictionary<string, (bool OnGateway, DateTime At)> _cache = new();
    private readonly ConcurrentDictionary<string, Task> _refreshing = new();

    public AgentOnGatewayDetector(
        AgentEnrollmentService enrollment,
        SiteConnectionRegistry siteConnections,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        ILogger<AgentOnGatewayDetector> logger)
    {
        _enrollment = enrollment;
        _siteConnections = siteConnections;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Whether the site's online agent appears to run on the site's UniFi
    /// gateway. Answers instantly from cache once a site has ever been resolved
    /// (a stale answer is served while a background refresh runs); the first
    /// query for a site awaits one refresh (bounded by the refresh timeout) so
    /// speed-test surfaces never paint from a made-up false. False for the
    /// default site.
    /// </summary>
    public async Task<bool> IsAgentOnGatewayAsync(string siteSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug)
            return false;

        var hasCached = _cache.TryGetValue(siteSlug, out var cached);
        if (hasCached && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.OnGateway;

        // Cold start: after a restart the cache is empty but the site's console
        // (which reconnects through the agent tunnel) may not be back yet, so a
        // live refresh can't answer. The persisted last verdict bridges that
        // gap - seeded stale, and BEFORE the refresh starts, so a refresh that
        // finds the console down keeps this answer instead of racing an
        // invented false into the empty cache.
        if (!hasCached)
        {
            var persisted = await LoadPersistedAsync(siteSlug);
            if (persisted != null)
            {
                _cache.TryAdd(siteSlug, (persisted.Value, DateTime.MinValue));
                hasCached = _cache.TryGetValue(siteSlug, out cached);
            }
        }

        var refresh = StartOrJoinRefresh(siteSlug);
        if (hasCached)
            return cached.OnGateway;

        try
        {
            await refresh.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Caller gave up (page disposed) - the refresh itself continues.
        }
        return _cache.TryGetValue(siteSlug, out var fresh) && fresh.OnGateway;
    }

    /// <summary>One in-flight refresh per site; result lands in the cache, and first-time callers await the returned task.</summary>
    private Task StartOrJoinRefresh(string siteSlug) =>
        _refreshing.GetOrAdd(siteSlug, slug => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(RefreshTimeout);
                await RefreshAsync(slug, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Agent-on-gateway detection failed for site {Slug}", slug);
            }
            finally
            {
                _refreshing.TryRemove(slug, out _);
            }
        }));

    private async Task RefreshAsync(string siteSlug, CancellationToken ct)
    {
        var agentIp = await _enrollment.GetOnlineAgentLanIpAsync(siteSlug);
        if (string.IsNullOrEmpty(agentIp))
        {
            // Agent momentarily offline - not evidence of location either way
            // (the speed-test surfaces gate on the missing agent IP anyway).
            KeepLastAnswer(siteSlug);
            return;
        }

        var connection = _siteConnections.GetFor(siteSlug);
        if (!connection.IsConnected || connection.Client == null)
        {
            // Console momentarily down (it reconnects through the same agent
            // tunnel, so this is the norm right after a restart).
            KeepLastAnswer(siteSlug);
            return;
        }

        var devices = await connection.Client.GetDevicesAsync(ct) ?? new();
        var gatewayIps = devices
            .Where(d => d.DeviceType == DeviceType.Gateway && !string.IsNullOrEmpty(d.Ip))
            .Select(d => d.Ip!)
            .ToList();
        try
        {
            var lanIp = await Monitoring.SnmpDeviceRules.ResolveGatewayLanIpAsync(connection.Client, ct);
            if (!string.IsNullOrEmpty(lanIp))
                gatewayIps.Add(lanIp!);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway LAN IP resolution failed for site {Slug} during on-gateway detection", siteSlug);
        }

        var onGateway = gatewayIps.Contains(agentIp!, StringComparer.OrdinalIgnoreCase);
        _cache[siteSlug] = (onGateway, DateTime.UtcNow);
        await PersistAsync(siteSlug, onGateway);
    }

    /// <summary>
    /// A degraded refresh (agent or console unreachable) must never invent an
    /// answer: re-stamp whatever the cache holds (last real verdict or the
    /// persisted seed) so expiry doesn't hammer refreshes, and leave a cache
    /// with no entry EMPTY so the site stays unknown and keeps retrying rather
    /// than trusting a made-up false for a full TTL.
    /// </summary>
    private void KeepLastAnswer(string siteSlug)
    {
        if (_cache.TryGetValue(siteSlug, out var cached))
            _cache[siteSlug] = (cached.OnGateway, DateTime.UtcNow);
    }

    /// <summary>The persisted last verdict for a site, or null when never detected.</summary>
    private async Task<bool?> LoadPersistedAsync(string siteSlug)
    {
        try
        {
            await using var db = _siteDbFactory.CreateForSite(siteSlug, isDefault: false);
            var value = (await db.SystemSettings.FindAsync(SystemSettingKeys.AgentOnGateway))?.Value;
            return value == null ? null : value == "true";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load persisted agent-on-gateway verdict for site {Slug}", siteSlug);
            return null;
        }
    }

    /// <summary>Persists a real (console-backed) verdict; writes only on change to spare the site DB.</summary>
    private async Task PersistAsync(string siteSlug, bool onGateway)
    {
        try
        {
            var value = onGateway ? "true" : "false";
            await using var db = _siteDbFactory.CreateForSite(siteSlug, isDefault: false);
            var setting = await db.SystemSettings.FindAsync(SystemSettingKeys.AgentOnGateway);
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = SystemSettingKeys.AgentOnGateway,
                    Value = value
                });
            else if (setting.Value != value)
            {
                setting.Value = value;
                setting.UpdatedAt = DateTime.UtcNow;
            }
            else
                return;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist agent-on-gateway verdict for site {Slug}", siteSlug);
        }
    }
}
