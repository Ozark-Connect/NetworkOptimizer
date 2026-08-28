using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Turns one AP Agent client record into the live fields Client Performance shows.
///
/// The agent has already resolved an MLO client to one record keyed on its MLD MAC, with scalars
/// describing the ACTIVE link, so this reads what it resolved rather than re-deriving from the
/// links: picking a link here would render a healthy client as dying, since one measured client's
/// links span 56 dB.
/// </summary>
public static class ApAgentClientIdentityMapper
{
    /// <summary>
    /// The agent's band token in the console's spelling, which is what the page's band display,
    /// band classes, and radio matching all key on. Null when the token is not recognized.
    /// </summary>
    public static string? MapBand(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        "2.4" or "2.4ghz" or "ng" => "ng",
        "5" or "5ghz" or "na" => "na",
        "6" or "6ghz" or "6e" or "6g" => "6e",
        _ => null,
    };

    /// <summary>
    /// The console's radio-protocol spelling for a driver phy-mode token such as
    /// "IEEE80211_MODE_11AXA_HE160". Null when unrecognized, and the caller then leaves whatever
    /// the console last reported in place rather than replacing it with a guess.
    /// </summary>
    public static string? MapProtocol(string? mode, string? bandToken)
    {
        var m = (mode ?? "").ToUpperInvariant();
        if (m.Length == 0) return null;

        // Checked widest-generation first: "11AXA" contains "11A", and "11BE" contains "11B".
        if (m.Contains("11BE")) return "be";
        if (m.Contains("11AX")) return "ax";
        if (m.Contains("11AC")) return "ac";
        if (m.Contains("11N")) return bandToken == "ng" ? "ng" : "na";
        if (m.Contains("11G")) return "ng";
        if (m.Contains("11B")) return "ng";
        if (m.Contains("11A")) return "na";
        return null;
    }

    /// <summary>
    /// The live fields for one client, or null when the record cannot be placed on a band. Only
    /// fields the access point actually reported are set, so the caller merges rather than
    /// overwrites and a value the agent does not carry keeps its console-sourced reading.
    /// </summary>
    public static ClientIdentity? ToLiveIdentity(ApAgentClient client, string apMac)
    {
        var band = MapBand(client.Band) ?? MapBand(ActiveLink(client)?.Band);
        if (band == null) return null;

        var active = ActiveLink(client);
        var identity = new ClientIdentity
        {
            Mac = client.MldMac is { Length: > 0 } mld ? mld : client.Mac,
            SignalDbm = client.Signal,
            NoiseDbm = client.Noise,
            Channel = client.Channel > 0 ? client.Channel : null,
            ChannelWidth = client.Bandwidth > 0 ? client.Bandwidth : null,
            Band = band,
            Protocol = MapProtocol(active?.Mode, band),
            TxRateKbps = client.TxRateKbps > 0 ? client.TxRateKbps : null,
            RxRateKbps = client.RxRateKbps > 0 ? client.RxRateKbps : null,
            Satisfaction = client.Satisfaction,
            IsMlo = client.IsMlo,
            ApMac = ApAgentWifiFieldMapper.NormalizeMac(apMac),
            HasApAgentData = true,
        };

        if (client.IsMlo && client.Links.Count > 0)
            identity.MloLinks = client.Links.Select(l => ToLinkDetail(l, band)).ToList();

        return identity;
    }

    /// <summary>One association, in the shape the MLO pill and its tooltip already render.</summary>
    private static MloLinkDetail ToLinkDetail(ApAgentClientLink link, string clientBand)
    {
        var band = MapBand(link.Band);
        return new MloLinkDetail
        {
            Mac = link.Mac,
            Radio = band,
            RadioProto = MapProtocol(link.Mode, band ?? clientBand),
            Channel = link.Channel > 0 ? link.Channel : null,
            ChannelWidth = link.Bandwidth > 0 ? link.Bandwidth : null,
            Signal = link.Signal,
            Noise = link.Noise,
            Rssi = link.Snr,
            Nss = link.Nss > 0 ? link.Nss : null,
            TxRate = link.TxRateKbps > 0 ? link.TxRateKbps : null,
            RxRate = link.RxRateKbps > 0 ? link.RxRateKbps : null,
            Satisfaction = link.Satisfaction,
            // The access point says outright which link carries traffic, so the page does not have
            // to infer it from a rate being non-zero the way the console path must.
            ActiveLink = link.Active,
        };
    }

    /// <summary>The link carrying traffic, as the agent marked it. Falls back to the only link.</summary>
    private static ApAgentClientLink? ActiveLink(ApAgentClient client)
    {
        ApAgentClientLink? first = null;
        foreach (var link in client.Links)
        {
            if (link.Active) return link;
            first ??= link;
        }
        return first;
    }
}
