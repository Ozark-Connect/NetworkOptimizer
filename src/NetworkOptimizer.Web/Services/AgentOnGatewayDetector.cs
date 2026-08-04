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
    // The agent address the last detection compared against the gateway's. Kept so callers that
    // need it - anything asking "is this target the box the agent runs on" - can have it from here
    // rather than asking the enrollment service again, which is gated and unusable from background
    // work without a system scope.
    private readonly ConcurrentDictionary<string, string> _agentIp = new();
    private readonly ConcurrentDictionary<string, Task> _refreshing = new();
    // The site's gateway addresses from the last resolution, so the per-connection check below can
    // answer for an agent the site-level verdict never considered. Cached and refreshed on the same
    // TTL as the verdict itself; a site with 2+ agents has one gateway either way.
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Ips, DateTime At)> _gatewayIps = new();
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Ips, DateTime At)> _gatewayHostIps = new();
    private readonly ConcurrentDictionary<string, Task> _gatewayIpRefreshing = new();

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

    /// <summary>
    /// The address the site's agent reported at the last detection, or null if none has completed.
    /// Only meaningful alongside <see cref="IsAgentOnGatewayAsync"/> saying true, where it is the
    /// gateway's own address - which is to say, the one target that agent must not probe.
    /// </summary>
    public string? LastKnownAgentIp(string siteSlug) =>
        _agentIp.TryGetValue(siteSlug, out var ip) ? ip : null;

    /// <summary>
    /// Whether a specific address is one of the site's gateway addresses - the per-connection
    /// counterpart to <see cref="IsAgentOnGatewayAsync"/>, for the questions that are about ONE
    /// agent rather than about the site. A site with several agents has one gateway, but only one
    /// of those agents may be sitting on it, and the site-level verdict cannot tell them apart: it
    /// correlates against whichever agent the enrollment registry answers with.
    ///
    /// Deliberately not gated on the site being non-default. The site-level verdict keeps its
    /// existing "false for the default site" contract for its existing consumers; this one answers
    /// from the gateway addresses alone, so a main-site agent running on the gateway is recognized
    /// as such - which is exactly the deployment multi-WAN contexts target.
    /// </summary>
    public async Task<bool> IsIpOnGatewayAsync(string siteSlug, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug) || string.IsNullOrWhiteSpace(ip))
            return false;

        var hasCached = _gatewayIps.TryGetValue(siteSlug, out var cached);
        if (!hasCached || DateTime.UtcNow - cached.At >= CacheTtl)
        {
            var refresh = StartOrJoinGatewayIpRefresh(siteSlug);
            if (!hasCached)
            {
                try
                {
                    await refresh.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Caller gave up - the refresh itself continues and fills the cache.
                }
                hasCached = _gatewayIps.TryGetValue(siteSlug, out cached);
            }
        }

        return hasCached && cached.Ips.Contains(ip!.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first of <paramref name="candidates"/> that is one of this site's gateway addresses, or
    /// null when none is.
    /// <para>
    /// The gateway address set is unchanged - this only asks the same question of more candidates.
    /// An agent picks ONE address to report itself by, and on a gateway that choice is whichever
    /// Ethernet interface the kernel enumerates first, which can easily be an uplink the console
    /// never lists as the gateway's own. Comparing every address the host holds answers "is this
    /// that machine" instead of "did it happen to name the address we know".
    /// </para>
    /// <para>
    /// Returns the MATCHING address rather than a bool because callers that skip the gateway's own
    /// target need the address the site knows it by, not the one the agent named itself with.
    /// </para>
    /// </summary>
    public async Task<string?> MatchGatewayAddressAsync(
        string siteSlug, IEnumerable<string> candidates, CancellationToken ct = default)
    {
        var addresses = candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).ToList();
        if (addresses.Count == 0) return null;

        // Narrow set first, so a caller using the answer as an ADDRESS gets the one the site knows
        // the gateway by rather than some other interface of the same box.
        foreach (var candidate in addresses)
            if (await IsIpOnGatewayAsync(siteSlug, candidate, ct)) return candidate;

        if (!_gatewayHostIps.TryGetValue(siteSlug, out var host)) return null;
        return addresses.FirstOrDefault(c => host.Ips.Contains(c, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>One in-flight gateway-address resolution per site; the result lands in the cache.</summary>
    private Task StartOrJoinGatewayIpRefresh(string siteSlug) =>
        _gatewayIpRefreshing.GetOrAdd(siteSlug, slug => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(RefreshTimeout);
                var connection = _siteConnections.GetFor(slug);
                if (connection.IsConnected && connection.Client != null)
                    await ResolveGatewayIpsAsync(slug, connection.Client, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Gateway address resolution failed for site {Slug}", slug);
            }
            finally
            {
                _gatewayIpRefreshing.TryRemove(slug, out _);
            }
        }));

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

        var gatewayIps = await ResolveGatewayIpsAsync(siteSlug, connection.Client, ct);

        var onGateway = gatewayIps.Contains(agentIp!, StringComparer.OrdinalIgnoreCase);
        _cache[siteSlug] = (onGateway, DateTime.UtcNow);
        _agentIp[siteSlug] = agentIp!;
        await PersistAsync(siteSlug, onGateway);
    }

    /// <summary>
    /// The site's gateway addresses: every gateway device's reported IP (on a gateway agent that is
    /// the WAN address) plus the LAN-side gateway IP, in case the agent's own detection landed
    /// there instead. Caches what it found so the per-connection check can answer without its own
    /// console round trip.
    /// </summary>
    private async Task<List<string>> ResolveGatewayIpsAsync(
        string siteSlug, UniFi.UniFiApiClient client, CancellationToken ct)
    {
        var devices = await client.GetDevicesAsync(ct) ?? new();
        var gatewayIps = devices
            .Where(d => d.DeviceType == DeviceType.Gateway && !string.IsNullOrEmpty(d.Ip))
            .Select(d => d.Ip!)
            .ToList();

        // Superset, cached alongside and never mixed into the set above: EVERY address the console
        // reports the gateway holding, for the one question that needs it - is an agent running on
        // this box. A gateway holds a dozen addresses and an agent that reports only one may name
        // any of them, so the narrow set answers that question with a false no. Deliberately built
        // from the gateway's own interfaces only; inform_ip and connect_request_ip are the console's
        // loopback and would match every host alive, so they are not read at all.
        var hostIps = new List<string>(gatewayIps);
        foreach (var device in devices.Where(d => d.DeviceType == DeviceType.Gateway))
        {
            AddHostIp(hostIps, device.LanIp);
            AddHostIp(hostIps, device.ConfigNetwork?.Ip);
            foreach (var port in device.PortTable ?? new())
                AddHostIp(hostIps, port.Ip);
        }
        try
        {
            var lanIp = await Monitoring.SnmpDeviceRules.ResolveGatewayLanIpAsync(client, ct);
            if (!string.IsNullOrEmpty(lanIp))
                gatewayIps.Add(lanIp!);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway LAN IP resolution failed for site {Slug} during on-gateway detection", siteSlug);
        }

        if (gatewayIps.Count > 0)
        {
            _gatewayIps[siteSlug] = (gatewayIps, DateTime.UtcNow);
            foreach (var ip in gatewayIps) AddHostIp(hostIps, ip);
            _gatewayHostIps[siteSlug] = (hostIps, DateTime.UtcNow);
        }
        return gatewayIps;
    }

    /// <summary>
    /// Adds an address to the host set when it can identify a host: not empty, not a duplicate, and
    /// neither loopback nor link-local - the two an unrelated machine could hold as readily as this
    /// one, where a match would mean nothing.
    /// </summary>
    private static void AddHostIp(List<string> hostIps, string? ip)
    {
        var value = ip?.Trim();
        if (string.IsNullOrEmpty(value)) return;
        if (!System.Net.IPAddress.TryParse(value, out var parsed)) return;
        if (System.Net.IPAddress.IsLoopback(parsed)) return;
        if (value.StartsWith("169.254.", StringComparison.Ordinal)) return;
        if (!hostIps.Contains(value, StringComparer.OrdinalIgnoreCase)) hostIps.Add(value);
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
