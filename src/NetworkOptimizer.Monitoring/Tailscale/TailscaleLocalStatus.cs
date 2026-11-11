using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Monitoring.Tailscale;

/// <summary>One node as a local tailscaled reports it: itself, or one of its peers.</summary>
/// <param name="DnsName">The MagicDNS name with its trailing dot trimmed ("laptop.tail1234.ts.net"); null for a shared-in node.</param>
/// <param name="HostName">The OS hostname the node reported ("localhost" on iOS).</param>
/// <param name="Os">The node's OS ("linux", "iOS", "windows").</param>
/// <param name="User">The owner's login name, from the status's user map.</param>
/// <param name="TailscaleIps">The node's tailnet addresses.</param>
/// <param name="CurAddr">The endpoint of a direct path; empty when traffic goes through a relay.</param>
/// <param name="Relay">The DERP region code ("dfw"): the relay in use when there is no direct path.</param>
/// <param name="PeerRelay">The peer relay in use ("ip:port:vni"); empty when none.</param>
/// <param name="LastHandshake">The last WireGuard handshake, UTC; null when there has been none.</param>
/// <param name="Addrs">The node's known endpoints (ip:port), LAN and public.</param>
/// <param name="PrimaryRoutes">Subnet routes this node is the primary router for.</param>
/// <param name="ExitNodeOption">Whether the node offers itself as an exit node.</param>
public sealed record TailscaleLocalPeer(
    string? DnsName,
    string? HostName,
    string? Os,
    string? User,
    IReadOnlyList<string> TailscaleIps,
    string? CurAddr,
    string? Relay,
    string? PeerRelay,
    long RxBytes,
    long TxBytes,
    DateTime? LastHandshake,
    bool Online,
    bool Active,
    bool ExitNodeOption,
    IReadOnlyList<string> PrimaryRoutes,
    IReadOnlyList<string> Addrs,
    IReadOnlyList<string> Tags);

/// <summary>A local tailscaled's view of the tailnet: the node itself and every peer it knows.</summary>
public sealed record TailscaleLocalStatus(
    string? BackendState,
    TailscaleLocalPeer? Self,
    IReadOnlyList<TailscaleLocalPeer> Peers);

/// <summary>
/// Parses the JSON of LocalAPI <c>/localapi/v0/status</c>, which <c>tailscale status --json</c> prints
/// as well. Field names are tailscaled's Go field names (ipn/ipnstate.Status and PeerStatus).
/// </summary>
public static class TailscaleStatusParser
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The parsed status; throws <see cref="JsonException"/> on JSON that is not a status.</summary>
    public static TailscaleLocalStatus Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<RawStatus>(json, Options)
            ?? throw new JsonException("tailscaled returned an empty status.");
        var users = raw.User ?? new Dictionary<string, RawUser>();
        return new TailscaleLocalStatus(
            raw.BackendState,
            raw.Self == null ? null : Map(raw.Self, users),
            (raw.Peer ?? new Dictionary<string, RawPeer>()).Values.Select(p => Map(p, users)).ToList());
    }

    private static TailscaleLocalPeer Map(RawPeer p, IReadOnlyDictionary<string, RawUser> users)
    {
        var user = users.TryGetValue(p.UserID.ToString(System.Globalization.CultureInfo.InvariantCulture), out var u)
            ? Blank(u.LoginName) ?? Blank(u.DisplayName) : null;
        return new TailscaleLocalPeer(
            Blank(p.DNSName?.TrimEnd('.')),
            Blank(p.HostName),
            Blank(p.OS),
            user,
            p.TailscaleIPs ?? [],
            Blank(p.CurAddr),
            Blank(p.Relay),
            Blank(p.PeerRelay),
            p.RxBytes,
            p.TxBytes,
            // Go's zero time ("0001-01-01T00:00:00Z") means no handshake yet.
            p.LastHandshake is { Year: > 1 } at ? at.ToUniversalTime() : null,
            p.Online,
            p.Active,
            p.ExitNodeOption,
            p.PrimaryRoutes ?? [],
            p.Addrs ?? [],
            p.Tags ?? []);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class RawStatus
    {
        public string? BackendState { get; set; }
        public RawPeer? Self { get; set; }
        public Dictionary<string, RawPeer>? Peer { get; set; }
        public Dictionary<string, RawUser>? User { get; set; }
    }

    private sealed class RawPeer
    {
        public string? HostName { get; set; }
        public string? DNSName { get; set; }
        public string? OS { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long UserID { get; set; }
        public List<string>? TailscaleIPs { get; set; }
        public List<string>? Addrs { get; set; }
        public string? CurAddr { get; set; }
        public string? Relay { get; set; }
        public string? PeerRelay { get; set; }
        public long RxBytes { get; set; }
        public long TxBytes { get; set; }
        public DateTime? LastHandshake { get; set; }
        public bool Online { get; set; }
        public bool Active { get; set; }
        public bool ExitNodeOption { get; set; }
        public List<string>? PrimaryRoutes { get; set; }
        public List<string>? Tags { get; set; }
    }

    private sealed class RawUser
    {
        public string? LoginName { get; set; }
        public string? DisplayName { get; set; }
    }
}
