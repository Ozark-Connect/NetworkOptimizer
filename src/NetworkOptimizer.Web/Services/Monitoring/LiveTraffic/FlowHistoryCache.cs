using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// One site's Live Traffic History: the console's own flow records (it keeps about the last 48
/// minutes), held in memory while someone views History and dropped when the last History viewer
/// leaves. Nothing reaches disk, and History never asks the gateway agent for anything.
/// </summary>
public sealed class FlowHistoryCache
{
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(15);
    /// <summary>Each refresh re-reads this far behind the newest record, so updated records replace older copies.</summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
    /// <summary>The span asked for on a first load and kept after; the console holds less than this.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan ViewerTimeout = TimeSpan.FromMinutes(2);
    public const int PageSize = 500;
    public const int MaxPages = 40;
    private const int MaxRecords = 25_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, LiveFlowRow> _rows = new();
    private readonly Dictionary<string, DateTime> _viewers = new();
    private HashSet<string> _wanNames = new(StringComparer.OrdinalIgnoreCase);
    private string _gatewayName = "Gateway";
    private IReadOnlyList<CustomAppRule> _customApps = Array.Empty<CustomAppRule>();
    private DateTime _lastRefresh = DateTime.MinValue;
    private DateTime? _newest;
    private bool _loaded;
    private bool _failed;
    /// <summary>Bumped when the cache empties, so a refresh that started before then writes nothing.</summary>
    private int _generation;

    public int Generation
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>A first load has finished, so the cache answers for the whole window.</summary>
    public bool Loaded
    {
        get { lock (_gate) return _loaded; }
    }

    /// <summary>The last refresh could not read the console.</summary>
    public bool Failed
    {
        get { lock (_gate) return _failed; }
    }

    // ---------------------------------------------------------------- viewers

    public void Touch(string viewerId, DateTime now)
    {
        lock (_gate) _viewers[viewerId] = now;
    }

    /// <summary>No viewer has asked within the viewer timeout: a client's cache that can be let go.</summary>
    public bool Idle(DateTime now)
    {
        lock (_gate) return _viewers.Values.All(seen => now - seen > ViewerTimeout);
    }

    /// <summary>Drops a viewer, and every record once nobody is left. True when the cache emptied.</summary>
    public bool Release(string viewerId, DateTime now)
    {
        lock (_gate)
        {
            if (!_viewers.Remove(viewerId)) return false;
            foreach (var stale in _viewers.Where(kv => now - kv.Value > ViewerTimeout).Select(kv => kv.Key).ToList())
                _viewers.Remove(stale);
            if (_viewers.Count > 0) return false;
            _rows.Clear();
            _newest = null;
            _loaded = false;
            _failed = false;
            _lastRefresh = DateTime.MinValue;
            _generation++;
            return true;
        }
    }

    // ---------------------------------------------------------------- refresh

    /// <summary>True at most once per <see cref="RefreshEvery"/>: the caller then runs one refresh.</summary>
    public bool RefreshDue(DateTime now)
    {
        lock (_gate)
        {
            if (now - _lastRefresh < RefreshEvery) return false;
            _lastRefresh = now;
            return true;
        }
    }

    /// <summary>Where the next refresh starts: just behind the newest record, or the whole window on a first load.</summary>
    public DateTime FetchFrom(DateTime now)
    {
        lock (_gate) return _newest is { } n && _loaded ? n - Overlap : now - Window;
    }

    /// <summary>The site's WAN names (any other external exit is a VPN) and the gateway's name.</summary>
    public void SetSiteNames(IEnumerable<string> wanNames, string? gatewayName)
    {
        lock (_gate)
        {
            _wanNames = new HashSet<string>(wanNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(gatewayName)) _gatewayName = gatewayName!;
        }
    }

    /// <summary>
    /// The site's custom app definitions. Applied when a view is built, not when records arrive, so a
    /// new definition renames the records already held.
    /// </summary>
    public void SetCustomApps(IReadOnlyList<CustomAppRule> rules)
    {
        lock (_gate) _customApps = rules;
    }

    public void Merge(IEnumerable<UniFiTrafficFlow> flows, DateTime now, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            foreach (var f in flows)
            {
                var row = ToRow(f, _wanNames, _gatewayName);
                if (row == null) continue;
                _rows[row.Key] = row;
                if (row.LastSeen is { } seen && (_newest == null || seen > _newest)) _newest = seen;
            }
            Prune(now);
        }
    }

    public void MarkRefreshed(bool ok, int generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _failed = !ok;
            if (ok) _loaded = true;
        }
    }

    private void Prune(DateTime now)
    {
        var cutoff = now - Window;
        foreach (var key in _rows.Where(kv => (kv.Value.LastSeen ?? DateTime.MinValue) < cutoff).Select(kv => kv.Key).ToList())
            _rows.Remove(key);
        if (_rows.Count <= MaxRecords) return;
        foreach (var key in _rows.OrderBy(kv => kv.Value.LastSeen).Take(_rows.Count - MaxRecords).Select(kv => kv.Key).ToList())
            _rows.Remove(key);
    }

    // ---------------------------------------------------------------- reading

    public LiveHistoryView BuildView(LiveHistoryQuery query, LiveHistoryState state, DateTime now)
    {
        lock (_gate)
        {
            IReadOnlyCollection<LiveFlowRow> all = _customApps.Count == 0 ? _rows.Values
                : CustomAppMatcher.Apply(_rows.Values.ToList(), _customApps);
            var oldest = all.Count == 0 ? null : all.Min(r => r.LastSeen);
            var matching = all
                .Where(r => query.Client is not { } scope || LiveTrafficStore.InScope(r, scope))
                .Where(r => LiveTrafficStore.Matches(r, query.Filters, query.Search)).ToList();
            var windowed = matching.Where(r => InWindow(r, query.From, query.To)).ToList();
            var sorted = query.Sort == LiveHistorySort.Data
                ? windowed.OrderByDescending(r => r.TotalBytes).ThenByDescending(r => r.LastSeen).ToList()
                : windowed.OrderByDescending(r => r.LastSeen).ThenByDescending(r => r.TotalBytes).ToList();

            return new LiveHistoryView
            {
                State = state,
                Oldest = oldest,
                Newest = _newest,
                Buckets = Buckets(matching, oldest, now),
                Flows = sorted.Take(Math.Max(1, query.MaxRows)).ToList(),
                Groups = query.GroupBy == LiveTrafficGroupBy.Flows ? Array.Empty<LiveGroupRow>() : LiveTrafficStore.Group(sorted, query.GroupBy),
                MatchingFlows = windowed.Count,
                MatchingBlocked = windowed.Count(IsBlocked),
                MatchingBytes = windowed.Sum(r => r.TotalBytes),
                TotalFlows = all.Count,
            };
        }
    }

    private static bool InWindow(LiveFlowRow r, DateTime? from, DateTime? to)
    {
        if (r.LastSeen is not { } seen) return from == null && to == null;
        return (from == null || seen >= from) && (to == null || seen < to);
    }

    private static bool IsBlocked(LiveFlowRow r) => r.Action == "Blocked";

    /// <summary>One bucket per minute from the oldest record to now, empty minutes included.</summary>
    internal static IReadOnlyList<LiveHistoryBucket> Buckets(IReadOnlyList<LiveFlowRow> rows, DateTime? oldest, DateTime now)
    {
        if (oldest is not { } first) return Array.Empty<LiveHistoryBucket>();
        var start = FloorMinute(first < now - Window ? now - Window : first);
        var end = FloorMinute(now);
        var count = (int)((end - start).TotalMinutes) + 1;
        var allowed = new int[count];
        var blocked = new int[count];
        var allowedBytes = new long[count];
        var blockedBytes = new long[count];
        foreach (var r in rows)
        {
            if (r.LastSeen is not { } seen) continue;
            var i = (int)((FloorMinute(seen) - start).TotalMinutes);
            if (i < 0 || i >= count) continue;
            if (IsBlocked(r)) { blocked[i]++; blockedBytes[i] += r.TotalBytes; }
            else { allowed[i]++; allowedBytes[i] += r.TotalBytes; }
        }
        return Enumerable.Range(0, count)
            .Select(i => new LiveHistoryBucket(start.AddMinutes(i), allowed[i], blocked[i], allowedBytes[i], blockedBytes[i]))
            .ToList();
    }

    private static DateTime FloorMinute(DateTime t) =>
        new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    // ---------------------------------------------------------------- record to row

    /// <summary>
    /// One console record as a flow row, in the client's frame. Outgoing and local records have the
    /// client as the source; incoming ones (a port forward, a WAN hit on the gateway) as the destination.
    /// </summary>
    [NetworkOptimizer.Core.VendorSpecific("UniFi", "traffic-flows direction, zone_name, and in/out network semantics")]
    internal static LiveFlowRow? ToRow(UniFiTrafficFlow f, IReadOnlySet<string> wanNames, string gatewayName)
    {
        var incoming = Eq(f.Direction, "incoming");
        var local = incoming ? f.Destination : f.Source;
        var remote = incoming ? f.Source : f.Destination;
        if (string.IsNullOrEmpty(local?.Ip) || string.IsNullOrEmpty(remote?.Ip)) return null;

        var proto = (f.Protocol ?? "").ToUpperInvariant();
        var remotePort = remote.Port ?? 0;
        var remoteIsGateway = Eq(remote.ZoneName, "Gateway");

        var clientName = Clean(local.ClientName) ?? Clean(local.HostName)
            ?? (Eq(local.ZoneName, "Gateway") ? gatewayName : local.Ip!);
        var destination = remote.Domains?.Select(Clean).FirstOrDefault(d => d != null)
            ?? Clean(remote.ClientName) ?? (remoteIsGateway ? gatewayName : remote.Ip!);

        string route;
        LiveRouteKind kind;
        if (Eq(remote.ZoneName, "External"))
        {
            route = Clean(incoming ? f.In?.NetworkName : f.Out?.NetworkName) ?? "WAN";
            kind = wanNames.Count == 0 || wanNames.Contains(route) ? LiveRouteKind.Wan : LiveRouteKind.Vpn;
        }
        else
        {
            route = "LAN";
            kind = LiveRouteKind.Lan;
        }

        string? app = DomainAppCatalog.ProtocolApp(proto), category = null;
        if (app == null && f.Application is { App: { } a, Category: { } c })
        {
            app = DpiCatalog.AppName(c, a);
            category = DpiCatalog.CategoryName(c);
        }
        var clientPort = local.Port ?? 0;
        var portName = FlowNaming.PortName(clientPort, remotePort);
        var service = FlowNaming.ConsoleService(f.Service) ?? portName;
        var barePort = FlowNaming.BarePort(proto, clientPort, remotePort);
        var destSub = service != null
            ? (proto == "UDP" && service is "HTTPS" ? "QUIC" : service)
            : barePort;
        var encryptedDns = remotePort == 853
            || (remotePort == 443 && kind != LiveRouteKind.Lan && DohProviderRegistry.IdentifyProviderFromIp(remote.Ip!) != null);
        if (app == null && encryptedDns) app = remotePort == 853 ? "DNS over TLS" : "DNS over HTTPS";
        // The console's own domains for the remote, as Live names an app it has not: before the port name.
        app ??= remote.Domains?.Select(d => DomainAppCatalog.AppFor(d)).FirstOrDefault(n => n != null);
        app ??= service ?? barePort;

        var policy = f.Policies?.FirstOrDefault(p => !string.IsNullOrEmpty(p.Name));
        var seenMs = f.Time > 0 ? f.Time : f.FlowEndTime > 0 ? f.FlowEndTime : f.FlowStartTime;

        return new LiveFlowRow
        {
            Key = f.Id ?? $"{proto}|{local.Ip}|{local.Port}|{remote.Ip}|{remotePort}|{f.FlowStartTime}",
            ClientMac = (local.Mac ?? "").Replace('-', ':').ToLowerInvariant(),
            ClientName = clientName,
            ClientIp = local.Ip!,
            ClientVia = Clean(incoming ? f.Out?.NetworkName : f.In?.NetworkName) ?? Clean(local.NetworkName),
            Destination = destination,
            DestinationIp = remote.Ip!,
            DestinationSub = destSub,
            App = app,
            Category = category,
            Route = route,
            RouteKind = kind,
            Started = Utc(f.FlowStartTime),
            Proto = proto,
            ClientPort = clientPort,
            RemotePort = remotePort,
            PolicyName = policy?.Name,
            PolicyId = policy?.Id,
            PolicyType = policy?.Type,
            Network = Clean(incoming ? f.Out?.NetworkName : f.In?.NetworkName) ?? Clean(local.NetworkName),
            DnsBypass = encryptedDns,
            Ended = f.FlowEndTime > 0,
            Action = Eq(f.Action, "blocked") ? "Blocked" : "Allowed",
            LastSeen = Utc(seenMs),
            Duration = f.DurationMilliseconds > 0 ? TimeSpan.FromMilliseconds(f.DurationMilliseconds) : null,
            CombinedBytes = f.TrafficData?.BytesTotal ?? 0,
            CombinedPackets = f.TrafficData?.PacketsTotal ?? 0,
        };
    }

    private static DateTime? Utc(long unixMs) => unixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
