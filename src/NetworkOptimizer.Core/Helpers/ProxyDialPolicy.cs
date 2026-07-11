using System.Net;
using System.Net.Sockets;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Address-scope policy for the on-site agent's tunnel TCP proxy: decides whether a
/// proxy dial target is inside the allowed scope. The default policy admits only
/// site-local addresses (RFC1918, IPv6 unique-local, IPv6 link-local) and is
/// hardcoded so a compromised central server cannot widen it over the tunnel - the
/// tunnel carries no message that reaches this policy. An operator can replace the
/// default with an explicit CIDR/host list via agent.json ("proxyAllowedCidrs"),
/// which is both the narrowing knob (pin to a management VLAN) and the only escape
/// hatch for an exotic public-IP target.
/// </summary>
public sealed class ProxyDialPolicy
{
    private readonly IReadOnlyList<string>? _pinnedCidrs;

    private ProxyDialPolicy(IReadOnlyList<string>? pinnedCidrs)
    {
        _pinnedCidrs = pinnedCidrs;
    }

    /// <summary>The built-in default: RFC1918, IPv6 unique-local, and IPv6 link-local only.</summary>
    public static ProxyDialPolicy SiteLocal { get; } = new(null);

    /// <summary>Whether this is the built-in site-local default (no operator pin).</summary>
    public bool IsDefault => _pinnedCidrs == null;

    /// <summary>Number of operator-pinned entries; 0 for the default policy.</summary>
    public int PinnedCount => _pinnedCidrs?.Count ?? 0;

    /// <summary>
    /// Build an operator-pinned policy from CIDR entries (bare IPs are treated as
    /// /32 or /128). The pin fully replaces the site-local default. Returns null
    /// with an error message when any entry is invalid or the list is empty -
    /// callers must fail loudly rather than run with a partial or ambiguous pin.
    /// </summary>
    public static ProxyDialPolicy? FromPinnedCidrs(IEnumerable<string?> entries, out string? error)
    {
        var normalized = new List<string>();
        foreach (var raw in entries)
        {
            var entry = raw?.Trim();
            if (string.IsNullOrEmpty(entry))
                continue;

            if (entry.Contains('/'))
            {
                var (network, prefixLength) = NetworkUtilities.ParseCidr(entry);
                var maxPrefix = network?.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                if (network == null || prefixLength < 0 || prefixLength > maxPrefix)
                {
                    error = $"invalid CIDR '{entry}'";
                    return null;
                }
                normalized.Add(entry);
            }
            else if (IPAddress.TryParse(entry, out var ip))
            {
                normalized.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"{entry}/128" : $"{entry}/32");
            }
            else
            {
                error = $"invalid entry '{entry}' (expected an IP or CIDR)";
                return null;
            }
        }

        if (normalized.Count == 0)
        {
            error = "the list is empty";
            return null;
        }

        error = null;
        return new ProxyDialPolicy(normalized);
    }

    /// <summary>
    /// Whether the policy allows dialing the given address. IPv4-mapped IPv6
    /// addresses are unwrapped before evaluation so a public IPv4 target cannot
    /// slip through as ::ffff:a.b.c.d.
    /// </summary>
    public bool IsAllowed(IPAddress address)
    {
        var ip = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (_pinnedCidrs != null)
            return _pinnedCidrs.Any(cidr => NetworkUtilities.IsIpInSubnet(ip, cidr));

        return NetworkUtilities.IsRfc1918(ip)
            || NetworkUtilities.IsIPv6UniqueLocal(ip)
            || ip.IsIPv6LinkLocal;
    }
}
