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
///
/// Since #1108 the hello can carry the installer-written on_gateway flag, and a
/// reported flag is the authoritative tier: it answers without any console round
/// trip. EVERYTHING below it - correlation, caches, persisted verdicts - is the
/// fallback for agents that did not say, and ages out with them; the
/// address-based questions (MatchGatewayAddressAsync, IsIpOnGatewayAsync) stay
/// correlation-based forever, because they need the matched address, which no
/// flag can provide.
/// </summary>
public class AgentOnGatewayDetector : ISiteScopedRegistry
{
    /// <summary>
    /// Site removal sweep: drops every cached answer keyed by the slug so a site re-created
    /// under the same name cannot inherit the removed site's verdicts or gateway addresses.
    /// Nothing here owns a disposable, so there is no teardown callback.
    /// </summary>
    public Func<ValueTask>? EvictSite(string slug)
    {
        _cache.TryRemove(slug, out _);
        _gatewayIps.TryRemove(slug, out _);
        _gatewayHostIps.TryRemove(slug, out _);
        foreach (var key in _agentCache.Keys.Where(k => k.Slug == slug).ToList())
            _agentCache.TryRemove(key, out _);
        foreach (var key in _reported.Keys.Where(k => k.Slug == slug).ToList())
            _reported.TryRemove(key, out _);
        return null;
    }
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);

    private readonly AgentEnrollmentService _enrollment;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteAgentCoverage _agentCoverage;
    private readonly ILogger<AgentOnGatewayDetector> _logger;
    private readonly ConcurrentDictionary<string, (bool OnGateway, DateTime At)> _cache = new();
    private readonly ConcurrentDictionary<string, Task> _refreshing = new();
    // Per-agent verdicts and their in-flight refreshes, for the per-agent overload. Separate from
    // the site-level pair above rather than replacing it: the two answer different questions and
    // the site-level one keeps its own contract.
    private readonly ConcurrentDictionary<(string Slug, int AgentId), (bool OnGateway, DateTime At)> _agentCache = new();
    private readonly ConcurrentDictionary<(string Slug, int AgentId), Task> _agentRefreshing = new();
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
        SiteAgentCoverage agentCoverage,
        AgentTunnelRegistry tunnelRegistry,
        ILogger<AgentOnGatewayDetector> logger)
    {
        _enrollment = enrollment;
        _siteConnections = siteConnections;
        _siteDbFactory = siteDbFactory;
        _agentCoverage = agentCoverage;
        _tunnelRegistry = tunnelRegistry;
        _logger = logger;
    }

    private readonly AgentTunnelRegistry _tunnelRegistry;

    // Verdicts agents REPORTED in their hello (#1108): installer-recorded fact, authoritative
    // over correlation whenever present. Absent from the hello leaves an agent out of this map
    // entirely, which is what keeps every pre-flag install on the correlation path unchanged.
    private readonly ConcurrentDictionary<(string Slug, int AgentId), bool> _reported = new();

    /// <summary>
    /// Adopts an agent's self-reported on-gateway flag (from its hello) as the authoritative
    /// answer for that agent, and persists it under the same per-agent key the correlation
    /// path uses, so a restart answers identically before the tunnel is back. Called only when
    /// the hello actually carried the flag - never for absent, whose meaning is "ask the
    /// correlation path exactly as before".
    /// </summary>
    public async Task NoteReportedAsync(string siteSlug, int agentId, bool onGateway)
    {
        _reported[(siteSlug, agentId)] = onGateway;
        _agentCache[(siteSlug, agentId)] = (onGateway, DateTime.UtcNow);
        await PersistAsync(siteSlug, SystemSettingKeys.AgentOnGatewayFor(agentId), onGateway);
    }

    /// <summary>
    /// The reported verdict for a site, when any of its CONNECTED agents carries one: true if
    /// any reported true, false if every connected agent reported (all false), null when none
    /// reported - which sends the caller to the correlation path.
    /// </summary>
    private bool? ReportedForSite(string siteSlug)
    {
        var connections = _tunnelRegistry.GetForSite(siteSlug);
        if (connections.Count == 0) return null;
        var any = false;
        foreach (var connection in connections)
        {
            if (connection.OnGateway is not { } reported) return null;
            any |= reported;
        }
        return any;
    }

    /// <summary>
    /// Whether the site's online agent appears to run on the site's UniFi
    /// gateway. Answers instantly from cache once a site has ever been resolved
    /// (a stale answer is served while a background refresh runs); the first
    /// query for a site awaits one refresh (bounded by the refresh timeout) so
    /// speed-test surfaces never paint from a made-up false. False for the
    /// default site unless its agent covers collection - a vantage-only agent
    /// is not the site's agent for this purpose.
    /// </summary>
    public async Task<bool> IsAgentOnGatewayAsync(string siteSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug)) return false;
        if (siteSlug == SiteManagementService.DefaultSiteSlug && !_agentCoverage.Covers(siteSlug))
            return false;

        // Reported flags first (#1108): when every connected agent said where it runs, the
        // installer's answer is authoritative and no console round trip happens at all. Any
        // agent that did not say sends the whole question to the correlation path below,
        // exactly as before the flag existed.
        if (ReportedForSite(siteSlug) is { } reported)
            return reported;

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
            var persisted = await LoadPersistedAsync(siteSlug, SystemSettingKeys.AgentOnGateway);
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
    /// Whether ONE agent runs on its site's gateway, given the addresses that agent is known by.
    /// The per-agent counterpart of <see cref="IsAgentOnGatewayAsync(string, CancellationToken)"/>,
    /// carrying the same durability: a persisted verdict seeds the answer before the console can be
    /// asked, and a refresh that cannot reach the console keeps the last answer rather than
    /// inventing a no.
    /// <para>
    /// That durability is the whole point. The addresses to compare against come from the site's
    /// UniFi Console, which on an agent site reconnects through that agent's own tunnel - so a
    /// caller asking right after a restart, an agent update, or a page switch would otherwise get a
    /// silent no and show a gateway agent the wrong instructions. Callers must not block on the
    /// console to avoid that: a site with a broken or unconfigured console is exactly where someone
    /// goes to fix it, and it has to stay responsive.
    /// </para>
    /// <para>
    /// Not gated on covers (the site-level verdict is, so a vantage-only default-site agent stays
    /// false there). This overload answers the physical question for any agent on any site.
    /// </para>
    /// </summary>
    public async Task<bool> IsAgentOnGatewayAsync(
        string siteSlug, int agentId, IReadOnlyList<string> candidates, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug))
            return false;

        var key = (siteSlug, agentId);

        // Reported flag first (#1108): the live connection's is the source of truth, and the
        // adopted copy answers across the reconnect gap. An agent that never reported falls
        // through to the correlation path below, byte-for-byte the pre-flag behavior.
        var live = _tunnelRegistry.GetForSite(siteSlug).FirstOrDefault(c => c.AgentId == agentId);
        if (live?.OnGateway is { } liveReported)
            return liveReported;
        if (_reported.TryGetValue(key, out var adopted))
            return adopted;

        var hasCached = _agentCache.TryGetValue(key, out var cached);
        if (hasCached && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.OnGateway;

        // Seeded before the refresh starts, for the same reason the site-level path does it: a
        // refresh that finds the console down must keep this answer rather than race a made-up
        // false into an empty cache.
        if (!hasCached)
        {
            var persisted = await LoadPersistedAsync(siteSlug, SystemSettingKeys.AgentOnGatewayFor(agentId));
            if (persisted != null)
            {
                _agentCache.TryAdd(key, (persisted.Value, DateTime.MinValue));
                hasCached = _agentCache.TryGetValue(key, out cached);
            }
        }

        // Nothing to compare: an agent that has never reported an address. Never persisted, so a
        // known verdict is kept and an unknown one stays unknown.
        if (candidates.Count == 0)
            return hasCached && cached.OnGateway;

        // TODO(#1106): the FIRST resolve for an agent has no verdict to fall back on, so if the
        // console cannot answer at that moment this returns a no and the caller renders it. It
        // self-corrects on the next ask (the refresh persists), and in practice only a page parked
        // through a server restart sees it, so it is low severity rather than none.
        // Every agent enrolled before this key existed gets one pass through that window too: the
        // site-level verdict persists under AgentOnGateway and nothing reads it back under the
        // per-agent key. Backfilling it is not worth building - the site-level row does not record
        // WHICH agent it was about, so it is only unambiguous on a single-agent site, and the main
        // site (the one this overload exists for) never persisted a row at all.
        var refresh = StartOrJoinAgentRefresh(siteSlug, agentId, candidates);
        if (hasCached)
            return cached.OnGateway;

        try
        {
            await refresh.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Caller gave up (panel closed) - the refresh itself continues and fills the cache.
        }
        return _agentCache.TryGetValue(key, out var fresh) && fresh.OnGateway;
    }

    /// <summary>One in-flight refresh per agent; the result lands in the cache and is persisted.</summary>
    private Task StartOrJoinAgentRefresh(string siteSlug, int agentId, IReadOnlyList<string> candidates) =>
        _agentRefreshing.GetOrAdd((siteSlug, agentId), key => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(RefreshTimeout);
                var connection = _siteConnections.GetFor(key.Slug);
                if (!connection.IsConnected || connection.Client == null)
                {
                    // Console not up (the norm on an agent site right after a restart, since it
                    // reconnects through the agent's tunnel).
                    if (_agentCache.TryGetValue(key, out var last))
                        _agentCache[key] = (last.OnGateway, DateTime.UtcNow);
                    return;
                }

                await ResolveGatewayIpsAsync(key.Slug, connection.Client, cts.Token);
                var onGateway = await MatchGatewayAddressAsync(key.Slug, candidates, cts.Token) != null;
                _agentCache[key] = (onGateway, DateTime.UtcNow);
                await PersistAsync(key.Slug, SystemSettingKeys.AgentOnGatewayFor(key.AgentId), onGateway);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Agent-on-gateway detection failed for agent {AgentId} at site {Slug}", key.AgentId, key.Slug);
            }
            finally
            {
                _agentRefreshing.TryRemove(key, out _);
            }
        }));

    /// <summary>
    /// Whether a specific address is one of the site's gateway addresses - the per-connection
    /// counterpart to <see cref="IsAgentOnGatewayAsync"/>, for the questions that are about ONE
    /// agent rather than about the site. A site with several agents has one gateway, but only one
    /// of those agents may be sitting on it, and the site-level verdict cannot tell them apart: it
    /// correlates against whichever agent the enrollment registry answers with.
    ///
    /// Not gated on covers - answers from the gateway addresses alone, so a main-site agent
    /// running on the gateway is recognized as such regardless of whether it collects.
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
        await PersistAsync(siteSlug, SystemSettingKeys.AgentOnGateway, onGateway);
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
        // TODO(#1106): this takes ONE LAN address - ResolveGatewayLanIpAsync is FirstOrDefault()
        // over the corporate networks - so a multi-VLAN gateway contributes its default LAN and
        // nothing else (a real case: 2 of the 6 addresses the box holds). Harmless for an agent
        // reporting local_ips, since one of those will hit, but an older single-address agent
        // matches only if it happened to name one of the two. NetworkInfo.Gateway is already
        // mapped for EVERY network in UniFiConnectionService / UniFiDiscovery and closes it.
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

    /// <summary>The persisted last verdict under the given key, or null when never detected.</summary>
    private async Task<bool?> LoadPersistedAsync(string siteSlug, string settingKey)
    {
        try
        {
            await using var db = _siteDbFactory.CreateForSite(siteSlug, IsDefaultSite(siteSlug));
            var value = (await db.SystemSettings.FindAsync(settingKey))?.Value;
            return value == null ? null : value == "true";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load persisted agent-on-gateway verdict for site {Slug}", siteSlug);
            return null;
        }
    }

    /// <summary>
    /// The default site keeps its settings in the main database rather than a per-site one. The
    /// site-level verdict never reaches here for it (it answers false and returns), but the
    /// per-agent one does, and asking for a site database it does not have would write the verdict
    /// somewhere nothing reads it back from.
    /// </summary>
    private static bool IsDefaultSite(string siteSlug) => siteSlug == SiteManagementService.DefaultSiteSlug;

    /// <summary>Persists a real (console-backed) verdict; writes only on change to spare the site DB.</summary>
    private async Task PersistAsync(string siteSlug, string settingKey, bool onGateway)
    {
        try
        {
            var value = onGateway ? "true" : "false";
            await using var db = _siteDbFactory.CreateForSite(siteSlug, IsDefaultSite(siteSlug));
            var setting = await db.SystemSettings.FindAsync(settingKey);
            if (setting == null)
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = settingKey,
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
