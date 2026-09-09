namespace NetworkOptimizer.Web.Services.WiredPortPresence;

/// <summary>A wired client present on a switch port by the port's link rather than the console's list.</summary>
public sealed record WiredPortPresence(string ClientMac, string SwitchMac, int Port, string? Ip, string? Name, DateTime LastListedAt);

/// <summary>A (switch, port, client) placement the console made, and when it last made it.</summary>
public sealed record PortSighting(string SwitchMac, int Port, string ClientMac, DateTime LastSeen, string? Ip, string? Name);

/// <summary>
/// The rule behind wired port presence. UniFi Network drops a wired client from its lists while
/// the port it sits on is up and the host is still sending; a client the console does not list is
/// present on a port when the console itself last placed it there, the port is up now, the host
/// behind it is transmitting, nothing else is on the port, and the port is not an uplink. Failing
/// any one, the console's verdict stands. The rule never marks a listed client offline.
/// </summary>
public static class WiredPortPresenceRule
{
    /// <summary>How far back the console's last placement of a client still counts.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    /// <summary>Over how long the host's own traffic into the switch is judged.</summary>
    public static readonly TimeSpan TransmitWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Host-to-switch rate below which a linked port is a sleeping NIC, not a client. Only the
    /// inbound side counts: the switch floods broadcast and multicast out of every linked port,
    /// awake host or not, so the outbound rate says nothing about the host.
    /// </summary>
    public const double TransmitFloorBps = 1_000;

    /// <summary>
    /// Every client present by port. <paramref name="sightings"/> is the newest sighting per
    /// (switch, port, client); <paramref name="listedClientMacs"/> and <paramref name="occupiedPorts"/>
    /// are the console's current client list; <paramref name="portUp"/> answers for a port the
    /// console or SNMP knows, null for one neither does; <paramref name="hostTransmitting"/> is
    /// whether the host's traffic into that port over <see cref="TransmitWindow"/> clears
    /// <see cref="TransmitFloorBps"/>, null when the port has no counters; <paramref name="uplinkPorts"/>
    /// are ports another device hangs off.
    /// </summary>
    public static IReadOnlyList<WiredPortPresence> Evaluate(
        IReadOnlyList<PortSighting> sightings,
        ISet<string> listedClientMacs,
        ISet<(string SwitchMac, int Port)> occupiedPorts,
        Func<string, int, bool?> portUp,
        Func<string, int, bool?> hostTransmitting,
        ISet<(string SwitchMac, int Port)> uplinkPorts,
        DateTime now)
    {
        var result = new List<WiredPortPresence>();
        var cutoff = now - Lookback;
        var newestOnPort = sightings
            .GroupBy(s => (s.SwitchMac, s.Port))
            .ToDictionary(g => g.Key, g => g.Max(s => s.LastSeen));

        foreach (var s in sightings
                     .GroupBy(s => s.ClientMac)
                     .Select(g => g.OrderByDescending(s => s.LastSeen).First()))
        {
            if (listedClientMacs.Contains(s.ClientMac)) continue;
            if (s.LastSeen < cutoff) continue;
            var port = (s.SwitchMac, s.Port);
            if (occupiedPorts.Contains(port)) continue;
            if (uplinkPorts.Contains(port)) continue;
            // A later placement of another client on this port means it changed hands.
            if (newestOnPort.TryGetValue(port, out var newest) && newest > s.LastSeen) continue;
            if (portUp(s.SwitchMac, s.Port) != true) continue;
            if (hostTransmitting(s.SwitchMac, s.Port) != true) continue;
            result.Add(new WiredPortPresence(s.ClientMac, s.SwitchMac, s.Port, s.Ip, s.Name, s.LastSeen));
        }
        return result;
    }
}
