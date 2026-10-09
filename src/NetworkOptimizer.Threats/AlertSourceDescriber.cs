using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Threats;

/// <summary>
/// Describes an alert's source IP. A public source is named by its country and ASN; an internal
/// source has neither, so it is named by its UniFi client name and network instead.
/// </summary>
internal sealed class AlertSourceDescriber
{
    private readonly Dictionary<string, string> _networkByIp = new(StringComparer.Ordinal);
    private readonly Func<UniFiApiClient?> _clientFactory;
    private Dictionary<string, string>? _clientNames;

    public AlertSourceDescriber(IEnumerable<ThreatEvent> events, Func<UniFiApiClient?> clientFactory)
    {
        _clientFactory = clientFactory;
        AddEvents(events);
    }

    /// <summary>Learns each source IP's network name from flow events (latest wins).</summary>
    public void AddEvents(IEnumerable<ThreatEvent> events)
    {
        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            if (!string.IsNullOrEmpty(evt.NetworkName) && !string.IsNullOrEmpty(evt.SourceIp))
                _networkByIp[evt.SourceIp] = evt.NetworkName;
        }
    }

    public async Task<AlertSource> DescribeAsync(string ip, string? countryCode, string? asnOrg,
        CancellationToken cancellationToken)
    {
        if (!NetworkUtilities.IsPrivateIpAddress(ip))
            return AlertSource.Public(ip, countryCode, asnOrg);

        _networkByIp.TryGetValue(ip, out var network);
        var names = await LoadClientNamesAsync(cancellationToken);
        names.TryGetValue(ip, out var client);
        return AlertSource.Internal(ip, client, network);
    }

    // One console call per collection cycle, and only when an internal source is alerted on.
    private async Task<Dictionary<string, string>> LoadClientNamesAsync(CancellationToken cancellationToken)
    {
        if (_clientNames != null) return _clientNames;
        _clientNames = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var apiClient = _clientFactory();
            if (apiClient == null) return _clientNames;
            foreach (var c in await apiClient.GetAllKnownClientsAsync(cancellationToken))
            {
                var name = !string.IsNullOrEmpty(c.Name) ? c.Name : c.Hostname;
                if (!string.IsNullOrEmpty(c.BestIp) && !string.IsNullOrEmpty(name))
                    _clientNames.TryAdd(c.BestIp, name);
            }
        }
        catch
        {
            // Names are a nicety: the alert still goes out with the IP and network.
        }
        return _clientNames;
    }
}

/// <summary>
/// How an alert names its source IP.
/// </summary>
internal sealed record AlertSource(string Ip, bool IsInternal, string? CountryCode, string? AsnOrg,
    string? ClientName, string? NetworkName)
{
    public static AlertSource Public(string ip, string? countryCode, string? asnOrg) =>
        new(ip, false, countryCode, asnOrg, null, null);

    public static AlertSource Internal(string ip, string? clientName, string? networkName) =>
        new(ip, true, null, null, clientName, networkName);

    /// <summary>"203.0.113.5 (US)" for a public source, "10.0.0.20 (Phone, Main LAN)" for an internal one.</summary>
    public string Text => $"{Ip} ({Detail})";

    /// <summary>" (Phone, Main LAN)" for an internal source, empty for a public one.</summary>
    public string InternalSuffix => IsInternal ? $" ({Detail})" : "";

    private string Detail => IsInternal
        ? string.Join(", ", new[] { ClientName, NetworkName }.Where(p => !string.IsNullOrEmpty(p))) is { Length: > 0 } d
            ? d
            : "internal"
        : CountryCode ?? "unknown";

    /// <summary>Adds the source keys: country and ASN for a public source, client and network for an internal one.</summary>
    public Dictionary<string, string> WithContext(Dictionary<string, string> context)
    {
        if (IsInternal)
        {
            context["client"] = ClientName ?? "";
            context["network"] = NetworkName ?? "";
        }
        else
        {
            context["country"] = CountryCode ?? "unknown";
            context["asn"] = AsnOrg ?? "unknown";
        }
        return context;
    }
}
