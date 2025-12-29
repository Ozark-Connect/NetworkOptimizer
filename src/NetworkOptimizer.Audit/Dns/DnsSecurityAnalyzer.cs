using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Analyzes DNS security configuration for DoH, firewall rules, and DNS leak prevention
/// </summary>
public class DnsSecurityAnalyzer
{
    private readonly ILogger<DnsSecurityAnalyzer> _logger;

    public DnsSecurityAnalyzer(ILogger<DnsSecurityAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze DNS security from settings and firewall policies
    /// </summary>
    public DnsSecurityResult Analyze(JsonElement? settingsData, JsonElement? firewallData)
        => Analyze(settingsData, firewallData, switches: null, networks: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall policies, and device configuration
    /// </summary>
    public DnsSecurityResult Analyze(JsonElement? settingsData, JsonElement? firewallData, List<SwitchInfo>? switches, List<NetworkInfo>? networks)
        => Analyze(settingsData, firewallData, switches, networks, deviceData: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall policies, device configuration, and raw device data
    /// </summary>
    public DnsSecurityResult Analyze(JsonElement? settingsData, JsonElement? firewallData, List<SwitchInfo>? switches, List<NetworkInfo>? networks, JsonElement? deviceData)
    {
        var result = new DnsSecurityResult();

        // Analyze DoH configuration from settings
        if (settingsData.HasValue)
        {
            AnalyzeDohConfiguration(settingsData.Value, result);
        }
        else
        {
            _logger.LogWarning("No settings data available for DNS security analysis");
        }

        // Extract WAN DNS from device port_table (where network_name is "wan")
        if (deviceData.HasValue)
        {
            ExtractWanDnsFromDevices(deviceData.Value, result);
        }
        else
        {
            _logger.LogDebug("No device data available for WAN DNS extraction");
        }

        // Analyze firewall rules
        if (firewallData.HasValue)
        {
            AnalyzeFirewallRules(firewallData.Value, result);
        }
        else
        {
            _logger.LogWarning("No firewall data available for DNS security analysis");
        }

        // Analyze device DNS configuration
        if (switches != null && networks != null)
        {
            AnalyzeDeviceDnsConfiguration(switches, networks, result);
        }

        // Generate issues based on findings
        GenerateAuditIssues(result);

        _logger.LogInformation("DNS security analysis complete: DoH={DoHState}, Firewall rules found: DNS53={Dns53}, DoT={DoT}, DoH={DoHBlock}, DeviceDns={DeviceDnsOk}, WanDns={WanDnsCount}",
            result.DohState, result.HasDns53BlockRule, result.HasDotBlockRule, result.HasDohBlockRule, result.DeviceDnsPointsToGateway, result.WanDnsServers.Count);

        return result;
    }

    private void AnalyzeDohConfiguration(JsonElement settings, DnsSecurityResult result)
    {
        // Look for DoH configuration in settings array
        var settingsArray = settings.UnwrapDataArray().ToList();
        var keys = settingsArray
            .Where(s => s.TryGetProperty("key", out _))
            .Select(s => s.GetProperty("key").GetString())
            .ToList();
        _logger.LogDebug("Found {Count} settings with keys: {Keys}", keys.Count, string.Join(", ", keys.Take(20)));

        foreach (var setting in settingsArray)
        {
            if (!setting.TryGetProperty("key", out var keyProp))
                continue;

            var key = keyProp.GetString();

            if (key == "doh")
            {
                ParseDohSettings(setting, result);
            }
            else if (key == "dns" || key == "wan_dns")
            {
                _logger.LogDebug("Found WAN DNS settings with key '{Key}'", key);
                ParseWanDnsSettings(setting, result);
            }
        }
    }

    private void ParseDohSettings(JsonElement dohSettings, DnsSecurityResult result)
    {
        // Get DoH state
        if (dohSettings.TryGetProperty("state", out var stateProp))
        {
            result.DohState = stateProp.GetString() ?? "disabled";
        }

        // Parse custom servers (SDNS stamps)
        if (dohSettings.TryGetProperty("custom_servers", out var customServers) && customServers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in customServers.EnumerateArray())
            {
                var serverName = server.GetStringOrNull("server_name");
                var sdnsStamp = server.GetStringOrNull("sdns_stamp");
                var enabled = server.GetBoolOrDefault("enabled", true);

                if (!string.IsNullOrEmpty(sdnsStamp))
                {
                    var decoded = DnsStampDecoder.Decode(sdnsStamp);
                    if (decoded != null)
                    {
                        result.ConfiguredServers.Add(new DnsServerConfig
                        {
                            ServerName = serverName ?? decoded.Hostname ?? "Unknown",
                            StampInfo = decoded,
                            Enabled = enabled,
                            IsCustom = true
                        });
                        _logger.LogDebug("Found custom DoH server: {Name} ({Protocol})",
                            serverName, decoded.ProtocolName);
                    }
                }
            }
        }

        // Parse built-in server names
        if (dohSettings.TryGetProperty("server_names", out var serverNames) && serverNames.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in serverNames.EnumerateArray())
            {
                var serverName = name.GetString();
                if (!string.IsNullOrEmpty(serverName))
                {
                    var provider = DohProviderRegistry.IdentifyProviderFromName(serverName);
                    result.ConfiguredServers.Add(new DnsServerConfig
                    {
                        ServerName = serverName,
                        Provider = provider,
                        Enabled = true,
                        IsCustom = false
                    });
                }
            }
        }

        result.DohConfigured = result.ConfiguredServers.Any(s => s.Enabled);
    }

    private void ParseWanDnsSettings(JsonElement dnsSettings, DnsSecurityResult result)
    {
        // WAN DNS servers (fallback or primary)
        if (dnsSettings.TryGetProperty("dns_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                var ip = server.GetString();
                if (!string.IsNullOrEmpty(ip))
                {
                    result.WanDnsServers.Add(ip);
                }
            }
        }

        // Check for ISP DNS (auto mode)
        if (dnsSettings.TryGetProperty("mode", out var modeProp))
        {
            var mode = modeProp.GetString();
            result.UsingIspDns = mode == "auto" || mode == "dhcp";
        }
    }

    /// <summary>
    /// Extract WAN DNS servers from device port_table.
    /// UniFi stores WAN DNS in port_table entries where network_name is "wan".
    /// </summary>
    private void ExtractWanDnsFromDevices(JsonElement deviceData, DnsSecurityResult result)
    {
        foreach (var device in deviceData.UnwrapDataArray())
        {
            // Only check gateways/routers for WAN DNS
            var deviceType = device.GetStringOrNull("type");
            if (deviceType == null || !UniFiDeviceTypes.IsGateway(deviceType))
                continue;

            // Look in port_table for WAN ports
            if (!device.TryGetProperty("port_table", out var portTable) || portTable.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var port in portTable.EnumerateArray())
            {
                var networkName = port.GetStringOrNull("network_name");
                if (string.IsNullOrEmpty(networkName) || !networkName.Equals("wan", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Found WAN port - extract DNS servers
                if (port.TryGetProperty("dns", out var dnsArray) && dnsArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dns in dnsArray.EnumerateArray())
                    {
                        var dnsIp = dns.GetString();
                        if (!string.IsNullOrEmpty(dnsIp) && !result.WanDnsServers.Contains(dnsIp))
                        {
                            result.WanDnsServers.Add(dnsIp);
                            _logger.LogDebug("Found WAN DNS server from device port_table: {DnsIp}", dnsIp);
                        }
                    }
                }
            }

            // Found gateway with WAN port, no need to check other devices
            if (result.WanDnsServers.Any())
                break;
        }

        if (result.WanDnsServers.Any())
        {
            _logger.LogInformation("Extracted {Count} WAN DNS servers from device port_table: {Servers}",
                result.WanDnsServers.Count, string.Join(", ", result.WanDnsServers));
        }
        else
        {
            _logger.LogDebug("No WAN DNS servers found in device port_table");
        }
    }

    private void AnalyzeFirewallRules(JsonElement firewallData, DnsSecurityResult result)
    {
        // Parse firewall policies to find DNS-related rules
        foreach (var policy in firewallData.UnwrapDataArray())
        {
            if (!policy.TryGetProperty("name", out var nameProp))
                continue;

            var name = nameProp.GetString() ?? "";
            var nameLower = name.ToLowerInvariant();
            var enabled = policy.GetBoolOrDefault("enabled", true);
            var action = policy.GetStringOrNull("action")?.ToLowerInvariant() ?? "";

            if (!enabled)
                continue;

            // Check destination port and matching target
            string? destPort = null;
            string? matchingTarget = null;
            List<string>? webDomains = null;

            if (policy.TryGetProperty("destination", out var dest))
            {
                destPort = dest.GetStringOrNull("port");
                matchingTarget = dest.GetStringOrNull("matching_target");

                if (dest.TryGetProperty("web_domains", out var domains) && domains.ValueKind == JsonValueKind.Array)
                {
                    webDomains = domains.EnumerateArray()
                        .Select(d => d.GetString())
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Select(d => d!)
                        .ToList();
                }
            }

            var isBlockAction = action is "drop" or "reject" or "block";

            // Check for DNS port 53 blocking
            if (isBlockAction && destPort?.Contains("53") == true && !destPort.Contains("853"))
            {
                result.HasDns53BlockRule = true;
                result.Dns53RuleName = name;
                _logger.LogDebug("Found DNS53 block rule: {Name}", name);
            }

            // Check for DNS over TLS (port 853) blocking
            if (isBlockAction && destPort?.Contains("853") == true)
            {
                result.HasDotBlockRule = true;
                result.DotRuleName = name;
                _logger.LogDebug("Found DoT block rule: {Name}", name);
            }

            // Check for DoH/QUIC blocking (port 443 with web domains containing DNS providers)
            if (isBlockAction && destPort?.Contains("443") == true && matchingTarget == "WEB" && webDomains?.Count > 0)
            {
                // Check if web domains include DNS providers
                var dnsProviderDomains = webDomains.Where(d =>
                    d.Contains("dns") ||
                    d.Contains("doh") ||
                    d.Contains("cloudflare-dns") ||
                    d.Contains("quad9") ||
                    d.Contains("nextdns") ||
                    d.Contains("adguard") ||
                    d.Contains("opendns")
                ).ToList();

                if (dnsProviderDomains.Count > 0)
                {
                    result.HasDohBlockRule = true;
                    result.DohBlockedDomains.AddRange(dnsProviderDomains);
                    result.DohRuleName = name;
                    _logger.LogDebug("Found DoH block rule: {Name} with {Count} DNS domains", name, dnsProviderDomains.Count);
                }
            }

            // Check for QUIC protocol blocking (UDP 443)
            var protocol = policy.GetStringOrNull("protocol")?.ToLowerInvariant();
            if (isBlockAction && protocol is "udp" or "tcp_udp" && destPort?.Contains("443") == true)
            {
                if (nameLower.Contains("quic") || nameLower.Contains("doh"))
                {
                    result.HasQuicBlockRule = true;
                }
            }
        }
    }

    private void GenerateAuditIssues(DnsSecurityResult result)
    {
        // Issue: DoH not configured
        if (!result.DohConfigured)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_NO_DOH",
                Severity = AuditSeverity.Recommended,
                Message = "DNS-over-HTTPS (DoH) is not configured. Network traffic uses unencrypted DNS which can be monitored or manipulated.",
                RecommendedAction = "Configure DoH in Network Settings with a trusted provider like NextDNS or Cloudflare",
                RuleId = "DNS-DOH-001",
                ScoreImpact = 8
            });
        }
        else if (result.DohState == "auto")
        {
            // DoH is auto-negotiated, may fall back to unencrypted
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_DOH_AUTO",
                Severity = AuditSeverity.Investigate,
                Message = "DoH is set to 'auto' mode which may fall back to unencrypted DNS. Consider setting to 'custom' for guaranteed encryption.",
                RecommendedAction = "Configure DoH with explicit custom servers for guaranteed encryption",
                RuleId = "DNS-DOH-002",
                ScoreImpact = 3
            });
        }

        // Validate WAN DNS against DoH provider
        ValidateWanDnsConfiguration(result);

        // Issue: No DNS port 53 blocking (DNS leak prevention)
        if (!result.HasDns53BlockRule)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_NO_53_BLOCK",
                Severity = AuditSeverity.Critical,
                Message = "No firewall rule blocks external DNS (port 53). Devices can bypass network DNS settings and leak queries to untrusted servers.",
                RecommendedAction = "Create firewall rule: Block outbound UDP/TCP port 53 to Internet for all VLANs (except gateway)",
                RuleId = "DNS-LEAK-001",
                ScoreImpact = 12
            });
        }

        // Issue: No DoT (853) blocking
        if (!result.HasDotBlockRule)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_NO_DOT_BLOCK",
                Severity = AuditSeverity.Recommended,
                Message = "No firewall rule blocks DNS-over-TLS (port 853). Devices can use encrypted DNS that bypasses your DoH configuration.",
                RecommendedAction = "Create firewall rule: Block outbound TCP port 853 to Internet for all VLANs",
                RuleId = "DNS-LEAK-002",
                ScoreImpact = 6
            });
        }

        // Issue: No DoH bypass blocking
        if (!result.HasDohBlockRule && result.DohConfigured)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_NO_DOH_BLOCK",
                Severity = AuditSeverity.Recommended,
                Message = "No firewall rule blocks public DoH providers. Devices can bypass your DNS filtering by using their own DoH servers.",
                RecommendedAction = "Create firewall rule: Block HTTPS (port 443) to known DoH provider domains",
                RuleId = "DNS-LEAK-003",
                ScoreImpact = 5,
                Metadata = new Dictionary<string, object>
                {
                    { "suggested_domains", "dns.google, cloudflare-dns.com, dns.quad9.net, doh.opendns.com" }
                }
            });
        }

        // Issue: Using ISP DNS
        if (result.UsingIspDns && !result.DohConfigured)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_ISP",
                Severity = AuditSeverity.Investigate,
                Message = "Network is using ISP-provided DNS servers. This may expose browsing history to your ISP and lacks filtering capabilities.",
                RecommendedAction = "Configure custom DNS servers or enable DoH with a privacy-focused provider",
                RuleId = "DNS-ISP-001",
                ScoreImpact = 4
            });
        }

        // Positive: All protections in place
        if (result.DohConfigured && result.HasDns53BlockRule && result.HasDotBlockRule && result.HasDohBlockRule)
        {
            result.HardeningNotes.Add("DNS leak prevention fully configured with DoH and firewall blocking");
        }
        else if (result.DohConfigured && result.HasDns53BlockRule)
        {
            result.HardeningNotes.Add("DoH configured with basic DNS leak prevention (port 53 blocked)");
        }
        else if (result.DohConfigured)
        {
            result.HardeningNotes.Add($"DoH configured: {string.Join(", ", result.ConfiguredServers.Where(s => s.Enabled).Select(s => s.ServerName))}");
        }
    }

    private void ValidateWanDnsConfiguration(DnsSecurityResult result)
    {
        if (!result.DohConfigured || result.WanDnsServers.Count == 0)
        {
            // No DoH or no WAN DNS servers to validate
            return;
        }

        // Find the primary DoH provider
        var primaryServer = result.ConfiguredServers.FirstOrDefault(s => s.Enabled);
        if (primaryServer == null)
            return;

        var expectedProvider = primaryServer.StampInfo?.ProviderInfo ?? primaryServer.Provider;
        if (expectedProvider == null)
        {
            // Try to identify from server name
            expectedProvider = DohProviderRegistry.IdentifyProviderFromName(primaryServer.ServerName);
        }

        if (expectedProvider == null)
        {
            _logger.LogDebug("Could not identify DoH provider for WAN DNS validation");
            return;
        }

        result.ExpectedDnsProvider = expectedProvider.Name;

        // Check each WAN DNS server
        var matchingServers = new List<string>();
        var mismatchedServers = new List<string>();

        foreach (var wanDns in result.WanDnsServers)
        {
            var wanProvider = DohProviderRegistry.IdentifyProviderFromIp(wanDns);
            if (wanProvider != null)
            {
                result.WanDnsProvider = wanProvider.Name;
                if (wanProvider.Name == expectedProvider.Name)
                {
                    matchingServers.Add(wanDns);
                }
                else
                {
                    mismatchedServers.Add($"{wanDns} ({wanProvider.Name})");
                }
            }
            else
            {
                // Unknown DNS - could be ISP or other provider
                mismatchedServers.Add($"{wanDns} (Unknown)");
            }
        }

        result.WanDnsMatchesDoH = matchingServers.Count > 0 && mismatchedServers.Count == 0;

        if (matchingServers.Count > 0 && mismatchedServers.Count == 0)
        {
            result.HardeningNotes.Add($"WAN DNS correctly configured for {expectedProvider.Name}");
        }
        else if (mismatchedServers.Count > 0)
        {
            var expectedIps = string.Join(", ", expectedProvider.DnsIps.Take(2));
            result.Issues.Add(new AuditIssue
            {
                Type = "DNS_WAN_MISMATCH",
                Severity = AuditSeverity.Recommended,
                Message = $"WAN DNS servers don't match DoH provider. DoH uses {expectedProvider.Name} but WAN DNS is set to: {string.Join(", ", mismatchedServers)}",
                RecommendedAction = $"Set WAN DNS to {expectedProvider.Name} servers: {expectedIps}",
                RuleId = "DNS-WAN-001",
                ScoreImpact = 4,
                Metadata = new Dictionary<string, object>
                {
                    { "expected_provider", expectedProvider.Name },
                    { "expected_ips", expectedProvider.DnsIps },
                    { "actual_servers", result.WanDnsServers }
                }
            });
        }
    }

    private void AnalyzeDeviceDnsConfiguration(List<SwitchInfo> switches, List<NetworkInfo> networks, DnsSecurityResult result)
    {
        // Find the gateway device
        var gateway = switches.FirstOrDefault(s => s.IsGateway);
        if (gateway == null)
        {
            _logger.LogDebug("No gateway found for device DNS validation");
            return;
        }

        // Find management network (usually VLAN 1 or labeled management)
        var managementNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Management)
            ?? networks.FirstOrDefault(n => n.IsNative);

        // Log network Gateway info for debugging
        foreach (var net in networks.Take(5))
        {
            _logger.LogDebug("Network '{Name}' (VLAN {Vlan}, Purpose={Purpose}): Gateway={Gateway}, Subnet={Subnet}",
                net.Name, net.VlanId, net.Purpose, net.Gateway ?? "null", net.Subnet ?? "null");
        }

        // Use the internal gateway IP from the management network, not the WAN IP
        // The Gateway property is the internal IP (e.g., 192.168.1.1), not gateway.IpAddress which is the WAN IP
        var expectedGatewayIp = managementNetwork?.Gateway
            ?? networks.FirstOrDefault(n => !string.IsNullOrEmpty(n.Gateway))?.Gateway;

        if (string.IsNullOrEmpty(expectedGatewayIp))
        {
            _logger.LogDebug("Could not determine expected internal gateway IP for device DNS validation. ManagementNetwork={MgmtNet}, IsNative networks: {NativeNets}",
                managementNetwork?.Name ?? "not found",
                string.Join(", ", networks.Where(n => n.IsNative).Select(n => n.Name)));
            return;
        }

        _logger.LogDebug("Using internal gateway IP {GatewayIp} for device DNS validation (from network: {NetworkName})",
            expectedGatewayIp, managementNetwork?.Name ?? "fallback");

        // Get all non-gateway devices
        var allDevices = switches.Where(s => !s.IsGateway).ToList();

        // Separate devices by network config type
        var devicesWithStaticDns = allDevices.Where(s => !string.IsNullOrEmpty(s.ConfiguredDns1)).ToList();
        var devicesWithDhcp = allDevices.Where(s =>
            string.IsNullOrEmpty(s.ConfiguredDns1) &&
            (s.NetworkConfigType == "dhcp" || string.IsNullOrEmpty(s.NetworkConfigType))).ToList();

        result.TotalDevicesChecked = devicesWithStaticDns.Count;
        result.DhcpDeviceCount = devicesWithDhcp.Count;

        // Check devices with static DNS configuration
        foreach (var device in devicesWithStaticDns)
        {
            var pointsToGateway = device.ConfiguredDns1 == expectedGatewayIp;

            result.DeviceDnsDetails.Add(new DeviceDnsInfo
            {
                DeviceName = device.Name,
                DeviceType = device.Type ?? "unknown",
                DeviceIp = device.IpAddress,
                ConfiguredDns = device.ConfiguredDns1,
                ExpectedGateway = expectedGatewayIp,
                PointsToGateway = pointsToGateway,
                UsesDhcp = false
            });

            if (pointsToGateway)
            {
                result.DevicesWithCorrectDns++;
            }
        }

        // Track DHCP devices (assumed to get DNS from gateway's DHCP server)
        foreach (var device in devicesWithDhcp)
        {
            result.DeviceDnsDetails.Add(new DeviceDnsInfo
            {
                DeviceName = device.Name,
                DeviceType = device.Type ?? "unknown",
                DeviceIp = device.IpAddress,
                ConfiguredDns = null,
                ExpectedGateway = expectedGatewayIp,
                PointsToGateway = true, // Assumed correct if using DHCP
                UsesDhcp = true
            });
        }

        result.DeviceDnsPointsToGateway = result.DevicesWithCorrectDns == result.TotalDevicesChecked;

        // Generate summary notes and issues
        if (result.TotalDevicesChecked > 0 || result.DhcpDeviceCount > 0)
        {
            var summaryParts = new List<string>();

            if (result.TotalDevicesChecked > 0)
            {
                if (result.DeviceDnsPointsToGateway)
                {
                    summaryParts.Add($"{result.TotalDevicesChecked} static DNS device(s) point to gateway");
                }
                else
                {
                    var misconfigured = result.TotalDevicesChecked - result.DevicesWithCorrectDns;
                    var deviceNames = result.DeviceDnsDetails
                        .Where(d => !d.PointsToGateway && !d.UsesDhcp)
                        .Select(d => d.DeviceName)
                        .ToList();

                    result.Issues.Add(new AuditIssue
                    {
                        Type = "DNS_DEVICE_MISCONFIGURED",
                        Severity = AuditSeverity.Investigate,
                        Message = $"{misconfigured} of {result.TotalDevicesChecked} infrastructure devices have DNS pointing to non-gateway address",
                        RecommendedAction = $"Configure device DNS to point to gateway ({expectedGatewayIp})",
                        RuleId = "DNS-DEVICE-001",
                        ScoreImpact = 3,
                        Metadata = new Dictionary<string, object>
                        {
                            { "misconfigured_devices", deviceNames },
                            { "expected_gateway", expectedGatewayIp }
                        }
                    });
                }
            }

            if (result.DhcpDeviceCount > 0)
            {
                summaryParts.Add($"{result.DhcpDeviceCount} device(s) use DHCP-assigned DNS");
            }

            if (summaryParts.Any() && result.DeviceDnsPointsToGateway)
            {
                result.HardeningNotes.Add(string.Join(", ", summaryParts));
            }
        }
    }

    /// <summary>
    /// Get a summary of DNS security status
    /// </summary>
    public DnsSecuritySummary GetSummary(DnsSecurityResult result)
    {
        var providerNames = result.ConfiguredServers
            .Where(s => s.Enabled)
            .Select(s => s.StampInfo?.ProviderInfo?.Name ?? s.Provider?.Name ?? s.ServerName)
            .Distinct()
            .ToList();

        return new DnsSecuritySummary
        {
            DohEnabled = result.DohConfigured,
            DohProviders = providerNames,
            DnsLeakProtection = result.HasDns53BlockRule,
            DotBlocked = result.HasDotBlockRule,
            DohBypassBlocked = result.HasDohBlockRule,
            FullyProtected = result.DohConfigured && result.HasDns53BlockRule && result.HasDotBlockRule && result.HasDohBlockRule && result.WanDnsMatchesDoH && result.DeviceDnsPointsToGateway,
            IssueCount = result.Issues.Count,
            CriticalIssueCount = result.Issues.Count(i => i.Severity == AuditSeverity.Critical),
            WanDnsServers = result.WanDnsServers.ToList(),
            WanDnsMatchesDoH = result.WanDnsMatchesDoH,
            WanDnsProvider = result.WanDnsProvider,
            ExpectedDnsProvider = result.ExpectedDnsProvider,
            DeviceDnsPointsToGateway = result.DeviceDnsPointsToGateway,
            TotalDevicesChecked = result.TotalDevicesChecked,
            DevicesWithCorrectDns = result.DevicesWithCorrectDns,
            DhcpDeviceCount = result.DhcpDeviceCount
        };
    }
}

/// <summary>
/// Result of DNS security analysis
/// </summary>
public class DnsSecurityResult
{
    // DoH Configuration
    public string DohState { get; set; } = "disabled";
    public bool DohConfigured { get; set; }
    public List<DnsServerConfig> ConfiguredServers { get; } = new();

    // WAN DNS Configuration
    public List<string> WanDnsServers { get; } = new();
    public bool UsingIspDns { get; set; }
    public bool WanDnsMatchesDoH { get; set; }
    public string? WanDnsProvider { get; set; }
    public string? ExpectedDnsProvider { get; set; }

    // Firewall Rules
    public bool HasDns53BlockRule { get; set; }
    public string? Dns53RuleName { get; set; }
    public bool HasDotBlockRule { get; set; }
    public string? DotRuleName { get; set; }
    public bool HasDohBlockRule { get; set; }
    public string? DohRuleName { get; set; }
    public List<string> DohBlockedDomains { get; } = new();
    public bool HasQuicBlockRule { get; set; }

    // Device DNS Configuration
    public bool DeviceDnsPointsToGateway { get; set; } = true;
    public int TotalDevicesChecked { get; set; }
    public int DevicesWithCorrectDns { get; set; }
    public int DhcpDeviceCount { get; set; }
    public List<DeviceDnsInfo> DeviceDnsDetails { get; } = new();

    // Audit Issues
    public List<AuditIssue> Issues { get; } = new();
    public List<string> HardeningNotes { get; } = new();
}

/// <summary>
/// Device DNS configuration details
/// </summary>
public class DeviceDnsInfo
{
    public required string DeviceName { get; init; }
    public required string DeviceType { get; init; }
    public string? DeviceIp { get; init; }
    public string? ConfiguredDns { get; init; }
    public string? ExpectedGateway { get; init; }
    public bool PointsToGateway { get; init; }
    public bool UsesDhcp { get; init; }
}

/// <summary>
/// Configured DNS server information
/// </summary>
public class DnsServerConfig
{
    public required string ServerName { get; init; }
    public DnsStampInfo? StampInfo { get; init; }
    public DohProviderInfo? Provider { get; init; }
    public bool Enabled { get; init; }
    public bool IsCustom { get; init; }
}

/// <summary>
/// Summary of DNS security status for display
/// </summary>
public class DnsSecuritySummary
{
    public bool DohEnabled { get; init; }
    public List<string> DohProviders { get; init; } = new();
    public bool DnsLeakProtection { get; init; }
    public bool DotBlocked { get; init; }
    public bool DohBypassBlocked { get; init; }
    public bool FullyProtected { get; init; }
    public int IssueCount { get; init; }
    public int CriticalIssueCount { get; init; }

    // WAN DNS validation
    public List<string> WanDnsServers { get; init; } = new();
    public bool WanDnsMatchesDoH { get; init; }
    public string? WanDnsProvider { get; init; }
    public string? ExpectedDnsProvider { get; init; }

    // Device DNS validation
    public bool DeviceDnsPointsToGateway { get; init; }
    public int TotalDevicesChecked { get; init; }
    public int DevicesWithCorrectDns { get; init; }
    public int DhcpDeviceCount { get; init; }
}
