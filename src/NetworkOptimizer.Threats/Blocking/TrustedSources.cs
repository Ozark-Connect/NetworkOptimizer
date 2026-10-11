using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>SystemSettings keys for Trusted Sources, per site (see <see cref="BlockingSettingKeys.ForSite"/>).</summary>
public static class TrustedSourcesKeys
{
    /// <summary>JSON list of <see cref="TrustedAddress"/>: the addresses a site has been seen at.</summary>
    public const string Addresses = "trusted.addresses";

    /// <summary>JSON array of the site keys whose addresses this site trusts. Unset = its sharing group.</summary>
    public const string Sites = "trusted.sites";

    /// <summary>"true" to keep the Trusted Sources address groups in this site's UniFi console.</summary>
    public const string UniFiGroup = "trusted.unifi";

    /// <summary>"true" to add Cloudflare's ranges to the UniFi groups (never to the WAF list).</summary>
    public const string UniFiCloudflare = "trusted.unifi.cloudflare";

    /// <summary>JSON <see cref="TrustedSourcesGroupState"/>.</summary>
    public const string UniFiGroupState = "trusted.unifi.state";

    /// <summary>The WAF allow list the members are pushed as; routes opt in with /check?allow=trusted.</summary>
    public const string WafListName = "trusted";
}

/// <summary>
/// One address a site has been seen at: a public WAN IPv4 or IPv6 address, or a LAN IPv6 prefix
/// (IPv6 has no NAT, so a site's traffic leaves from its LAN prefixes).
/// </summary>
public sealed record TrustedAddress
{
    public required string Address { get; init; }
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; init; }

    /// <summary>When the site was last read without this address. Cleared when it comes back.</summary>
    public DateTime? ReplacedAt { get; init; }
}

/// <summary>
/// The addresses each site has been seen at, kept so a site that goes down stays trusted. Only a
/// successful read of a site changes its list: an unreachable site keeps its addresses however long
/// it is gone. An address the site no longer reports stays for <see cref="ReplacedRetention"/>, so a
/// changed WAN address keeps working while anything pinned to it catches up.
/// </summary>
public static class TrustedAddressBook
{
    public static readonly TimeSpan ReplacedRetention = TimeSpan.FromDays(7);

    /// <summary>
    /// Folds one successful read into a site's list. Returns the new list; null only when the read
    /// was empty, which changes nothing. Every read refreshes LastSeen on the addresses it saw.
    /// </summary>
    public static List<TrustedAddress>? Observe(IReadOnlyList<TrustedAddress> stored, IReadOnlyCollection<string> current, DateTime now)
    {
        if (current.Count == 0) return null;
        var seen = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        var result = new List<TrustedAddress>();
        foreach (var a in stored)
        {
            if (seen.Remove(a.Address))
            {
                changed = true;
                result.Add(a with { LastSeen = now, ReplacedAt = null });
            }
            else if (a.ReplacedAt is { } replaced && now - replaced > ReplacedRetention)
            {
                changed = true;
            }
            else
            {
                changed |= a.ReplacedAt == null;
                result.Add(a with { ReplacedAt = a.ReplacedAt ?? now });
            }
        }
        foreach (var address in seen)
        {
            changed = true;
            result.Add(new TrustedAddress { Address = address, FirstSeen = now, LastSeen = now });
        }
        return changed ? result.OrderBy(a => a.Address, StringComparer.OrdinalIgnoreCase).ToList() : null;
    }

    public static async Task<List<TrustedAddress>> LoadAsync(IThreatSettingsAccessor settings, string? site, CancellationToken ct)
    {
        var json = await settings.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.Addresses, site), ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<TrustedAddress>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static Task SaveAsync(IThreatSettingsAccessor settings, string? site, IReadOnlyList<TrustedAddress> list, CancellationToken ct) =>
        settings.SaveSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.Addresses, site), JsonSerializer.Serialize(list), ct);

    /// <summary>
    /// A site's current public addresses from its gateways, or null when they could not be read
    /// (no connection, a failed request, or no gateway in the response).
    /// </summary>
    public static async Task<IReadOnlyList<string>?> ReadSiteAsync(IUniFiClientAccessor clients, string? site, CancellationToken ct)
    {
        var client = clients.GetClient(site);
        if (client == null) return null;
        var gateways = (await client.GetDevicesAsync(ct)).Where(d => d.DeviceType.IsGateway()).ToList();
        if (gateways.Count == 0) return null;
        var addresses = FromGateways(gateways);
        return addresses.Count == 0 ? null : addresses;
    }

    /// <summary>
    /// Every WAN's public IPv4 and global IPv6 address (up or not: a standby WAN still has one),
    /// the gateway's own address, and each LAN's global IPv6 prefix.
    /// </summary>
    public static List<string> FromGateways(IEnumerable<UniFiDeviceResponse> gateways)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in gateways)
        {
            var wans = g.GetWanInterfaces();
            foreach (var ip in wans.Select(w => w.Ip).Append(g.Ip))
            {
                if (PublicAddress(ip) is { } a) result.Add(a);
            }
            foreach (var ip in wans.SelectMany(w => w.Ipv6 ?? []))
            {
                if (PublicAddress(ip) is { } a) result.Add(a);
            }
            foreach (var subnet in (g.NetworkTable ?? []).SelectMany(n => (n.Ipv6Subnets ?? []).Concat(n.Ipv6Subnet ?? [])))
            {
                if (PublicBlockLists.Normalize(subnet) is { } prefix && prefix.Contains('/')
                    && IPNetwork.TryParse(prefix, out var n) && IsGlobalV6(n.BaseAddress))
                    result.Add(prefix);
            }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // The sample responses carry WAN IPv6 without a length, but accept one.
    private static string? PublicAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var ip = value.Split('/')[0].Trim();
        if (!IPAddress.TryParse(ip, out var addr)) return null;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        if (addr.AddressFamily == AddressFamily.InterNetworkV6 ? !IsGlobalV6(addr) : !NetworkUtilities.IsPublicIpAddress(addr))
            return null;
        return addr.ToString();
    }

    private static readonly IPNetwork GlobalUnicast = IPNetwork.Parse("2000::/3");

    // Global unicast only: ULA (fc00::/7), link-local, and multicast are local to the site.
    private static bool IsGlobalV6(IPAddress addr) => GlobalUnicast.Contains(addr);
}

/// <summary>Which sites a site trusts, and its own extra addresses.</summary>
public static class TrustedSourcesSettings
{
    /// <summary>The site keys this site trusts (null = default site), or null when it has not chosen.</summary>
    public static async Task<List<string?>?> LoadSitesAsync(IThreatSettingsAccessor settings, string? site, CancellationToken ct)
    {
        var json = await settings.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, site), ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Task SaveSitesAsync(IThreatSettingsAccessor settings, string? site, IEnumerable<string?> sites, CancellationToken ct) =>
        settings.SaveSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.Sites, site), JsonSerializer.Serialize(sites.Distinct().ToList()), ct);

    /// <summary>
    /// The sites a site trusts: its choice, or else its sharing group. Always itself: a site never
    /// blocks its own addresses.
    /// </summary>
    public static async Task<List<string?>> ResolveSitesAsync(IThreatSettingsAccessor settings, IBlockSharingScope? sharing,
        string? site, CancellationToken ct)
    {
        var chosen = await LoadSitesAsync(settings, site, ct)
            ?? (sharing == null ? [site] : [.. await sharing.GetPeerSitesAsync(site, ct)]);
        return chosen.Contains(site) ? chosen : [site, .. chosen];
    }

    /// <summary>Whether a site's UniFi groups also hold Cloudflare's ranges, for port forwards to proxied hosts.</summary>
    public static async Task<bool> UniFiIncludesCloudflareAsync(IThreatSettingsAccessor settings, string? site, CancellationToken ct) =>
        bool.TryParse(await settings.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.UniFiCloudflare, site), ct), out var on) && on;

    /// <summary>Whether a site keeps the Trusted Sources groups in its UniFi console.</summary>
    public static async Task<bool> UniFiGroupEnabledAsync(IThreatSettingsAccessor settings, string? site, CancellationToken ct) =>
        bool.TryParse(await settings.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.UniFiGroup, site), ct), out var on) && on;
}
