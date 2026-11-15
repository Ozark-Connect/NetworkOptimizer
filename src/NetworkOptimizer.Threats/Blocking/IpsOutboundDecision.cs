using System.Text.Json;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// One CyberSecure (Suricata) alert on a flow a device on the site started toward the internet, from
/// the gateway agent's live feed or the console's threat log.
/// </summary>
public sealed record IpsOutboundAlert
{
    /// <summary>The device that started the flow.</summary>
    public required string ClientIp { get; init; }
    public string? ClientMac { get; init; }

    /// <summary>The internet end of the flow: the address a block would name.</summary>
    public required string RemoteIp { get; init; }
    public int RemotePort { get; init; }
    public string Protocol { get; init; } = "";

    public long SignatureId { get; init; }
    public string Signature { get; init; } = "";

    /// <summary>The rule's classtype description, e.g. "Malware Command and Control Activity Detected".</summary>
    public string Category { get; init; } = "";

    /// <summary>UniFi's rule group, e.g. MALWARE or BOTCC; empty when the source does not carry it.</summary>
    public string UbntCategory { get; init; } = "";

    /// <summary>1 (high) to 3 (low), as Suricata reports it.</summary>
    public int Severity { get; init; }

    /// <summary>The rule's confidence metadata (High, Medium, Low); empty when absent.</summary>
    public string Confidence { get; init; } = "";

    /// <summary>The rule's attack_target metadata values.</summary>
    public IReadOnlyList<string> AttackTargets { get; init; } = [];

    /// <summary>The HTTP host or TLS server name, when the alert carries one.</summary>
    public string? Hostname { get; init; }

    public DateTime Timestamp { get; init; }

    /// <summary>True for the gateway agent's live feed, false for the console's threat log.</summary>
    public bool FromAgent { get; init; }

    /// <summary>The key both sources share for one alert (the console's threat log carries no flow id).</summary>
    public string DedupKey => $"{ClientIp.ToLowerInvariant()}|{RemoteIp.ToLowerInvariant()}|{SignatureId}";
}

/// <summary>Where the collector hands the console's outbound IPS alerts (Automatic Blocking).</summary>
public interface IIpsOutboundSink
{
    /// <summary>Acts on an outbound alert from a site (null = default site); null when it was already handled.</summary>
    Task<IpsOutboundOutcome?> OnIpsOutboundAlertAsync(string? site, IpsOutboundAlert alert, CancellationToken ct);
}

/// <summary>What an alert says about its remote end.</summary>
public enum IpsAlertClass
{
    /// <summary>Not a threat (policy, informational, low severity or confidence): recorded only.</summary>
    Noise = 0,

    /// <summary>The device on the site is the attacker; the remote end is its victim and is never blocked.</summary>
    DeviceAttacking = 1,

    /// <summary>The remote end is the threat.</summary>
    RemoteThreat = 2,

    /// <summary>The remote end is a command-and-control server: the longest first ban.</summary>
    CommandAndControl = 3,
}

/// <summary>The evidence stored on an IPS block (<see cref="ThreatBlock.Evidence"/>).</summary>
public sealed record IpsBlockEvidence(long SignatureId, string Signature, string Category, string ClientIp, string? ClientMac,
    string? Hostname, string OriginSite, IpsAlertClass Class)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static IpsBlockEvidence? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<IpsBlockEvidence>(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>The decision on one alert: a block to record, or why there is none.</summary>
public sealed record IpsOutboundOutcome(IpsAlertClass Class, ThreatBlock? Block, string? Skipped);

/// <summary>
/// Decides whether an outbound IPS alert blocks its remote end. Pure: the caller supplies the settings,
/// the history, and the remote end's ASN.
/// </summary>
public static class IpsOutboundDecision
{
    /// <summary>First ban for a command-and-control server, before strikes.</summary>
    public const int CommandAndControlBanMinutes = 1440;

    /// <summary>An alert older than this is history, not an attack in progress, and blocks nothing.</summary>
    public static readonly TimeSpan MaxAlertAge = TimeSpan.FromMinutes(15);

    // Rule groups where the device on the site attacks: the remote end is the victim.
    private static readonly HashSet<string> AttackingGroups = new(StringComparer.OrdinalIgnoreCase)
        { "SCAN", "DOS", "EXPLOIT", "WEB_SERVER", "WEB_SPECIFIC_APPS", "SQL", "WORM", "SHELLCODE", "NETBIOS", "RPC", "SNMP", "TELNET", "FTP", "TFTP", "SMTP", "IMAP", "POP3", "VOIP" };

    // Rule groups that name an address, or content, as malicious.
    private static readonly HashSet<string> ThreatGroups = new(StringComparer.OrdinalIgnoreCase)
        { "MALWARE", "MOBILE_MALWARE", "BOTCC", "COMPROMISED", "CIARMY", "DSHIELD", "DROP", "WEB_CLIENT", "ATTACK_RESPONSE", "USER_AGENTS", "ACTIVEX", "PHISHING", "CURRENT_EVENTS", "EXPLOIT_KIT", "DNS", "MALICIOUS_HOSTS" };

    // Rule groups and signature prefixes that describe policy or activity, not a threat.
    private static readonly HashSet<string> NoiseGroups = new(StringComparer.OrdinalIgnoreCase)
        { "POLICY", "INFO", "GAMES", "P2P", "CHAT", "HUNTING", "TOR", "INAPPROPRIATE", "ADWARE_PUP" };
    private static readonly string[] NoisePrefixes =
        ["ET POLICY", "ET INFO", "ET GAMES", "ET P2P", "ET CHAT", "ET HUNTING", "ET TOR", "ET INAPPROPRIATE", "GPL ICMP_INFO", "ET ADWARE_PUP"];
    private static readonly HashSet<string> NoiseCategories = new(StringComparer.OrdinalIgnoreCase)
        { "Not Suspicious Traffic", "Potential Corporate Privacy Violation", "Misc activity", "Unknown Traffic", "Generic Protocol Command Decode" };

    private const string CommandAndControlCategory = "Malware Command and Control Activity Detected";

    // attack_target values that put the device on the site on the receiving end.
    private static readonly HashSet<string> ClientTargets = new(StringComparer.OrdinalIgnoreCase)
        { "Client_Endpoint", "Mobile_Client", "IoT", "Client_and_Server" };

    /// <summary>
    /// Networks whose addresses many unrelated sites share (CDNs, hyperscaler front ends). A block there
    /// cuts every sharing site off from everything else behind the same address, so it never happens.
    /// </summary>
    public static readonly IReadOnlySet<int> SharedHostingAsns = new HashSet<int>
    {
        13335, 209242,                       // Cloudflare
        20940, 16625, 16702, 21342, 32787,   // Akamai
        54113,                               // Fastly
        16509, 14618,                        // Amazon
        15169, 396982, 19527, 36040,         // Google
        8075, 8068, 8069,                    // Microsoft
        714, 6185,                           // Apple
        32934,                               // Meta
        15133,                               // Edgio
        22822,                               // Limelight
        200325,                              // Bunny
        12989, 20446,                        // StackPath
        31898,                               // Oracle
        45102,                               // Alibaba
    };

    /// <summary>
    /// The outbound alert in a collected IPS event from the console's threat log, or null when the event is
    /// not one: a private address started it toward a public one. The log carries no rule group or metadata.
    /// </summary>
    public static IpsOutboundAlert? FromConsoleEvent(ThreatEvent evt)
    {
        if (evt.EventSource != EventSource.Ips || evt.SignatureId <= 0) return null;
        if (!NetworkUtilities.IsLocalAddress(evt.SourceIp, []) || !NetworkUtilities.IsPublicIpAddress(evt.DestIp)) return null;
        return new IpsOutboundAlert
        {
            ClientIp = evt.SourceIp,
            ClientMac = evt.SourceMac,
            RemoteIp = evt.DestIp,
            RemotePort = evt.DestPort,
            Protocol = evt.Protocol.ToLowerInvariant(),
            SignatureId = evt.SignatureId,
            Signature = evt.SignatureName,
            Category = evt.Category,
            // ThreatEvent keeps severity on a 1-5 scale (5 = critical); Suricata's is 1 (high) to 4.
            Severity = evt.Severity switch { 5 => 1, 4 => 2, 2 => 3, 1 => 4, _ => 3 },
            Hostname = evt.Domain,
            Timestamp = evt.Timestamp,
            FromAgent = false,
        };
    }

    /// <summary>What the alert says about its remote end.</summary>
    public static IpsAlertClass Classify(IpsOutboundAlert alert)
    {
        if (alert.Severity >= 3) return IpsAlertClass.Noise;
        if (alert.Confidence.Equals("Low", StringComparison.OrdinalIgnoreCase)) return IpsAlertClass.Noise;
        if (NoiseGroups.Contains(alert.UbntCategory) || NoiseCategories.Contains(alert.Category)
            || NoisePrefixes.Any(p => alert.Signature.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return IpsAlertClass.Noise;

        if (alert.UbntCategory.Equals("BOTCC", StringComparison.OrdinalIgnoreCase)
            || alert.Category.Equals(CommandAndControlCategory, StringComparison.OrdinalIgnoreCase)
            || alert.Signature.StartsWith("ET CNC", StringComparison.OrdinalIgnoreCase))
            return IpsAlertClass.CommandAndControl;

        // attack_target is the rule author's own statement of who is attacked, so it wins over the group.
        if (alert.AttackTargets.Count > 0)
            return alert.AttackTargets.Any(ClientTargets.Contains) ? IpsAlertClass.RemoteThreat : IpsAlertClass.DeviceAttacking;

        if (AttackingGroups.Contains(alert.UbntCategory)) return IpsAlertClass.DeviceAttacking;
        if (ThreatGroups.Contains(alert.UbntCategory)) return IpsAlertClass.RemoteThreat;
        // The console's threat log carries no rule group; an outbound alert at severity 1 is a threat.
        return string.IsNullOrEmpty(alert.UbntCategory) && alert.Severity == 1 ? IpsAlertClass.RemoteThreat : IpsAlertClass.Noise;
    }

    /// <summary>
    /// The block for an alert, or why there is none. <paramref name="activeOutbound"/> holds addresses the
    /// site already blocks outbound; <paramref name="priorStrikes"/> its blocks per address in the strike window.
    /// </summary>
    public static IpsOutboundOutcome Decide(
        IpsOutboundAlert alert,
        string? originSite,
        BlockingSettings settings,
        DateTime now,
        IReadOnlySet<string> activeOutbound,
        IReadOnlyDictionary<string, int> priorStrikes,
        BlockAllowlist allowlist,
        int? remoteAsn)
    {
        var cls = Classify(alert);
        if (cls is IpsAlertClass.Noise or IpsAlertClass.DeviceAttacking) return new(cls, null, null);
        // The collector's backfill re-reads weeks of the console's log; only a current alert is acted on.
        if (now - alert.Timestamp > MaxAlertAge) return new(cls, null, null);
        if (settings.Mode == BlockingMode.Off || !settings.IpsOutbound) return new(cls, null, "IPS outbound blocking is off");
        if (BlockAllowlist.Normalize(alert.RemoteIp) is not { } ip) return new(cls, null, "Not an address");
        if (!NetworkUtilities.IsPublicIpAddress(ip)) return new(cls, null, "Not a public address");
        if (activeOutbound.Contains(ip)) return new(cls, null, "Already blocked");
        if (allowlist.Exempts(ip) is { } exempt) return new(cls, null, $"Exempt: {exempt}");
        if (remoteAsn is { } asn && SharedHostingAsns.Contains(asn)) return new(cls, null, $"Shared hosting (AS{asn})");

        var strikes = priorStrikes.TryGetValue(ip, out var s) ? s : 0;
        var baseMinutes = cls == IpsAlertClass.CommandAndControl
            ? Math.Max(CommandAndControlBanMinutes, settings.BanMinutes)
            : settings.BanMinutes;
        var evidence = new IpsBlockEvidence(alert.SignatureId, alert.Signature, alert.Category, alert.ClientIp, alert.ClientMac,
            alert.Hostname, originSite ?? "", cls);
        return new(cls, new ThreatBlock
        {
            SourceIp = ip,
            Trigger = BlockTrigger.IpsOutbound,
            Direction = BlockDirection.Outbound,
            Reason = Truncate(cls == IpsAlertClass.CommandAndControl ? $"Command and control: {alert.Signature}" : $"IPS: {alert.Signature}", 256),
            Evidence = evidence.ToJson(),
            Strikes = strikes,
            Enforced = settings.Mode == BlockingMode.Enforce,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(BlockDecisionEngine.BanMinutes(baseMinutes, strikes)),
        }, null);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
