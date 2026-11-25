namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// Finds the LAN Health link behind a pair of neighbors on a speed test path (or a client and the port it
/// sits on), so a path drawn elsewhere can open the link's page. Device MACs in any case or separator.
/// </summary>
public static class LanHealthLinkMatch
{
    /// <summary>
    /// The link between two neighbors: a fabric link joining both devices (either way round), else the access
    /// port one of them is the client on, whose switch is the other.
    /// </summary>
    public static LinkHealth? Between(IEnumerable<LinkHealth> links, string? macA, string? macB)
    {
        var a = LanLinkBuilder.Mac(macA);
        var b = LanLinkBuilder.Mac(macB);
        if (a.Length == 0 || b.Length == 0) return null;
        var list = links as IReadOnlyCollection<LinkHealth> ?? links.ToList();
        return list.FirstOrDefault(l => l.Link.IsFabric && Joins(l.Link, a, b))
            ?? list.FirstOrDefault(l => !l.Link.IsFabric
                && (On(l.Link, a) && l.Link.Upstream.DeviceMac == b || On(l.Link, b) && l.Link.Upstream.DeviceMac == a));
    }

    /// <summary>The access port a wired client sits on, by the client's MAC.</summary>
    public static LinkHealth? ForClient(IEnumerable<LinkHealth> links, string? clientMac)
    {
        var mac = LanLinkBuilder.Mac(clientMac);
        return mac.Length == 0 ? null : links.FirstOrDefault(l => !l.Link.IsFabric && On(l.Link, mac));
    }

    /// <summary>
    /// Each device port's link, keyed (device MAC, port number): either end of a fabric link, or an access port.
    /// </summary>
    public static Dictionary<(string Mac, int Port), LinkHealth> ByPort(IEnumerable<LinkHealth> links)
    {
        var map = new Dictionary<(string, int), LinkHealth>();
        foreach (var l in links)
            foreach (var end in new[] { l.Link.Upstream, l.Link.Downstream })
                if (end.PortIdx is int p && end.DeviceMac.Length > 0)
                    map.TryAdd((LanLinkBuilder.Mac(end.DeviceMac), p), l);
        return map;
    }

    /// <summary>Whether a client is on an access port: its named client, or any of the port's occupants.</summary>
    private static bool On(LinkRecord link, string clientMac) =>
        link.ClientMac == clientMac || link.OccupantMacs.Contains(clientMac);

    /// <summary>Whether a link joins the two devices, either way round.</summary>
    internal static bool Joins(LinkRecord link, string a, string b) =>
        link.Upstream.DeviceMac == a && link.Downstream.DeviceMac == b || link.Upstream.DeviceMac == b && link.Downstream.DeviceMac == a;
}
