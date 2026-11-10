using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// Live Traffic for the site in context: every flow through the gateway ranked by what is using
/// the link now, Where It Goes, and Live DNS. Each read also keeps the viewer's lease alive, which
/// is what lets the gateway agent send per-flow and DNS detail at all; when the last viewer leaves
/// the lease ends. Reads only, open to any role, as Live View is.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ILiveTrafficService
{
    /// <summary>The view for one refresh. Grants or renews the agent's lease for this viewer.</summary>
    [RequireRole(Roles.Viewer)]
    Task<LiveTrafficView> GetViewAsync(LiveTrafficQuery query, CancellationToken ct = default);

    /// <summary>
    /// History: the console's own flow records for the last ~48 minutes, filtered like Live. Needs no
    /// agent and never touches the agent's lease.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<LiveHistoryView> GetHistoryAsync(LiveHistoryQuery query, CancellationToken ct = default);

    /// <summary>
    /// The viewer left (card collapsed, page closed, or switched mode): the lease ends now if nobody
    /// else watches, and the History records go once no History viewer is left.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task ReleaseAsync(string viewerId);
}

/// <inheritdoc cref="ILiveTrafficService" />
public sealed class LiveTrafficService : ILiveTrafficService
{
    private const int LeaseMaxFlows = 500;
    private const int PageSize = 500;
    private const int MaxPages = 4;
    /// <summary>The first read covers half an hour of records, so it may take more pages.</summary>
    private const int BackfillPages = 12;
    /// <summary>Pages per older ten-minute window: a busy site writes more than one page in ten minutes.</summary>
    private const int BackfillWindowPages = 4;

    private readonly LiveTrafficRegistry _registry;
    private readonly AgentTunnelRegistry _tunnels;
    private readonly SiteContextService _site;
    private readonly UniFiConnectionService _connection;
    private readonly SiteDbContextFactory _siteDb;
    private readonly IConntrackStatusService _agentStatus;
    private readonly ThreatBlockingOperations _threatOps;
    private readonly Authorization.ISiteAccessFilter _siteAccess;
    private readonly ISiteManagementService _sites;
    private readonly GeoEnrichmentService _geo;
    private readonly LiveTrafficPolicyResolver _policies;
    private readonly ILogger<LiveTrafficService> _logger;
    private readonly IReadOnlySet<string> _serverHosts;

    /// <summary>This install's declared hosts (reverse-proxied name, HOST_NAME, HOST_IP), the names agents and browsers reach it by.</summary>
    private static IReadOnlySet<string> ServerHosts(CanonicalBaseUrlProvider canonical)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in new[] { canonical.HttpsUrl, canonical.Url, canonical.UrlForCallbacks })
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) hosts.Add(uri.Host);
        return hosts;
    }

    /// <summary>Flows evaluated against the firewall rules per label refresh (every 10 s); in-memory once the rules are read.</summary>
    private const int PolicyEvaluationsPerRefresh = 300;

    /// <summary>PTR lookups per label refresh (every 10 s), so a busy page never floods the resolver.</summary>
    private const int PtrPerRefresh = 16;

    public LiveTrafficService(
        LiveTrafficRegistry registry,
        AgentTunnelRegistry tunnels,
        SiteContextService site,
        UniFiConnectionService connection,
        SiteDbContextFactory siteDb,
        IConntrackStatusService agentStatus,
        ThreatBlockingOperations threatOps,
        Authorization.ISiteAccessFilter siteAccess,
        ISiteManagementService sites,
        GeoEnrichmentService geo,
        LiveTrafficPolicyResolver policies,
        CanonicalBaseUrlProvider canonical,
        ILogger<LiveTrafficService> logger)
    {
        _serverHosts = ServerHosts(canonical);
        _registry = registry;
        _tunnels = tunnels;
        _site = site;
        _connection = connection;
        _siteDb = siteDb;
        _agentStatus = agentStatus;
        _threatOps = threatOps;
        _siteAccess = siteAccess;
        _sites = sites;
        _geo = geo;
        _policies = policies;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LiveTrafficView> GetViewAsync(LiveTrafficQuery query, CancellationToken ct = default)
    {
        var slug = _site.Slug;
        var store = _registry.GetFor(slug);
        var now = DateTime.UtcNow;
        store.SetServerHosts(_serverHosts);

        var agents = _tunnels.GetForSite(slug).Where(c => c.HasCapability(AgentTunnelConnection.LiveTrafficCapability)).ToList();
        LiveTrafficState state;
        if (agents.Count == 0)
        {
            // The agent advertises conntrack accounting and Live Traffic together, and only when it can
            // read the table: accounting without Live Traffic is an older build, and a gateway agent
            // with neither cannot read the table (accounting predates every supported agent).
            var connected = _tunnels.GetForSite(slug).ToList();
            state = connected.Any(c => c.HasCapability(AgentTunnelConnection.ConntrackCapability)) ? LiveTrafficState.AgentNeedsUpdate
                : connected.Any(c => c.OnGateway == true) ? LiveTrafficState.NoConntrack
                : LiveTrafficState.NoGatewayAgent;
            // An enrolled agent that has not said hello yet (server start, agent restart) may be the
            // gateway agent: wait for it, as the Dashboard card does, rather than pitch an install.
            if (state == LiveTrafficState.NoGatewayAgent
                && (await _agentStatus.GetStatusAsync(ct)).State == GatewayAgentConntrackState.AwaitingAgent)
                state = LiveTrafficState.AwaitingAgent;
        }
        else
        {
            if (store.TouchViewer(query.ViewerId, now))
            {
                foreach (var agent in agents)
                {
                    agent.TrySend(new ServerMessage
                    {
                        LiveTrafficConfig = new LiveTrafficConfig
                        {
                            LeaseSeconds = (int)LiveTrafficStore.LeaseLength.TotalSeconds,
                            MaxFlows = LeaseMaxFlows,
                            Dns = agent.HasCapability(AgentTunnelConnection.LiveDnsCapability),
                        },
                    });
                }
                store.NoteLeasePushed(now);
            }
            state = store.HasRates(now) ? LiveTrafficState.Live : LiveTrafficState.Starting;

            // Awaited here, in the viewer's own request: the address books are scoped to the sites
            // this viewer may see, so another site's addresses never label a flow they could not open.
            if (store.SiteAddressesDue(now)) await LoadSiteAddressesAsync(slug, store, ct);

            // Labels refresh in the background: a slow console never holds up the live rows.
            var refreshClients = store.ClientsDue(now);
            var refreshFlows = store.EnrichmentDue(now);
            if (refreshClients || refreshFlows)
            {
                var gatewayIps = agents.SelectMany(a => a.HostAddresses).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _ = Task.Run(() => EnrichAsync(slug, store, refreshClients, refreshFlows, gatewayIps));
            }
        }

        return store.BuildView(query, state);
    }

    /// <inheritdoc />
    public async Task<LiveHistoryView> GetHistoryAsync(LiveHistoryQuery query, CancellationToken ct = default)
    {
        var slug = _site.Slug;
        var cache = _registry.HistoryFor(slug);
        var now = DateTime.UtcNow;
        cache.Touch(query.ViewerId, now);

        var client = _connection.Client;
        if (client == null) return cache.BuildView(query, LiveHistoryState.NoConsole, now);
        if (client.TrafficFlowsUnsupported) return cache.BuildView(query, LiveHistoryState.Unsupported, now);

        if (cache.RefreshDue(now))
        {
            // The first load is awaited so the page opens on data; later refreshes run behind the view.
            if (!cache.Loaded) await RefreshHistoryAsync(slug, cache, client, ct);
            else _ = Task.Run(() => RefreshHistoryAsync(slug, cache, client, CancellationToken.None));
        }

        now = DateTime.UtcNow;
        var state = client.TrafficFlowsUnsupported ? LiveHistoryState.Unsupported
            : cache.Loaded ? LiveHistoryState.Ready
            : cache.Failed ? LiveHistoryState.Failed
            : LiveHistoryState.Loading;
        return cache.BuildView(query, state, now);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string viewerId)
    {
        var slug = _site.Slug;
        var now = DateTime.UtcNow;
        _registry.HistoryFor(slug).Release(viewerId, now);
        var store = _registry.GetFor(slug);
        if (store.ReleaseViewer(viewerId, now))
        {
            foreach (var agent in _tunnels.GetForSite(slug).Where(c => c.HasCapability(AgentTunnelConnection.LiveTrafficCapability)))
                agent.TrySend(new ServerMessage { LiveTrafficConfig = new LiveTrafficConfig { LeaseSeconds = 0 } });
        }
        return Task.CompletedTask;
    }

    private async Task RefreshHistoryAsync(string slug, FlowHistoryCache cache, UniFiApiClient client, CancellationToken ct)
    {
        var ok = true;
        var generation = cache.Generation;
        try
        {
            if (!cache.Loaded)
            {
                // The console's own WAN networks: traffic-flows names exits by these. The WAN profiles'
                // names are only a fallback, recorded when speeds were read.
                var wanNames = (await client.GetWanConfigsAsync(ct)).Select(w => w.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                if (wanNames.Count == 0)
                {
                    var (routes, primary) = await LoadRoutesAsync(slug);
                    wanNames = routes.Values.Append(primary).OfType<LiveRouteInfo>().Select(r => r.Name).ToList();
                }
                var devices = await _connection.GetDiscoveredDevicesAsync();
                var gateway = devices.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);
                cache.SetSiteNames(wanNames!, gateway?.Name);
            }

            var to = DateTime.UtcNow;
            var from = cache.FetchFrom(to);
            // The first load opens from the client's shared cache when it already holds the hour, and
            // reads only what is newer; otherwise the console's whole hour is read once, and kept there.
            if (!cache.Loaded && client.FlowCache.Covered is { } covered
                && covered.From <= from + TimeSpan.FromMinutes(2) && covered.To > from)
            {
                cache.Merge(client.FlowCache.Since(from), DateTime.UtcNow, generation);
                from = (covered.To - TimeSpan.FromSeconds(30)).UtcDateTime;
            }
            var read = new List<UniFiTrafficFlow>();
            var complete = false;
            for (var page = 0; page < FlowHistoryCache.MaxPages; page++)
            {
                var result = await client.GetTrafficFlowPageAsync(from, to, page, FlowHistoryCache.PageSize, ct);
                if (result == null)
                {
                    ok = false;
                    break;
                }
                cache.Merge(result.Data, DateTime.UtcNow, generation);
                read.AddRange(result.Data);
                if (result.Data.Count < FlowHistoryCache.PageSize)
                {
                    complete = true;
                    break;
                }
            }
            if (complete) client.FlowCache.Record(from, to, read, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            _logger.LogDebug(ex, "Live Traffic: History refresh failed for site {Slug}", slug);
        }
        cache.MarkRefreshed(ok, generation);
    }

    private async Task EnrichAsync(string slug, LiveTrafficStore store, bool clients, bool flows, IReadOnlyList<string> gatewayIps)
    {
        var client = _connection.Client;
        if (client == null) return;
        try
        {
            if (clients)
            {
                var list = await client.GetClientsAsync();
                var devices = await _connection.GetDiscoveredDevicesAsync();
                var deviceNames = devices
                    .Where(d => !string.IsNullOrEmpty(d.Mac))
                    .GroupBy(d => d.Mac!.Replace('-', ':').ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.First().Name ?? g.Key);
                var gateway = devices.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);

                var infos = list.Select(c =>
                {
                    var name = !string.IsNullOrWhiteSpace(c.Name) ? c.Name : !string.IsNullOrWhiteSpace(c.Hostname) ? c.Hostname : c.Ip;
                    string? via = null;
                    // A port number means nothing without its switch. "#" keeps the cell short; its tooltip says Port.
                    if (c.IsWired && c.SwPort is { } port)
                        via = c.SwMac is { } swMac && deviceNames.TryGetValue(swMac.Replace('-', ':').ToLowerInvariant(), out var sw)
                            ? $"{sw} · # {port}"
                            : $"# {port}";
                    else if (!c.IsWired && c.Radio is { } radio) via = radio switch
                    {
                        "ng" => "2.4 GHz",
                        "na" => "5 GHz",
                        "6e" => "6 GHz",
                        _ => null,
                    };
                    return new LiveClientInfo(c.Mac.Replace('-', ':').ToLowerInvariant(), name, c.Ip, via, c.Network,
                        c.IsWired ? c.SwMac?.Replace('-', ':').ToLowerInvariant() : null, c.IsWired ? c.SwPort : null);
                }).ToList();

                // UniFi devices (APs, switches) are not in the client list; name them by their LAN address.
                var clientIps = infos.Select(i => i.Ip).Where(ip => !string.IsNullOrEmpty(ip)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                infos.AddRange(devices
                    .Where(d => d != gateway && !string.IsNullOrEmpty(d.Mac) && !string.IsNullOrEmpty(d.DisplayIpAddress)
                        && !clientIps.Contains(d.DisplayIpAddress))
                    .Select(d => new LiveClientInfo(d.Mac.Replace('-', ':').ToLowerInvariant(),
                        string.IsNullOrWhiteSpace(d.Name) ? d.FriendlyModelName : d.Name, d.DisplayIpAddress, d.FriendlyModelName, null)));

                var (routes, primary) = await LoadRoutesAsync(slug);
                store.SetSiteData(infos, gatewayIps, gateway?.Name, routes, primary);

                var networks = await client.GetNetworkConfigsAsync();
                store.SetSiteNetworks(networks.SelectMany(n => new[] { n.IpSubnet, n.Ipv6Subnet }).OfType<string>());
                store.SetNetworkPurposes(networks.Select(n => (n.IpSubnet, (string?)n.Purpose)).ToList());
            }

            if (flows)
            {
                var to = DateTime.UtcNow;
                var from = store.UniFiFetchFrom(to);
                // A first read takes what the client's shared cache already covers (Threat Intelligence
                // reads every record each minute) and asks the console only for what is newer.
                if (store.UniFiBackfilling && client.FlowCache.Covered is { } covered
                    && covered.From <= from && covered.To > from)
                {
                    store.MergeUniFiFlows(client.FlowCache.Since(from));
                    from = (covered.To - TimeSpan.FromSeconds(30)).UtcDateTime;
                }
                var pages = store.UniFiBackfilling ? BackfillPages : MaxPages;
                var read = new List<UniFiTrafficFlow>();
                var complete = false;
                for (var page = 0; page < pages; page++)
                {
                    var result = await client.GetTrafficFlowPageAsync(from, to, page, PageSize);
                    if (result == null) break;
                    store.MergeUniFiFlows(result.Data);
                    read.AddRange(result.Data);
                    if (result.Data.Count < PageSize)
                    {
                        complete = true;
                        break;
                    }
                }
                if (complete) client.FlowCache.Record(from, to, read, DateTimeOffset.UtcNow);

                // One older window per refresh, until long-running flows have found their records.
                if (store.NextBackfillWindow(to) is { } window)
                {
                    for (var page = 0; page < BackfillWindowPages; page++)
                    {
                        var result = await client.GetTrafficFlowPageAsync(window.From, window.To, page, PageSize);
                        if (result == null) break;
                        store.MergeBackfill(result.Data);
                        if (result.Data.Count < PageSize) break;
                    }
                }

                if (store.MatchSummaryDue(DateTime.UtcNow))
                    _logger.LogDebug("Live Traffic match for site {Slug}: {Summary}", slug, store.MatchSummary());

                // Who owns each unnamed address: the offline GeoLite2 ASN data, no network call.
                if (_geo.IsAsnAvailable)
                {
                    var looked = store.AddressesWithoutOwner(500).Select(ip => (Ip: ip, Geo: _geo.Enrich(ip))).ToList();
                    var owners = looked.ToDictionary(x => x.Ip, x => x.Geo is { Asn: > 0 } g ? AsnNameCleanup.Clean(g.AsnOrg) : null);
                    // The ASN number too: an ISP-embedded CDN cache is told by it.
                    var asns = looked.Where(x => x.Geo.Asn is > 0).ToDictionary(x => x.Ip, x => x.Geo.Asn!.Value);
                    if (owners.Count > 0) store.SetOwners(owners, asns);
                }

                // The policy for live flows UniFi Network has not reported yet, from the site's own rules.
                var toEvaluate = store.FlowsToEvaluate(PolicyEvaluationsPerRefresh);
                if (toEvaluate.Count > 0)
                {
                    var queries = toEvaluate.Select(e => e.Query)
                        .Concat(toEvaluate.Select(e => e.Reverse).OfType<LiveTrafficPolicyResolver.FlowQuery>()).ToList();
                    var verdicts = await _policies.DecideAsync(slug, queries, CancellationToken.None);
                    store.SetEvaluatedPolicies(toEvaluate.Select(e => (e.Key, LiveTrafficPolicyResolver.Name(verdicts, e.Query, e.Reverse))));
                }

                // PTR names for the busiest unnamed addresses, a few per refresh, cached for hours.
                var ptrTargets = store.AddressesWithoutPtr(PtrPerRefresh);
                if (ptrTargets.Count > 0)
                {
                    var answers = await Task.WhenAll(ptrTargets.Select(async ip => (ip, (await ReverseDnsCache.ResolveAsync(ip)).Hostname)));
                    store.SetPtrNames(answers.ToDictionary(a => a.ip, a => a.Hostname));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Traffic: label refresh failed for site {Slug}", slug);
        }
    }

    /// <summary>
    /// Every public address the viewer's sites have been seen at, from Trusted Sources' address books
    /// (the same observation that feeds the blocking allowlists), keyed by the site's name.
    /// </summary>
    private async Task LoadSiteAddressesAsync(string slug, LiveTrafficStore store, CancellationToken ct)
    {
        try
        {
            var overview = await _threatOps.GetTrustedSourcesAsync(slug, await _siteAccess.AuthorizedSlugsAsync(), ct);
            var names = (await _sites.GetSitesAsync())
                .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.Slug, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            store.SetSiteAddresses(overview.Sites.SelectMany(s =>
                s.Addresses.Select(a => (a.Address, names.GetValueOrDefault(s.Slug) ?? s.Slug))));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Traffic: site address books unavailable for site {Slug}", slug);
        }
    }

    private async Task<(Dictionary<string, LiveRouteInfo> ByIfname, LiveRouteInfo? Primary)> LoadRoutesAsync(string slug)
    {
        var byIfname = new Dictionary<string, LiveRouteInfo>(StringComparer.OrdinalIgnoreCase);
        LiveRouteInfo? primary = null;
        await using var db = _siteDb.CreateForSite(slug, slug == SiteManagementService.DefaultSiteSlug);
        foreach (var p in await db.WanProfiles.AsNoTracking().ToListAsync())
        {
            var name = !string.IsNullOrWhiteSpace(p.Name) ? p.Name! : p.WanNetworkgroup;
            var info = new LiveRouteInfo(name, LiveRouteKind.Wan,
                p.DownloadMbps is > 0 ? p.DownloadMbps * 1_000_000 : null,
                p.UploadMbps is > 0 ? p.UploadMbps * 1_000_000 : null);
            if (!string.IsNullOrEmpty(p.DataPathInterface)) byIfname[p.DataPathInterface!] = info;
            if (!string.IsNullOrEmpty(p.CounterInterface)) byIfname.TryAdd(p.CounterInterface!, info);
            if (p.IsPrimary == true) primary = info;
        }
        primary ??= byIfname.Values.FirstOrDefault();
        return (byIfname, primary);
    }
}
