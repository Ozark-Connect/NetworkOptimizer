using System.Text.Json.Nodes;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>How a UniFi device finds the UniFi Console after a re-address.</summary>
public enum InformRoute
{
    /// <summary>Its inform host is not in a moved subnet.</summary>
    NotAffected,

    /// <summary>The Console runs on the gateway (or a CloudKey) and provisions its own new address.</summary>
    FollowsAutomatically,

    /// <summary>A name the gateway's Local DNS serves, and the plan moves that record.</summary>
    FollowsByDns,

    /// <summary>The plan moves the Inform Host override of a UniFi OS Server to its new address.</summary>
    ConsoleRepointed,

    /// <summary>Anything else: Network Optimizer runs set-inform over SSH if it does not reconnect.</summary>
    SshFallback,
}

/// <summary>
/// One device's verdict, with the addresses the SSH fallback dials and the URL it sends. <c>ConsoleOnLink</c>:
/// the Console's new address is in the device's own network, so a host route reaches it without a gateway.
/// <c>SetInformIpUrl</c>: the same URL with the Console's new IP, for a device still on its old address, whose DNS is gone.
/// </summary>
public sealed record InformDevice(string Mac, string Name, DeviceType Role, InformRoute Route, string InformUrl,
    string SetInformUrl, string? NewIp, string? OldIp, bool ConsoleOnLink = false, string? SetInformIpUrl = null);

/// <summary>
/// The verdict for every device, and the Inform Host override write the plan adds (null for none).
/// <c>DeviceSsh</c>: Device SSH is configured, so the SSH fallback can run. <c>ConsoleFollows</c>: the Console runs on
/// the gateway (or a CloudKey) and moves with the plan.
/// </summary>
public sealed record InformVerdicts(List<InformDevice> Devices, string? OverrideIp, bool ConsoleRepointed, bool DeviceSsh = true, bool ConsoleFollows = false);

/// <summary>
/// Decides, without any API call, how each device reaches the UniFi Console once a re-address applies.
/// The caller reads stat/device, the Console type, and DNS; everything here is the config and those answers.
/// </summary>
[NetworkOptimizer.Core.VendorSpecific("UniFi", "stat/device inform_url / ip / type; device config_network; super_mgmt Inform Host override")]
public static class ReaddressInform
{
    /// <summary>Plan context key carrying the devices the SSH fallback may dial (JSON list of <see cref="InformDevice"/>).</summary>
    public const string Context = "readdress_inform";

    /// <param name="devices">stat/device rows.</param>
    /// <param name="resolved">IPv4 address of each inform host name, as Network Optimizer resolves it; absent or null when it does not.</param>
    /// <param name="standaloneConsole">The Console does not learn its own new address: UniFi OS Server (Firmware Rollout's detection),
    /// a self-hosted Network Application, or a Console whose type did not read.</param>
    public static InformVerdicts Classify(IEnumerable<JsonObject> devices, ConfigState live, ConfigState desired,
        IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> map, bool standaloneConsole,
        IReadOnlyDictionary<string, uint?> resolved)
    {
        var rows = devices.Where(d => d["mac"] != null && Uri.TryCreate(d["inform_url"]?.ToString(), UriKind.Absolute, out _)).ToList();
        var served = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Served(string host) => served.TryGetValue(host, out var s) ? s : served[host] = ChangeControlService.MovedByLocalDns(live, host, map);
        uint? Address(string host) => Readdressing.ParseIp(host) ?? (Served(host) ? null : resolved.GetValueOrDefault(host));

        // The Console's address: where most devices in a moved subnet inform to.
        var consoleOld = rows.Select(d => Address(new Uri(d["inform_url"]!.ToString()).Host))
            .OfType<uint>().Where(ip => Readdressing.MapIp(ip, map) != null)
            .GroupBy(ip => ip).OrderByDescending(g => g.Count()).Select(g => (uint?)g.Key).FirstOrDefault();
        // LAN networks only: a VPN server or WAN network's address is not where the gateway serves its LANs.
        var gatewayIps = live.Get("networkconf").Where(Readdressing.IsLan).Select(n => Readdressing.ParseIp(n["ip_subnet"]?.ToString()?.Split('/')[0]))
            .OfType<uint>().ToHashSet();
        // A Console at a gateway LAN address is the gateway: it follows by itself, whatever the type read said.
        var onGateway = consoleOld is { } g0 && gatewayIps.Contains(g0);
        var (overrideIp, repointed) = standaloneConsole && consoleOld is { } at && !onGateway ? Override(live, map, at) : (null, false);
        var liveDevices = ByMac(live);
        var desiredDevices = ByMac(desired);
        var result = new List<InformDevice>();
        foreach (var d in rows)
        {
            var mac = d["mac"]!.ToString().ToLowerInvariant();
            var url = d["inform_url"]!.ToString();
            var host = new Uri(url).Host;
            var isName = Readdressing.ParseIp(host) == null;
            var role = DeviceTypeExtensions.FromUniFiApiType(d["type"]?.ToString(), d["model"]?.ToString());
            var ip = Address(host);
            var moved = (isName && Served(host)) || (ip is { } a && Readdressing.MapIp(a, map) != null);
            // An unresolved name may be moved: only the Console type or an answer can clear it.
            var route = !moved && ip != null ? InformRoute.NotAffected
                : !standaloneConsole || (ip is { } g && gatewayIps.Contains(g)) ? InformRoute.FollowsAutomatically
                : moved && repointed ? InformRoute.ConsoleRepointed
                : isName && Served(host) && ResolvesThroughGateway(live, liveDevices.GetValueOrDefault(mac), d, role, gatewayIps) ? InformRoute.FollowsByDns
                : InformRoute.SshFallback;
            var newIp = StaticIp(desiredDevices.GetValueOrDefault(mac));
            var oldIp = d["ip"]?.ToString() is { Length: > 0 } now ? now : StaticIp(liveDevices.GetValueOrDefault(mac));
            var after = InformUrlAfter(url, map, isName && Served(host), ip);
            var ipUrl = InformUrlAfter(url, map, servedName: false, ip ?? (isName ? ChangeControlService.LocalDnsMovedAddress(live, host, map) : null));
            // The new static is already in the new space; only the old address maps (a swap would map it twice).
            var deviceAt = Readdressing.ParseIp(newIp) ?? (Readdressing.ParseIp(oldIp) is { } o ? Readdressing.MapIp(o, map) ?? o : null);
            result.Add(new InformDevice(mac, d["name"]?.ToString() is { Length: > 0 } n ? n : mac, role, route, url,
                after, newIp, oldIp, OnLink(desired, deviceAt, ipUrl), ipUrl == url ? null : ipUrl));
        }
        return new InformVerdicts(result, overrideIp, repointed, ConsoleFollows: !standaloneConsole || onGateway);
    }

    /// <summary>
    /// The Inform Host override for a UniFi OS Server at <paramref name="consoleOld"/>: on with an IP, the
    /// rewrite already moves it; on with a name, it is left alone; off, the plan turns it on at the new IP.
    /// </summary>
    private static (string? Write, bool Repointed) Override(ConfigState live, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> map, uint consoleOld)
    {
        if (SuperMgmt(live) is not { } mgmt || Readdressing.MapIp(consoleOld, map) is not { } next) return (null, false);
        if (!Readdressing.IsTrue(mgmt["override_inform_host"])) return (Readdressing.Format(next), true);
        return Readdressing.ParseIp(mgmt["override_inform_host_location"]?.ToString()) is { } at
            ? (null, Readdressing.MapIp(at, map) != null)
            : (null, false);
    }

    /// <summary>The site's super_mgmt setting, which holds the Inform Host override.</summary>
    internal static JsonObject? SuperMgmt(ConfigState state) =>
        state.Get("setting").FirstOrDefault(s => s["key"]?.ToString() == "super_mgmt");

    /// <summary>
    /// The URL to set-inform after the move: an IP host is mapped; a name the gateway serves stays a name;
    /// a name that resolves into a moved subnet becomes its new address, since the device may not resolve it.
    /// </summary>
    public static string InformUrlAfter(string informUrl, IReadOnlyList<(Readdressing.Subnet Old, Readdressing.Subnet New)> map,
        bool servedName, uint? resolved)
    {
        if (!Uri.TryCreate(informUrl, UriKind.Absolute, out var uri)) return informUrl;
        var ip = Readdressing.ParseIp(uri.Host)
            ?? (!servedName && resolved is { } r && Readdressing.MapIp(r, map) != null ? r : null);
        if (ip is not { } at) return informUrl;
        return new UriBuilder(uri) { Host = Readdressing.Format(Readdressing.MapIp(at, map) ?? at) }.Uri.ToString();
    }

    /// <summary>
    /// The set-inform a person types in the device's shell. <paramref name="atOldAddress"/>: the device never moved, so
    /// its DNS server is gone and the URL carries the Console's new IP.
    /// </summary>
    public static string Command(InformDevice device, bool atOldAddress = false) => $"set-inform {Url(device, atOldAddress)}";

    /// <summary>
    /// The same over a non-interactive SSH command: set-inform is only an /etc/profile alias for mca-cli-op set-inform,
    /// which such a command never loads (exit 127), so the target is called directly, as Firmware Rollout's upgrade is.
    /// </summary>
    public static string SshCommand(InformDevice device, bool atOldAddress = false)
    {
        var url = Url(device, atOldAddress);
        return $"if command -v mca-cli-op >/dev/null 2>&1; then mca-cli-op set-inform '{url}'; else set-inform '{url}'; fi";
    }

    private static string Url(InformDevice device, bool atOldAddress) =>
        atOldAddress ? device.SetInformIpUrl ?? device.SetInformUrl : device.SetInformUrl;

    /// <summary>
    /// The command a person runs by hand. A device left on its old address has no route to the Console's new one;
    /// on the same network, a host route on its own interface reaches it.
    /// </summary>
    public static string HandCommand(InformDevice device) =>
        device.ConsoleOnLink && Uri.TryCreate(device.SetInformIpUrl ?? device.SetInformUrl, UriKind.Absolute, out var uri)
            ? $"ip route add {uri.Host}/32 dev <if>; {Command(device, atOldAddress: true)}"
            : Command(device);

    /// <summary>The URL's host is an IP in the same new subnet as <paramref name="deviceAt"/>, the device's address after the move.</summary>
    private static bool OnLink(ConfigState desired, uint? deviceAt, string url)
    {
        if (deviceAt is not { } at || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || Readdressing.ParseIp(uri.Host) is not { } console) return false;
        return desired.Get("networkconf").Any(n => Readdressing.ParseSubnet(n["ip_subnet"]?.ToString()) is { } s && s.Contains(at) && s.Contains(console));
    }

    /// <summary>
    /// The device asks the gateway for names: the gateway itself; a static address whose DNS is a gateway
    /// address; or DHCP on a network that hands out its gateway. A DNS DNAT on the network wins over both.
    /// </summary>
    private static bool ResolvesThroughGateway(ConfigState live, JsonObject? config, JsonObject stat, DeviceType role, IReadOnlySet<uint> gatewayIps)
    {
        if (role == DeviceType.Gateway) return true;
        var cn = config?["config_network"] as JsonObject;
        var at = Readdressing.ParseIp(cn?["type"]?.ToString() == "static" ? cn["ip"]?.ToString() : stat["ip"]?.ToString());
        var network = at is { } ip ? live.Get("networkconf").FirstOrDefault(n => Readdressing.ParseSubnet(n["ip_subnet"]?.ToString())?.Contains(ip) == true) : null;
        if (network == null) return false;
        var gateway = network["ip_subnet"]?.ToString()?.Split('/')[0];
        if (cn?["type"]?.ToString() == "static" && cn["dns1"]?.ToString() is { Length: > 0 } dns
            && !live.Get("nat").Any(r => r["in_interface"]?.ToString() == ConfigState.IdOf(network) && LockoutChecker.RedirectsDns(live, r)))
            return Readdressing.ParseIp(dns) is { } d && gatewayIps.Contains(d);
        return LockoutChecker.Resolvers(live, network).FirstOrDefault() == gateway;
    }

    private static Dictionary<string, JsonObject> ByMac(ConfigState state) =>
        state.Get("device").Where(d => d["mac"] != null)
            .GroupBy(d => d["mac"]!.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static string? StaticIp(JsonObject? device) =>
        device?["config_network"] is JsonObject cn && cn["type"]?.ToString() == "static" && Readdressing.ParseIp(cn["ip"]?.ToString()) != null
            ? cn["ip"]!.ToString() : null;
}
