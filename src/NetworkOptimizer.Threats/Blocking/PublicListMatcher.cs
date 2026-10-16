using System.Net;
using System.Net.Sockets;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// Which of a site's public block lists holds an address. Built from the lists' entries (IPs and CIDR
/// ranges) and looked up once per prefix length present, so a lookup costs a few hash probes however
/// long the lists are.
/// </summary>
public sealed class PublicListMatcher
{
    public static readonly PublicListMatcher Empty = new([]);

    // Keyed by family and prefix length; each holds the masked network and the list it came from.
    private readonly Dictionary<(bool V6, int Prefix), Dictionary<string, PublicBlockList>> _byPrefix = new();
    private readonly List<(bool V6, int Prefix)> _order;

    /// <param name="entries">Each normalized entry (see <see cref="PublicBlockLists.Normalize"/>) with its list.</param>
    public PublicListMatcher(IEnumerable<(string Entry, PublicBlockList List)> entries)
    {
        foreach (var (entry, list) in entries)
        {
            var slash = entry.IndexOf('/');
            if (!IPAddress.TryParse(slash < 0 ? entry : entry[..slash], out var ip)) continue;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
            var prefix = slash < 0 ? (v6 ? 128 : 32) : int.Parse(entry[(slash + 1)..]);
            var key = (v6, prefix);
            if (!_byPrefix.TryGetValue(key, out var networks))
                _byPrefix[key] = networks = new Dictionary<string, PublicBlockList>(StringComparer.Ordinal);
            networks.TryAdd(Masked(ip, prefix), list);
        }
        // Most specific first, so an address in two lists reports the narrower entry's list.
        _order = _byPrefix.Keys.OrderByDescending(k => k.Prefix).ToList();
        Count = _byPrefix.Values.Sum(n => n.Count);
    }

    public int Count { get; }

    /// <summary>The list holding <paramref name="address"/>, or null when none does.</summary>
    public PublicBlockList? Find(string? address)
    {
        if (Count == 0 || !IPAddress.TryParse(address, out var ip)) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        foreach (var key in _order)
        {
            if (key.V6 != v6) continue;
            if (_byPrefix[key].TryGetValue(Masked(ip, key.Prefix), out var list)) return list;
        }
        return null;
    }

    private static string Masked(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] &= (byte)(0xFF << (8 - Math.Clamp(prefix - i * 8, 0, 8)));
        return Convert.ToHexString(bytes);
    }
}

/// <summary>The public block lists each site has enabled, for flagging devices that reach them.</summary>
public interface IPublicListLookup
{
    /// <summary>The site's enabled lists as of the last blocking cycle (null site = default site).</summary>
    PublicListMatcher For(string? site);
}
