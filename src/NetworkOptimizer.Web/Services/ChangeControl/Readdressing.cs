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

    public static uint? ParseIp(string? text)
    {
        if (text == null || !IpShape().IsMatch(text) || !IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static string Format(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

    /// <summary>The site's LAN networks with a subnet, each proposed a new one from <paramref name="pattern"/> when given.</summary>
    public static List<SubnetMove> Proposal(ConfigState live, string? pattern)
    {
        var moves = new List<SubnetMove>();
        foreach (var n in live.Get("networkconf"))
        {
            if (!LanPurposes.Contains(n["purpose"]?.ToString() ?? "") || ParseSubnet(n["ip_subnet"]?.ToString()) is not { } old) continue;
            var vlan = n["vlan_enabled"] is JsonValue ve && ve.TryGetValue<bool>(out var on) && on && int.TryParse(n["vlan"]?.ToString(), out var v) ? v : (int?)null;
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
        var text = pattern.Trim()
            .Replace("{vlan}", (vlan ?? 1).ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{octet3}", ((old.Network >> 8) & 255).ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
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
        return errors.Distinct().ToList();
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

    /// <summary>Rewrites every address in the moved subnets across the whole config.</summary>
    public static ReaddressResult Apply(ConfigState live, IReadOnlyList<SubnetMove> moves)
    {
        var map = Map(moves);
        var result = new ReaddressResult { Desired = live.Clone() };
        if (map.Count == 0) return result;

        foreach (var (key, list) in result.Desired.Collections)
        {
            var def = ConfigCollections.Get(key);
            foreach (var obj in list)
            {
                var name = ConfigState.DisplayName(def, obj);
                if (Walk(obj, "", map, key, name, result)) result.Collections.Add(key);
            }
        }
        return result;
    }

    /// <summary>Maps one address through the moves; null when no move holds it.</summary>
    public static uint? MapIp(uint ip, IReadOnlyList<(Subnet Old, Subnet New)> map)
    {
        foreach (var (old, @new) in map)
            if (old.Contains(ip)) return @new.Network | (ip & ~old.Mask);
        return null;
    }

    private static bool Walk(JsonNode? node, string path, IReadOnlyList<(Subnet Old, Subnet New)> map, string collection, string name, ReaddressResult result)
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
                    if (v is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        if (Rewrite(s, map) is { } rewritten)
                        {
                            o[k] = rewritten;
                            result.Rewrites.Add(new AddressRewrite(collection, name, childPath, s, rewritten));
                            changed = true;
                        }
                        else if (Mentions(s, map)) result.Mentions.Add(new AddressMention(collection, name, childPath, s));
                    }
                    else changed |= Walk(v, childPath, map, collection, name, result);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var childPath = $"{path}[{i}]";
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        if (Rewrite(s, map) is { } rewritten)
                        {
                            a[i] = rewritten;
                            result.Rewrites.Add(new AddressRewrite(collection, name, childPath, s, rewritten));
                            changed = true;
                        }
                        else if (Mentions(s, map)) result.Mentions.Add(new AddressMention(collection, name, childPath, s));
                    }
                    else changed |= Walk(a[i], childPath, map, collection, name, result);
                }
                break;
        }
        return changed;
    }

    /// <summary>
    /// The value rewritten when it is exactly an address, a CIDR, or an a-b range inside a moved subnet;
    /// null otherwise. A CIDR as wide as the old subnet takes the new prefix (a network's own subnet).
    /// </summary>
    public static string? Rewrite(string value, IReadOnlyList<(Subnet Old, Subnet New)> map)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
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
