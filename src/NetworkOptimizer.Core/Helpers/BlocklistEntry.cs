using System.Net;
using System.Net.Sockets;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>Validation for addresses and ranges a gateway is asked to block.</summary>
public static class BlocklistEntry
{
    /// <summary>
    /// The entry as an ipset member (network address, host ranges without a prefix), or null when it
    /// is not a public address or range. Ranges wider than /8 (IPv4) or /16 (IPv6) are refused: a block
    /// list entry is never that wide, and one that is would cut off a large part of the Internet.
    /// </summary>
    public static (string Text, bool IsV6)? Canonical(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        var parts = entry.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        var max = v6 ? 128 : 32;
        var prefix = max;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max)) return null;
        if (prefix < (v6 ? 16 : 8)) return null;
        var network = Mask(ip, prefix);
        if (!NetworkUtilities.IsPublicIpAddress(network)) return null;
        return (prefix == max ? network.ToString() : $"{network}/{prefix}", v6);
    }

    /// <summary>
    /// <paramref name="range"/> without <paramref name="holes"/>: the fewest prefixes that cover the rest
    /// of it, at most one per bit of host part per hole. Holes outside the range are ignored, so a range
    /// with none inside comes back whole.
    /// </summary>
    public static List<IPNetwork> CutOut(IPNetwork range, IEnumerable<IPAddress> holes)
    {
        var family = range.BaseAddress.AddressFamily;
        var bits = family == AddressFamily.InterNetworkV6 ? 128 : 32;
        var inside = holes
            .Select(h => h.IsIPv4MappedToIPv6 ? h.MapToIPv4() : h)
            .Where(h => h.AddressFamily == family && range.Contains(h))
            .Select(ToNumber)
            .Distinct()
            .ToList();
        var pieces = new List<IPNetwork>();
        Split(ToNumber(range.BaseAddress), range.PrefixLength, inside);
        return pieces;

        void Split(UInt128 network, int prefix, List<UInt128> within)
        {
            if (within.Count == 0)
            {
                pieces.Add(new IPNetwork(ToAddress(network, bits), prefix));
                return;
            }
            if (prefix == bits) return;
            var upper = UInt128.One << (bits - prefix - 1);
            Split(network, prefix + 1, within.Where(h => (h & upper) == 0).ToList());
            Split(network | upper, prefix + 1, within.Where(h => (h & upper) != 0).ToList());
        }
    }

    private static UInt128 ToNumber(IPAddress ip)
    {
        UInt128 n = 0;
        foreach (var b in ip.GetAddressBytes()) n = (n << 8) | b;
        return n;
    }

    private static IPAddress ToAddress(UInt128 n, int bits)
    {
        var bytes = new byte[bits / 8];
        for (var i = bytes.Length - 1; i >= 0; i--, n >>= 8) bytes[i] = (byte)(n & 0xFF);
        return new IPAddress(bytes);
    }

    private static IPAddress Mask(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }
        return new IPAddress(bytes);
    }
}
