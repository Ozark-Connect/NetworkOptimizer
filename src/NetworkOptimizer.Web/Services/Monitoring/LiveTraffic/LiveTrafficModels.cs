namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>What a Live Traffic filter chip filters on. <see cref="Action"/> exists in History only.</summary>
public enum LiveTrafficField { Client, Destination, App, Route, Resolver, Protocol, Port, Action }

/// <summary>One filter chip: keep only matching rows, or with <see cref="Exclude"/>, drop them.</summary>
public sealed record LiveTrafficFilter(LiveTrafficField Field, string Value, bool Exclude = false);

/// <summary>
/// One client, for a page that is about that client (Client Performance). A row is the client's when
/// its MAC matches, or its client address is any of <see cref="Ips"/>: a client the console reports with
/// several addresses, or a VPN peer known only by its tunnel address, still matches every row.
/// </summary>
public sealed record LiveClientScope(string? Mac, IReadOnlyCollection<string> Ips)
{
    public bool Contains(string? mac, string? ip) =>
        (!string.IsNullOrEmpty(Mac) && !string.IsNullOrEmpty(mac) && string.Equals(Normalize(Mac), Normalize(mac), StringComparison.Ordinal))
        || (!string.IsNullOrEmpty(ip) && Ips.Contains(ip, StringComparer.OrdinalIgnoreCase));

    private static string Normalize(string mac) => mac.Replace('-', ':').ToLowerInvariant();
}

/// <summary>How the flows table rolls up.</summary>
public enum LiveTrafficGroupBy { Flows, Client, App, Destination, Route }

/// <summary>Which DNS lookups Live DNS lists.</summary>
public enum LiveDnsMode { All, Bypass, Nxdomain }

/// <summary>The kind of route a flow leaves by, for its color.</summary>
public enum LiveRouteKind { Wan, Vpn, Lan, Gateway }

/// <summary>A viewer's question. The viewer id keeps the agent's lease alive while it asks.</summary>
public sealed record LiveTrafficQuery
{
    public required string ViewerId { get; init; }
    public IReadOnlyList<LiveTrafficFilter> Filters { get; init; } = Array.Empty<LiveTrafficFilter>();
    public string? Search { get; init; }
    /// <summary>Only this client's flows and lookups, before the filters. Null is every client.</summary>
    public LiveClientScope? Client { get; init; }
    public LiveTrafficGroupBy GroupBy { get; init; } = LiveTrafficGroupBy.Flows;
    public bool Ended { get; init; }
    public LiveDnsMode DnsMode { get; init; } = LiveDnsMode.All;
    /// <summary>Leave out lookups whose every answer is on one of the site's Networks.</summary>
    public bool HideLocalDns { get; init; }
    public bool IncludeDns { get; init; } = true;
    public bool IncludeSankey { get; init; } = true;
    /// <summary>Where It Goes sized by flow count instead of rate.</summary>
    public bool SankeyByFlows { get; init; }
    /// <summary>WAN and VPN flows only, as the Live View card shows them.</summary>
    public bool WanOnly { get; init; }
    public int MaxRows { get; init; } = 200;
    /// <summary>The Flows table's sort column; Ended flows default to most recently ended.</summary>
    public LiveFlowSort Sort { get; init; } = LiveFlowSort.Download;
    public bool Descending { get; init; } = true;
}

/// <summary>The Flows table's sortable columns.</summary>
public enum LiveFlowSort { Download, Upload, Total, Started, Client, Destination, App, Route }

/// <summary>Whether Live Traffic can run on this site, and why not.</summary>
public enum LiveTrafficState { Live, Starting, NoGatewayAgent, AgentNeedsUpdate, NoConntrack, MonitoringOff, AwaitingAgent }

/// <summary>One flow row, enriched and in the client's frame (download is toward the client).</summary>
public sealed record LiveFlowRow
{
    public required string Key { get; init; }
    public string ClientMac { get; init; } = "";
    public string ClientName { get; init; } = "";
    public string ClientIp { get; init; } = "";
    /// <summary>"Office Switch · # 3", "5 GHz", or the network name: the sub-line under the client.</summary>
    public string? ClientVia { get; init; }
    public string Destination { get; init; } = "";
    public string DestinationIp { get; init; } = "";
    /// <summary>"HTTPS", "UDP 51820": the sub-line under the destination.</summary>
    public string DestinationSub { get; init; } = "";
    public string App { get; init; } = "";
    public string? Category { get; init; }
    public string Route { get; init; } = "";
    public LiveRouteKind RouteKind { get; init; }
    public double DownBps { get; init; }
    public double UpBps { get; init; }
    public long TotalDownBytes { get; init; }
    public long TotalUpBytes { get; init; }
    public long TotalDownPackets { get; init; }
    public long TotalUpPackets { get; init; }
    public DateTime? Started { get; init; }
    /// <summary>The flow is at least this old: it predates the agent and the kernel keeps no start time.</summary>
    public bool StartedIsUpperBound { get; init; }
    public string Proto { get; init; } = "";
    public int ClientPort { get; init; }
    public int RemotePort { get; init; }
    public string? PolicyName { get; init; }
    public string? PolicyId { get; init; }
    public string? PolicyType { get; init; }
    public string? Network { get; init; }
    public string? DnsDetail { get; init; }
    /// <summary>The domain whose favicon stands for the App (as Client Performance shows it), or null.</summary>
    public string? AppIconDomain { get; init; }
    /// <summary>The Font Awesome class for the App, or null for the initial alone.</summary>
    public string? AppIconClass { get; init; }
    /// <summary>Parallel streams this row stands for (one client to one destination, port, and protocol); 1 for a single flow.</summary>
    public int Streams { get; init; } = 1;
    /// <summary>The gateway's own traffic, not a client's.</summary>
    public bool IsGateway { get; init; }
    /// <summary>UniFi Network has a record of this flow; without one its policy is unknown, not "none".</summary>
    public bool ConsoleReported { get; init; }
    /// <summary>The policy was named by evaluating the site's firewall rules, not by UniFi Network's record.</summary>
    public bool PolicyEvaluated { get; init; }
    /// <summary>The far end's MAC when it is a site client or UniFi device (inter-VLAN traffic), else null.</summary>
    public string? RemoteClientMac { get; init; }
    /// <summary>How that far-end client connects ("Office Switch · # 9", "5 GHz", or its network), as the client column shows it.</summary>
    public string? RemoteClientVia { get; init; }
    /// <summary>The far-end client's network (VLAN name), for its badge.</summary>
    public string? RemoteNetwork { get; init; }
    /// <summary>The site whose public address the remote end is (a site WAN), or null.</summary>
    public string? RemoteSite { get; init; }
    /// <summary>The organization that owns the remote address (GeoLite2 ASN), or null.</summary>
    public string? RemoteOrg { get; init; }
    /// <summary>DoH or DoT: a DNS lookup we cannot read.</summary>
    public bool EncryptedDns { get; init; }
    /// <summary>Encrypted DNS that is not a site resolver's own upstream.</summary>
    public bool DnsBypass { get; init; }
    /// <summary>Oldest first, one point per agent window, at most two minutes.</summary>
    public IReadOnlyList<(double Down, double Up)> History { get; init; } = Array.Empty<(double, double)>();
    public bool Ended { get; init; }
    /// <summary>When the flow left the table; null while it is active.</summary>
    public DateTime? EndedAt { get; init; }

    /// <summary>History: "Allowed" or "Blocked". Live rows leave it null.</summary>
    public string? Action { get; init; }
    /// <summary>History: when the console last updated the record.</summary>
    public DateTime? LastSeen { get; init; }
    public TimeSpan? Duration { get; init; }
    /// <summary>History: both directions in one number, as the console reports them.</summary>
    public long? CombinedBytes { get; init; }
    public long? CombinedPackets { get; init; }

    public long TotalBytes => CombinedBytes ?? TotalDownBytes + TotalUpBytes;
}

/// <summary>A roll-up row when the table groups by client, app, destination, or route.</summary>
public sealed record LiveGroupRow(
    string Name,
    string? Sub,
    LiveTrafficField Field,
    double DownBps,
    double UpBps,
    long TotalBytes,
    int Flows,
    LiveRouteKind? RouteKind,
    string? ClientMac);

/// <summary>One route's live load, for the tiles at the top of the full view.</summary>
public sealed record LiveRouteStat(
    string Name,
    LiveRouteKind Kind,
    double DownBps,
    double UpBps,
    double? CapacityDownBps,
    double? CapacityUpBps,
    int Clients);

/// <summary>One Live DNS row.</summary>
/// <param name="Blocked">Refused the way DNS blockers refuse: NXDOMAIN, or an answer of 0.0.0.0 or ::.</param>
public sealed record LiveDnsRow(
    DateTime At,
    string ClientName,
    string ClientIp,
    string Name,
    string QType,
    string Resolver,
    string ResolverIp,
    bool Bypass,
    bool Encrypted,
    string RCode,
    double LatencyMs,
    bool Answered,
    bool Blocked = false,
    string ClientMac = "",
    bool Local = false,
    string? RedirectedTo = null);

/// <summary>The Where It Goes diagram: three columns of nodes and the links between them.</summary>
public sealed record LiveSankey(IReadOnlyList<LiveSankeyNode> Nodes, IReadOnlyList<LiveSankeyLink> Links, bool ValueIsFlows = false);

/// <param name="DownBps">The node's download rate, for its subtitle; 0 when sized by flow count.</param>
/// <param name="UpBps">The node's upload rate.</param>
/// <param name="ClientIp">A client node's address, for its Client Performance link; null for Other and the other columns.</param>
public sealed record LiveSankeyNode(int Column, string Name, double Value, LiveTrafficField Field, LiveRouteKind? RouteKind, bool IsOther,
    string? IconDomain = null, string? IconClass = null, double DownBps = 0, double UpBps = 0, string? ClientIp = null);

/// <param name="DownBps">The band's download rate: the band splits into a download and an upload strand by these.</param>
/// <param name="UpBps">The band's upload rate.</param>
public sealed record LiveSankeyLink(int Column, string From, string To, double Value, LiveRouteKind RouteKind,
    double DownBps = 0, double UpBps = 0);

/// <summary>How History orders its flows.</summary>
public enum LiveHistorySort { Time, Data }

/// <summary>A History viewer's question: the same filters as Live, plus an optional window.</summary>
public sealed record LiveHistoryQuery
{
    public required string ViewerId { get; init; }
    public IReadOnlyList<LiveTrafficFilter> Filters { get; init; } = Array.Empty<LiveTrafficFilter>();
    public string? Search { get; init; }
    /// <summary>Only this client's records, before the filters. Null is every client.</summary>
    public LiveClientScope? Client { get; init; }
    public LiveTrafficGroupBy GroupBy { get; init; } = LiveTrafficGroupBy.Flows;
    /// <summary>UTC window picked on the Timeline; null on either side is open.</summary>
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public LiveHistorySort Sort { get; init; } = LiveHistorySort.Time;
    public int MaxRows { get; init; } = 200;
}

/// <summary>Whether History can answer for this site.</summary>
public enum LiveHistoryState { Ready, Loading, Failed, Unsupported, NoConsole }

/// <summary>One minute of the Timeline, after the filters but before the window.</summary>
public sealed record LiveHistoryBucket(DateTime Start, int Allowed, int Blocked, long AllowedBytes, long BlockedBytes);

/// <summary>Everything one refresh of History draws.</summary>
public sealed record LiveHistoryView
{
    public LiveHistoryState State { get; init; }
    /// <summary>The oldest and newest records the console still holds.</summary>
    public DateTime? Oldest { get; init; }
    public DateTime? Newest { get; init; }
    public IReadOnlyList<LiveHistoryBucket> Buckets { get; init; } = Array.Empty<LiveHistoryBucket>();
    public IReadOnlyList<LiveFlowRow> Flows { get; init; } = Array.Empty<LiveFlowRow>();
    public IReadOnlyList<LiveGroupRow> Groups { get; init; } = Array.Empty<LiveGroupRow>();
    /// <summary>Rows that pass the filters and the window, before <see cref="LiveHistoryQuery.MaxRows"/>.</summary>
    public int MatchingFlows { get; init; }
    public int MatchingBlocked { get; init; }
    public long MatchingBytes { get; init; }
    /// <summary>Every record the console holds.</summary>
    public int TotalFlows { get; init; }
}

/// <summary>Everything one refresh of the card or the full view draws.</summary>
public sealed record LiveTrafficView
{
    public LiveTrafficState State { get; init; }
    public DateTime? At { get; init; }
    public IReadOnlyList<LiveFlowRow> Flows { get; init; } = Array.Empty<LiveFlowRow>();
    public IReadOnlyList<LiveGroupRow> Groups { get; init; } = Array.Empty<LiveGroupRow>();
    /// <summary>Rows that pass the filters, before <see cref="LiveTrafficQuery.MaxRows"/>.</summary>
    public int MatchingFlows { get; init; }
    /// <summary>The rate of every row that passes the filters, toward the client and from it.</summary>
    public double MatchingDownBps { get; init; }
    public double MatchingUpBps { get; init; }
    /// <summary>Every entry in the gateway's conntrack table.</summary>
    public int TotalFlows { get; init; }
    public int Clients { get; init; }
    public int NewLastMinute { get; init; }
    public IReadOnlyList<LiveRouteStat> Routes { get; init; } = Array.Empty<LiveRouteStat>();
    public IReadOnlyList<LiveDnsRow> Dns { get; init; } = Array.Empty<LiveDnsRow>();
    public double DnsPerSecond { get; init; }
    public double NxdomainPercent { get; init; }
    public int BypassingClients { get; init; }
    public bool DnsActive { get; init; }
    public LiveSankey? Sankey { get; init; }
}
