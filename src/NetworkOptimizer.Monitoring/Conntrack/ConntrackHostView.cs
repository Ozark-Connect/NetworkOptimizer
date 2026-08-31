using System.Net;
using System.Net.Sockets;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>
/// The gateway's network identity as the conntrack classifier needs it: the host's own
/// addresses (with the interface holding each), its directly connected subnets, and the
/// neighbor table's ip-to-mac map. Built by the runner from live interfaces and refreshed
/// on its own cadence; fixtures construct it by hand.
/// </summary>
public sealed class ConntrackHostView
{
    private readonly Dictionary<IPAddress, string> _hostAddresses = new();
    private readonly List<(IPAddress Network, int PrefixLength, AddressFamily Family)> _subnets = new();
    private readonly Dictionary<IPAddress, string> _neighbors = new();

    public void AddHostAddress(IPAddress address, string interfaceName) =>
        _hostAddresses[address] = interfaceName;

    public void AddConnectedSubnet(IPAddress network, int prefixLength) =>
        _subnets.Add((network, prefixLength, network.AddressFamily));

    public void AddNeighbor(IPAddress address, string mac)
    {
        if (!string.IsNullOrEmpty(mac) && mac != "00:00:00:00:00:00")
            _neighbors[address] = mac.ToLowerInvariant();
    }

    /// <summary>Whether the address is one of the gateway's own, and which interface holds it.</summary>
    public bool IsHostAddress(IPAddress address, out string interfaceName)
    {
        if (_hostAddresses.TryGetValue(address, out var found))
        {
            interfaceName = found;
            return true;
        }
        interfaceName = "";
        return false;
    }

    /// <summary>Whether the address sits in a subnet the gateway is directly connected to.</summary>
    public bool IsInConnectedSubnet(IPAddress address)
    {
        foreach (var (network, prefix, family) in _subnets)
        {
            if (address.AddressFamily != family) continue;
            if (InSubnet(address, network, prefix)) return true;
        }
        return false;
    }

    /// <summary>The neighbor table's MAC for a LAN address, or null when it holds none.</summary>
    public string? MacFor(IPAddress address) =>
        _neighbors.TryGetValue(address, out var mac) ? mac : null;

    private static bool InSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var addr = address.GetAddressBytes();
        var net = network.GetAddressBytes();
        if (addr.Length != net.Length) return false;
        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
            if (addr[i] != net[i]) return false;
        var remaining = prefixLength % 8;
        if (remaining == 0 || fullBytes >= addr.Length) return true;
        var mask = (byte)(0xFF << (8 - remaining));
        return (addr[fullBytes] & mask) == (net[fullBytes] & mask);
    }
}
