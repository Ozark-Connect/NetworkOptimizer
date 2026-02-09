using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

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
        public bool IsAdGuardHome { get; init; }
        public string? AdGuardHomeVersion { get; init; }
        public string DnsProviderName { get; init; } = "Third-Party LAN DNS";
    }

    /// <summary>
    /// Detection result for a network using DNS outside configured subnets
    /// </summary>
    public class ExternalDnsInfo
    {
        public required string DnsServerIp { get; init; }
        public required string NetworkName { get; init; }
        public int NetworkVlanId { get; init; }
        public string? ProviderName { get; init; }
        /// <summary>
        /// True if the DNS IP is a public/routable address (e.g., 1.1.1.1, 8.8.8.8).
        /// False if it's a private IP outside configured subnets.
        /// </summary>
        public bool IsPublicDns { get; init; }
    }

    /// <summary>
    /// Detect third-party LAN DNS servers across all networks
    /// </summary>
    /// <param name="networks">List of networks to check</param>
    /// <param name="customPort">Optional custom port for third-party DNS management interface (Pi-hole, AdGuard Home, etc.)</param>
    public async Task<List<ThirdPartyDnsInfo>> DetectThirdPartyDnsAsync(List<NetworkInfo> networks, int? customPort = null)
    {
        var results = new List<ThirdPartyDnsInfo>();
        var probedIps = new HashSet<string>(); // Avoid probing the same IP multiple times

        _logger.LogInformation("Checking {Count} networks for third-party DNS servers", networks.Count);

        foreach (var network in networks)
        {
            // Skip disabled networks - their config is dormant
            if (!network.Enabled)
            {
                _logger.LogDebug("Network {Network}: Skipping (disabled)", network.Name);
                continue;
            }

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

                // Check if this is a LAN IP (private address)
                if (!NetworkUtilities.IsPrivateIpAddress(dnsServer))
                {
                    _logger.LogDebug("Network {Network}: DNS {DnsServer} is not private, skipping", network.Name, dnsServer);
                    continue;
                }

                _logger.LogInformation("Network {Network} uses third-party LAN DNS: {DnsServer} (gateway: {Gateway})",
                    network.Name, dnsServer, gatewayIp);

                // Only probe each IP once
                bool isPihole = false;
                string? piholeVersion = null;
                bool isAdGuardHome = false;
                string? adGuardHomeVersion = null;
                string providerName = "Third-Party LAN DNS";

                if (!probedIps.Contains(dnsServer))
                {
                    probedIps.Add(dnsServer);

                    // Try Pi-hole detection first
                    (isPihole, piholeVersion) = await ProbePiholeAsync(dnsServer, customPort);
                    if (isPihole)
                    {
                        providerName = "Pi-hole";
                        _logger.LogInformation("Detected Pi-hole at {Ip} (version: {Version})", dnsServer, piholeVersion ?? "unknown");
                    }
                    else
                    {
                        // If not Pi-hole, try AdGuard Home detection
                        (isAdGuardHome, adGuardHomeVersion) = await ProbeAdGuardHomeAsync(dnsServer, customPort);
                        if (isAdGuardHome)
                        {
                            providerName = "AdGuard Home";
                            _logger.LogInformation("Detected AdGuard Home at {Ip} (version: {Version})", dnsServer, adGuardHomeVersion ?? "unknown");
                        }
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
                        isAdGuardHome = existingResult.IsAdGuardHome;
                        adGuardHomeVersion = existingResult.AdGuardHomeVersion;
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
                    IsAdGuardHome = isAdGuardHome,
                    AdGuardHomeVersion = adGuardHomeVersion,
                    DnsProviderName = providerName
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Detect networks configured to use external public DNS servers (e.g., 1.1.1.1, 8.8.8.8).
    /// These networks bypass all local DNS filtering (gateway DoH, Pi-hole, etc.).
    /// A DNS server is considered "internal" if it falls within any configured network subnet.
    /// </summary>
    public List<ExternalDnsInfo> DetectExternalDns(List<NetworkInfo> networks)
    {
        var results = new List<ExternalDnsInfo>();

        // Collect all internal subnets for checking
        var internalSubnets = networks
            .Where(n => !string.IsNullOrEmpty(n.Subnet))
            .Select(n => n.Subnet!)
            .Distinct()
            .ToList();

        foreach (var network in networks)
        {
            // Skip disabled networks or networks without DHCP or custom DNS servers
            if (!network.Enabled || !network.DhcpEnabled || network.DnsServers == null || !network.DnsServers.Any())
                continue;

            var gatewayIp = network.Gateway;

            foreach (var dnsServer in network.DnsServers)
            {
                if (string.IsNullOrEmpty(dnsServer))
                    continue;

                // Skip if this DNS server is the gateway
                if (dnsServer == gatewayIp)
                    continue;

                // Skip if DNS server is within any configured internal network subnet
                if (NetworkUtilities.IsIpInAnySubnet(dnsServer, internalSubnets))
                    continue;

                // This is a DNS server not within any internal subnet
                var isPublic = NetworkUtilities.IsPublicIpAddress(dnsServer);
                var providerName = isPublic ? GetPublicDnsProviderName(dnsServer) : null;
                var dnsType = isPublic ? "public" : "private (outside configured subnets)";
                _logger.LogInformation("Network {Network} uses external DNS: {DnsServer} ({DnsType}, {Provider})",
                    network.Name, dnsServer, dnsType, providerName ?? "unknown provider");

                results.Add(new ExternalDnsInfo
                {
                    DnsServerIp = dnsServer,
                    NetworkName = network.Name,
                    NetworkVlanId = network.VlanId,
                    ProviderName = providerName,
                    IsPublicDns = isPublic
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Get the provider name for well-known public DNS servers
    /// </summary>
    private static string? GetPublicDnsProviderName(string ipAddress)
    {
        return ipAddress switch
        {
            "1.1.1.1" or "1.0.0.1" => "Cloudflare",
            "8.8.8.8" or "8.8.4.4" => "Google",
            "9.9.9.9" or "149.112.112.112" => "Quad9",
            "208.67.222.222" or "208.67.220.220" => "OpenDNS",
            "94.140.14.14" or "94.140.15.15" => "AdGuard DNS",
            "76.76.2.0" or "76.76.10.0" => "Control D",
            "185.228.168.9" or "185.228.169.9" => "CleanBrowsing",
            _ => null
        };
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
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

    /// <summary>
    /// Probe an IP address to detect if it's running AdGuard Home
    /// </summary>
    /// <param name="ipAddress">IP address to probe</param>
    /// <param name="customPort">Optional custom port (default: 80)</param>
    private async Task<(bool IsAdGuardHome, string? Version)> ProbeAdGuardHomeAsync(string ipAddress, int? customPort = null)
    {
        // Build list of ports to try
        var portsToTry = new List<(int Port, bool UseHttps)>();

        // If custom port is specified, try it first (both HTTP and HTTPS)
        if (customPort.HasValue && customPort.Value > 0)
        {
            portsToTry.Add((customPort.Value, false));
            portsToTry.Add((customPort.Value, true));
        }

        // Add default ports: 80 (default), 443 (HTTPS), 3000 (setup wizard)
        portsToTry.Add((80, false));
        portsToTry.Add((443, true));
        portsToTry.Add((3000, false));

        foreach (var (port, useHttps) in portsToTry)
        {
            var result = await TryProbeAdGuardHomeEndpointAsync(ipAddress, port, useHttps);
            if (result.IsAdGuardHome)
                return result;
        }

        return (false, null);
    }

    private async Task<(bool IsAdGuardHome, string? Version)> TryProbeAdGuardHomeEndpointAsync(string ipAddress, int port, bool useHttps = false)
    {
        try
        {
            var scheme = useHttps ? "https" : "http";
            var loginUrl = $"{scheme}://{ipAddress}:{port}/login.html";

            _logger.LogDebug("Probing AdGuard Home at {Url}", loginUrl);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var response = await _httpClient.GetAsync(loginUrl, cts.Token);

            if (!response.IsSuccessStatusCode)
                return (false, null);

            var content = await response.Content.ReadAsStringAsync(cts.Token);

            // AdGuard Home login.html references a login.*.js file
            // Extract the JS filename and fetch it to check for "AdGuard" string
            var jsMatch = System.Text.RegularExpressions.Regex.Match(content, @"src=""(login\.[^""]+\.js)""");
            if (!jsMatch.Success)
                return (false, null);

            var jsFileName = jsMatch.Groups[1].Value;
            var jsUrl = $"{scheme}://{ipAddress}:{port}/{jsFileName}";

            _logger.LogDebug("Fetching AdGuard Home JS bundle at {Url}", jsUrl);

            var jsResponse = await _httpClient.GetAsync(jsUrl, cts.Token);
            if (!jsResponse.IsSuccessStatusCode)
                return (false, null);

            var jsContent = await jsResponse.Content.ReadAsStringAsync(cts.Token);

            // Check if the JS bundle contains "AdGuard"
            if (jsContent.Contains("AdGuard"))
            {
                _logger.LogInformation("Detected AdGuard Home at {Url}", loginUrl);
                return (true, "detected");
            }

            return (false, null);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("AdGuard Home probe to {Ip}:{Port} timed out", ipAddress, port);
            return (false, null);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("AdGuard Home probe to {Ip}:{Port} failed: {Message}", ipAddress, port, ex.Message);
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("AdGuard Home probe to {Ip}:{Port} error: {Type} - {Message}", ipAddress, port, ex.GetType().Name, ex.Message);
            return (false, null);
        }
    }
}
