using System.Text.Json;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Models;

/// <summary>
/// A wireless client's link to its access point, as the network path draws it: what a speed test
/// stored when it ran, or what the client reports now. Rates are the access point's (TX is toward
/// the client), in Kbps.
/// </summary>
public sealed record ClientWifiLink
{
    public string? ClientMac { get; init; }

    /// <summary>The radio code ("ng", "na", "6e").</summary>
    public string? Radio { get; init; }

    public string? RadioProto { get; init; }
    public int? Channel { get; init; }
    public int? ChannelWidthMhz { get; init; }
    public int? SignalDbm { get; init; }
    public int? NoiseDbm { get; init; }
    public long? TxRateKbps { get; init; }
    public long? RxRateKbps { get; init; }

    /// <summary>True for an MLO client, whether or not its per-link detail is known.</summary>
    public bool IsMlo { get; init; }

    /// <summary>An MLO client's links. Empty when the client is not MLO or the detail is absent.</summary>
    public IReadOnlyList<ClientWifiLinkBand> MloLinks { get; init; } = [];

    /// <summary>The link a speed test stored. Every field is empty for a test with no Wi-Fi capture.</summary>
    public static ClientWifiLink FromResult(Iperf3Result result)
    {
        return new ClientWifiLink
        {
            ClientMac = result.ClientMac,
            Radio = result.WifiRadio,
            RadioProto = result.WifiRadioProto,
            Channel = result.WifiChannel,
            ChannelWidthMhz = result.WifiChannelWidthMhz,
            SignalDbm = result.WifiSignalDbm,
            NoiseDbm = result.WifiNoiseDbm,
            TxRateKbps = result.WifiTxRateKbps,
            RxRateKbps = result.WifiRxRateKbps,
            IsMlo = result.WifiIsMlo,
            MloLinks = ParseMloLinks(result.WifiMloLinksJson)
        };
    }

    /// <summary>The link a connected client reports now, or null for a wired or VPN client.</summary>
    public static ClientWifiLink? FromClient(ClientIdentity? client)
    {
        if (client == null || client.IsWired || client.IsVpn)
            return null;

        return new ClientWifiLink
        {
            ClientMac = client.Mac,
            Radio = client.Band,
            RadioProto = client.Protocol,
            Channel = client.Channel,
            ChannelWidthMhz = client.ChannelWidth,
            SignalDbm = client.SignalDbm,
            NoiseDbm = client.NoiseDbm,
            TxRateKbps = client.TxRateKbps,
            RxRateKbps = client.RxRateKbps,
            IsMlo = client.IsMlo,
            MloLinks = client.MloLinks?
                .Select(l => new ClientWifiLinkBand(l.Radio, l.Channel, l.ChannelWidth, l.Signal, l.Noise, l.TxRate, l.RxRate))
                .ToList() ?? []
        };
    }

    // The shape UniFiConnectionService serializes into Iperf3Result.WifiMloLinksJson.
    private sealed record StoredMloLink(string? radio, int? channel, int? channelWidth, int? signal, int? noise, long? txRate, long? rxRate);

    private static IReadOnlyList<ClientWifiLinkBand> ParseMloLinks(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<StoredMloLink>>(json)?
                .Select(l => new ClientWifiLinkBand(l.radio, l.channel, l.channelWidth, l.signal, l.noise, l.txRate, l.rxRate))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>One link of an MLO client. Rates are the access point's, in Kbps.</summary>
public sealed record ClientWifiLinkBand(
    string? Radio,
    int? Channel,
    int? ChannelWidthMhz,
    int? SignalDbm,
    int? NoiseDbm,
    long? TxRateKbps,
    long? RxRateKbps);
