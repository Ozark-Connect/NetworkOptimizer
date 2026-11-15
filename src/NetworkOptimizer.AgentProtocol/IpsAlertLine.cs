using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.AgentProtocol;

/// <summary>
/// Reads one line of a UniFi gateway's IPS threat log (<see cref="LogPath"/>) into an <see cref="IpsAlert"/>.
/// Only an outbound alert passes: a device in HOME_NET (or a private address) started the flow, and its
/// other end is public. Anything malformed, foreign, or stale is rejected with a reason.
/// </summary>
[VendorSpecific("UniFi", "syslog-ng writes ubnt-idsips-daemon's Suricata EVE alerts to /var/log/ulog/threat.log")]
public static class IpsAlertLine
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "ips-alert-relay";

    /// <summary>Where the gateway logs each alert. It does not exist until the first alert after boot.</summary>
    public const string LogPath = "/var/log/ulog/threat.log";

    /// <summary>The file holding Suricata's HOME_NET: the networks IPS inspects.</summary>
    public const string HomeNetPath = "/run/ips/config/homenet.yaml";

    /// <summary>Alerts older (or newer) than this against the agent's clock are not relayed.</summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(2);

    private const string Tag = "ubnt-idsips-daemon[";

    /// <summary>Why a line was not relayed.</summary>
    public enum Reject { None, NotIpsDaemon, NotJson, NotAlert, IncompleteAlert, BadAddresses, NotOutbound, Stale }

    /// <summary>The subnets in homenet.yaml's HOME_NET line; empty when it is absent or unreadable.</summary>
    public static List<string> ParseHomeNet(string? yaml)
    {
        if (string.IsNullOrEmpty(yaml)) return [];
        foreach (var line in yaml.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("HOME_NET:", StringComparison.Ordinal))
                return NetworkUtilities.ParseCidrList(trimmed["HOME_NET:".Length..]);
        }
        return [];
    }

    /// <summary>Parses a line; null with <paramref name="reject"/> set when it is not a relayable outbound alert.</summary>
    public static IpsAlert? Parse(string line, IReadOnlyList<string> homeNet, DateTimeOffset now, out Reject reject)
    {
        reject = Reject.None;
        var tag = line.IndexOf(Tag, StringComparison.Ordinal);
        var json = tag < 0 ? -1 : line.IndexOf("]: {", tag, StringComparison.Ordinal);
        if (tag < 0 || json < 0) { reject = Reject.NotIpsDaemon; return null; }

        EveLine? eve;
        try { eve = JsonSerializer.Deserialize<EveLine>(line.AsSpan(json + 3)); }
        catch (JsonException) { eve = null; }
        if (eve == null) { reject = Reject.NotJson; return null; }
        if (eve.EventType != "alert" || eve.Alert == null) { reject = Reject.NotAlert; return null; }

        var a = eve.Alert;
        if (a.SignatureId <= 0 || a.Gid != 1 || string.IsNullOrWhiteSpace(a.Signature) || string.IsNullOrWhiteSpace(a.Category)
            || a.Severity is < 1 or > 4)
        { reject = Reject.IncompleteAlert; return null; }

        // The flow's initiator decides direction, whichever way the rule matched.
        var initiator = eve.Flow?.SrcIp;
        var responder = eve.Flow?.DestIp;
        if (!Valid(initiator) || !Valid(responder) || !Valid(eve.SrcIp) || !Valid(eve.DestIp)
            || !SamePair(initiator!, responder!, eve.SrcIp!, eve.DestIp!))
        { reject = Reject.BadAddresses; return null; }
        if (!NetworkUtilities.IsLocalAddress(initiator!, homeNet) || !NetworkUtilities.IsPublicIpAddress(responder!)
            || NetworkUtilities.IsIpInAnySubnet(responder!, homeNet))
        { reject = Reject.NotOutbound; return null; }

        if (ParseTimestamp(eve.Timestamp) is not { } at || (at - now).Duration() > MaxSkew)
        { reject = Reject.Stale; return null; }

        var initiatorIsSrc = NetworkUtilities.IpAddressesAreEqual(initiator, eve.SrcIp);
        var alert = new IpsAlert
        {
            ClientIp = initiator,
            ClientMac = ((initiatorIsSrc ? eve.SrcMac : eve.DstMac) ?? "").ToLowerInvariant(),
            RemoteIp = responder,
            RemotePort = initiatorIsSrc ? eve.DestPort : eve.SrcPort,
            Protocol = (eve.Proto ?? "").ToLowerInvariant(),
            SignatureId = a.SignatureId,
            Signature = a.Signature,
            Category = a.Category,
            UbntCategory = eve.Ubnt?.UbntCategory ?? First(a.Metadata, "ubnt_category") ?? "",
            Severity = a.Severity,
            Confidence = First(a.Metadata, "confidence") ?? "",
            Hostname = eve.Http?.Hostname ?? eve.Tls?.Sni ?? "",
            TimestampUnixMs = at.ToUnixTimeMilliseconds(),
            FlowId = eve.FlowId,
            Action = a.Action ?? "",
        };
        if (a.Metadata?.TryGetValue("attack_target", out var targets) == true)
            alert.AttackTargets.AddRange(targets.Where(t => !string.IsNullOrWhiteSpace(t)));
        return alert;
    }

    private static bool Valid(string? ip) => System.Net.IPAddress.TryParse(ip, out _);

    private static bool SamePair(string a1, string b1, string a2, string b2) =>
        (NetworkUtilities.IpAddressesAreEqual(a1, a2) && NetworkUtilities.IpAddressesAreEqual(b1, b2))
        || (NetworkUtilities.IpAddressesAreEqual(a1, b2) && NetworkUtilities.IpAddressesAreEqual(b1, a2));

    private static string? First(Dictionary<string, List<string>>? metadata, string key) =>
        metadata != null && metadata.TryGetValue(key, out var values) ? values.FirstOrDefault() : null;

    /// <summary>A Suricata timestamp. Suricata writes the offset without a colon ("-0600"), which DateTimeOffset does not parse.</summary>
    public static DateTimeOffset? ParseTimestamp(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 5 && value[^5] is '+' or '-' && value[^4..].All(char.IsAsciiDigit))
            value = value[..^2] + ":" + value[^2..];
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
    }

    private sealed class EveLine
    {
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("timestamp")] public string? Timestamp { get; set; }
        [JsonPropertyName("flow_id")] public long FlowId { get; set; }
        [JsonPropertyName("src_ip")] public string? SrcIp { get; set; }
        [JsonPropertyName("src_port")] public int SrcPort { get; set; }
        [JsonPropertyName("dest_ip")] public string? DestIp { get; set; }
        [JsonPropertyName("dest_port")] public int DestPort { get; set; }
        [JsonPropertyName("src_mac")] public string? SrcMac { get; set; }
        [JsonPropertyName("dst_mac")] public string? DstMac { get; set; }
        [JsonPropertyName("proto")] public string? Proto { get; set; }
        [JsonPropertyName("alert")] public EveAlert? Alert { get; set; }
        [JsonPropertyName("flow")] public EveFlow? Flow { get; set; }
        [JsonPropertyName("http")] public EveHttp? Http { get; set; }
        [JsonPropertyName("tls")] public EveTls? Tls { get; set; }
        [JsonPropertyName("ubnt")] public EveUbnt? Ubnt { get; set; }
    }

    private sealed class EveAlert
    {
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("gid")] public int Gid { get; set; }
        [JsonPropertyName("signature_id")] public long SignatureId { get; set; }
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("severity")] public int Severity { get; set; }
        [JsonPropertyName("metadata")] public Dictionary<string, List<string>>? Metadata { get; set; }
    }

    private sealed class EveFlow
    {
        [JsonPropertyName("src_ip")] public string? SrcIp { get; set; }
        [JsonPropertyName("dest_ip")] public string? DestIp { get; set; }
    }

    private sealed class EveHttp
    {
        [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    }

    private sealed class EveTls
    {
        [JsonPropertyName("sni")] public string? Sni { get; set; }
    }

    private sealed class EveUbnt
    {
        [JsonPropertyName("ubnt_category")] public string? UbntCategory { get; set; }
    }
}
