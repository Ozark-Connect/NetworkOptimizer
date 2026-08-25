using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Providers;

/// <summary>
/// AP-measured client readings for the access points whose AP Agent is currently the source.
///
/// The implementation reads the monitoring time series in the Web layer. The abstraction lives here
/// so the reference direction stays Web -> WiFi: the rule engine never learns what a bucket is.
/// </summary>
public interface IMeasuredWirelessClientSource
{
    /// <summary>
    /// Readings keyed by access point MAC, lower case, for whichever of <paramref name="apMacs"/>
    /// the AP Agent currently covers. An access point absent from the result keeps its console
    /// data, which is what makes a mixed fleet the normal case rather than an edge one, and an
    /// empty result means the console path is used exactly as it is today.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<MeasuredWirelessClient>>> GetMeasuredClientsAsync(
        IReadOnlyCollection<string> apMacs,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// AP-measured samples for one client over a range, at most one per <paramref name="bucket"/>.
    /// An empty result means the series holds nothing for the range, and the console's own history
    /// stands alone exactly as it does today.
    /// </summary>
    Task<IReadOnlyList<MeasuredClientSample>> GetMeasuredClientHistoryAsync(
        string clientMac,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan bucket,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One bucket of AP-measured history for a client. Only the values the access point actually
/// measured are here: protocol and packet counts are console-only, and the stored counters are
/// cumulative rather than the per-bucket deltas the console report gives, so neither is carried.
/// </summary>
public sealed class MeasuredClientSample
{
    /// <summary>Start of the bucket this sample represents.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>Access point that measured it.</summary>
    public string? ApMac { get; init; }

    /// <summary>Band of the link measured.</summary>
    public RadioBand Band { get; init; }

    /// <summary>Channel of the link measured.</summary>
    public int? Channel { get; init; }

    /// <summary>Channel width in MHz of the link measured.</summary>
    public int? ChannelWidth { get; init; }

    /// <summary>Signal in dBm.</summary>
    public int? Signal { get; init; }

    /// <summary>Transmit rate in Kbps (access point to client).</summary>
    public long? TxRateKbps { get; init; }

    /// <summary>Receive rate in Kbps (client to access point).</summary>
    public long? RxRateKbps { get; init; }

    /// <summary>The access point's satisfaction score.</summary>
    public double? Satisfaction { get; init; }
}

/// <summary>
/// One client as its access point measured it.
///
/// Every scalar describes the SAME link, the active one: the AP Agent resolves an MLO client to one
/// record with active-link values before anything is stored, so nothing downstream re-derives them.
/// </summary>
public sealed class MeasuredWirelessClient
{
    /// <summary>Client key: the MLD MAC for an MLO client, the station MAC otherwise.</summary>
    public string Mac { get; init; } = string.Empty;

    /// <summary>MAC of the access point that measured it.</summary>
    public string ApMac { get; init; } = string.Empty;

    /// <summary>When the access point took the reading.</summary>
    public DateTimeOffset MeasuredAt { get; init; }

    /// <summary>Active link's band.</summary>
    public RadioBand Band { get; init; }

    /// <summary>Active link's channel.</summary>
    public int? Channel { get; init; }

    /// <summary>Active link's channel width in MHz.</summary>
    public int? ChannelWidth { get; init; }

    /// <summary>Active link's signal in dBm. The AP's own dBm reading, never its SNR.</summary>
    public int? Signal { get; init; }

    /// <summary>Active link's noise floor in dBm.</summary>
    public int? Noise { get; init; }

    /// <summary>Active link's signal-to-noise ratio in dB, which is what the AP calls RSSI.</summary>
    public int? Rssi { get; init; }

    /// <summary>Active link's transmit rate in Kbps.</summary>
    public long? TxRate { get; init; }

    /// <summary>Active link's receive rate in Kbps.</summary>
    public long? RxRate { get; init; }

    /// <summary>The access point's satisfaction score for the client.</summary>
    public int? Satisfaction { get; init; }

    /// <summary>
    /// Bands this client has been measured on over the capability lookback. A client that has
    /// associated on a band demonstrably supports it, and that is the only capability evidence the
    /// series carries.
    /// </summary>
    public IReadOnlyCollection<RadioBand> ObservedBands { get; init; } = Array.Empty<RadioBand>();
}
