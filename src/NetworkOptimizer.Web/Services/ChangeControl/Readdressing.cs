using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One LAN network's move: its old subnet and the new one (network address and prefix).</summary>
public sealed record SubnetMove(string NetworkId, string NetworkName, int? Vlan, string OldCidr, string NewCidr);

/// <summary>An address the move rewrites: where, and from what to what.</summary>
public sealed record AddressRewrite(string Collection, string ObjectName, string Path, string Old, string New);

/// <summary>An old address inside a value that is not one address, CIDR, or range (free text, a URL); left alone.</summary>
public sealed record AddressMention(string Collection, string ObjectName, string Path, string Value);

/// <summary>The rewritten config, what moved, and what needs a person.</summary>
public sealed class ReaddressResult
{
    public ConfigState Desired { get; init; } = new();
    public List<AddressRewrite> Rewrites { get; } = [];
    public List<AddressMention> Mentions { get; } = [];
    /// <summary>Addresses inside a moved subnet that belong to a WAN or a VPN's far side, so they stay.</summary>
    public List<AddressMention> Remote { get; } = [];
    public HashSet<string> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Re-address Networks: maps old IPv4 subnets to new ones and rewrites every address in them, keeping
/// each host's offset. Generic on purpose: it walks every string in the config, so a field nobody
/// listed is still covered. A value that holds an old address but is not one (free text, a URL) is
/// reported, never rewritten.
/// </summary>
public static partial class Readdressing
{
    /// <summary>Network purposes that carry a LAN subnet.</summary>
    private static readonly HashSet<string> LanPurposes = new(StringComparer.Ordinal) { "corporate", "guest", "vlan-only" };

    /// <summary>One parsed subnet: network address and prefix length.</summary>
    public readonly record struct Subnet(uint Network, int Prefix)
    {
        public uint Mask => Prefix == 0 ? 0 : uint.MaxValue << (32 - Prefix);
        public bool Contains(uint address) => (address & Mask) == Network;
        public override string ToString() => $"{Format(Network)}/{Prefix}";
    }

    /// <summary>Parses "a.b.c.d/n" (host bits allowed, as UniFi's ip_subnet carries the gateway) into its subnet.</summary>
    public static Subnet? ParseSubnet(string? cidr)
    {
        if (cidr == null) return null;
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2 || ParseIp(parts[0]) is not { } ip
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 8 or > 30) return null;
        var mask = uint.MaxValue << (32 - prefix);
        return new Subnet(ip & mask, prefix);
    }

    /// <summary>Parses any CIDR (/1 to /32) or a bare address (as a /32), for stored remote ranges.</summary>
    public static Subnet? ParseRange(string? text)
    {
        if (text == null) return null;
        if (ParseIp(text.Trim()) is { } ip) return new Subnet(ip, 32);
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || ParseIp(parts[0]) is not { } a
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix is < 1 or > 32) return null;
        return new Subnet(a & (uint.MaxValue << (32 - prefix)), prefix);
    }

    public static uint? ParseIp(string? text)
    {
        if (text == null || !IpShape().IsMatch(text) || !IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static string Format(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

    /// <summary>A UniFi flag: a JSON bool, or the string "true" some consoles store (a default LAN's <c>dhcpd_enabled</c>).</summary>
    public static bool IsTrue(JsonNode? value) =>
        value is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) && p);

    /// <summary>The site's LAN networks with a subnet, each proposed a new one from <paramref name="pattern"/> when given.</summary>
    public static List<SubnetMove> Proposal(ConfigState live, string? pattern)
    {
        var moves = new List<SubnetMove>();
        foreach (var n in live.Get("networkconf"))
        {
            if (!LanPurposes.Contains(n["purpose"]?.ToString() ?? "") || ParseSubnet(n["ip_subnet"]?.ToString()) is not { } old) continue;
            var vlan = IsTrue(n["vlan_enabled"]) && int.TryParse(n["vlan"]?.ToString(), out var v) ? v : (int?)null;
            var proposed = string.IsNullOrWhiteSpace(pattern) ? old.ToString() : ApplyPattern(pattern, vlan, old) ?? "";
            moves.Add(new SubnetMove(ConfigState.IdOf(n), n["name"]?.ToString() ?? "", vlan, old.ToString(), proposed));
        }
        return moves.OrderBy(m => m.Vlan ?? 1).ThenBy(m => m.NetworkName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A pattern such as <c>10.42.{vlan}.0/24</c>: <c>{vlan}</c> is the VLAN ID (1 untagged), <c>{octet3}</c>
    /// the old subnet's third octet. Null when the result is not a valid subnet.
    /// </summary>
    public static string? ApplyPattern(string pattern, int? vlan, Subnet old)
    {
        var v = (vlan ?? 1).ToString(CultureInfo.InvariantCulture);
        var text = pattern.Trim()
            .Replace("{vlan}", v, StringComparison.OrdinalIgnoreCase)
            .Replace("{octet3}", ((old.Network >> 8) & 255).ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        // Shorthand people type: "x" or "*" is the VLAN in the third octet and 0 in the fourth
        // (10.42.x.x, 10.42.*.0), and no prefix keeps the network's own.
        var slash = text.Split('/');
        var octets = slash[0].Split('.');
        if (octets.Length == 4)
        {
            for (var i = 0; i < 4; i++)
                if (octets[i].Trim() is "x" or "X" or "*")
                    octets[i] = i switch { 2 => v, 3 => "0", _ => ((old.Network >> (24 - 8 * i)) & 255).ToString(CultureInfo.InvariantCulture) };
            text = string.Join('.', octets) + "/" + (slash.Length == 2 ? slash[1] : old.Prefix.ToString(CultureInfo.InvariantCulture));
        }
        return ParseSubnet(text)?.ToString();
    }

    /// <summary>Problems that stop the move: bad or shrinking subnets, overlaps, and clashes with what stays.</summary>
    public static List<string> Validate(ConfigState live, IReadOnlyList<SubnetMove> moves)
    {
        var errors = new List<string>();
        var active = moves.Where(m => m.OldCidr != m.NewCidr).ToList();
        var parsed = new List<(SubnetMove Move, Subnet New)>();
        foreach (var m in active)
        {
            if (ParseSubnet(m.NewCidr) is not { } n) { errors.Add($"{m.NetworkName}: {m.NewCidr} is not a subnet (a.b.c.d/n)."); continue; }
            if (ParseSubnet(m.OldCidr) is { } o && n.Prefix > o.Prefix)
                errors.Add($"{m.NetworkName}: {n} is smaller than {o}. Keep the prefix or make it larger, so every address fits.");
            parsed.Add((m, n));
        }
        for (var i = 0; i < parsed.Count; i++)
            for (var j = i + 1; j < parsed.Count; j++)
                if (Overlaps(parsed[i].New, parsed[j].New))
                    errors.Add($"{parsed[i].Move.NetworkName} and {parsed[j].Move.NetworkName} would overlap ({parsed[i].New}, {parsed[j].New}).");

        // What keeps its address: unmoved LANs, WANs, VPN and site-to-site subnets.
        var moving = active.Select(m => m.NetworkId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in live.Get("networkconf").Where(n => !moving.Contains(ConfigState.IdOf(n))))
            foreach (var cidr in StaticSubnets(n))
                if (ParseSubnet(cidr) is { } kept)
                    foreach (var (m, s) in parsed.Where(p => Overlaps(p.New, kept)))
                        errors.Add($"{m.NetworkName}: {s} overlaps {n["name"]} ({kept}).");
        // And what is reached over a WAN or a VPN: a new LAN there would shadow it.
        foreach (var (range, where) in RemoteRanges(live))
            foreach (var (m, s) in parsed.Where(p => Overlaps(p.New, range)))
                errors.Add($"{m.NetworkName}: {s} overlaps {range}, reached over {where}.");
        return errors.Distinct().ToList();
    }

    /// <summary>Network purposes that are not this site's LANs: their addresses are set by the ISP or the VPN's far side.</summary>
    private static bool IsLan(JsonObject n) => LanPurposes.Contains(n["purpose"]?.ToString() ?? "");

    private static bool IsWan(JsonObject n) => n["purpose"]?.ToString() == "wan";

    /// <summary>
    /// A WAN's carrier-side fields (its address, gateway, netmask, DNS, aliases): the ISP sets them. The
    /// rest of a WAN can be local, such as a GRE tunnel's <c>ip_subnet</c>, which is the modem's LAN address.
    /// </summary>
    private static bool IsCarrierField(string key) => key.StartsWith("wan_", StringComparison.Ordinal);

    /// <summary>
    /// Ranges that belong to something other than this site's LANs: WAN and VPN client networks (their
    /// own addresses and peers), and the destinations of static routes, traffic routes, and firewall
    /// policies that leave over a WAN or a VPN. An address inside one is never rewritten, even when it
    /// also falls in a moved LAN subnet (a WAN's static /30 inside a LAN range, a VPN far side reusing it).
    /// </summary>
    public static List<(Subnet Range, string Where)> RemoteRanges(ConfigState live)
    {
        var ranges = new List<(Subnet, string)>();
        var networks = live.Get("networkconf").ToDictionary(ConfigState.IdOf, n => n, StringComparer.OrdinalIgnoreCase);
        string? Remote(string? networkId) => networkId != null && networks.TryGetValue(networkId, out var n) && !IsLan(n) ? n["name"]?.ToString() ?? networkId : null;
        void Add(JsonNode? node, string where)
        {
            foreach (var text in Strings(node))
                foreach (var r in Ranges(text)) ranges.Add((r, where));
        }

        foreach (var n in networks.Values.Where(n => !IsLan(n)))
        {
            if (!IsWan(n)) { Add(n, n["name"]?.ToString() ?? ""); continue; }
            foreach (var (k, v) in n.Where(p => IsCarrierField(p.Key))) Add(v, n["name"]?.ToString() ?? "");
        }
        foreach (var v in live.Get("vpn_connections")) Add(v, Remote(v["network_id"]?.ToString()) ?? "a VPN");
        foreach (var r in live.Get("routing"))
            if (Remote(r["static-route_interface"]?.ToString()) is { } via) Add(r["static-route_network"], via);
        foreach (var t in live.Get("trafficroute"))
            if (Remote(t["network_id"]?.ToString()) is { } via) { Add(t["ip_addresses"], via); Add(t["ip_ranges"], via); }
        var remoteZones = RemoteZones(live, networks);
        foreach (var p in live.Get("firewall_policy"))
            foreach (var end in new[] { "source", "destination" })
                if (p[end] is JsonObject e && e["zone_id"]?.ToString() is { } z && remoteZones.TryGetValue(z, out var zoneName))
                    Add(e["ips"], $"the {zoneName} zone");
        return ranges;
    }

    /// <summary>Zones that hold a WAN or VPN network (External, VPN), by ID with their name.</summary>
    private static Dictionary<string, string> RemoteZones(ConfigState live, Dictionary<string, JsonObject> networks) =>
        live.Get("firewall_zone")
            .Where(z => z["zone_key"]?.ToString() is "external" or "vpn"
                || (z["network_ids"] is JsonArray ids && ids.Any(i => i?.ToString() is { } id && networks.TryGetValue(id, out var n) && !IsLan(n))))
            .ToDictionary(ConfigState.IdOf, z => z["name"]?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s): yield return s; break;
            case JsonObject o: foreach (var (_, c) in o) foreach (var s in Strings(c)) yield return s; break;
            case JsonArray a: foreach (var c in a) foreach (var s in Strings(c)) yield return s; break;
        }
    }

    /// <summary>The ranges a value names: an address as a /32, a CIDR, an a-b range as the CIDR blocks that cover it.</summary>
    private static IEnumerable<Subnet> Ranges(string text)
    {
        text = text.Trim();
        if (ParseIp(text) is { } ip) { yield return new Subnet(ip, 32); yield break; }
        var slash = text.Split('/');
        if (slash.Length == 2 && ParseIp(slash[0]) is { } a && int.TryParse(slash[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 32)
        {
            yield return new Subnet(a & (p == 0 ? 0 : uint.MaxValue << (32 - p)), p);
            yield break;
        }
        var dash = text.Split('-');
        if (dash.Length == 2 && ParseIp(dash[0].Trim()) is { } from && ParseIp(dash[1].Trim()) is { } to && from <= to)
        {
            // Largest aligned block at each step, so a range is covered exactly.
            ulong at = from;
            while (at <= to)
            {
                var size = 32;
                while (size > 1)
                {
                    var block = 1UL << (33 - size); // the block one prefix length larger
                    if (at % block != 0 || at + block - 1 > to) break;
                    size--;
                }
                yield return new Subnet((uint)at, size);
                at += 1UL << (32 - size);
            }
        }
    }

    private static IEnumerable<string> StaticSubnets(JsonObject n)
    {
        if (n["ip_subnet"]?.ToString() is { Length: > 0 } s) yield return s;
        if (n["wan_ip"]?.ToString() is { Length: > 0 } w && n["wan_netmask"]?.ToString() is { Length: > 0 } mask && ParseIp(mask) is { } m)
            yield return $"{w}/{System.Numerics.BitOperations.PopCount(m)}";
        if (n["remote_vpn_subnets"] is JsonArray remote)
            foreach (var r in remote) if (r?.ToString() is { Length: > 0 } rs) yield return rs;
    }

    public static bool Overlaps(Subnet a, Subnet b)
    {
        var mask = a.Prefix < b.Prefix ? a.Mask : b.Mask;
        return (a.Network & mask) == (b.Network & mask);
    }

    /// <summary>
    /// Rewrites every address in the moved subnets across the whole config, except what belongs to a WAN
    /// or a VPN's far side (<see cref="RemoteRanges"/>): those are reported in <see cref="ReaddressResult.Remote"/>.
    /// </summary>
    public static ReaddressResult Apply(ConfigState live, IReadOnlyList<SubnetMove> moves)
    {
        var map = Map(moves);
        var result = new ReaddressResult { Desired = live.Clone() };
        if (map.Count == 0) return result;

        var remote = RemoteRanges(live).Select(r => r.Range).ToList();
        var networks = live.Get("networkconf").ToDictionary(ConfigState.IdOf, n => n, StringComparer.OrdinalIgnoreCase);
        bool RemoteNetwork(string? id) => id != null && networks.TryGetValue(id, out var n) && !IsLan(n);
        var remoteZones = RemoteZones(live, networks);

        foreach (var (key, list) in result.Desired.Collections)
        {
            var def = ConfigCollections.Get(key);
            foreach (var obj in list)
            {
                // UniFi regenerates these from the network itself, and plans never write them.
                if (def?.IsGenerated?.Invoke(obj) == true) continue;
                var ctx = new WalkContext(map, remote, key, ConfigState.DisplayName(def, obj), result);
                // Whole objects the far side sets: a VPN client or server, a VPN connection.
                if ((key == "networkconf" && !IsLan(obj) && !IsWan(obj)) || key is "vpn_connections" or "wan_defaults")
                {
                    Walk(obj, "", ctx with { ReportOnly = true });
                    continue;
                }
                // A WAN's carrier fields, and destinations that leave over a WAN or a VPN.
                var far = new HashSet<string>(StringComparer.Ordinal);
                if (key == "networkconf" && IsWan(obj)) far.UnionWith(obj.Select(p => p.Key).Where(IsCarrierField));
                if (key == "routing" && RemoteNetwork(obj["static-route_interface"]?.ToString())) far.Add("static-route_network");
                if (key == "trafficroute" && RemoteNetwork(obj["network_id"]?.ToString())) { far.Add("ip_addresses"); far.Add("ip_ranges"); }
                if (key == "firewall_policy")
                    foreach (var end in new[] { "source", "destination" })
                        if (obj[end] is JsonObject e && e["zone_id"]?.ToString() is { } z && remoteZones.ContainsKey(z)) far.Add(end);
                if (Walk(obj, "", ctx, far)) result.Collections.Add(key);
            }
        }
        return result;
    }

    private sealed record WalkContext(IReadOnlyList<(Subnet Old, Subnet New)> Map, IReadOnlyList<Subnet> Remote,
        string Collection, string Name, ReaddressResult Result, bool ReportOnly = false);

    /// <summary>Maps one address through the moves; null when no move holds it.</summary>
    public static uint? MapIp(uint ip, IReadOnlyList<(Subnet Old, Subnet New)> map)
    {
        foreach (var (old, @new) in map)
            if (old.Contains(ip)) return @new.Network | (ip & ~old.Mask);
        return null;
    }

    private static bool Walk(JsonNode? node, string path, WalkContext ctx, IReadOnlySet<string>? farKeys = null)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o.ToList())
                {
                    // Live state (last_ip and the like) is not config: the diff skips it, so the move does too.
                    if (k is "_id" or "site_id" || k == ConfigCollections.DeviceMetaField || ConfigDiffEngine.IsChurnField(k)) continue;
                    var childPath = path.Length == 0 ? k : $"{path}.{k}";
                    var child = farKeys?.Contains(k) == true ? ctx with { ReportOnly = true } : ctx;
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        if (Visit(s, childPath, child) is { } rewritten) { o[k] = rewritten; changed = true; }
                    }
                    else changed |= Walk(v, childPath, child);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var childPath = $"{path}[{i}]";
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        if (Visit(s, childPath, ctx) is { } rewritten) { a[i] = rewritten; changed = true; }
                    }
                    else changed |= Walk(a[i], childPath, ctx);
                }
                break;
        }
        return changed;
    }

    /// <summary>One string: rewritten (returned), reported as remote, reported as unreadable, or left.</summary>
    private static string? Visit(string value, string path, WalkContext ctx)
    {
        if (!Mentions(value, ctx.Map)) return null;
        var mention = new AddressMention(ctx.Collection, ctx.Name, path, value);
        if (ctx.ReportOnly || InRemote(value, ctx.Remote)) { ctx.Result.Remote.Add(mention); return null; }
        if (Rewrite(value, ctx.Map) is { } rewritten)
        {
            ctx.Result.Rewrites.Add(new AddressRewrite(ctx.Collection, ctx.Name, path, value, rewritten));
            return rewritten;
        }
        ctx.Result.Mentions.Add(mention);
        return null;
    }

    /// <summary>True when any address the value names sits in a remote range.</summary>
    public static bool InRemote(string value, IReadOnlyList<Subnet> remote) =>
        remote.Count > 0 && IpInText().Matches(value).Any(m => ParseIp(m.Value) is { } ip && remote.Any(r => r.Contains(ip)));

    /// <summary>
    /// The value rewritten when it is exactly an address, a CIDR, or an a-b range inside a moved subnet,
    /// and none of it is in <paramref name="remote"/>; null otherwise. A CIDR as wide as the old subnet
    /// takes the new prefix (a network's own subnet).
    /// </summary>
    public static string? Rewrite(string value, IReadOnlyList<(Subnet Old, Subnet New)> map, IReadOnlyList<Subnet>? remote = null)
    {
        var text = value.Trim();
        if (text.Length == 0 || (remote != null && InRemote(text, remote))) return null;
        if (ParseIp(text) is { } ip) return MapIp(ip, map) is { } m ? Format(m) : null;

        var slash = text.Split('/');
        if (slash.Length == 2 && ParseIp(slash[0]) is { } addr && int.TryParse(slash[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            foreach (var (old, @new) in map)
            {
                if (!old.Contains(addr) || prefix < old.Prefix) continue;
                var mapped = @new.Network | (addr & ~old.Mask);
                return $"{Format(mapped)}/{(prefix == old.Prefix ? @new.Prefix : prefix)}";
            }
            return null;
        }

        var dash = text.Split('-');
        if (dash.Length == 2 && ParseIp(dash[0].Trim()) is { } from && ParseIp(dash[1].Trim()) is { } to
            && MapIp(from, map) is { } f && MapIp(to, map) is { } t)
            return $"{Format(f)}-{Format(t)}";
        return null;
    }

    private static bool Mentions(string value, IReadOnlyList<(Subnet Old, Subnet New)> map) =>
        IpInText().Matches(value).Any(m => ParseIp(m.Value) is { } ip && map.Any(p => p.Old.Contains(ip)));

    /// <summary>The moves as the (old, new) subnet pairs <see cref="Rewrite"/> takes.</summary>
    public static List<(Subnet Old, Subnet New)> Map(IEnumerable<SubnetMove> moves) =>
        moves.Where(m => m.OldCidr != m.NewCidr)
            .Select(m => (Old: ParseSubnet(m.OldCidr), New: ParseSubnet(m.NewCidr)))
            .Where(p => p.Old != null && p.New != null).Select(p => (p.Old!.Value, p.New!.Value)).ToList();

    /// <summary>Reverses moves, for undoing a re-address.</summary>
    public static List<SubnetMove> Reverse(IEnumerable<SubnetMove> moves) =>
        moves.Select(m => m with { OldCidr = m.NewCidr, NewCidr = m.OldCidr }).ToList();

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")]
    private static partial Regex IpShape();

    [GeneratedRegex(@"(?<![\d.])\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?![\d.])")]
    private static partial Regex IpInText();
}
