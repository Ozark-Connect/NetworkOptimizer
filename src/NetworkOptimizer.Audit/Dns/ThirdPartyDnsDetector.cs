using System.Net;
using System.Net.Http.Json;
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
    public async Task<List<ThirdPartyDnsInfo>> DetectThirdPartyDnsAsync(List<NetworkInfo> networks)
    {
        var results = new List<ThirdPartyDnsInfo>();
        var probedIps = new HashSet<string>(); // Avoid probing the same IP multiple times

        foreach (var network in networks)
        {
            // Skip networks without DHCP or without custom DNS servers
            if (!network.DhcpEnabled || network.DnsServers == null || !network.DnsServers.Any())
                continue;

            var gatewayIp = network.Gateway;

            foreach (var dnsServer in network.DnsServers)
            {
                if (string.IsNullOrEmpty(dnsServer))
                    continue;

                // Skip if this DNS server is the gateway
                if (dnsServer == gatewayIp)
                    continue;

                // Check if this is a LAN IP (RFC1918 private address)
                if (!IsRfc1918Address(dnsServer))
                    continue;

                _logger.LogDebug("Network {Network} uses third-party LAN DNS: {DnsServer} (gateway: {Gateway})",
                    network.Name, dnsServer, gatewayIp);

                // Only probe each IP once
                bool isPihole = false;
                string? piholeVersion = null;
                string providerName = "Third-Party LAN DNS";

                if (!probedIps.Contains(dnsServer))
                {
                    probedIps.Add(dnsServer);
                    (isPihole, piholeVersion) = await ProbePiholeAsync(dnsServer);
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
    private async Task<(bool IsPihole, string? Version)> ProbePiholeAsync(string ipAddress)
    {
        // Try standard HTTP port first
        var result = await TryProbePiholeEndpointAsync(ipAddress, 80);
        if (result.IsPihole)
            return result;

        // Try Pi-hole's alternate port
        result = await TryProbePiholeEndpointAsync(ipAddress, 4711);
        if (result.IsPihole)
            return result;

        // Try HTTPS on port 443
        result = await TryProbePiholeEndpointAsync(ipAddress, 443, useHttps: true);
        return result;
    }

    private async Task<(bool IsPihole, string? Version)> TryProbePiholeEndpointAsync(string ipAddress, int port, bool useHttps = false)
    {
        try
        {
            var scheme = useHttps ? "https" : "http";
            var url = $"{scheme}://{ipAddress}:{port}/admin/api.php?summary";

            _logger.LogDebug("Probing Pi-hole at {Url}", url);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync(url, cts.Token);

            if (!response.IsSuccessStatusCode)
                return (false, null);

            var content = await response.Content.ReadAsStringAsync(cts.Token);

            // Pi-hole API returns JSON with fields like "status", "dns_queries_today", etc.
            if (content.Contains("dns_queries_today") || content.Contains("ads_blocked_today") || content.Contains("status"))
            {
                // Try to extract version from response
                string? version = null;
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    if (doc.RootElement.TryGetProperty("version", out var versionProp))
                    {
                        version = versionProp.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("gravity_last_updated", out _))
                    {
                        // Definitely Pi-hole if it has gravity data
                        version = "detected";
                    }
                }
                catch
                {
                    // JSON parsing failed, but content indicates Pi-hole
                    version = "detected";
                }

                return (true, version);
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
