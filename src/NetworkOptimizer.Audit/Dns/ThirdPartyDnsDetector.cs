using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;

namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Detects third-party LAN DNS servers (like Pi-hole) that are used instead of gateway DNS.
/// </summary>
public class ThirdPartyDnsDetector
{
    private readonly ILogger<ThirdPartyDnsDetector> _logger;
    private readonly HttpClient _httpClient;

    public ThirdPartyDnsDetector(ILogger<ThirdPartyDnsDetector> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    /// <summary>
    /// Detection result for a third-party DNS server
    /// </summary>
    public class ThirdPartyDnsInfo
    {
        public required string DnsServerIp { get; init; }
        public required string NetworkName { get; init; }
        public int NetworkVlanId { get; init; }
        public bool IsLanIp { get; init; }
        public bool IsPihole { get; init; }
        public string? PiholeVersion { get; init; }
        public string DnsProviderName { get; init; } = "Third-Party LAN DNS";
    }

    /// <summary>
    /// Detect third-party LAN DNS servers across all networks
    /// </summary>
    /// <param name="networks">List of networks to check</param>
    /// <param name="customPiholePort">Optional custom port for Pi-hole admin interface</param>
    public async Task<List<ThirdPartyDnsInfo>> DetectThirdPartyDnsAsync(List<NetworkInfo> networks, int? customPiholePort = null)
    {
        var results = new List<ThirdPartyDnsInfo>();
        var probedIps = new HashSet<string>(); // Avoid probing the same IP multiple times

        _logger.LogInformation("Checking {Count} networks for third-party DNS servers", networks.Count);

        foreach (var network in networks)
        {
            // Skip networks without DHCP or without custom DNS servers
            if (!network.DhcpEnabled)
            {
                _logger.LogDebug("Network {Network}: Skipping (DHCP not enabled)", network.Name);
                continue;
            }

            if (network.DnsServers == null || !network.DnsServers.Any())
            {
                _logger.LogDebug("Network {Network}: Skipping (no custom DNS servers configured)", network.Name);
                continue;
            }

            var gatewayIp = network.Gateway;
            _logger.LogDebug("Network {Network}: Gateway={Gateway}, DnsServers=[{DnsServers}]",
                network.Name, gatewayIp, string.Join(", ", network.DnsServers));

            foreach (var dnsServer in network.DnsServers)
            {
                if (string.IsNullOrEmpty(dnsServer))
                    continue;

                // Skip if this DNS server is the gateway
                if (dnsServer == gatewayIp)
                {
                    _logger.LogDebug("Network {Network}: DNS {DnsServer} is gateway, skipping", network.Name, dnsServer);
                    continue;
                }

                // Check if this is a LAN IP (RFC1918 private address)
                if (!IsRfc1918Address(dnsServer))
                {
                    _logger.LogDebug("Network {Network}: DNS {DnsServer} is not RFC1918, skipping", network.Name, dnsServer);
                    continue;
                }

                _logger.LogInformation("Network {Network} uses third-party LAN DNS: {DnsServer} (gateway: {Gateway})",
                    network.Name, dnsServer, gatewayIp);

                // Only probe each IP once
                bool isPihole = false;
                string? piholeVersion = null;
                string providerName = "Third-Party LAN DNS";

                if (!probedIps.Contains(dnsServer))
                {
                    probedIps.Add(dnsServer);
                    (isPihole, piholeVersion) = await ProbePiholeAsync(dnsServer, customPiholePort);
                    if (isPihole)
                    {
                        providerName = "Pi-hole";
                        _logger.LogInformation("Detected Pi-hole at {Ip} (version: {Version})", dnsServer, piholeVersion ?? "unknown");
                    }
                }
                else
                {
                    // Reuse result from previous probe
                    var existingResult = results.FirstOrDefault(r => r.DnsServerIp == dnsServer);
                    if (existingResult != null)
                    {
                        isPihole = existingResult.IsPihole;
                        piholeVersion = existingResult.PiholeVersion;
                        providerName = existingResult.DnsProviderName;
                    }
                }

                results.Add(new ThirdPartyDnsInfo
                {
                    DnsServerIp = dnsServer,
                    NetworkName = network.Name,
                    NetworkVlanId = network.VlanId,
                    IsLanIp = true,
                    IsPihole = isPihole,
                    PiholeVersion = piholeVersion,
                    DnsProviderName = providerName
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Check if an IP address is an RFC1918 private address
    /// </summary>
    public static bool IsRfc1918Address(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        var bytes = ip.GetAddressBytes();
        if (bytes.Length != 4)
            return false; // IPv6 not supported

        // 10.0.0.0 - 10.255.255.255
        if (bytes[0] == 10)
            return true;

        // 172.16.0.0 - 172.31.255.255
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;

        // 192.168.0.0 - 192.168.255.255
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;

        return false;
    }

    /// <summary>
    /// Probe an IP address to detect if it's running Pi-hole
    /// </summary>
    /// <param name="ipAddress">IP address to probe</param>
    /// <param name="customPort">Optional custom port to try (both HTTP and HTTPS)</param>
    private async Task<(bool IsPihole, string? Version)> ProbePiholeAsync(string ipAddress, int? customPort = null)
    {
        // Build list of ports to try
        var portsToTry = new List<(int Port, bool UseHttps)>();

        // If custom port is specified, try it first (both HTTP and HTTPS)
        if (customPort.HasValue && customPort.Value > 0)
        {
            portsToTry.Add((customPort.Value, false)); // Try HTTP first
            portsToTry.Add((customPort.Value, true));  // Then HTTPS
        }

        // Add default ports: 80 (default), 443 (HTTPS), 8080 (alternate)
        portsToTry.Add((80, false));
        portsToTry.Add((443, true));
        portsToTry.Add((8080, false));

        foreach (var (port, useHttps) in portsToTry)
        {
            var result = await TryProbePiholeEndpointAsync(ipAddress, port, useHttps);
            if (result.IsPihole)
                return result;
        }

        return (false, null);
    }

    private async Task<(bool IsPihole, string? Version)> TryProbePiholeEndpointAsync(string ipAddress, int port, bool useHttps = false)
    {
        try
        {
            var scheme = useHttps ? "https" : "http";
            var url = $"{scheme}://{ipAddress}:{port}/api/info/login";

            _logger.LogDebug("Probing Pi-hole at {Url}", url);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
                return (false, null);

            var content = await response.Content.ReadAsStringAsync(cts.Token);

            // Pi-hole /api/info/login returns {"dns":true,"https_port":...,"took":...}
            if (content.Contains("\"dns\""))
            {
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("dns", out var dnsProp) && dnsProp.GetBoolean())
                    {
                        _logger.LogInformation("Detected Pi-hole at {Url}", url);
                        return (true, "detected");
                    }
                }
                catch
                {
                    // JSON parsing failed, but content indicates Pi-hole
                    return (true, "detected");
                }
            }

            return (false, null);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Pi-hole probe to {Ip}:{Port} timed out", ipAddress, port);
            return (false, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("Pi-hole probe to {Ip}:{Port} failed: {Message}", ipAddress, port, ex.Message);
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Pi-hole probe to {Ip}:{Port} error: {Type} - {Message}", ipAddress, port, ex.GetType().Name, ex.Message);
            return (false, null);
        }
    }
}
