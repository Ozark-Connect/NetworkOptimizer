using System.Net;
using System.Net.Sockets;

namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Maps a LAN IPv6 address to the MAC that holds it, from the gateway's IPv6 neighbor table
/// (<c>ip -6 neigh show</c> over Gateway SSH). UniFi Network often lists only a client's IPv4
/// address, so this is how an IPv6 source is tied to a device. One instance per site, owned by
/// <see cref="GatewaySshRegistry"/>; Client Performance and client speed tests share it.
/// </summary>
public class GatewayNeighborTable
{
    // Short enough that a moved address is picked up quickly; long enough that a page polling
    // several addresses, or a burst of speed tests, runs one SSH command rather than many.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private readonly IGatewaySshService _gatewaySsh;
    private readonly ILogger<GatewayNeighborTable> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _expiresUtc;
    private string _output = string.Empty;

    public GatewayNeighborTable(IGatewaySshService gatewaySsh, ILogger<GatewayNeighborTable> logger)
    {
        _gatewaySsh = gatewaySsh;
        _logger = logger;
    }

    /// <summary>
    /// The MAC holding a global or unique-local IPv6 address, or null when the address is not
    /// IPv6, is link-local, is not in the table, or the gateway cannot be read. A failed read is
    /// cached like a successful one, so an unreachable gateway is not retried on every call.
    /// </summary>
    public async Task<string?> ResolveMacAsync(string clientIp)
    {
        if (!IsResolvable(clientIp))
            return null;

        await _gate.WaitAsync();
        try
        {
            if (DateTime.UtcNow >= _expiresUtc)
            {
                _output = string.Empty;
                try
                {
                    var (success, output) = await _gatewaySsh.RunCommandAsync("ip -6 neigh show", TimeSpan.FromSeconds(5));
                    if (success) _output = output;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Gateway IPv6 neighbor lookup failed while resolving {Ip}", clientIp);
                }
                _expiresUtc = DateTime.UtcNow.Add(CacheFor);
            }
            return TryGetMacFromNeighborOutput(_output, clientIp);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The device holding an IPv6 address: its MAC, and the IPv4 address UniFi Network lists it
    /// under. Everything keyed by client IP (the topology, WiFiman, speed test snapshots) knows the
    /// device by that IPv4, so a caller swaps it in for the IPv6 source. Null when the address
    /// does not resolve; Ipv4 is null when UniFi Network does not list the device.
    /// </summary>
    public async Task<(string Mac, string? Ipv4)?> ResolveClientAsync(string clientIp, NetworkOptimizer.UniFi.UniFiApiClient? console)
    {
        var mac = await ResolveMacAsync(clientIp);
        if (mac == null)
            return null;

        string? ipv4 = null;
        if (console != null)
        {
            try
            {
                var listed = (await console.GetClientAsync(mac))?.Ip;
                if (IPAddress.TryParse(listed, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                    ipv4 = listed;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "No UniFi Network client record for {Mac} while resolving {Ip}", mac, clientIp);
            }
        }
        return (mac, ipv4);
    }

    /// <summary>
    /// Whether an address can be looked up: IPv6, not IPv4-mapped, not link-local, no scope.
    /// </summary>
    public static bool IsResolvable(string? clientIp) =>
        IPAddress.TryParse(clientIp, out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6
        && !address.IsIPv4MappedToIPv6
        && !address.IsIPv6LinkLocal
        && address.ScopeId == 0;

    /// <summary>
    /// The MAC for <paramref name="clientIp"/> in <c>ip -6 neigh show</c> output. A stale entry is
    /// usable; FAILED and INCOMPLETE entries are not, and an address listed with two different MACs
    /// is ambiguous and returns null.
    /// </summary>
    internal static string? TryGetMacFromNeighborOutput(string output, string clientIp)
    {
        if (string.IsNullOrWhiteSpace(output) || !IsResolvable(clientIp))
            return null;

        var requested = IPAddress.Parse(clientIp);
        string? match = null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !IPAddress.TryParse(parts[0], out var listed) || !listed.Equals(requested))
                continue;
            if (parts.Any(p => p.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                || p.Equals("INCOMPLETE", StringComparison.OrdinalIgnoreCase)))
                continue;
            var index = Array.FindIndex(parts, p => p.Equals("lladdr", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= parts.Length)
                continue;
            var mac = parts[index + 1];
            var octets = mac.Split(':');
            if (octets.Length != 6 || octets.Any(o => o.Length != 2 || !o.All(Uri.IsHexDigit)))
                continue;
            if (match != null && !string.Equals(match, mac, StringComparison.OrdinalIgnoreCase))
                return null;
            match = mac.ToLowerInvariant();
        }
        return match;
    }
}
