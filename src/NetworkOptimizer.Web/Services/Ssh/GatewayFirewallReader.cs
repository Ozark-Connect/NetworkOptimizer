using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Threats.Blocking;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Reads the gateway's firewall state for the Security Audit in one read-only SSH round trip: the live udapi
/// filter (GET only), both filter tables, and the ipset headers (never members). A partial read is discarded, so
/// a dropped session never reads as rules missing from the gateway.
/// </summary>
public sealed class GatewayFirewallReader
{
    private const string UdapiMarker = "###NO_UDAPI";
    private const string Ipv4Marker = "###NO_IPT4";
    private const string Ipv6Marker = "###NO_IPT6";
    private const string IpsetMarker = "###NO_IPSET";
    private const string EndMarker = "###NO_END";

    // Every command is a read. udapi takes GET only; PUT, POST, DELETE, and INTERNAL write the gateway.
    internal const string Command =
        $"echo '{UdapiMarker}'; command -v ubios-udapi-client >/dev/null 2>&1 && ubios-udapi-client -r -t 10 GET /firewall/filter 2>/dev/null; echo; " +
        $"echo '{Ipv4Marker}'; iptables-save -t filter 2>/dev/null; " +
        $"echo '{Ipv6Marker}'; ip6tables-save -t filter 2>/dev/null; " +
        $"echo '{IpsetMarker}'; ipset list -t 2>/dev/null; " +
        $"echo '{EndMarker}'; true";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private readonly IGatewaySshService _ssh;
    private readonly ILogger _logger;

    public GatewayFirewallReader(IGatewaySshService ssh, ILogger logger)
    {
        _ssh = ssh;
        _logger = logger;
    }

    /// <summary>The snapshot, or null when the site has no usable gateway SSH or the read did not complete.</summary>
    public async Task<GatewayFirewallSnapshot?> ReadAsync(CancellationToken ct = default)
    {
        var settings = await _ssh.GetSettingsAsync();
        if (!settings.Enabled || string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
        {
            _logger.LogInformation("Gateway firewall check skipped: no gateway SSH configured");
            return null;
        }
        if (await _ssh.IsAwaitingAgentTunnelAsync())
        {
            _logger.LogInformation("Gateway firewall check skipped: the gateway's On-Site Agent is offline");
            return null;
        }

        var (success, output) = await _ssh.RunCommandAsync(Command, Timeout, ct);
        if (!success)
        {
            _logger.LogWarning("Gateway firewall check skipped: the gateway read failed ({Error})", output.Trim());
            return null;
        }
        var snapshot = Parse(output);
        if (snapshot == null)
            _logger.LogWarning("Gateway firewall check skipped: the gateway read ended early ({Bytes:N0} bytes)", output.Length);
        return snapshot;
    }

    /// <summary>Splits the command's output into its sections; null when the end marker is missing.</summary>
    internal static GatewayFirewallSnapshot? Parse(string output)
    {
        var sections = new Dictionary<string, System.Text.StringBuilder>(StringComparer.Ordinal);
        System.Text.StringBuilder? current = null;
        var ended = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line is UdapiMarker or Ipv4Marker or Ipv6Marker or IpsetMarker)
            {
                sections[line] = current = new System.Text.StringBuilder();
                continue;
            }
            if (line == EndMarker)
            {
                ended = true;
                break;
            }
            current?.AppendLine(line);
        }
        if (!ended) return null;

        string? Section(string marker) =>
            sections.TryGetValue(marker, out var text) && text.ToString().Trim() is { Length: > 0 } s ? s : null;
        return new GatewayFirewallSnapshot
        {
            UdapiFilterJson = Section(UdapiMarker),
            Iptables = Section(Ipv4Marker),
            Ip6tables = Section(Ipv6Marker),
            IpsetHeaders = Section(IpsetMarker),
        };
    }

    /// <summary>
    /// What Threat Intelligence's gateway agent should hold, from what the server last sent it; null when the site
    /// does not block through a gateway agent.
    /// </summary>
    public static GatewayBlocklistExpectation? Expectation(IGatewayAgentBlocklist? blocklist, string? site)
    {
        if (blocklist?.LastSent(site) is not { } sent) return null;
        var entries = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [GatewayBlocklistRules.BlockV4] = sent.BlockV4,
            [GatewayBlocklistRules.BlockV6] = sent.BlockV6,
            [GatewayBlocklistRules.OutV4] = sent.OutV4,
            [GatewayBlocklistRules.OutV6] = sent.OutV6,
        };
        if (sent.FeedsSplit)
        {
            entries[GatewayBlocklistRules.FeedV4] = sent.FeedV4;
            entries[GatewayBlocklistRules.FeedV6] = sent.FeedV6;
        }
        return new GatewayBlocklistExpectation
        {
            ChainName = GatewayBlocklistRules.ChainName,
            CoreRulesV4 = GatewayBlocklistRules.CoreChain(sent.FeedsSplit, v6: false),
            CoreRulesV6 = GatewayBlocklistRules.CoreChain(sent.FeedsSplit, v6: true),
            SentEntries = entries,
        };
    }
}
