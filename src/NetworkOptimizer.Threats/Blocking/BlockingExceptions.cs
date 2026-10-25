using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>Which way an exception lets traffic through the public block lists.</summary>
public enum ExceptionDirection
{
    /// <summary>A device on the site reaches a listed address.</summary>
    Outbound = 0,

    /// <summary>A listed address reaches a device on the site, or the gateway itself.</summary>
    Inbound = 1,
}

/// <summary>The protocols an exception matches.</summary>
public enum ExceptionProtocol
{
    Any = 0,
    Tcp = 1,
    Udp = 2,
    TcpUdp = 3,
}

/// <summary>
/// Traffic the gateway lets through although a public block list has the address. It covers the
/// public lists only: a source Automatic Blocking itself blocks stays blocked.
/// </summary>
public sealed record BlockingException
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public ExceptionDirection Direction { get; init; }

    /// <summary>The device on the site, an IP or CIDR; null for every device.</summary>
    public string? Device { get; init; }

    /// <summary>The listed address, a public IP or CIDR; null for every listed address.</summary>
    public string? Address { get; init; }

    public ExceptionProtocol Protocol { get; init; }

    /// <summary>The device's port when inbound, the listed address's when outbound; null for every port.</summary>
    public int? PortFrom { get; init; }
    public int? PortTo { get; init; }

    public string Note { get; init; } = "";

    /// <summary>When it stops applying; null for never.</summary>
    public DateTime? ExpiresAt { get; init; }

    public string CreatedBy { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    public bool IsActive(DateTime now) => ExpiresAt == null || ExpiresAt > now;

    /// <summary>"443" or "3478-3481"; empty for every port.</summary>
    [JsonIgnore]
    public string PortText => PortFrom is not { } from ? "" : PortTo is { } to && to != from ? $"{from}-{to}" : $"{from}";

    /// <summary>True for IPv6, false for IPv4, null when neither address is set (both families).</summary>
    [JsonIgnore]
    public bool? IsV6 => (Device ?? Address) is { } a ? a.Contains(':') : null;

    /// <summary>The protocol names this exception matches ("tcp", "udp"); empty for every protocol.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Protocols => Protocol switch
    {
        ExceptionProtocol.Tcp => ["tcp"],
        ExceptionProtocol.Udp => ["udp"],
        ExceptionProtocol.TcpUdp => ["tcp", "udp"],
        _ => [],
    };

    /// <summary>Whether a connection from <paramref name="deviceIp"/> to <paramref name="addressIp"/> is let through.</summary>
    /// <param name="protocol">"tcp", "udp", or anything else (matched only by <see cref="ExceptionProtocol.Any"/>).</param>
    /// <param name="port">The listed address's port when outbound, the device's when inbound.</param>
    public bool Matches(ExceptionDirection direction, string deviceIp, string addressIp, string? protocol, int? port)
    {
        if (direction != Direction) return false;
        if (Device != null && !InRange(deviceIp, Device)) return false;
        if (Address != null && !InRange(addressIp, Address)) return false;
        if (Protocol != ExceptionProtocol.Any && !Protocols.Contains((protocol ?? "").ToLowerInvariant())) return false;
        if (PortFrom is { } from && (port is not { } p || p < from || p > (PortTo ?? from))) return false;
        return true;
    }

    /// <summary>Whether the device or the listed address it names (when set) covers <paramref name="ip"/>.</summary>
    public bool Names(string ip) =>
        (Device != null && InRange(ip, Device)) || (Address != null && InRange(ip, Address));

    private static bool InRange(string ip, string range)
    {
        if (BlockAllowlist.Normalize(ip) is not { } n || !IPAddress.TryParse(n, out var addr)) return false;
        if (!range.Contains('/')) return string.Equals(n, range, StringComparison.OrdinalIgnoreCase);
        return IPNetwork.TryParse(range, out var network) && network.Contains(addr);
    }
}

/// <summary>A site's blocking exceptions: stored, validated, and described.</summary>
public static class BlockingExceptions
{
    /// <summary>The most a site holds. On the console each one is a policy per zone, so the count stays small.</summary>
    public const int MaxPerSite = 25;

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A site's exceptions, expired ones included (null site = default site).</summary>
    public static async Task<List<BlockingException>> LoadAsync(IThreatSettingsAccessor settings, string? site, CancellationToken ct)
    {
        var raw = await settings.GetSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.Exceptions, site), ct);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BlockingException>>(raw, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static Task SaveAsync(IThreatSettingsAccessor settings, string? site, IEnumerable<BlockingException> list, CancellationToken ct) =>
        settings.SaveSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.Exceptions, site),
            JsonSerializer.Serialize(list.ToList(), Json), ct);

    /// <summary>
    /// The exception in canonical form. Throws <see cref="ArgumentException"/> with a message for the
    /// form when a field is invalid, or when nothing narrows it (no device, address, or port).
    /// </summary>
    public static BlockingException Validate(BlockingException e)
    {
        string? device = null;
        if (!string.IsNullOrWhiteSpace(e.Device))
            device = Canonical(e.Device) ?? throw new ArgumentException("Device must be an IP address or a CIDR range.");

        string? address = null;
        if (!string.IsNullOrWhiteSpace(e.Address))
            address = BlocklistEntry.Canonical(e.Address)?.Text
                ?? throw new ArgumentException("Listed address must be a public IP address or a CIDR range of /8 or narrower (/16 for IPv6).");

        if (device != null && address != null && device.Contains(':') != address.Contains(':'))
            throw new ArgumentException("Device and Listed address must both be IPv4 or both be IPv6.");

        int? from = null, to = null;
        if (e.PortFrom is { } f)
        {
            if (e.Protocol == ExceptionProtocol.Any)
                throw new ArgumentException("Choose TCP, UDP, or TCP and UDP to set a port.");
            var t = e.PortTo ?? f;
            if (f < 1 || t > 65535 || t < f)
                throw new ArgumentException("Port must be from 1 to 65535, and a range must run low to high.");
            (from, to) = (f, t);
        }

        if (device == null && address == null && from == null)
            throw new ArgumentException("Set a device, a listed address, or a port. An exception for everything would turn the lists off.");

        return e with
        {
            Device = device,
            Address = address,
            PortFrom = from,
            PortTo = to,
            Note = (e.Note ?? "").Trim(),
        };
    }

    /// <summary>Parses "443" or "3478-3481"; null for empty. Throws <see cref="ArgumentException"/> on anything else.</summary>
    public static (int From, int To)? ParsePort(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length is 1 or 2 && int.TryParse(parts[0], out var from))
        {
            if (parts.Length == 1) return (from, from);
            if (int.TryParse(parts[1], out var to)) return (from, to);
        }
        throw new ArgumentException("Port must be a number or a range, such as 3478-3481.");
    }

    /// <summary>The exceptions in force: active ones, in a stable order.</summary>
    public static List<BlockingException> Active(IEnumerable<BlockingException> list, DateTime now) =>
        list.Where(e => e.IsActive(now)).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

    /// <summary>Identity of what the gateway enforces, so a change to any field is sent.</summary>
    public static string Fingerprint(IEnumerable<BlockingException> active) =>
        string.Join(";", active.Select(e => $"{e.Id}:{e.Direction}:{e.Device}:{e.Address}:{e.Protocol}:{e.PortText}"));

    /// <summary>
    /// What gets through, in one sentence: "192.168.1.50 can reach 203.0.113.7 on TCP 443." Device names
    /// come from <paramref name="name"/> when given.
    /// </summary>
    public static string Describe(BlockingException e, Func<string, string?>? name = null)
    {
        string Named(string? ip, string any) =>
            ip == null ? any : name?.Invoke(ip) is { } n ? $"{ip} ({n})" : ip;
        var on = ProtocolText(e) is { Length: > 0 } p ? $" on {p}" : "";
        return e.Direction == ExceptionDirection.Outbound
            ? $"{Named(e.Device, "Every device")} can reach {Named(e.Address, "every listed address")}{on}."
            : $"{Named(e.Address, "Every listed address")} can reach {Named(e.Device, "every device and the gateway")}{on}.";
    }

    /// <summary>"TCP 443", "TCP and UDP 3478-3481", "UDP", or empty for every protocol and port.</summary>
    public static string ProtocolText(BlockingException e)
    {
        var protocol = e.Protocol switch
        {
            ExceptionProtocol.Tcp => "TCP",
            ExceptionProtocol.Udp => "UDP",
            ExceptionProtocol.TcpUdp => "TCP and UDP",
            _ => "",
        };
        return e.PortText.Length > 0 ? $"{protocol} {e.PortText}" : protocol;
    }

    // An IP or CIDR as a gateway rule takes it: the network address, and a host without its prefix.
    private static string? Canonical(string entry)
    {
        if (PublicBlockLists.Normalize(entry) is not { } n) return null;
        if (n.EndsWith("/32", StringComparison.Ordinal) && !n.Contains(':')) return n[..^3];
        if (n.EndsWith("/128", StringComparison.Ordinal)) return n[..^4];
        var host = n.Split('/')[0];
        return IPAddress.TryParse(host, out var a) && a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 ? n : null;
    }
}
