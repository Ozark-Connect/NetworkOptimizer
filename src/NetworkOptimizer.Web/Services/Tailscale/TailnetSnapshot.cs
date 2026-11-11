using System.Net;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Tailscale;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>A tailnet device with what the local tailscaled adds about it, when one was read.</summary>
public sealed record TailnetPeer
{
    /// <summary>The machine name the admin console shows.</summary>
    public required string Name { get; init; }
    public string? Hostname { get; init; }
    public string? Os { get; init; }

    /// <summary>The owner's login name; null for a tagged device.</summary>
    public string? User { get; init; }
    public IReadOnlyList<string> Addresses { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Whether the device serves as an exit node (a default route).</summary>
    public bool IsExitNode { get; init; }

    /// <summary>The subnets the device routes for the tailnet, exit-node defaults excluded.</summary>
    public IReadOnlyList<string> SubnetRoutes { get; init; } = [];

    /// <summary>The device's private LAN addresses, from its endpoints.</summary>
    public IReadOnlyList<string> LanAddresses { get; init; } = [];

    /// <summary>Every UDP endpoint (ip:port) the device is reached at, public and LAN: what a gateway sees its WireGuard traffic go to.</summary>
    public IReadOnlyList<string> Endpoints { get; init; } = [];

    /// <summary>How the local tailscaled reaches it right now; null without local status or a live path.</summary>
    public TailnetPathInfo? Path { get; init; }
    public long? RxBytes { get; init; }
    public long? TxBytes { get; init; }
    public bool? Online { get; init; }

    /// <summary>"Tailscale exit node", "Tailscale subnet router", both, or null.</summary>
    public string? RouterBadge => (IsExitNode, SubnetRoutes.Count > 0) switch
    {
        (true, true) => "Tailscale exit node and subnet router",
        (true, false) => "Tailscale exit node",
        (false, true) => "Tailscale subnet router",
        _ => null,
    };
}

/// <summary>
/// One site's tailnet at one moment, keyed by every tailnet address. Immutable: readers take the
/// current instance and never see it change underneath them.
/// </summary>
public sealed class TailnetSnapshot
{
    /// <summary>A snapshot with nothing in it, for a site with Tailscale off or not read yet.</summary>
    public static readonly TailnetSnapshot Empty = new([], null, [], connected: false);

    /// <summary>Whether the site reads its tailnet. False only for <see cref="Empty"/>: off, or not read yet.</summary>
    public bool Connected { get; }

    private readonly DerpMapClient.DerpMap? _derp;

    /// <summary>The DERP region of a relay server's address, from Tailscale's DERP map; null when it is none.</summary>
    public DerpRegion? DerpAt(string? ip) =>
        _derp != null && Normalize(ip) is { } key && _derp.ByAddress.TryGetValue(key, out var r) ? r : null;

    /// <summary>A DERP region's name ("Dallas") from its code ("dfw"); the code when the map is not read.</summary>
    public string RegionName(string code) =>
        _derp != null && _derp.ByCode.TryGetValue(code, out var r) ? r.Name : code;

    private readonly Dictionary<string, TailnetPeer> _byIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TailnetPeer> _byLanIp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Proto, string Ip, int Port, string Remote, int RemotePort), string> _snat = new();
    /// <summary>Peers by endpoint; a null value marks one two peers share, which names neither.</summary>
    private readonly Dictionary<(string Ip, int Port), TailnetPeer?> _byEndpoint = new();
    private readonly Dictionary<string, TailnetPeer?> _byEndpointIp = new(StringComparer.OrdinalIgnoreCase);

    public TailnetSnapshot(IReadOnlyList<TailnetPeer> peers, TailnetPeer? self, IReadOnlyList<TailnetSnatEntry> snat,
        DateTime? readAt = null, bool connected = true, DerpMapClient.DerpMap? derp = null)
    {
        Peers = peers;
        Self = self;
        ReadAt = readAt;
        Connected = connected;
        _derp = derp;
        foreach (var peer in self == null ? peers : peers.Append(self))
        {
            foreach (var ip in peer.Addresses)
                if (Normalize(ip) is { } key) _byIp.TryAdd(key, peer);
            if (peer.RouterBadge != null)
                foreach (var ip in peer.LanAddresses)
                    if (Normalize(ip) is { } key) _byLanIp.TryAdd(key, peer);
        }
        // The local node's own endpoints are this host, not a peer it talks to.
        foreach (var peer in peers)
            foreach (var ep in peer.Endpoints)
                if (ParseEndpoint(ep) is var (ip, port))
                {
                    var text = ip.ToString();
                    Claim(_byEndpoint, (text, port), peer);
                    Claim(_byEndpointIp, text, peer);
                }
        foreach (var e in snat)
            if (Normalize(e.NatIp) is { } nat && Normalize(e.RemoteIp) is { } remote)
                _snat.TryAdd((e.Proto, nat, e.NatPort, remote, e.RemotePort), e.PeerIp);
        SnatCount = _snat.Count;
    }

    private static void Claim<TKey>(Dictionary<TKey, TailnetPeer?> index, TKey key, TailnetPeer peer) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var held)) index[key] = peer;
        else if (held != null && !ReferenceEquals(held, peer)) index[key] = null;
    }

    /// <summary>
    /// The peer a WireGuard flow goes to, by the remote end's address and port. With
    /// <paramref name="addressAlone"/>, a port the peer's NAT has since remapped still matches, but only
    /// while exactly one peer holds that address: a carrier's shared address names nobody.
    /// </summary>
    public TailnetPeer? PeerAtEndpoint(string? ip, int port, bool addressAlone)
    {
        if (Normalize(ip) is not { } key) return null;
        if (_byEndpoint.TryGetValue((key, port), out var exact)) return exact;
        return addressAlone && _byEndpointIp.TryGetValue(key, out var byIp) ? byIp : null;
    }

    /// <summary>Every device in the tailnet, the local node excluded.</summary>
    public IReadOnlyList<TailnetPeer> Peers { get; }

    /// <summary>The node whose local status was read, when one was.</summary>
    public TailnetPeer? Self { get; }

    /// <summary>When the device list was last read, UTC; null for <see cref="Empty"/>.</summary>
    public DateTime? ReadAt { get; }

    /// <summary>How many routed connections this snapshot can attribute.</summary>
    public int SnatCount { get; }

    /// <summary>The device at a tailnet address, or null.</summary>
    public TailnetPeer? Peer(string? ip) => Normalize(ip) is { } key && _byIp.TryGetValue(key, out var p) ? p : null;

    /// <summary>Whether a tailnet device holds the address.</summary>
    public bool IsPeer(string? ip) => Peer(ip) != null;

    /// <summary>The device's name at a tailnet address, or null.</summary>
    public string? NameFor(string? ip) => Peer(ip)?.Name;

    /// <summary>The exit node or subnet router at a LAN address, or null.</summary>
    public TailnetPeer? RouterAtLanAddress(string? ip) => Normalize(ip) is { } key && _byLanIp.TryGetValue(key, out var p) ? p : null;

    /// <summary>
    /// The tailnet source behind a connection a subnet router or exit node translated, matched by the
    /// router's address and port and the remote end; null when this snapshot holds no such entry.
    /// </summary>
    public string? SnatSource(string proto, string ip, int port, string remoteIp, int remotePort) =>
        _snat.Count > 0 && Normalize(ip) is { } nat && Normalize(remoteIp) is { } remote
        && _snat.TryGetValue((proto.ToLowerInvariant(), nat, port, remote, remotePort), out var peer) ? peer : null;

    private static string? Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var text = ip.Trim();
        var slash = text.IndexOf('/');
        if (slash > 0) text = text[..slash];
        if (!IPAddress.TryParse(text, out var parsed)) return null;
        if (parsed.IsIPv4MappedToIPv6) parsed = parsed.MapToIPv4();
        return parsed.ToString();
    }

    /// <summary>
    /// Builds a snapshot from an API device list and an optional local status. With no device list the
    /// local status's peers are the list. A local peer joins the device that shares a tailnet address.
    /// </summary>
    public static TailnetSnapshot Build(IReadOnlyList<TailnetDevice>? devices, TailscaleLocalStatus? local,
        IReadOnlyList<TailnetSnatEntry>? snat, DateTime now, DerpMapClient.DerpMap? derp = null)
    {
        var localByIp = new Dictionary<string, TailscaleLocalPeer>(StringComparer.OrdinalIgnoreCase);
        if (local != null)
            foreach (var p in local.Peers)
                foreach (var ip in p.TailscaleIps)
                    if (Normalize(ip) is { } key) localByIp.TryAdd(key, p);

        var selfIps = local?.Self?.TailscaleIps.Select(Normalize).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<TailnetPeer> peers;
        TailnetPeer? self = local?.Self is { } s ? FromLocal(s, now, isSelf: true) : null;
        if (devices != null)
        {
            peers = new List<TailnetPeer>();
            foreach (var d in devices)
            {
                var localPeer = d.Addresses.Select(Normalize).OfType<string>()
                    .Select(ip => localByIp.GetValueOrDefault(ip)).FirstOrDefault(p => p != null);
                var peer = FromDevice(d, localPeer, now);
                if (d.Addresses.Select(Normalize).Any(ip => ip != null && selfIps.Contains(ip)))
                    self = peer with { LanAddresses = peer.LanAddresses.Union(self?.LanAddresses ?? []).ToList() };
                else
                    peers.Add(peer);
            }
        }
        else
        {
            peers = (local?.Peers ?? []).Select(p => FromLocal(p, now, isSelf: false)).ToList();
        }
        return new TailnetSnapshot(peers, self, snat ?? [], now, connected: true, derp);
    }

    private static TailnetPeer FromDevice(TailnetDevice d, TailscaleLocalPeer? local, DateTime now)
    {
        var (exit, subnets) = SplitRoutes(d.Routes);
        return new TailnetPeer
        {
            Name = d.Name,
            Hostname = d.Hostname ?? local?.HostName,
            Os = d.Os ?? local?.Os,
            User = d.User ?? local?.User,
            Addresses = d.Addresses,
            Tags = d.Tags,
            IsExitNode = exit || local?.ExitNodeOption == true,
            SubnetRoutes = subnets.Count > 0 ? subnets : SplitRoutes(local?.PrimaryRoutes ?? []).Subnets,
            LanAddresses = LanAddresses(d.Endpoints.Concat(local?.Addrs ?? [])),
            Endpoints = AllEndpoints(d.Endpoints, local?.Addrs ?? [], [local?.CurAddr]),
            Path = local == null ? null : PathOf(local, now),
            RxBytes = local?.RxBytes,
            TxBytes = local?.TxBytes,
            Online = local?.Online ?? d.Online,
        };
    }

    private static TailnetPeer FromLocal(TailscaleLocalPeer p, DateTime now, bool isSelf) => new()
    {
        Name = TailnetNames.MachineName(p.DnsName) ?? p.HostName ?? p.TailscaleIps.FirstOrDefault() ?? "Tailscale device",
        Hostname = p.HostName,
        Os = p.Os,
        User = p.User,
        Addresses = p.TailscaleIps,
        Tags = p.Tags,
        IsExitNode = p.ExitNodeOption,
        SubnetRoutes = SplitRoutes(p.PrimaryRoutes).Subnets,
        LanAddresses = LanAddresses(p.Addrs),
        Endpoints = AllEndpoints(p.Addrs, [p.CurAddr]),
        Path = isSelf ? null : PathOf(p, now),
        RxBytes = isSelf ? null : p.RxBytes,
        TxBytes = isSelf ? null : p.TxBytes,
        Online = isSelf ? null : p.Online,
    };

    /// <summary>
    /// The path tailscaled uses to a peer: direct when it holds a current address, else a peer relay,
    /// else its DERP region once the two have shaken hands. Null when nothing is flowing.
    /// </summary>
    public static TailnetPathInfo? PathOf(TailscaleLocalPeer p, DateTime now)
    {
        TailnetPathKind kind;
        string? endpoint = null;
        if (p.CurAddr != null) { kind = TailnetPathKind.Direct; endpoint = p.CurAddr; }
        else if (p.PeerRelay != null) { kind = TailnetPathKind.PeerRelay; endpoint = p.PeerRelay; }
        else if (p.Relay != null && (p.Active || p.LastHandshake != null)) kind = TailnetPathKind.Derp;
        else return null;
        return new TailnetPathInfo { Kind = kind, Relay = p.Relay, Endpoint = endpoint, LastHandshake = p.LastHandshake, ReadAt = now };
    }

    private static (bool Exit, IReadOnlyList<string> Subnets) SplitRoutes(IEnumerable<string> routes)
    {
        var exit = false;
        var subnets = new List<string>();
        foreach (var r in routes)
        {
            if (r is "0.0.0.0/0" or "::/0") exit = true;
            else if (!string.IsNullOrWhiteSpace(r)) subnets.Add(r);
        }
        return (exit, subnets);
    }

    private static IReadOnlyList<string> LanAddresses(IEnumerable<string> endpoints)
    {
        var list = new List<string>();
        foreach (var ep in endpoints)
        {
            if (ParseEndpoint(ep) is var (ip, _) && NetworkUtilities.IsPrivateIpAddress(ip) && !NetworkUtilities.IsTailscaleAddress(ip))
            {
                var text = ip.ToString();
                if (!list.Contains(text, StringComparer.OrdinalIgnoreCase)) list.Add(text);
            }
        }
        return list;
    }

    /// <summary>Every distinct endpoint, as given: the API's, the local status's known ones, and the one in use.</summary>
    private static IReadOnlyList<string> AllEndpoints(params IEnumerable<string?>[] sources) =>
        sources.SelectMany(s => s).OfType<string>().Where(e => ParseEndpoint(e) != null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"192.0.2.10:41641" or "[2001:db8::1]:41641" as an address and port; null when it is neither.</summary>
    public static (IPAddress Ip, int Port)? ParseEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        var ep = endpoint.Trim();
        string host, port;
        if (ep.StartsWith('['))
        {
            var close = ep.IndexOf(']');
            if (close < 0 || close + 1 >= ep.Length || ep[close + 1] != ':') return null;
            (host, port) = (ep[1..close], ep[(close + 2)..]);
        }
        else
        {
            var colon = ep.LastIndexOf(':');
            // More than one colon without brackets is a bare IPv6 address: no port to match on.
            if (colon <= 0 || ep.IndexOf(':') != colon) return null;
            (host, port) = (ep[..colon], ep[(colon + 1)..]);
        }
        if (!IPAddress.TryParse(host, out var ip) || !int.TryParse(port, out var p) || p is <= 0 or > 65535) return null;
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip, p);
    }
}
