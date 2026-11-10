using System.Net;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>An inclusive port range; a single port is a range of one.</summary>
public readonly record struct PortRange(int From, int To)
{
    public bool Contains(int port) => port >= From && port <= To;

    public override string ToString() => From == To ? From.ToString() : $"{From}-{To}";
}

/// <summary>A <see cref="CustomAppDefinition"/> parsed once, ready to match rows.</summary>
public sealed record CustomAppRule(
    int Id,
    string Name,
    CustomAppProtocol Protocol,
    IReadOnlyList<PortRange> Ports,
    CustomAppPortOn PortOn,
    CustomAppScope Scope,
    string? Remote,
    string? IconClass);

/// <summary>How an unsaved definition lands on the live flows.</summary>
/// <param name="LiveMatches">Active flows it matches.</param>
/// <param name="ShadowedBy">A saved definition above it that names those flows first, or null.</param>
public sealed record CustomAppPreview(int LiveMatches, string? ShadowedBy);

/// <summary>What a Name This App link prefills: the protocol, port, and where the flow went.</summary>
public sealed record CustomAppSeed(CustomAppProtocol Protocol, int Port, CustomAppScope Scope)
{
    /// <summary>The URL form, "udp-21324-lan".</summary>
    public string Encode() =>
        $"{(Protocol == CustomAppProtocol.Udp ? "udp" : "tcp")}-{Port}-{Scope switch { CustomAppScope.Lan => "lan", CustomAppScope.Internet => "internet", _ => "any" }}";

    /// <summary>Reads <see cref="Encode"/>'s form; null for anything else.</summary>
    public static CustomAppSeed? Decode(string? value)
    {
        var parts = (value ?? "").Split('-');
        if (parts.Length != 3) return null;
        CustomAppProtocol? proto = parts[0].ToLowerInvariant() switch { "tcp" => CustomAppProtocol.Tcp, "udp" => CustomAppProtocol.Udp, _ => null };
        if (proto == null || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535) return null;
        var scope = parts[2].ToLowerInvariant() switch { "lan" => CustomAppScope.Lan, "internet" => CustomAppScope.Internet, _ => CustomAppScope.Anywhere };
        return new CustomAppSeed(proto.Value, port, scope);
    }
}

/// <summary>
/// Matches a site's custom app definitions against Live Traffic rows. The rows' own naming chain runs
/// first; this pass then renames any row a definition matches, so Live, Ended, History, and the
/// Name This App preview all apply one rule set the same way.
/// </summary>
public static class CustomAppMatcher
{
    /// <summary>The glyphs the icon picker offers, Font Awesome 6 solid.</summary>
    public static readonly IReadOnlyList<string> IconChoices =
    [
        "fa-solid fa-lightbulb", "fa-solid fa-tv", "fa-solid fa-video", "fa-solid fa-camera", "fa-solid fa-music",
        "fa-solid fa-headphones", "fa-solid fa-gamepad", "fa-solid fa-desktop", "fa-solid fa-mobile-screen",
        "fa-solid fa-print", "fa-solid fa-server", "fa-solid fa-hard-drive", "fa-solid fa-database",
        "fa-solid fa-network-wired", "fa-solid fa-wifi", "fa-solid fa-satellite-dish", "fa-solid fa-cloud",
        "fa-solid fa-house", "fa-solid fa-temperature-half", "fa-solid fa-plug", "fa-solid fa-microchip",
        "fa-solid fa-robot", "fa-solid fa-shield-halved", "fa-solid fa-car", "fa-solid fa-gauge", "fa-solid fa-gear",
    ];

    /// <summary>
    /// The icon class of a custom app with no glyph chosen: it draws the initial (CSS hides the glyph
    /// box). Without it the icon falls back to a lookup by name and could show some brand's mark.
    /// </summary>
    public const string InitialOnlyIcon = "lt-app-initial-only";

    /// <summary>Our own traffic keeps its name: a definition never relabels Network Optimizer.</summary>
    private const string OwnApp = "Network Optimizer";

    // ---------------------------------------------------------------- parsing and validation

    /// <summary>Parses "21324", "5000-5010", or "80, 8080". The error is the form's message, or null.</summary>
    public static bool TryParsePorts(string? text, out List<PortRange> ports, out string? error)
    {
        ports = new List<PortRange>();
        error = null;
        const string invalid = "Enter a port from 1 to 65535, a range like 5000-5010, or a list like 80, 8080.";
        var items = (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length == 0)
        {
            error = invalid;
            return false;
        }
        foreach (var item in items)
        {
            var bounds = item.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length is < 1 or > 2 || !TryPort(bounds[0], out var from)
                || !TryPort(bounds.Length == 2 ? bounds[1] : bounds[0], out var to))
            {
                error = invalid;
                return false;
            }
            if (from > to)
            {
                error = "That range runs backward. Put the lower port first.";
                return false;
            }
            ports.Add(new PortRange(from, to));
        }
        return true;
    }

    private static bool TryPort(string s, out int port) => int.TryParse(s, out port) && port is >= 1 and <= 65535;

    /// <summary>The form's message for a Remote that is not a domain, an address, or a CIDR; null when it is fine or empty.</summary>
    public static string? ValidateRemote(string? remote)
    {
        var value = remote?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (IPAddress.TryParse(value, out _)) return null;
        if (value.Contains('/'))
        {
            var (network, prefix) = NetworkUtilities.ParseCidr(value);
            var max = network?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            return network != null && prefix >= 0 && prefix <= max ? null : RemoteError;
        }
        var domain = value.TrimStart('*', '.').TrimEnd('.');
        return domain.Length > 0 && domain.Contains('.') && Uri.CheckHostName(domain) == UriHostNameType.Dns ? null : RemoteError;
    }

    private const string RemoteError = "Enter a domain like example.com, an address, or a CIDR like 192.0.2.0/24.";

    /// <summary>The definitions in list order as rules; a stored row that no longer parses is skipped.</summary>
    public static IReadOnlyList<CustomAppRule> ToRules(IEnumerable<CustomAppDefinition> definitions) =>
        definitions.OrderBy(d => d.SortOrder).ThenBy(d => d.Id)
            .Select(ToRule).OfType<CustomAppRule>().ToList();

    public static CustomAppRule? ToRule(CustomAppDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Name) || !TryParsePorts(d.Ports, out var ports, out _)) return null;
        var remote = string.IsNullOrWhiteSpace(d.Remote) ? null : d.Remote.Trim();
        if (ValidateRemote(remote) != null) return null;
        return new CustomAppRule(d.Id, d.Name.Trim(), d.Protocol, ports, d.PortOn, d.Scope,
            remote, string.IsNullOrWhiteSpace(d.IconClass) ? null : d.IconClass);
    }

    // ---------------------------------------------------------------- matching

    public static bool Matches(CustomAppRule rule, LiveFlowRow r)
    {
        if (r.App == OwnApp) return false;
        var protoOk = rule.Protocol switch
        {
            CustomAppProtocol.Tcp => r.Proto == "TCP",
            CustomAppProtocol.Udp => r.Proto == "UDP",
            _ => r.Proto is "TCP" or "UDP",
        };
        if (!protoOk) return false;
        bool On(int port) => port > 0 && rule.Ports.Any(p => p.Contains(port));
        var portOk = rule.PortOn switch
        {
            CustomAppPortOn.Source => On(r.ClientPort),
            CustomAppPortOn.Either => On(r.RemotePort) || On(r.ClientPort),
            _ => On(r.RemotePort),
        };
        if (!portOk) return false;
        var lan = r.RouteKind == LiveRouteKind.Lan;
        if (rule.Scope == CustomAppScope.Lan && !lan) return false;
        if (rule.Scope == CustomAppScope.Internet && lan) return false;
        return rule.Remote == null || RemoteMatches(rule.Remote, r);
    }

    private static bool RemoteMatches(string remote, LiveFlowRow r)
    {
        if (IPAddress.TryParse(remote, out var address))
            return IPAddress.TryParse(r.DestinationIp, out var dest) && dest.Equals(address);
        if (remote.Contains('/')) return NetworkUtilities.IsIpInSubnet(r.DestinationIp, remote);
        var suffix = remote.TrimStart('*', '.').TrimEnd('.');
        var name = r.Destination.TrimEnd('.');
        return name.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The first rule, in list order, that matches the row; null when none does.</summary>
    public static CustomAppRule? FirstMatch(IReadOnlyList<CustomAppRule> rules, LiveFlowRow r)
    {
        foreach (var rule in rules)
            if (Matches(rule, r)) return rule;
        return null;
    }

    /// <summary>Renames each row the first matching rule names. Returns the list unchanged when there are no rules.</summary>
    public static List<LiveFlowRow> Apply(List<LiveFlowRow> rows, IReadOnlyList<CustomAppRule> rules)
    {
        if (rules.Count == 0) return rows;
        for (var i = 0; i < rows.Count; i++)
        {
            if (FirstMatch(rules, rows[i]) is not { } rule) continue;
            rows[i] = rows[i] with
            {
                App = rule.Name,
                AppIconClass = rule.IconClass ?? InitialOnlyIcon,
                AppIconDomain = null,
                Category = null,
                AppIsFallback = false,
                CustomAppId = rule.Id,
            };
        }
        return rows;
    }

    // ---------------------------------------------------------------- display

    /// <summary>"UDP 21324, either end, between my devices": the list's Matches column.</summary>
    public static string Describe(CustomAppProtocol protocol, IReadOnlyList<PortRange> ports, CustomAppPortOn portOn, CustomAppScope scope, string? remote)
    {
        var proto = protocol switch { CustomAppProtocol.Tcp => "TCP", CustomAppProtocol.Udp => "UDP", _ => "TCP and UDP" };
        var parts = new List<string> { $"{proto} {string.Join(", ", ports)}" };
        if (portOn == CustomAppPortOn.Source) parts.Add("source port");
        else if (portOn == CustomAppPortOn.Either) parts.Add("either end");
        if (scope == CustomAppScope.Lan) parts.Add("between my devices");
        else if (scope == CustomAppScope.Internet) parts.Add("to the internet");
        if (!string.IsNullOrWhiteSpace(remote)) parts.Add(remote.Trim());
        return string.Join(", ", parts);
    }

    public static string Describe(CustomAppRule rule) => Describe(rule.Protocol, rule.Ports, rule.PortOn, rule.Scope, rule.Remote);

    /// <summary>
    /// The protocol and port of an App that is only the bare "UDP 21324" fallback, or null. A row's
    /// <see cref="LiveFlowRow.UnnamedPort"/> uses it; so do the Sankey node and the Group by App row.
    /// </summary>
    public static (CustomAppProtocol Protocol, int Port)? ParseUnnamed(string app, string? proto = null, int? port = null)
    {
        var space = app.IndexOf(' ');
        if (space <= 0) return null;
        CustomAppProtocol? p = app[..space] switch { "TCP" => CustomAppProtocol.Tcp, "UDP" => CustomAppProtocol.Udp, _ => null };
        if (p == null || !int.TryParse(app[(space + 1)..], out var n) || n is < 1 or > 65535) return null;
        if (proto != null && app[..space] != proto) return null;
        if (port != null && n != port) return null;
        return (p.Value, n);
    }

    /// <summary>Where a set of rows went, for a seed: all LAN, all out, or mixed (Anywhere).</summary>
    public static CustomAppScope ScopeOf(IEnumerable<LiveFlowRow> rows)
    {
        bool any = false, lan = false, internet = false;
        foreach (var r in rows)
        {
            any = true;
            if (r.RouteKind == LiveRouteKind.Lan) lan = true; else internet = true;
        }
        return !any || (lan && internet) ? CustomAppScope.Anywhere : lan ? CustomAppScope.Lan : CustomAppScope.Internet;
    }
}
