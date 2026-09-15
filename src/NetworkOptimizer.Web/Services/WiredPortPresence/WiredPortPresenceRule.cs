namespace NetworkOptimizer.Web.Services.WiredPortPresence;

/// <summary>A wired client present on a switch port by the port's link rather than the console's list.</summary>
public sealed record WiredPortPresence(string ClientMac, string SwitchMac, int Port, string? Ip, string? Name, DateTime LastListedAt);

/// <summary>A (switch, port, client) placement the console made, and when it last made it.</summary>
public sealed record PortSighting(string SwitchMac, int Port, string ClientMac, DateTime LastSeen, string? Ip, string? Name);

/// <summary>
/// The rule behind wired port presence. UniFi Network drops a wired client from its lists while
/// the port it sits on is up and the host is still sending; a client the console does not list is
/// present on a port when the console itself last placed it there, the port has carried no other
/// client, the port is up now, unicast is flowing into it, nothing else is on the port, and the
/// port is not an uplink. Failing any one, the console's verdict stands. The rule never marks a
/// listed client offline.
/// </summary>
public static class WiredPortPresenceRule
{
    /// <summary>How far back the console's last placement of a client still counts.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    /// <summary>
    /// How recent the newest unicast into the port has to be. Covers a missed poll at the slowest
    /// cadence a site can set, so one quiet sample does not read as a departure.
    /// </summary>
    public static readonly TimeSpan UnicastWindow = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Unicast packets into the port between two samples below which the host is asleep. A NIC
    /// with ARP offload answers requests for a sleeping host, and those replies are unicast; a
    /// trickle of them is not a client. Broadcast and multicast never count: the switch floods
    /// those out of every linked port, awake host or not.
    /// </summary>
    public const long UnicastFloorPackets = 20;

    /// <summary>
    /// Every client present by port. <paramref name="sightings"/> is the newest sighting per
    /// (switch, port, client); <paramref name="listedClientMacs"/> and <paramref name="occupiedPorts"/>
    /// are the console's current client list; <paramref name="portUp"/> answers for a port the
    /// console or SNMP knows, null for one neither does; <paramref name="unicastFlowing"/> is
    /// whether unicast into that port within <see cref="UnicastWindow"/> cleared
    /// <see cref="UnicastFloorPackets"/>, null when the port has no counters; <paramref name="uplinkPorts"/>
    /// are ports another device hangs off.
    /// </summary>
    public static IReadOnlyList<WiredPortPresence> Evaluate(
        IReadOnlyList<PortSighting> sightings,
        ISet<string> listedClientMacs,
        ISet<(string SwitchMac, int Port)> occupiedPorts,
        Func<string, int, bool?> portUp,
        Func<string, int, bool?> unicastFlowing,
        ISet<(string SwitchMac, int Port)> uplinkPorts,
        DateTime now)
    {
        var result = new List<WiredPortPresence>();
        var cutoff = now - Lookback;
        // Port counters cannot say which host sent the unicast, so a port that has carried more
        // than one client within the lookback is nobody's until a forwarding table can say.
        var clientsOnPort = sightings
            .Where(s => s.LastSeen >= cutoff)
            .GroupBy(s => (s.SwitchMac, s.Port))
            .ToDictionary(g => g.Key, g => g.Select(s => s.ClientMac).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var s in sightings
                     .GroupBy(s => s.ClientMac)
                     .Select(g => g.OrderByDescending(s => s.LastSeen).First()))
        {
            if (listedClientMacs.Contains(s.ClientMac)) continue;
            if (s.LastSeen < cutoff) continue;
            var port = (s.SwitchMac, s.Port);
            if (occupiedPorts.Contains(port)) continue;
            if (uplinkPorts.Contains(port)) continue;
            if (clientsOnPort.TryGetValue(port, out var count) && count > 1) continue;
            if (portUp(s.SwitchMac, s.Port) != true) continue;
            if (unicastFlowing(s.SwitchMac, s.Port) != true) continue;
            result.Add(new WiredPortPresence(s.ClientMac, s.SwitchMac, s.Port, s.Ip, s.Name, s.LastSeen));
        }
        return result;
    }
}
