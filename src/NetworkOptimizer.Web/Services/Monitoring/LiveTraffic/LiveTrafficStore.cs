using System.Collections.Concurrent;
using System.Net;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>A site client as Live Traffic names it.</summary>
public sealed record LiveClientInfo(string Mac, string Name, string Ip, string? Via, string? Network,
    string? SwitchMac = null, int? SwitchPort = null);

/// <summary>A route a flow can leave by, with the console's expected speeds when it is a WAN.</summary>
public sealed record LiveRouteInfo(string Name, LiveRouteKind Kind, double? CapacityDownBps, double? CapacityUpBps);

/// <summary>
/// One site's Live Traffic state, in memory only: the agent's latest flows with two minutes of
/// rate history each, recently ended flows, ten minutes of DNS lookups, the names DNS answers gave
/// remote addresses, and the console data that labels it all. Nothing here is persisted: when the
/// last viewer leaves, the agent stops sending and this ages out.
/// </summary>
public sealed class LiveTrafficStore
{
    public static readonly TimeSpan LeaseLength = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LeaseRenewEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleFlowKept = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan EndedKept = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DnsKept = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan EnrichEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UniFiMatchKept = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UniFiBackfill = TimeSpan.FromMinutes(30);
    private const int HistoryPoints = 60;
    private const int MaxEnded = 300;
    private const int MaxDns = 2000;
    private const int MaxNames = 20000;
    private const int MaxUniFiFlows = 20000;

    private sealed class FlowState
    {
        public required LiveFlow Last;
        public DateTime LastSeen;
        public double DownBps, UpBps;
        public readonly Queue<(double Down, double Up)> History = new();
    }

    private sealed record DnsEntry(DateTime At, LiveDnsEvent Event);

    private sealed record DnsLookup(string ClientIp, string Name, string QType, string ServerIp, double LatencyMs, DateTime At, DateTime Expires);

    /// <summary>The response time in ms: microseconds when the agent sends them, else its whole milliseconds.</summary>
    private static double LatencyOf(LiveDnsEvent e) => e.LatencyUs > 0 ? e.LatencyUs / 1000.0 : e.LatencyMs;

    private sealed record UniFiMatch(
        string? App, string? Category, string? PolicyName, string? PolicyId, string? PolicyType,
        string? OutNetwork, DateTime? Started, string? Domain, string? Service, string? InNetwork, DateTime Seen);

    private readonly object _gate = new();
    private readonly Dictionary<string, FlowState> _flows = new();
    private readonly LinkedList<(DateTime EndedAt, LiveFlow Flow)> _ended = new();
    private readonly LinkedList<DnsEntry> _dns = new();
    /// <summary>Site addresses other clients send DNS to, by when that was last seen.</summary>
    private readonly Dictionary<string, DateTime> _siteResolvers = new();
    private static readonly TimeSpan SiteResolverKept = TimeSpan.FromMinutes(30);
    /// <summary>The organization that owns each public remote address (GeoLite2 ASN); null when unknown.</summary>
    private readonly Dictionary<string, string?> _orgs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>PTR name per public remote address with no DNS name; null when it has none.</summary>
    private readonly Dictionary<string, string?> _ptr = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// The policy our own evaluation names for a live flow UniFi Network has not reported, by 5-tuple; a null
    /// name marks one that could not be decided, so it is not tried again every refresh.
    /// </summary>
    private readonly Dictionary<(string, int, string, int, string), (string? Name, DateTime At)> _evaluated = new();
    private static readonly TimeSpan EvaluatedKept = TimeSpan.FromMinutes(2);
    /// <summary>The site each known site public address belongs to (Trusted Sources' address books).</summary>
    private Dictionary<string, string> _siteByAddress = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Prefix, string Site)> _siteByPrefix = new();
    private List<string> _siteSubnets = new();
    private readonly ConcurrentDictionary<string, DateTime> _localNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LocalNameKept = TimeSpan.FromHours(1);
    private DateTime _siteAddressesAt = DateTime.MinValue;
    private static readonly TimeSpan SiteAddressesEvery = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, DnsLookup> _names = new();
    /// <summary>Console records by 5-tuple: the client port tells two connections to one server apart.</summary>
    private readonly Dictionary<(string Client, int ClientPort, string Remote, int Port, string Proto), UniFiMatch> _uniFiFlows = new();
    private readonly Dictionary<string, string> _learnedRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _viewers = new();

    private Dictionary<string, LiveClientInfo> _clientsByMac = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _macByIp = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LiveRouteInfo> _routesByIfname = new(StringComparer.OrdinalIgnoreCase);
    private LiveRouteInfo? _defaultRoute;
    private HashSet<string> _gatewayIps = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>This install's own host names and address: a flow to one is Network Optimizer's own traffic.</summary>
    private IReadOnlySet<string> _serverHosts = new HashSet<string>();
    private string _gatewayName = "Gateway";

    private DateTime _lastBatchAt = DateTime.MinValue;
    /// <summary>Batches received back to back; a gap of several windows starts it again.</summary>
    private int _streak;
    private int _windowSeconds = 2;
    private int _totalFlows;
    private bool _dnsActive;
    private DateTime _lastPushAt = DateTime.MinValue;
    private DateTime _lastEnrichAt = DateTime.MinValue;
    private DateTime _lastClientsAt = DateTime.MinValue;
    private DateTime? _uniFiNewest;

    // ---------------------------------------------------------------- agent side

    /// <summary>Takes one batch from the gateway agent.</summary>
    public void Ingest(LiveTrafficBatch batch)
    {
        var now = DateTime.UtcNow;
        var window = Math.Max(1, batch.WindowSeconds);
        lock (_gate)
        {
            _streak = now - _lastBatchAt <= TimeSpan.FromSeconds(Math.Max(10, window * 4)) ? _streak + 1 : 1;
            // A new stream: rows from the last one carry rates from before the gap.
            if (_streak == 1) _flows.Clear();
            _lastBatchAt = now;
            _windowSeconds = window;
            _totalFlows = batch.TotalFlows;
            _dnsActive = batch.DnsActive;

            foreach (var flow in batch.Flows)
            {
                if (!_flows.TryGetValue(flow.Key, out var state))
                    _flows[flow.Key] = state = new FlowState { Last = flow };
                state.Last = flow;
                state.LastSeen = now;
                state.DownBps = flow.DownBytes * 8.0 / window;
                state.UpBps = flow.UpBytes * 8.0 / window;
                state.History.Enqueue((state.DownBps, state.UpBps));
                while (state.History.Count > HistoryPoints) state.History.Dequeue();
            }
            // A flow the batch did not carry moved nothing this window.
            foreach (var state in _flows.Values)
            {
                if (state.LastSeen == now) continue;
                state.DownBps = state.UpBps = 0;
                state.History.Enqueue((0, 0));
                while (state.History.Count > HistoryPoints) state.History.Dequeue();
            }

            foreach (var flow in batch.Ended)
            {
                _flows.Remove(flow.Key);
                _ended.AddFirst((now, flow));
                while (_ended.Count > MaxEnded) _ended.RemoveLast();
            }

            foreach (var e in batch.Dns)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(e.AtUnixMs).UtcDateTime;
                _dns.AddFirst(new DnsEntry(at, e));
                while (_dns.Count > MaxDns) _dns.RemoveLast();
                if (!_gatewayIps.Contains(e.ServerIp) && IsSiteAddress(e.ServerIp))
                    _siteResolvers[e.ServerIp] = now;
                if (!e.Answered) continue;
                foreach (var a in e.Answers)
                {
                    if (a.Type is not ("A" or "AAAA")) continue;
                    if (_names.Count >= MaxNames && !_names.ContainsKey(a.Data)) continue;
                    var ttl = TimeSpan.FromSeconds(Math.Clamp(a.Ttl, 60, 3600));
                    _names[a.Data] = new DnsLookup(e.ClientIp, e.Name, e.Qtype, e.ServerIp, LatencyOf(e), at, now + ttl);
                }
            }

            Prune(now);
        }
    }

    private void Prune(DateTime now)
    {
        List<string>? idle = null;
        foreach (var (key, state) in _flows)
            if (now - state.LastSeen > IdleFlowKept) (idle ??= new List<string>()).Add(key);
        if (idle != null)
            foreach (var key in idle) _flows.Remove(key);
        while (_ended.Last is { } oldest && now - oldest.Value.EndedAt > EndedKept) _ended.RemoveLast();
        while (_dns.Last is { } oldestDns && now - oldestDns.Value.At > DnsKept) _dns.RemoveLast();
        foreach (var stale in _siteResolvers.Where(kv => now - kv.Value > SiteResolverKept).Select(kv => kv.Key).ToList())
            _siteResolvers.Remove(stale);
        if (_names.Count > MaxNames / 2)
        {
            foreach (var key in _names.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToList())
                _names.Remove(key);
        }
    }

    // ---------------------------------------------------------------- viewers and leases

    /// <summary>
    /// Marks a viewer as present. True when the agent's lease is due to be granted or renewed;
    /// the caller sends it and calls <see cref="NoteLeasePushed"/>.
    /// </summary>
    public bool TouchViewer(string viewerId, DateTime now)
    {
        lock (_gate)
        {
            var first = !_viewers.Keys.Any(k => now - _viewers[k] <= ViewerTimeout);
            _viewers[viewerId] = now;
            return first || now - _lastPushAt >= LeaseRenewEvery;
        }
    }

    public void NoteLeasePushed(DateTime now)
    {
        lock (_gate) _lastPushAt = now;
    }

    /// <summary>
    /// This install's own host names (and HOST_IP): a flow whose destination's DNS name, or address,
    /// is one of them is named Network Optimizer, such as an On-Site Agent's tunnel or a browser on the app.
    /// </summary>
    public void SetServerHosts(IReadOnlySet<string> hosts)
    {
        lock (_gate) _serverHosts = hosts;
    }

    /// <summary>Drops a viewer. True when nobody is left, so the lease can end now rather than by timeout.</summary>
    public bool ReleaseViewer(string viewerId, DateTime now)
    {
        lock (_gate)
        {
            _viewers.Remove(viewerId);
            foreach (var stale in _viewers.Where(kv => now - kv.Value > ViewerTimeout).Select(kv => kv.Key).ToList())
                _viewers.Remove(stale);
            if (_viewers.Count > 0) return false;
            _lastPushAt = DateTime.MinValue;
            // The lease ends now and the agent's tracker with it: its next batch only seeds. Forget the
            // stream and its rows, or a return reads the old rows as live and the seed batch blanks them.
            _streak = 0;
            _flows.Clear();
            return true;
        }
    }

    // ---------------------------------------------------------------- enrichment

    public bool EnrichmentDue(DateTime now)
    {
        lock (_gate)
        {
            if (now - _lastEnrichAt < EnrichEvery) return false;
            _lastEnrichAt = now;
            return true;
        }
    }

    public bool ClientsDue(DateTime now)
    {
        lock (_gate)
        {
            if (now - _lastClientsAt < TimeSpan.FromSeconds(30)) return false;
            _lastClientsAt = now;
            return true;
        }
    }

    /// <summary>The console's client list, the gateway's own addresses and name, and the WAN routes.</summary>
    public void SetSiteData(
        IReadOnlyCollection<LiveClientInfo>? clients,
        IReadOnlyCollection<string>? gatewayIps,
        string? gatewayName,
        IReadOnlyDictionary<string, LiveRouteInfo>? routesByIfname,
        LiveRouteInfo? defaultRoute)
    {
        lock (_gate)
        {
            if (clients != null)
            {
                _clientsByMac = clients
                    .Where(c => !string.IsNullOrEmpty(c.Mac))
                    .GroupBy(c => c.Mac, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                _macByIp = clients
                    .Where(c => !string.IsNullOrEmpty(c.Ip) && !string.IsNullOrEmpty(c.Mac))
                    .GroupBy(c => c.Ip, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Mac, StringComparer.OrdinalIgnoreCase);
            }
            if (gatewayIps != null) _gatewayIps = new HashSet<string>(gatewayIps, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(gatewayName)) _gatewayName = gatewayName!;
            if (routesByIfname != null)
                _routesByIfname = new Dictionary<string, LiveRouteInfo>(routesByIfname, StringComparer.OrdinalIgnoreCase);
            if (defaultRoute != null) _defaultRoute = defaultRoute;
        }
    }

    /// <summary>
    /// Where the next label read starts: just behind the newest record merged, at most two minutes back.
    /// The first read goes back <see cref="UniFiBackfill"/>: a flow already running when the page opens
    /// has its record from when it started, which a two-minute window never reaches.
    /// </summary>
    public DateTime UniFiFetchFrom(DateTime now)
    {
        lock (_gate)
        {
            if (_uniFiNewest == null) return now - UniFiBackfill;
            var floor = now - TimeSpan.FromMinutes(2);
            return _uniFiNewest.Value - TimeSpan.FromSeconds(30) > floor ? _uniFiNewest.Value - TimeSpan.FromSeconds(30) : floor;
        }
    }

    /// <summary>True until the first console records are merged: the read that backfills.</summary>
    public bool UniFiBackfilling
    {
        get { lock (_gate) return _uniFiNewest == null; }
    }

    /// <summary>How far back the console records have been read: the oldest window start so far.</summary>
    private DateTime? _backfilledTo;
    private static readonly TimeSpan BackfillStep = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BackfillLimit = TimeSpan.FromHours(24);

    /// <summary>
    /// The next older window of console records to read, or null when every unmatched live flow's start
    /// is covered. A long-running flow (a VPN tunnel, a persistent connection) has its record from when
    /// it started, hours back, so each refresh reads one more step toward the oldest unmatched start,
    /// at most a day back. Claims the window, so a failed read just moves on.
    /// </summary>
    public (DateTime From, DateTime To)? NextBackfillWindow(DateTime now)
    {
        lock (_gate)
        {
            if (_uniFiNewest == null) return null;
            _backfilledTo ??= now - UniFiBackfill;
            var limit = now - BackfillLimit;
            DateTime? target = null;
            foreach (var s in _flows.Values)
            {
                var f = s.Last;
                if (f.Lan || f.Gateway) continue;
                if (_uniFiFlows.ContainsKey((f.ClientIp, f.ClientPort, f.RemoteIp, f.RemotePort, (f.Proto ?? "").ToLowerInvariant()))) continue;
                // A flow older than the agent has no known start. Never walk back for one: chasing it sent
                // every page open a day back, ten minutes per refresh, as load on the console (often the gateway).
                if (f.AgeIsLowerBound) continue;
                var start = now - TimeSpan.FromSeconds(f.AgeSeconds) - TimeSpan.FromMinutes(1);
                if (target == null || start < target) target = start;
            }
            if (target == null || _backfilledTo <= target || _backfilledTo <= limit) return null;
            var to = _backfilledTo.Value;
            var from = new[] { to - BackfillStep, target.Value, limit }.Max();
            _backfilledTo = from;
            return (from, to);
        }
    }

    /// <summary>Takes backfilled console records, keeping only those that match a live flow.</summary>
    public void MergeBackfill(IEnumerable<UniFiTrafficFlow> flows)
    {
        HashSet<(string, int, string, int, string)> live;
        lock (_gate)
        {
            live = _flows.Values
                .Select(s => (s.Last.ClientIp, s.Last.ClientPort, s.Last.RemoteIp, s.Last.RemotePort, (s.Last.Proto ?? "").ToLowerInvariant()))
                .ToHashSet();
        }
        MergeUniFiFlows(flows.Where(f => f.Source?.Ip is { } c && f.Destination?.Ip is { } r
            && live.Contains((c, f.Source.Port ?? 0, r, f.Destination.Port ?? 0, (f.Protocol ?? "").ToLowerInvariant()))));
    }

    /// <summary>Takes a page of the console's flow history: application, policy, egress, and start time per flow.</summary>
    public void MergeUniFiFlows(IEnumerable<UniFiTrafficFlow> flows)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var f in flows)
            {
                if (f.Time > 0 && DateTimeOffset.FromUnixTimeMilliseconds(f.Time).UtcDateTime is var seen && (_uniFiNewest == null || seen > _uniFiNewest))
                    _uniFiNewest = seen;
                var client = f.Source?.Ip;
                var remote = f.Destination?.Ip;
                if (string.IsNullOrEmpty(client) || string.IsNullOrEmpty(remote)) continue;
                var key = (client!, f.Source?.Port ?? 0, remote!, f.Destination?.Port ?? 0, (f.Protocol ?? "").ToLowerInvariant());
                string? app = null, category = null;
                if (f.Application is { App: { } a, Category: { } c })
                {
                    app = DpiCatalog.AppName(c, a);
                    category = DpiCatalog.CategoryName(c);
                }
                var policy = f.Policies?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Name));
                var started = f.FlowStartTime > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(f.FlowStartTime).UtcDateTime : (DateTime?)null;
                if (_uniFiFlows.TryGetValue(key, out var existing) && existing.Started is { } s && started is { } s2 && s < s2)
                    started = s;
                _uniFiFlows[key] = new UniFiMatch(app, category, policy?.Name, policy?.Id, policy?.Type,
                    f.Out?.NetworkName, started, f.Destination?.Domains?.FirstOrDefault(), f.Service, f.In?.NetworkName, now);
            }
            // A record whose flow is still live stays however long ago it was read: the console may not
            // write that flow again for as long as it runs.
            var live = _flows.Values
                .Select(s => (s.Last.ClientIp, s.Last.ClientPort, s.Last.RemoteIp, s.Last.RemotePort, (s.Last.Proto ?? "").ToLowerInvariant()))
                .ToHashSet();
            foreach (var stale in _uniFiFlows.Where(kv => now - kv.Value.Seen > UniFiMatchKept && !live.Contains(kv.Key)).Select(kv => kv.Key).ToList())
                _uniFiFlows.Remove(stale);
            if (_uniFiFlows.Count > MaxUniFiFlows)
            {
                foreach (var key in _uniFiFlows.Where(kv => !live.Contains(kv.Key)).OrderBy(kv => kv.Value.Seen)
                    .Take(_uniFiFlows.Count - MaxUniFiFlows / 2).Select(kv => kv.Key).ToList())
                    _uniFiFlows.Remove(key);
            }
        }
    }

    private DateTime _lastSummaryAt = DateTime.MinValue;

    /// <summary>True once a minute: when the match summary is due in the log.</summary>
    public bool MatchSummaryDue(DateTime now)
    {
        lock (_gate)
        {
            if (now - _lastSummaryAt < TimeSpan.FromMinutes(1)) return false;
            _lastSummaryAt = now;
            return true;
        }
    }

    /// <summary>
    /// How the active WAN flows got their App: matched to a console record (the gateway's DPI), by
    /// flow age, or which fallback named them. For the log, to see where the DPI match falls short.
    /// </summary>
    public string MatchSummary()
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            int total = 0, matched = 0, young = 0, youngMatched = 0, old = 0, oldMatched = 0, dpi = 0, dns = 0, owner = 0, bare = 0;
            foreach (var s in _flows.Values)
            {
                var f = s.Last;
                if (f.Lan || f.Gateway) continue;
                total++;
                var proto = (f.Proto ?? "").ToLowerInvariant();
                var hit = _uniFiFlows.TryGetValue((f.ClientIp, f.ClientPort, f.RemoteIp, f.RemotePort, proto), out var m);
                if (hit) matched++;
                if (hit && m!.App != null) dpi++;
                if (f.AgeSeconds < 120) { young++; if (hit) youngMatched++; }
                else { old++; if (hit) oldMatched++; }
                if (_names.TryGetValue(f.RemoteIp, out var n) && n.Expires > now) dns++;
                if (_orgs.TryGetValue(f.RemoteIp, out var o) && o != null) owner++;
                if (!(hit && m!.App != null) && NetworkUtilities.GetPortServiceName(f.RemotePort) == null) bare++;
            }
            return $"{total} WAN flows: {matched} matched a console record ({dpi} with a DPI app); " +
                $"under 2 min {youngMatched}/{young}, older {oldMatched}/{old}; " +
                $"DNS name {dns}, owner {owner}, no DPI app or known port {bare}; " +
                $"{_uniFiFlows.Count} console records held";
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>Whether the agent has sent anything recently.</summary>
    /// <summary>
    /// Receiving, and for at least two batches in a row: a lease's first batch has no earlier
    /// snapshot to difference against, so every rate in it reads zero.
    /// </summary>
    public bool HasRates(DateTime now)
    {
        lock (_gate) return _streak >= 2 && now - _lastBatchAt <= TimeSpan.FromSeconds(Math.Max(10, _windowSeconds * 4));
    }

    public LiveTrafficView BuildView(LiveTrafficQuery query, LiveTrafficState state)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            var active = _flows.Values.Select(s => ToRow(s.Last, s.DownBps, s.UpBps, s.History.ToList(), false, now)).ToList();
            var all = query.Ended
                ? _ended.Select(e => ToRow(e.Flow, 0, 0, Array.Empty<(double, double)>(), true, now, e.EndedAt)).ToList()
                : active;
            if (query.WanOnly) all = all.Where(r => r.RouteKind is LiveRouteKind.Wan or LiveRouteKind.Vpn).ToList();
            if (query.Client is { } scope) all = all.Where(r => InScope(r, scope)).ToList();

            var matching = all.Where(r => Matches(r, query)).ToList();
            // Ended flows carry no rate: a rate sort keeps them most recently ended first.
            var sorted = query.Ended && query.Sort is LiveFlowSort.Download or LiveFlowSort.Upload
                ? matching
                : SortFlows(matching, query.Sort, query.Descending);

            var dnsRows = query.IncludeDns ? BuildDns(query, active, now) : new List<LiveDnsRow>();
            var recentDns = _dns.Where(d => now - d.At <= TimeSpan.FromSeconds(60)).ToList();
            var bypassers = _dns
                .Where(d => d.Event.RedirectedTo.Length == 0 && IsBypassResolver(d.Event.ServerIp))
                .Select(d => d.Event.ClientIp)
                .Concat(active.Where(r => r.DnsBypass).Select(r => r.ClientIp))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return new LiveTrafficView
            {
                State = state,
                At = _lastBatchAt == DateTime.MinValue ? null : _lastBatchAt,
                Flows = (query.Ended ? sorted : SortFlows(Bundle(matching), query.Sort, query.Descending))
                    .Take(Math.Max(1, query.MaxRows)).ToList(),
                Groups = query.GroupBy == LiveTrafficGroupBy.Flows ? Array.Empty<LiveGroupRow>() : Group(sorted, query.GroupBy),
                MatchingFlows = matching.Count,
                MatchingDownBps = matching.Sum(r => r.DownBps),
                MatchingUpBps = matching.Sum(r => r.UpBps),
                TotalFlows = _totalFlows,
                Clients = active.Where(r => !r.IsGateway)
                    .Select(r => r.ClientMac.Length > 0 ? r.ClientMac : r.ClientIp).Distinct().Count(),
                NewLastMinute = active.Count(r => r.Started is { } s && now - s <= TimeSpan.FromMinutes(1) && !r.StartedIsUpperBound),
                Routes = RouteStats(active),
                Dns = dnsRows,
                DnsPerSecond = recentDns.Count / 60.0,
                NxdomainPercent = recentDns.Count == 0 ? 0 : 100.0 * recentDns.Count(d => IsBlocked(d.Event)) / recentDns.Count,
                BypassingClients = bypassers,
                DnsActive = _dnsActive,
                Sankey = query.IncludeSankey && !query.Ended ? BuildSankey(matching, query.SankeyByFlows) : null,
            };
        }
    }

    // ---------------------------------------------------------------- row building

    private LiveFlowRow ToRow(LiveFlow f, double down, double up, IReadOnlyList<(double, double)> history, bool ended, DateTime now, DateTime? endedAt = null)
    {
        var proto = f.Proto.ToLowerInvariant();
        _uniFiFlows.TryGetValue((f.ClientIp, f.ClientPort, f.RemoteIp, f.RemotePort, proto), out var match);
        var evaluatedPolicy = match == null && _evaluated.TryGetValue((f.ClientIp, f.ClientPort, f.RemoteIp, f.RemotePort, proto), out var ev)
            ? ev.Name : null;

        var (route, kind) = ResolveRoute(f, match);

        // Client: the agent's MAC, else the console's IP map, else the gateway itself.
        var mac = f.ClientMac.Length > 0 ? NormalizeMac(f.ClientMac)
            : _macByIp.TryGetValue(f.ClientIp, out var m) ? m : "";
        _clientsByMac.TryGetValue(mac, out var client);
        var clientName = f.Gateway || _gatewayIps.Contains(f.ClientIp) ? _gatewayName
            : client?.Name ?? UnlistedClientName(f.ClientIp);

        // Destination: the name DNS answered with, the console's domain, a site client, the address's
        // owner, or the address.
        _orgs.TryGetValue(f.RemoteIp, out var org);
        var remoteSite = f.Lan ? null : SiteFor(f.RemoteIp);
        // A site client or UniFi device at the far end (inter-VLAN traffic): its UniFi name wins over
        // whatever local DNS calls it, as it does for the client column.
        LiveClientInfo? peer = _macByIp.TryGetValue(f.RemoteIp, out var peerMac) && _clientsByMac.TryGetValue(peerMac, out var p) ? p : null;
        string destination;
        string? dnsDetail = null;
        var hasLookup = _names.TryGetValue(f.RemoteIp, out var lookup) && lookup.Expires > now;
        if (hasLookup)
            dnsDetail = $"{lookup!.QType} via {ResolverName(lookup.ServerIp)}" + (lookup.LatencyMs >= 0 ? $", answered in {DisplayFormatters.FormatResponseMs(lookup.LatencyMs)}" : "");
        if (peer != null) destination = peer.Name;
        else if (hasLookup) destination = lookup!.Name;
        else if (!string.IsNullOrEmpty(match?.Domain)) destination = match!.Domain!;
        else if (remoteSite != null) destination = f.RemoteIp;
        else if (_ptr.TryGetValue(f.RemoteIp, out var ptr) && ptr != null) destination = ptr;
        else if (org != null) destination = org;
        else destination = f.RemoteIp;

        // An inbound connection (a port forward) has the known port on the client's side.
        var remotePortName = NetworkUtilities.GetPortServiceName(f.RemotePort);
        var portName = remotePortName
            ?? (f.RemotePort >= 1024 ? NetworkUtilities.GetPortServiceName(f.ClientPort) : null);
        var service = match?.Service ?? portName;
        var protoLabel = proto.ToUpperInvariant();
        var destSub = service != null && proto == "tcp" ? service
            : service != null ? (proto == "udp" && service is "HTTPS" ? "QUIC" : service)
            : f.RemotePort > 0 ? $"{protoLabel} {f.RemotePort}" : protoLabel;
        // The gateway's DPI first, then the domain the flow went to, then the port, then the site or
        // who owns the address.
        var hostName = _names.TryGetValue(f.RemoteIp, out var named) && named.Expires > now ? named.Name
            : match?.Domain ?? (_ptr.TryGetValue(f.RemoteIp, out var ptrName) ? ptrName : null);
        var toServer = (hostName != null && _serverHosts.Contains(hostName.TrimEnd('.'))) || _serverHosts.Contains(f.RemoteIp);
        var app = DomainAppCatalog.ProtocolApp(proto) ?? (toServer ? "Network Optimizer" : null)
            ?? DomainAppCatalog.TailscaleApp(proto, f.ClientPort, f.RemotePort) ?? match?.App ?? DomainAppCatalog.AppFor(hostName)
            ?? DomainAppCatalog.SpeedtestApp(hostName, proto, f.RemotePort)
            ?? (f.Lan ? DomainAppCatalog.LanApp(proto, f.RemotePort) : DomainAppCatalog.WifiCalling(proto, f.RemotePort, org) ?? DomainAppCatalog.OwnerApp(org))
            // The client end may be the server (an iperf3 server answering a test): its port names it then.
            ?? DomainAppCatalog.PortApp(proto, f.RemotePort) ?? (f.RemotePort >= 1024 ? DomainAppCatalog.PortApp(proto, f.ClientPort) : null)
            ?? portName ?? remoteSite ?? (org != null && !f.Lan ? org : null)
            ?? (f.RemotePort > 0 ? $"{protoLabel} {f.RemotePort}" : protoLabel);
        var (appIconDomain, appIconClass) = remoteSite != null && app == remoteSite
            ? (null, "fa-solid fa-building")   // one of your sites, not an app: a site mark, not its initial
            : DpiCatalog.IconsForName(app);

        // A site resolver's own encrypted upstream is the site's DNS working as intended, not a bypass.
        // Its plain-DNS upstream still counts: that shows as a lookup to a public resolver.
        var encryptedDns = f.RemotePort == 853
            || (f.RemotePort == 443 && !f.Lan && DohProviderRegistry.IdentifyProviderFromIp(f.RemoteIp) != null);
        var resolverUpstream = encryptedDns && IsSiteResolver(f.ClientIp, mac);

        DateTime? started;
        var upperBound = false;
        // The kernel's own age is exact; the console's start time only fills in where the kernel has none.
        // An ended flow's age was taken when it ended, so it counts back from then.
        if (f.AgeIsLowerBound && match?.Started is { } s) started = s;
        else
        {
            started = (endedAt ?? now) - TimeSpan.FromSeconds(Math.Max(0, f.AgeSeconds));
            upperBound = f.AgeIsLowerBound;
        }

        return new LiveFlowRow
        {
            Key = f.Key,
            ClientMac = mac,
            ClientName = clientName,
            ClientIp = f.ClientIp,
            ClientVia = f.Gateway ? null : client?.Via ?? client?.Network,
            Destination = destination,
            DestinationIp = f.RemoteIp,
            DestinationSub = destSub,
            App = encryptedDns && match?.App == null ? (f.RemotePort == 853 ? "DNS over TLS" : "DNS over HTTPS") : app,
            Category = match?.Category,
            Route = route,
            RouteKind = kind,
            IsGateway = f.Gateway || _gatewayIps.Contains(f.ClientIp),
            DownBps = down,
            UpBps = up,
            TotalDownBytes = f.TotalDownBytes,
            TotalUpBytes = f.TotalUpBytes,
            TotalDownPackets = f.TotalDownPackets,
            TotalUpPackets = f.TotalUpPackets,
            Started = started,
            StartedIsUpperBound = upperBound,
            Proto = protoLabel,
            ClientPort = f.ClientPort,
            RemotePort = f.RemotePort,
            PolicyName = match?.PolicyName ?? evaluatedPolicy,
            ConsoleReported = match != null,
            PolicyEvaluated = match == null && evaluatedPolicy != null,
            PolicyId = match?.PolicyId,
            PolicyType = match?.PolicyType,
            Network = match?.InNetwork ?? client?.Network,
            DnsDetail = dnsDetail,
            EncryptedDns = encryptedDns,
            AppIconDomain = appIconDomain,
            AppIconClass = appIconClass,
            RemoteSite = remoteSite,
            RemoteClientMac = peer?.Mac,
            RemoteClientVia = peer == null ? null : peer.Via ?? peer.Network,
            RemoteNetwork = peer?.Network,
            RemoteOrg = f.Lan ? null : org,
            DnsBypass = encryptedDns && !resolverUpstream,
            History = history,
            Ended = ended,
            EndedAt = endedAt,
        };
    }

    private (string Name, LiveRouteKind Kind) ResolveRoute(LiveFlow f, UniFiMatch? match)
    {
        if (f.Lan) return ("LAN", LiveRouteKind.Lan);
        var ifname = f.WanIfname;
        if (ifname.Length > 0 && _routesByIfname.TryGetValue(ifname, out var wan)) return (wan.Name, wan.Kind);
        if (ifname.Length > 0 && match?.OutNetwork is { Length: > 0 } outName)
        {
            // One matched flow names the interface for every flow that leaves by it.
            _learnedRoutes[ifname] = outName;
        }
        if (ifname.Length > 0 && _learnedRoutes.TryGetValue(ifname, out var learned))
            return (learned, IsTunnel(ifname) ? LiveRouteKind.Vpn : LiveRouteKind.Wan);
        if (ifname.Length == 0) return (_defaultRoute?.Name ?? "WAN", LiveRouteKind.Wan);
        return (ifname, IsTunnel(ifname) ? LiveRouteKind.Vpn : LiveRouteKind.Wan);
    }

    // Kind only; the name comes from the console's network names, so Live and History agree. The
    // prefixes match InterfaceLabelResolver's tunnel rules (wgclt, tun, ovpn, vtun) plus IPsec.
    private static bool IsTunnel(string ifname) =>
        ifname.StartsWith("wg", StringComparison.OrdinalIgnoreCase)
        || ifname.StartsWith("tun", StringComparison.OrdinalIgnoreCase)
        || ifname.StartsWith("ovpn", StringComparison.OrdinalIgnoreCase)
        || ifname.StartsWith("vtun", StringComparison.OrdinalIgnoreCase)
        || ifname.StartsWith("vti", StringComparison.OrdinalIgnoreCase)
        || ifname.StartsWith("tap", StringComparison.OrdinalIgnoreCase)
        || ifname.StartsWith("ipsec", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeMac(string mac) => mac.Replace('-', ':').ToLowerInvariant();

    // ---------------------------------------------------------------- filters

    private static bool Matches(LiveFlowRow r, LiveTrafficQuery q) => Matches(r, q.Filters, q.Search);

    /// <summary>Whether a row is the scoped client's. The gateway's own traffic never is.</summary>
    internal static bool InScope(LiveFlowRow r, LiveClientScope scope) => !r.IsGateway && scope.Contains(r.ClientMac, r.ClientIp);

    /// <summary>Whether a row passes every chip and the search box. Live and History share it.</summary>
    internal static bool Matches(LiveFlowRow r, IReadOnlyList<LiveTrafficFilter> filters, string? search)
    {
        foreach (var f in filters)
        {
            if (f.Field == LiveTrafficField.Resolver) continue;
            var hit = FieldMatches(r, f.Field, f.Value);
            if (hit == f.Exclude) return false;
        }
        if (string.IsNullOrWhiteSpace(search)) return true;
        var s = search.Trim();
        return Contains(r.ClientName, s) || Contains(r.ClientIp, s) || Contains(r.Destination, s)
            || Contains(r.DestinationIp, s) || Contains(r.App, s) || Contains(r.Route, s)
            || Contains(r.DestinationSub, s) || AppOrService(r, s) || Eq(r.Proto, s) || Eq(r.Action, s)
            || r.RemotePort.ToString() == s || r.ClientPort.ToString() == s;
    }

    /// <summary>Whether a row carries the value in that field; the chip and the click share this.</summary>
    public static bool FieldMatches(LiveFlowRow r, LiveTrafficField field, string value) => field switch
    {
        LiveTrafficField.Client => Eq(r.ClientName, value) || Eq(r.ClientMac, value) || Eq(r.ClientIp, value),
        LiveTrafficField.Destination => Eq(r.Destination, value) || Eq(r.DestinationIp, value),
        LiveTrafficField.App => AppOrService(r, value),
        LiveTrafficField.Route => Eq(r.Route, value),
        LiveTrafficField.Protocol => Eq(r.Proto, value),
        LiveTrafficField.Port => r.RemotePort > 0 && r.RemotePort.ToString() == value.Trim(),
        LiveTrafficField.Action => Eq(r.Action, value),
        _ => false,
    };

    /// <summary>
    /// A service is a layer under the app: App = HTTPS keeps the HTTPS flows named Apple or Netflix too.
    /// HTTPS takes in QUIC (HTTP/3 on UDP 443); QUIC stays QUIC alone, so it still tells the two apart.
    /// </summary>
    private static bool AppOrService(LiveFlowRow r, string value) =>
        Eq(r.App, value) || Eq(r.DestinationSub, value)
        || (Eq(value.Trim(), "HTTPS") && (Eq(r.App, "QUIC") || Eq(r.DestinationSub, "QUIC")));

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string? a, string b) => a?.Contains(b, StringComparison.OrdinalIgnoreCase) == true;

    // ---------------------------------------------------------------- groups, routes, sankey

    /// <summary>Rolls rows up by client, app, destination, or route, busiest first. Live and History share it.</summary>
    internal static IReadOnlyList<LiveGroupRow> Group(IReadOnlyList<LiveFlowRow> rows, LiveTrafficGroupBy by)
    {
        var field = by switch
        {
            LiveTrafficGroupBy.Client => LiveTrafficField.Client,
            LiveTrafficGroupBy.App => LiveTrafficField.App,
            LiveTrafficGroupBy.Destination => LiveTrafficField.Destination,
            _ => LiveTrafficField.Route,
        };
        return rows
            .GroupBy(r => field switch
            {
                LiveTrafficField.Client => r.ClientName,
                LiveTrafficField.App => r.App,
                LiveTrafficField.Destination => r.Destination,
                _ => r.Route,
            }, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                string? sub = field switch
                {
                    LiveTrafficField.Client => first.ClientVia,
                    LiveTrafficField.App => first.Category,
                    LiveTrafficField.Destination => first.Destination != first.DestinationIp ? first.DestinationIp : null,
                    _ => g.Select(r => r.ClientName).Distinct().Count() is var n ? $"{n} client{(n == 1 ? "" : "s")}" : null,
                };
                return new LiveGroupRow(g.Key, sub, field,
                    g.Sum(r => r.DownBps), g.Sum(r => r.UpBps), g.Sum(r => r.TotalBytes), g.Count(),
                    field == LiveTrafficField.Route ? first.RouteKind : null,
                    field == LiveTrafficField.Client ? first.ClientMac : null);
            })
            .OrderByDescending(g => g.DownBps).ThenByDescending(g => g.UpBps).ThenByDescending(g => g.TotalBytes)
            .ToList();
    }

    private IReadOnlyList<LiveRouteStat> RouteStats(IReadOnlyList<LiveFlowRow> active)
    {
        var stats = active
            .Where(r => r.RouteKind is LiveRouteKind.Wan or LiveRouteKind.Vpn or LiveRouteKind.Gateway)
            .GroupBy(r => r.Route, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var info = _routesByIfname.Values.FirstOrDefault(v => Eq(v.Name, g.Key))
                    ?? (_defaultRoute is { } d && Eq(d.Name, g.Key) ? d : null);
                var kind = g.First().RouteKind == LiveRouteKind.Gateway ? LiveRouteKind.Wan : g.First().RouteKind;
                return new LiveRouteStat(g.Key, kind, g.Sum(r => r.DownBps), g.Sum(r => r.UpBps),
                    info?.CapacityDownBps, info?.CapacityUpBps,
                    g.Where(r => !r.IsGateway).Select(r => r.ClientMac.Length > 0 ? r.ClientMac : r.ClientIp).Distinct().Count());
            })
            .ToList();
        // Every known WAN gets a tile, moving or not.
        foreach (var info in _routesByIfname.Values.Append(_defaultRoute).OfType<LiveRouteInfo>().DistinctBy(i => i.Name))
            if (!stats.Any(s => Eq(s.Name, info.Name)))
                stats.Add(new LiveRouteStat(info.Name, info.Kind, 0, 0, info.CapacityDownBps, info.CapacityUpBps, 0));
        return stats.OrderBy(s => s.Kind).ThenByDescending(s => s.DownBps + s.UpBps).ToList();
    }

    private const int SankeyNodesPerColumn = 6;

    /// <summary>
    /// The gateway's own WireGuard or OpenVPN flow out a WAN: the outer tunnel whose bytes are the
    /// client flows routed through it, already shown under those clients.
    /// </summary>
    internal static bool IsTunnelCarrier(LiveFlowRow r) =>
        r.IsGateway && r.RouteKind == LiveRouteKind.Wan
        && (r.App.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) || r.App.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase));

    private static LiveSankey? BuildSankey(IReadOnlyList<LiveFlowRow> rows, bool byFlows)
    {
        // The tunnel itself would count its clients' traffic a second time. The Flows table and the
        // WAN tile still include it: those bytes do cross the WAN.
        rows = rows.Where(r => !IsTunnelCarrier(r)).ToList();
        var moving = byFlows ? rows.ToList() : rows.Where(r => r.DownBps + r.UpBps > 0).ToList();
        if (moving.Count == 0) return null;

        double V(LiveFlowRow r) => byFlows ? 1 : r.DownBps + r.UpBps;
        var topClients = moving.GroupBy(r => r.ClientName).OrderByDescending(g => g.Sum(V)).Take(SankeyNodesPerColumn).Select(g => g.Key).ToHashSet();
        var topApps = moving.GroupBy(r => r.App).OrderByDescending(g => g.Sum(V)).Take(SankeyNodesPerColumn).Select(g => g.Key).ToHashSet();
        var otherClients = moving.Select(r => r.ClientName).Distinct().Count(n => !topClients.Contains(n));
        var otherClientName = $"{otherClients} other{(otherClients == 1 ? "" : "s")}";
        const string otherApp = "Other";

        string ClientNode(LiveFlowRow r) => topClients.Contains(r.ClientName) ? r.ClientName : otherClientName;
        string AppNode(LiveFlowRow r) => topApps.Contains(r.App) ? r.App : otherApp;

        // Download and upload ride along so bands split by direction and nodes say both; a flow count has none.
        double Down(IEnumerable<LiveFlowRow> g) => byFlows ? 0 : g.Sum(r => r.DownBps);
        double Up(IEnumerable<LiveFlowRow> g) => byFlows ? 0 : g.Sum(r => r.UpBps);

        var nodes = new List<LiveSankeyNode>();
        nodes.AddRange(moving.GroupBy(ClientNode).Select(g => new LiveSankeyNode(0, g.Key, g.Sum(V), LiveTrafficField.Client, null, !topClients.Contains(g.Key),
            DownBps: Down(g), UpBps: Up(g), ClientIp: topClients.Contains(g.Key) ? g.First().ClientIp : null)));
        nodes.AddRange(moving.GroupBy(AppNode).Select(g => new LiveSankeyNode(1, g.Key, g.Sum(V), LiveTrafficField.App, null, !topApps.Contains(g.Key),
            g.First().AppIconDomain, g.First().AppIconClass, Down(g), Up(g))));
        nodes.AddRange(moving.GroupBy(r => r.Route).Select(g => new LiveSankeyNode(2, g.Key, g.Sum(V), LiveTrafficField.Route, g.First().RouteKind, false,
            DownBps: Down(g), UpBps: Up(g))));

        var links = new List<LiveSankeyLink>();
        links.AddRange(moving.GroupBy(r => (ClientNode(r), AppNode(r), r.RouteKind))
            .Select(g => new LiveSankeyLink(0, g.Key.Item1, g.Key.Item2, g.Sum(V), g.Key.RouteKind, Down(g), Up(g))));
        links.AddRange(moving.GroupBy(r => (AppNode(r), r.Route, r.RouteKind))
            .Select(g => new LiveSankeyLink(1, g.Key.Item1, g.Key.Route, g.Sum(V), g.Key.RouteKind, Down(g), Up(g))));
        return new LiveSankey(nodes, links, byFlows);
    }

    // ---------------------------------------------------------------- DNS

    private List<LiveDnsRow> BuildDns(LiveTrafficQuery query, IReadOnlyList<LiveFlowRow> active, DateTime now)
    {
        var rows = new List<LiveDnsRow>();
        foreach (var (name, seen) in _localNames)
            if (now - seen >= LocalNameKept) _localNames.TryRemove(name, out _);
        // Address answers first, so an AAAA with none ahead of its name's A in the window still counts as local.
        foreach (var d in _dns.Where(d => d.Event.Answers.Any(a => a.Type is "A" or "AAAA")))
            IsLocalLookup(d.Event, d.At);
        foreach (var d in _dns)
        {
            var e = d.Event;
            // A lookup the Gateway redirected to a site resolver tried to bypass it and did not.
            var redirected = e.RedirectedTo.Length > 0;
            var bypass = !redirected && IsBypassResolver(e.ServerIp);
            rows.Add(new LiveDnsRow(d.At, ClientNameFor(e.ClientMac, e.ClientIp), e.ClientIp, e.Name, e.Qtype,
                ResolverName(e.ServerIp), e.ServerIp, bypass, false, e.Rcode, LatencyOf(e), e.Answered, IsBlocked(e),
                ClientMacFor(e.ClientMac, e.ClientIp), IsLocalLookup(e, d.At), redirected ? ResolverName(e.RedirectedTo) : null));
        }
        // Encrypted DNS is a flow, not a lookup we can read: listed as a bypass with its provider.
        // A site resolver's own upstream is listed too, just not as a bypass.
        foreach (var r in active.Where(r => r.EncryptedDns))
        {
            var provider = DohProviderRegistry.IdentifyProviderFromIp(r.DestinationIp)?.Name;
            rows.Add(new LiveDnsRow(r.Started ?? now, r.ClientName, r.ClientIp, provider ?? r.Destination,
                r.RemotePort == 853 ? "DoT" : "DoH", r.DestinationIp, r.DestinationIp, r.DnsBypass, true, "", 0, true,
                ClientMac: r.ClientMac));
        }

        IEnumerable<LiveDnsRow> filtered = rows;
        if (query.Client is { } scope) filtered = filtered.Where(r => scope.Contains(r.ClientMac, r.ClientIp));
        if (query.HideLocalDns) filtered = filtered.Where(r => !r.Local);
        filtered = query.DnsMode switch
        {
            LiveDnsMode.Bypass => filtered.Where(r => r.Bypass || r.RedirectedTo != null),
            LiveDnsMode.Nxdomain => filtered.Where(r => r.Blocked),
            _ => filtered,
        };
        foreach (var f in query.Filters)
        {
            Func<LiveDnsRow, bool>? test = f.Field switch
            {
                LiveTrafficField.Client => r => Eq(r.ClientName, f.Value) || Eq(r.ClientMac, f.Value) || Eq(r.ClientIp, f.Value),
                LiveTrafficField.Resolver => r => Eq(r.Resolver, f.Value) || Eq(r.ResolverIp, f.Value),
                LiveTrafficField.Destination => r => Eq(r.Name, f.Value),
                _ => null,
            };
            if (test != null) filtered = filtered.Where(r => test(r) != f.Exclude);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            filtered = filtered.Where(r => Contains(r.ClientName, s) || Contains(r.Name, s) || Contains(r.Resolver, s) || Contains(r.ClientIp, s));
        }
        return CombineTypes(filtered.OrderByDescending(r => r.At)).Take(100).ToList();
    }

    private static readonly TimeSpan SameLookup = TimeSpan.FromSeconds(2);
    private static readonly string[] TypeOrder = ["A", "AAAA", "HTTPS"];

    /// <summary>
    /// One row per name a client asked its resolver for: the A, AAAA, and HTTPS lookups a client sends
    /// together (within <see cref="SameLookup"/> of each other) are one row typed "A · AAAA · HTTPS".
    /// The row is blocked when any of them was, and takes the slowest response. Expects newest first.
    /// </summary>
    internal static List<LiveDnsRow> CombineTypes(IEnumerable<LiveDnsRow> newestFirst)
    {
        // Groups stay in the order of their newest lookup, so the list stays newest first.
        var groups = new List<(LiveDnsRow Row, DateTime Oldest, List<string> Types)>();
        var latest = new Dictionary<(string, string, string, bool), int>();
        foreach (var r in newestFirst)
        {
            var key = (r.ClientMac.Length > 0 ? r.ClientMac : r.ClientIp, r.Name.ToLowerInvariant(), r.ResolverIp, r.Encrypted);
            if (latest.TryGetValue(key, out var i) && groups[i].Oldest - r.At <= SameLookup)
            {
                var g = groups[i];
                if (!g.Types.Contains(r.QType, StringComparer.OrdinalIgnoreCase)) g.Types.Add(r.QType);
                groups[i] = (g.Row with
                {
                    LatencyMs = Math.Max(g.Row.LatencyMs, r.LatencyMs),
                    Answered = g.Row.Answered || r.Answered,
                    Blocked = g.Row.Blocked || r.Blocked,
                    RCode = r.RCode == "NXDOMAIN" ? r.RCode : g.Row.RCode,
                }, r.At, g.Types);
                continue;
            }
            latest[key] = groups.Count;
            groups.Add((r, r.At, [r.QType]));
        }
        return groups.Select(g => Typed(g.Row, g.Types)).ToList();
    }

    private static LiveDnsRow Typed(LiveDnsRow row, List<string> types) => types.Count < 2 ? row : row with
    {
        QType = string.Join(" · ", types.OrderBy(t => Array.IndexOf(TypeOrder, t.ToUpperInvariant()) is var i && i >= 0 ? i : TypeOrder.Length)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)),
    };

    /// <summary>The lookup's MAC: the agent's, else the console's IP map, else empty.</summary>
    private string ClientMacFor(string mac, string ip) =>
        mac.Length > 0 ? NormalizeMac(mac) : _macByIp.TryGetValue(ip, out var m) ? m : "";

    private string ClientNameFor(string mac, string ip)
    {
        if (mac.Length > 0 && _clientsByMac.TryGetValue(NormalizeMac(mac), out var c)) return c.Name;
        if (_macByIp.TryGetValue(ip, out var m) && _clientsByMac.TryGetValue(m, out var c2)) return c2.Name;
        return UnlistedClientName(ip);
    }

    /// <summary>
    /// A client UniFi Network does not list: "VPN Client (192.0.2.7)" or "Tailscale (100.64.1.2)" when it
    /// connects that way, else its address. The address stays in the name, so two never merge.
    /// </summary>
    private string UnlistedClientName(string ip) => VpnKind(ip) is { } kind ? $"{kind} ({ip})" : ip;

    private string ResolverName(string ip)
    {
        if (_gatewayIps.Contains(ip)) return "Gateway";
        if (_macByIp.TryGetValue(ip, out var mac) && _clientsByMac.TryGetValue(mac, out var c)) return c.Name;
        return ip;
    }

    /// <summary>
    /// Parallel streams (one client, one destination address, port, and protocol, as a multi-connection
    /// download opens) become one row: rates, totals, and trend summed, the earliest start, and a stream
    /// count. A single flow passes through unchanged.
    /// </summary>
    internal static List<LiveFlowRow> Bundle(IReadOnlyList<LiveFlowRow> rows)
    {
        var bundled = new List<LiveFlowRow>(rows.Count);
        foreach (var g in rows.GroupBy(r => (r.ClientIp, r.DestinationIp, r.RemotePort, r.Proto)))
        {
            var streams = g.ToList();
            if (streams.Count == 1)
            {
                bundled.Add(streams[0]);
                continue;
            }
            var lead = streams.OrderByDescending(r => r.DownBps + r.UpBps).First();
            var length = streams.Max(r => r.History.Count);
            // Trends line up at their newest point; a stream that started later has fewer points.
            var history = Enumerable.Range(0, length).Select(i => (
                Down: streams.Sum(r => r.History.Count - length + i is var j && j >= 0 ? r.History[j].Down : 0),
                Up: streams.Sum(r => r.History.Count - length + i is var j && j >= 0 ? r.History[j].Up : 0))).ToList();
            var starts = streams.Where(r => r.Started != null).ToList();
            var earliest = starts.OrderBy(r => r.Started).FirstOrDefault();
            bundled.Add(lead with
            {
                Key = $"streams|{g.Key.ClientIp}|{g.Key.DestinationIp}|{g.Key.RemotePort}|{g.Key.Proto}",
                Streams = streams.Count,
                ClientPort = 0,
                DownBps = streams.Sum(r => r.DownBps),
                UpBps = streams.Sum(r => r.UpBps),
                TotalDownBytes = streams.Sum(r => r.TotalDownBytes),
                TotalUpBytes = streams.Sum(r => r.TotalUpBytes),
                TotalDownPackets = streams.Sum(r => r.TotalDownPackets),
                TotalUpPackets = streams.Sum(r => r.TotalUpPackets),
                Started = earliest?.Started,
                StartedIsUpperBound = earliest?.StartedIsUpperBound ?? false,
                History = history,
            });
        }
        return bundled;
    }

    /// <summary>Orders the Flows table by one column; ties fall back to download rate, then total bytes.</summary>
    internal static List<LiveFlowRow> SortFlows(List<LiveFlowRow> rows, LiveFlowSort sort, bool descending)
    {
        IOrderedEnumerable<LiveFlowRow> ordered = sort switch
        {
            LiveFlowSort.Upload => Order(rows, r => r.UpBps),
            LiveFlowSort.Total => Order(rows, r => r.TotalBytes),
            // Descending is newest first; an ended flow sorts by when it ended, as its label reads.
            LiveFlowSort.Started => Order(rows, r => (r.EndedAt ?? r.Started ?? DateTime.MinValue).Ticks),
            LiveFlowSort.Client => OrderText(rows, r => r.ClientName),
            LiveFlowSort.Destination => OrderText(rows, r => r.Destination),
            LiveFlowSort.App => OrderText(rows, r => r.App),
            LiveFlowSort.Route => OrderText(rows, r => r.Route),
            _ => Order(rows, r => r.DownBps),
        };
        return ordered.ThenByDescending(r => r.DownBps).ThenByDescending(r => r.TotalBytes).ToList();

        IOrderedEnumerable<LiveFlowRow> Order(IEnumerable<LiveFlowRow> src, Func<LiveFlowRow, double> key) =>
            descending ? src.OrderByDescending(key) : src.OrderBy(key);
        IOrderedEnumerable<LiveFlowRow> OrderText(IEnumerable<LiveFlowRow> src, Func<LiveFlowRow, string> key) =>
            descending ? src.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : src.OrderBy(key, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A lookup a blocker refused: NXDOMAIN, or the null-address answer Pi-hole, AdGuard Home, and
    /// most blocklists give instead (every A / AAAA answer 0.0.0.0 or ::).
    /// </summary>
    internal static bool IsBlocked(LiveDnsEvent e)
    {
        if (e.Rcode == "NXDOMAIN") return true;
        var addresses = e.Answers.Where(a => a.Type is "A" or "AAAA").ToList();
        return addresses.Count > 0 && addresses.All(a =>
            IPAddress.TryParse(a.Data, out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));
    }

    /// <summary>A resolver outside the site: the client is not using the site's DNS.</summary>
    private bool IsBypassResolver(string ip) => !_gatewayIps.Contains(ip) && !IsSiteAddress(ip);

    /// <summary>A private address or a known site client: inside the site, whatever its range.</summary>
    private bool IsSiteAddress(string ip) =>
        _macByIp.ContainsKey(ip) || (IPAddress.TryParse(ip, out var addr) && !NetworkUtilities.IsPublicIpAddress(addr));

    /// <summary>
    /// A site address other clients send DNS to, or another address on the same switch port as one:
    /// a resolver on several VLANs (a macvlan Pi-hole) goes upstream from an address it never answers
    /// on, and no MAC ties the two together.
    /// </summary>
    private bool IsSiteResolver(string ip, string mac)
    {
        if (_siteResolvers.ContainsKey(ip)) return true;
        if (!_clientsByMac.TryGetValue(mac, out var self) || self.SwitchMac == null || self.SwitchPort == null) return false;
        foreach (var resolverIp in _siteResolvers.Keys)
        {
            if (_macByIp.TryGetValue(resolverIp, out var rMac) && _clientsByMac.TryGetValue(rMac, out var r)
                && string.Equals(r.SwitchMac, self.SwitchMac, StringComparison.OrdinalIgnoreCase) && r.SwitchPort == self.SwitchPort)
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- address owners

    /// <summary>Public remote addresses of active flows that have no owner looked up yet (at most <paramref name="max"/>).</summary>
    public IReadOnlyList<string> AddressesWithoutOwner(int max)
    {
        lock (_gate)
        {
            return _flows.Values.Select(s => s.Last.RemoteIp)
                .Where(ip => !_orgs.ContainsKey(ip) && !_macByIp.ContainsKey(ip)
                    && IPAddress.TryParse(ip, out var a) && NetworkUtilities.IsPublicIpAddress(a))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }
    }

    /// <summary>Records who owns each address (the ASN's organization); null marks one with no answer.</summary>
    public void SetOwners(IReadOnlyDictionary<string, string?> owners)
    {
        lock (_gate)
        {
            if (_orgs.Count > MaxNames) _orgs.Clear();
            foreach (var (ip, org) in owners) _orgs[ip] = org;
        }
    }

    /// <summary>Public remote addresses of active flows with no DNS name, console domain, or PTR tried yet.</summary>
    /// <summary>
    /// Live client flows UniFi Network has not reported and our evaluation has not tried yet (at most
    /// <paramref name="max"/>, busiest first), as the connection the firewall saw: from the LAN end, unless the
    /// LAN end answers on a known service port from an ephemeral one outside (a port forward), which came in.
    /// A LAN-to-LAN flow does not say which end opened it, so it carries the reverse as well. Flows out a VPN
    /// client are left out: they leave through a VPN zone the address mapping does not place.
    /// </summary>
    public IReadOnlyList<((string, int, string, int, string) Key, LiveTrafficPolicyResolver.FlowQuery Query, LiveTrafficPolicyResolver.FlowQuery? Reverse)> FlowsToEvaluate(int max)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var stale in _evaluated.Where(kv => now - kv.Value.At > EvaluatedKept).Select(kv => kv.Key).ToList())
                _evaluated.Remove(stale);
            var list = new List<((string, int, string, int, string), LiveTrafficPolicyResolver.FlowQuery, LiveTrafficPolicyResolver.FlowQuery?)>();
            foreach (var s in _flows.Values.OrderByDescending(s => s.DownBps + s.UpBps))
            {
                var f = s.Last;
                if (f.Gateway || _gatewayIps.Contains(f.ClientIp) || IsTunnel(f.WanIfname)) continue;
                var proto = (f.Proto ?? "").ToLowerInvariant();
                var key = (f.ClientIp, f.ClientPort, f.RemoteIp, f.RemotePort, proto);
                if (_uniFiFlows.ContainsKey(key) || _evaluated.ContainsKey(key)) continue;
                var mac = f.ClientMac.Length > 0 ? NormalizeMac(f.ClientMac) : _macByIp.GetValueOrDefault(f.ClientIp);
                var remoteMac = _macByIp.GetValueOrDefault(f.RemoteIp);
                // The name the client asked for (a PTR name is not one, so it is not used).
                var name = _names.TryGetValue(f.RemoteIp, out var looked) && looked.Expires > now ? looked.Name : null;
                var outbound = new LiveTrafficPolicyResolver.FlowQuery(f.ClientIp, mac, f.ClientPort, f.RemoteIp, f.RemotePort, proto, name);
                var reverse = new LiveTrafficPolicyResolver.FlowQuery(f.RemoteIp, remoteMac, f.RemotePort, f.ClientIp, f.ClientPort, proto);
                var inbound = !f.Lan && f.RemotePort >= 1024 && NetworkUtilities.GetPortServiceName(f.ClientPort) != null
                    && NetworkUtilities.GetPortServiceName(f.RemotePort) == null;
                list.Add(f.Lan ? (key, outbound, reverse) : (key, inbound ? reverse : outbound, null));
                if (list.Count >= max) break;
            }
            return list;
        }
    }

    /// <summary>Records what our evaluation named for each flow; a null name marks one it could not decide.</summary>
    public void SetEvaluatedPolicies(IEnumerable<((string, int, string, int, string) Key, string? Name)> verdicts)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            foreach (var (key, name) in verdicts) _evaluated[key] = (name, now);
        }
    }

    public IReadOnlyList<string> AddressesWithoutPtr(int max)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            return _flows.Values.Select(s => s.Last)
                .Where(f => !_ptr.ContainsKey(f.RemoteIp) && !_macByIp.ContainsKey(f.RemoteIp)
                    && !(_names.TryGetValue(f.RemoteIp, out var n) && n.Expires > now)
                    && SiteFor(f.RemoteIp) == null
                    && IPAddress.TryParse(f.RemoteIp, out var a) && NetworkUtilities.IsPublicIpAddress(a))
                .OrderByDescending(f => f.DownBytes + f.UpBytes)
                .Select(f => f.RemoteIp)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }
    }

    /// <summary>Records PTR answers; null marks an address with none.</summary>
    public void SetPtrNames(IReadOnlyDictionary<string, string?> names)
    {
        lock (_gate)
        {
            if (_ptr.Count > MaxNames) _ptr.Clear();
            foreach (var (ip, name) in names) _ptr[ip] = name;
        }
    }

    /// <summary>True when the site address books are due a reload; marks them loading.</summary>
    public bool SiteAddressesDue(DateTime now)
    {
        lock (_gate)
        {
            if (now - _siteAddressesAt < SiteAddressesEvery) return false;
            _siteAddressesAt = now;
            return true;
        }
    }

    /// <summary>
    /// The public addresses each visible site has been seen at: single addresses and LAN IPv6 prefixes,
    /// keyed by the site's name.
    /// </summary>
    public void SetSiteAddresses(IEnumerable<(string Address, string Site)> addresses)
    {
        var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefixes = new List<(string, string)>();
        foreach (var (address, site) in addresses)
        {
            if (address.Contains('/')) prefixes.Add((address, site));
            else exact.TryAdd(address, site);
        }
        lock (_gate)
        {
            _siteByAddress = exact;
            _siteByPrefix = prefixes;
        }
    }

    /// <summary>The subnets of the site's own Networks (UniFi network configs), IPv4 and IPv6.</summary>
    public void SetSiteNetworks(IEnumerable<string> subnets)
    {
        var list = subnets.Where(s => !string.IsNullOrWhiteSpace(s) && s.Contains('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate) _siteSubnets = list;
    }

    /// <summary>
    /// The site's networks with their purpose, for naming a client UniFi Network does not list: a
    /// remote-user VPN, Tailscale, or Teleport client, classified as Client Performance does.
    /// </summary>
    public void SetNetworkPurposes(IReadOnlyCollection<(string? IpSubnet, string? Purpose)> networks)
    {
        lock (_gate) _networkPurposes = networks;
    }

    private IReadOnlyCollection<(string? IpSubnet, string? Purpose)>? _networkPurposes;

    /// <summary>
    /// "VPN Client" (inside a remote-user VPN network) or "Tailscale" for a VPN-sourced address; null for
    /// anything else. No Teleport guess: a site-to-site peer's clients would read as Teleport here.
    /// </summary>
    private string? VpnKind(string ip) => NetworkPathAnalyzer.ClassifyVpnClient(ip, _networkPurposes, teleportByElimination: false) switch
    {
        HopType.Tailscale => "Tailscale",
        HopType.Vpn => "VPN Client",
        _ => null,
    };

    /// <summary>
    /// A lookup answered only with addresses on the site's Networks, or one with no address answer for a name
    /// that resolved to the site within the hour (a LAN-only name has an A record and no AAAA). A refused
    /// lookup is not local.
    /// </summary>
    private bool IsLocalLookup(LiveDnsEvent e, DateTime at)
    {
        var subnets = _siteSubnets;
        if (subnets.Count == 0 || IsBlocked(e)) return false;
        var addresses = e.Answers.Where(a => a.Type is "A" or "AAAA").Select(a => a.Data).ToList();
        if (addresses.Count == 0)
            return _localNames.TryGetValue(e.Name, out var seen) && at - seen < LocalNameKept;
        if (!addresses.All(ip => subnets.Any(s => NetworkUtilities.IsIpInSubnet(ip, s)))) return false;
        if (!_localNames.TryGetValue(e.Name, out var last) || last < at) _localNames[e.Name] = at;
        return true;
    }

    /// <summary>The site a public address belongs to, or null.</summary>
    private string? SiteFor(string ip)
    {
        if (_siteByAddress.TryGetValue(ip, out var site)) return site;
        foreach (var (prefix, name) in _siteByPrefix)
            if (NetworkUtilities.IsIpInSubnet(ip, prefix)) return name;
        return null;
    }
}
