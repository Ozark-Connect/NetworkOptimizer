using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;
using static NetworkOptimizer.Core.Enums.DeviceTypeExtensions;

namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Analyzes DNS security configuration for DoH, firewall rules, and DNS leak prevention
/// </summary>
public class DnsSecurityAnalyzer
{
    private readonly ILogger<DnsSecurityAnalyzer> _logger;

    // UniFi settings keys
    private const string SettingsKeyDoh = "doh";
    private const string SettingsKeyDns = "dns";
    private const string SettingsKeyWanDns = "wan_dns";

    // DNS provider domain patterns for detecting DoH/DoQ block rules
    private static readonly string[] DnsProviderPatterns =
    [
        "dns",
        "doh",
        "cloudflare-dns",
        "quad9",
        "nextdns",
        "adguard",
        "opendns",
        "one.one.one"  // Cloudflare 1.1.1.1 alternate domain
    ];

    private readonly ThirdPartyDnsDetector _thirdPartyDetector;

    public DnsSecurityAnalyzer(ILogger<DnsSecurityAnalyzer> logger, ThirdPartyDnsDetector thirdPartyDetector)
    {
        _logger = logger;
        _thirdPartyDetector = thirdPartyDetector;
    }

    /// <summary>
    /// Analyze DNS security from settings and firewall policies
    /// </summary>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, JsonElement? firewallData)
        => AnalyzeAsync(settingsData, firewallData, switches: null, networks: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall policies, and device configuration
    /// </summary>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, JsonElement? firewallData, List<SwitchInfo>? switches, List<NetworkInfo>? networks)
        => AnalyzeAsync(settingsData, firewallData, switches, networks, deviceData: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall policies, device configuration, and raw device data
    /// </summary>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, JsonElement? firewallData, List<SwitchInfo>? switches, List<NetworkInfo>? networks, JsonElement? deviceData)
        => AnalyzeAsync(settingsData, firewallData, switches, networks, deviceData, customPiholePort: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall policies, device configuration, and raw device data
    /// </summary>
    /// <param name="customPiholePort">Optional custom port for Pi-hole management interface</param>
    public async Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, JsonElement? firewallData, List<SwitchInfo>? switches, List<NetworkInfo>? networks, JsonElement? deviceData, int? customPiholePort)
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

        // Analyze device DNS configuration - using raw device data to include APs
        if (deviceData.HasValue && networks != null)
        {
            AnalyzeAllDeviceDnsConfiguration(deviceData.Value, networks, result);
        }
        else if (switches != null && networks != null)
        {
            // Fallback to switches-only if no raw device data
            AnalyzeDeviceDnsConfiguration(switches, networks, result);
        }

        // Set gateway name for issue reporting
        if (switches != null)
        {
            result.GatewayName = switches.FirstOrDefault(s => s.IsGateway)?.Name;
        }

        // Detect third-party LAN DNS (Pi-hole, etc.)
        if (networks?.Any() == true)
        {
            await AnalyzeThirdPartyDnsAsync(networks, result, customPiholePort);
        }

        // Generate issues based on findings (includes async WAN DNS validation)
        await GenerateAuditIssuesAsync(result);

        _logger.LogDebug("DNS security analysis complete: DoH={DoHState}, Firewall rules found: DNS53={Dns53}, DoT={DoT}, DoH={DoHBlock}, DoQ={DoQBlock}, DeviceDns={DeviceDnsOk}, WanDns={WanDnsCount}",
            result.DohState, result.HasDns53BlockRule, result.HasDotBlockRule, result.HasDohBlockRule, result.HasDoqBlockRule, result.DeviceDnsPointsToGateway, result.WanDnsServers.Count);

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

            if (key == SettingsKeyDoh)
            {
                ParseDohSettings(setting, result);
            }
            else if (key == SettingsKeyDns || key == SettingsKeyWanDns)
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
                        _logger.LogDebug("DoH custom server: name={Name}, protocol={Protocol}, hostname={Hostname}, provider={Provider}",
                            serverName, decoded.ProtocolName, decoded.Hostname, decoded.ProviderInfo?.Name ?? "not identified");
                    }
                    else
                    {
                        // sdnsStamp is known non-null here due to the enclosing if check
                        var truncatedStamp = sdnsStamp.Length > 50 ? sdnsStamp[..50] + "..." : sdnsStamp;
                        _logger.LogWarning("Failed to decode SDNS stamp for server {Name}: {Stamp}", serverName, truncatedStamp);
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
    /// UniFi stores WAN DNS in port_table entries where network_name starts with "wan" (wan, wan2, etc.).
    /// </summary>
    private void ExtractWanDnsFromDevices(JsonElement deviceData, DnsSecurityResult result)
    {
        var wanInterfacesChecked = new List<string>();
        var wanInterfacesWithoutDns = new List<string>();

        foreach (var device in deviceData.UnwrapDataArray())
        {
            // Only check gateways/routers for WAN DNS
            var deviceType = device.GetStringOrNull("type");
            if (deviceType == null || !FromUniFiApiType(deviceType).IsGateway())
                continue;

            // Look in port_table for WAN ports (wan, wan2, etc.)
            if (!device.TryGetProperty("port_table", out var portTable) || portTable.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var port in portTable.EnumerateArray())
            {
                var networkName = port.GetStringOrNull("network_name");
                if (string.IsNullOrEmpty(networkName) || !networkName.StartsWith("wan", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Get additional port info for logging
                var portName = port.GetStringOrNull("name") ?? "unnamed";
                var portMedia = port.GetStringOrNull("media") ?? "unknown";
                var portUp = port.GetBoolOrDefault("up");
                var portIp = port.GetStringOrNull("ip");

                wanInterfacesChecked.Add(networkName);

                _logger.LogInformation("WAN interface detected: {Interface} (name={Name}, media={Media}, up={Up}, ip={Ip})",
                    networkName, portName, portMedia, portUp, portIp ?? "none");

                // Check for DNS servers on this WAN port
                var hasDnsProperty = port.TryGetProperty("dns", out var dnsArray);
                var dnsCount = hasDnsProperty && dnsArray.ValueKind == JsonValueKind.Array ? dnsArray.GetArrayLength() : 0;

                _logger.LogInformation("  DNS config: hasDnsProperty={HasDns}, arrayLength={Count}",
                    hasDnsProperty, dnsCount);

                // Create per-interface DNS record
                var interfaceDns = new WanInterfaceDns
                {
                    InterfaceName = networkName,
                    PortName = portName,
                    IpAddress = portIp,
                    IsUp = portUp,
                    DnsServers = new List<string>()
                };

                if (hasDnsProperty && dnsArray.ValueKind == JsonValueKind.Array && dnsCount > 0)
                {
                    foreach (var dns in dnsArray.EnumerateArray())
                    {
                        var dnsIp = dns.GetString();
                        if (!string.IsNullOrEmpty(dnsIp))
                        {
                            interfaceDns.DnsServers.Add(dnsIp);
                            if (!result.WanDnsServers.Contains(dnsIp))
                            {
                                result.WanDnsServers.Add(dnsIp);
                            }
                            _logger.LogInformation("  Found DNS server: {DnsIp}", dnsIp);
                        }
                    }
                }
                else
                {
                    // This WAN interface has no static DNS configured
                    wanInterfacesWithoutDns.Add(networkName);
                    _logger.LogInformation("  No static DNS configured on {Interface} - may use ISP DNS or DHCP", networkName);
                }

                result.WanInterfaces.Add(interfaceDns);
            }

            // Found gateway, stop checking other devices
            break;
        }

        if (result.WanDnsServers.Any())
        {
            _logger.LogDebug("Extracted {Count} WAN DNS servers from {InterfaceCount} interface(s): {Servers}",
                result.WanDnsServers.Count, wanInterfacesChecked.Count, string.Join(", ", result.WanDnsServers));
        }

        // Track interfaces without DNS for potential issue reporting
        if (wanInterfacesWithoutDns.Any())
        {
            result.UsingIspDns = true;
            _logger.LogDebug("WAN interfaces without static DNS: {Interfaces}", string.Join(", ", wanInterfacesWithoutDns));
        }

        if (!wanInterfacesChecked.Any())
        {
            _logger.LogDebug("No WAN interfaces found in device port_table");
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
            var enabled = policy.GetBoolOrDefault("enabled", true);
            var action = policy.GetStringOrNull("action")?.ToLowerInvariant() ?? "";
            var protocol = policy.GetStringOrNull("protocol")?.ToLowerInvariant() ?? "all";

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

            var isBlockAction = FirewallActionExtensions.Parse(action).IsBlockAction();

            // Check for DNS port 53 blocking - must include UDP (DNS is primarily UDP)
            // Valid protocols: udp, tcp_udp, all
            if (isBlockAction && IncludesPort(destPort, "53"))
            {
                if (IncludesUdp(protocol))
                {
                    result.HasDns53BlockRule = true;
                    result.Dns53RuleName = name;
                    _logger.LogDebug("Found DNS53 block rule: {Name} (protocol={Protocol})", name, protocol);
                }
                else
                {
                    _logger.LogDebug("Skipping DNS53 rule {Name}: protocol {Protocol} doesn't include UDP", name, protocol);
                }
            }

            // Check for DNS over TLS (port 853) blocking - must be TCP only
            // Valid protocols: tcp, tcp_udp, all
            if (isBlockAction && IncludesPort(destPort, "853"))
            {
                if (IncludesTcp(protocol))
                {
                    result.HasDotBlockRule = true;
                    result.DotRuleName = name;
                    _logger.LogDebug("Found DoT block rule: {Name} (protocol={Protocol})", name, protocol);
                }
                else
                {
                    _logger.LogDebug("Skipping DoT rule {Name}: protocol {Protocol} doesn't include TCP", name, protocol);
                }
            }

            // Check for DoH/DoQ blocking (port 443 with web domains containing DNS providers)
            // DoH = TCP 443, DoQ = UDP 443 (QUIC)
            // Rules can block either or both depending on protocol setting
            if (isBlockAction && IncludesPort(destPort, "443") && matchingTarget == "WEB" && webDomains?.Count > 0)
            {
                // Check if web domains include DNS providers
                var dnsProviderDomains = webDomains.Where(d =>
                    DnsProviderPatterns.Any(pattern => d.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (dnsProviderDomains.Count > 0)
                {
                    // DoH blocking requires TCP (tcp, tcp_udp, all)
                    if (IncludesTcp(protocol))
                    {
                        result.HasDohBlockRule = true;
                        foreach (var domain in dnsProviderDomains)
                        {
                            if (!result.DohBlockedDomains.Contains(domain))
                                result.DohBlockedDomains.Add(domain);
                        }
                        result.DohRuleName = name;
                        _logger.LogDebug("Found DoH block rule: {Name} (protocol={Protocol}) with {Count} DNS domains",
                            name, protocol, dnsProviderDomains.Count);
                    }

                    // DoQ blocking requires UDP (udp, tcp_udp, all) - QUIC runs over UDP
                    if (IncludesUdp(protocol))
                    {
                        result.HasDoqBlockRule = true;
                        foreach (var domain in dnsProviderDomains)
                        {
                            if (!result.DoqBlockedDomains.Contains(domain))
                                result.DoqBlockedDomains.Add(domain);
                        }
                        result.DoqRuleName = name;
                        _logger.LogDebug("Found DoQ block rule: {Name} (protocol={Protocol}) with {Count} DNS domains",
                            name, protocol, dnsProviderDomains.Count);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Check if protocol includes UDP (udp, tcp_udp, all)
    /// </summary>
    private static bool IncludesUdp(string? protocol)
    {
        if (string.IsNullOrEmpty(protocol))
            return true; // Default "all" includes UDP

        return protocol is "udp" or "tcp_udp" or "all";
    }

    /// <summary>
    /// Check if protocol includes TCP (tcp, tcp_udp, all)
    /// </summary>
    private static bool IncludesTcp(string? protocol)
    {
        if (string.IsNullOrEmpty(protocol))
            return true; // Default "all" includes TCP

        return protocol is "tcp" or "tcp_udp" or "all";
    }

    /// <summary>
    /// Check if a port specification includes a specific port.
    /// Handles comma-separated lists (e.g., "53,853") and single ports.
    /// </summary>
    private static bool IncludesPort(string? portSpec, string port)
    {
        if (string.IsNullOrEmpty(portSpec))
            return false;

        // Split by comma and check each port in the list
        var ports = portSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ports.Any(p => p == port);
    }

    private static string GetCorrectDnsOrder(List<string> servers, List<string?> ptrResults)
    {
        // Pair IPs with their PTR results and sort by dns1 first, dns2 second
        var paired = servers.Zip(ptrResults, (ip, ptr) => (Ip: ip, Ptr: ptr ?? "")).ToList();

        // Sort: dns1 should come before dns2
        var sorted = paired
            .OrderBy(p => p.Ptr.Contains("dns2", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(p => p.Ip)
            .ToList();

        return string.Join(", ", sorted);
    }


    private async Task GenerateAuditIssuesAsync(DnsSecurityResult result)
    {
        // Issue: DoH not configured
        if (!result.DohConfigured)
        {
            if (result.HasThirdPartyDns)
            {
                var dnsServerIps = result.ThirdPartyDnsServers.Select(t => t.DnsServerIp).Distinct().ToList();
                var networkNames = result.ThirdPartyDnsServers.Select(t => t.NetworkName).Distinct().ToList();

                // Known providers (Pi-hole, AdGuard) are trusted - neutral score impact
                // Unknown third-party DNS servers get a minor penalty since we can't verify their filtering
                var isKnownProvider = result.IsPiholeDetected; // Add AdGuard detection in the future
                var scoreImpact = isKnownProvider ? 0 : 3; // Minor penalty for unknown providers
                var severity = isKnownProvider ? AuditSeverity.Informational : AuditSeverity.Recommended;
                var recommendedAction = isKnownProvider
                    ? "Verify third-party DNS provides adequate security and filtering. Consider enabling DNS firewall rules to prevent bypass."
                    : "Consider using a known DNS filtering solution like Pi-hole or AdGuard Home, or configure DoH with a trusted provider.";

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsThirdPartyDetected,
                    Severity = severity,
                    DeviceName = result.GatewayName,
                    Message = $"{result.ThirdPartyDnsProviderName} detected handling DNS queries. Networks using third-party DNS: {string.Join(", ", networkNames)}. DNS server(s): {string.Join(", ", dnsServerIps)}.",
                    RecommendedAction = recommendedAction,
                    RuleId = "DNS-3RDPARTY-001",
                    ScoreImpact = scoreImpact,
                    Metadata = new Dictionary<string, object>
                    {
                        { "third_party_dns_ips", dnsServerIps },
                        { "is_pihole", result.IsPiholeDetected },
                        { "is_known_provider", isKnownProvider },
                        { "affected_networks", networkNames },
                        { "provider_name", result.ThirdPartyDnsProviderName ?? "Third-Party LAN DNS" },
                        { "configurable_setting", "Configure Pi-hole HTTP management port in Settings if detection fails" }
                    }
                });

                // Add hardening note only for known providers
                if (isKnownProvider)
                {
                    result.HardeningNotes.Add($"{result.ThirdPartyDnsProviderName} configured as DNS resolver on {networkNames.Count} network(s)");
                }
            }
            else
            {
                // No DoH and no third-party DNS - flag as needing attention
                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsUnknownConfig,
                    Severity = AuditSeverity.Informational,
                    DeviceName = result.GatewayName,
                    Message = "Unable to determine DNS security solution. No DoH configured and no third-party LAN DNS detected.",
                    RecommendedAction = "Configure DoH in Network Settings or deploy a DNS security solution like Pi-hole",
                    RuleId = "DNS-UNKNOWN-001",
                    ScoreImpact = 2
                });

                // Also add the standard DoH recommendation
                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsNoDoh,
                    Severity = AuditSeverity.Recommended,
                    DeviceName = result.GatewayName,
                    Message = "DNS-over-HTTPS (DoH) is not configured. Network traffic uses unencrypted DNS which can be monitored or manipulated.",
                    RecommendedAction = "Configure DoH in Network Settings with a trusted provider like NextDNS or Cloudflare",
                    RuleId = "DNS-DOH-001",
                    ScoreImpact = 8
                });
            }
        }
        else if (result.DohState == "auto")
        {
            // DoH is auto-negotiated, may fall back to unencrypted
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsDohAuto,
                Severity = AuditSeverity.Informational,
                DeviceName = result.GatewayName,
                Message = "DoH is set to 'auto' mode which may fall back to unencrypted DNS. Consider setting to 'custom' for guaranteed encryption.",
                RecommendedAction = "Configure DoH with explicit custom servers for guaranteed encryption",
                RuleId = "DNS-DOH-002",
                ScoreImpact = 3
            });
        }

        // Validate WAN DNS against DoH provider (uses PTR lookup)
        await ValidateWanDnsConfigurationAsync(result);

        // Issue: No DNS port 53 blocking (DNS leak prevention)
        if (!result.HasDns53BlockRule)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsNo53Block,
                Severity = AuditSeverity.Critical,
                DeviceName = result.GatewayName,
                Message = "No firewall rule blocks external DNS (port 53). Devices can bypass network DNS settings and leak queries to untrusted servers.",
                RecommendedAction = "Create firewall rule: Block outbound UDP port 53 to Internet for all VLANs (except gateway)",
                RuleId = "DNS-LEAK-001",
                ScoreImpact = 12
            });
        }

        // Issue: No DoT (853) blocking
        if (!result.HasDotBlockRule)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsNoDotBlock,
                Severity = AuditSeverity.Recommended,
                DeviceName = result.GatewayName,
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
                Type = IssueTypes.DnsNoDohBlock,
                Severity = AuditSeverity.Recommended,
                DeviceName = result.GatewayName,
                Message = "No firewall rule blocks public DoH providers. Devices can bypass your DNS filtering by using their own DoH servers.",
                RecommendedAction = "Create firewall rule: Block TCP 443 to known DoH provider domains",
                RuleId = "DNS-LEAK-003",
                ScoreImpact = 5,
                Metadata = new Dictionary<string, object>
                {
                    { "suggested_domains", "dns.google, cloudflare-dns.com, dns.quad9.net, doh.opendns.com" }
                }
            });
        }

        // Issue: No DoQ (DNS over QUIC) bypass blocking
        if (!result.HasDoqBlockRule && result.DohConfigured)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsNoDoqBlock,
                Severity = AuditSeverity.Recommended,
                DeviceName = result.GatewayName,
                Message = "No firewall rule blocks DNS over QUIC (DoQ). Devices can bypass your DNS filtering using QUIC-based DNS.",
                RecommendedAction = "Create firewall rule: Block UDP 443 to known DoQ provider domains",
                RuleId = "DNS-LEAK-004",
                ScoreImpact = 4,
                Metadata = new Dictionary<string, object>
                {
                    { "suggested_domains", "dns.google, cloudflare-dns.com, dns.quad9.net, dns.adguard.com" }
                }
            });
        }

        // Issue: Using ISP DNS
        if (result.UsingIspDns && !result.DohConfigured)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsIsp,
                Severity = AuditSeverity.Informational,
                DeviceName = result.GatewayName,
                Message = "Network is using ISP-provided DNS servers. This may expose browsing history to your ISP and lacks filtering capabilities.",
                RecommendedAction = "Configure custom DNS servers or enable DoH with a privacy-focused provider",
                RuleId = "DNS-ISP-001",
                ScoreImpact = 4
            });
        }

        // Positive: All protections in place
        if (result.DohConfigured && result.HasDns53BlockRule && result.HasDotBlockRule && result.HasDohBlockRule && result.HasDoqBlockRule)
        {
            result.HardeningNotes.Add("DNS leak prevention fully configured with DoH and firewall blocking (DNS53, DoT, DoH, DoQ)");
        }
        else if (result.DohConfigured && result.HasDns53BlockRule && result.HasDotBlockRule && result.HasDohBlockRule)
        {
            result.HardeningNotes.Add("DNS leak prevention configured with DoH and firewall blocking (DNS53, DoT, DoH)");
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

    private async Task ValidateWanDnsConfigurationAsync(DnsSecurityResult result)
    {
        if (!result.DohConfigured || result.WanDnsServers.Count == 0)
            return;

        var expectedProvider = await IdentifyExpectedDnsProviderAsync(result);
        if (expectedProvider == null)
        {
            _logger.LogDebug("Could not identify DoH provider for WAN DNS validation");
            return;
        }

        result.ExpectedDnsProvider = expectedProvider.Name;

        var validationResults = await ValidateAllWanInterfacesAsync(result, expectedProvider);

        result.WanDnsMatchesDoH = validationResults.CorrectInterfaces.Any() &&
                                  !validationResults.MismatchedInterfaces.Any() &&
                                  !validationResults.NoStaticDnsInterfaces.Any();

        if (result.WanDnsMatchesDoH)
            result.HardeningNotes.Add($"WAN DNS correctly configured for {expectedProvider.Name}");

        AddDnsMismatchIssues(result, expectedProvider, validationResults.MismatchedInterfaces);
        AddDnsOrderIssues(result);
        AddNoStaticDnsIssues(result, validationResults.NoStaticDnsInterfaces);
    }

    private async Task<DohProviderInfo?> IdentifyExpectedDnsProviderAsync(DnsSecurityResult result)
    {
        var primaryServer = result.ConfiguredServers.FirstOrDefault(s => s.Enabled);
        if (primaryServer == null)
            return null;

        // Try multiple sources to identify the provider
        var provider = primaryServer.StampInfo?.ProviderInfo ?? primaryServer.Provider;

        if (provider == null)
            provider = DohProviderRegistry.IdentifyProviderFromName(primaryServer.ServerName);

        if (provider == null && primaryServer.StampInfo?.Hostname != null)
            provider = DohProviderRegistry.IdentifyProvider(primaryServer.StampInfo.Hostname);

        if (provider == null && result.WanDnsServers.Any())
        {
            // Last resort: identify from WAN DNS IPs
            foreach (var wanDns in result.WanDnsServers)
            {
                var (wanProvider, _) = await DohProviderRegistry.IdentifyProviderFromIpWithPtrAsync(wanDns);
                if (wanProvider != null)
                {
                    _logger.LogInformation("Identified DoH provider from WAN DNS IP {Ip}: {Provider}", wanDns, wanProvider.Name);
                    return wanProvider;
                }
            }
        }

        return provider;
    }

    private record WanValidationResults(
        List<string> CorrectInterfaces,
        List<(string Interface, string? PortName, List<string> Servers)> MismatchedInterfaces,
        List<string> NoStaticDnsInterfaces);

    private async Task<WanValidationResults> ValidateAllWanInterfacesAsync(DnsSecurityResult result, DohProviderInfo expectedProvider)
    {
        var correctInterfaces = new List<string>();
        var mismatchedInterfaces = new List<(string Interface, string? PortName, List<string> Servers)>();
        var noStaticDnsInterfaces = new List<string>();

        foreach (var wanInterface in result.WanInterfaces)
        {
            if (!wanInterface.HasStaticDns)
            {
                noStaticDnsInterfaces.Add(wanInterface.InterfaceName);
                continue;
            }

            var mismatchedServers = await ValidateSingleWanInterfaceAsync(result, wanInterface, expectedProvider);

            if (wanInterface.MatchesDoH)
                correctInterfaces.Add(wanInterface.InterfaceName);
            else if (mismatchedServers.Any())
                mismatchedInterfaces.Add((wanInterface.InterfaceName, wanInterface.PortName, mismatchedServers));
        }

        return new WanValidationResults(correctInterfaces, mismatchedInterfaces, noStaticDnsInterfaces);
    }

    private async Task<List<string>> ValidateSingleWanInterfaceAsync(
        DnsSecurityResult result,
        WanInterfaceDns wanDns,
        DohProviderInfo expectedProvider)
    {
        var matchingServers = new List<string>();
        var mismatchedServers = new List<string>();
        var ptrResults = new List<string?>();

        foreach (var dnsServer in wanDns.DnsServers)
        {
            var (wanProvider, reverseDns) = await DohProviderRegistry.IdentifyProviderFromIpWithPtrAsync(dnsServer);
            ptrResults.Add(reverseDns);
            wanDns.DetectedProvider = wanProvider?.Name;

            if (wanProvider != null)
            {
                result.WanDnsProvider = wanProvider.Name;
                if (wanProvider.Name == expectedProvider.Name)
                {
                    matchingServers.Add(dnsServer);
                    if (!string.IsNullOrEmpty(reverseDns))
                        _logger.LogDebug("WAN DNS {Ip} verified as {Provider} via PTR: {ReverseDns}", dnsServer, wanProvider.Name, reverseDns);
                }
                else
                {
                    mismatchedServers.Add($"{dnsServer} ({wanProvider.Name})");
                }
            }
            else
            {
                var unknownLabel = !string.IsNullOrEmpty(reverseDns) ? reverseDns : "Unknown";
                mismatchedServers.Add($"{dnsServer} ({unknownLabel})");
            }
        }

        wanDns.ReverseDnsResults = ptrResults;
        wanDns.MatchesDoH = matchingServers.Count > 0 && mismatchedServers.Count == 0;

        // For NextDNS, verify correct ordering (dns1 before dns2)
        if (wanDns.MatchesDoH && expectedProvider.Name == "NextDNS" && ptrResults.Count >= 2)
            CheckNextDnsOrdering(wanDns, ptrResults);

        return mismatchedServers;
    }

    private void CheckNextDnsOrdering(WanInterfaceDns wanDns, List<string?> ptrResults)
    {
        var first = ptrResults[0]?.ToLowerInvariant() ?? "";
        var second = ptrResults[1]?.ToLowerInvariant() ?? "";

        if (first.Contains("dns2.") && second.Contains("dns1."))
        {
            wanDns.OrderCorrect = false;
            _logger.LogWarning("NextDNS WAN DNS servers are in reverse order: {First}, {Second}", ptrResults[0], ptrResults[1]);
        }
        else if (first.Contains("dns1.") && second.Contains("dns2."))
        {
            _logger.LogDebug("NextDNS WAN DNS servers are correctly ordered: {First}, {Second}", ptrResults[0], ptrResults[1]);
        }
    }

    private void AddDnsMismatchIssues(
        DnsSecurityResult result,
        DohProviderInfo expectedProvider,
        List<(string Interface, string? PortName, List<string> Servers)> mismatchedInterfaces)
    {
        foreach (var (interfaceName, portName, mismatchedServers) in mismatchedInterfaces)
        {
            var displayName = NetworkFormatHelpers.FormatWanInterfaceName(interfaceName, portName);
            var expectedIps = expectedProvider.DnsIps.Where(ip => !ip.EndsWith('.')).Take(2).ToList();
            var expectedIpsStr = expectedIps.Any() ? string.Join(", ", expectedIps) : "";
            var recommendation = expectedIps.Any()
                ? $"Set DNS to {expectedProvider.Name} servers: {expectedIpsStr}"
                : $"Set DNS to {expectedProvider.Name} servers";

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsWanMismatch,
                Severity = AuditSeverity.Recommended,
                Message = $"{displayName} uses {string.Join(", ", mismatchedServers)} instead of {expectedProvider.Name}",
                RecommendedAction = recommendation,
                DeviceName = result.GatewayName,
                Port = NetworkFormatHelpers.FormatWanInterfaceName(interfaceName, null),
                PortName = portName,
                RuleId = "DNS-WAN-001",
                ScoreImpact = 4,
                Metadata = new Dictionary<string, object>
                {
                    { "interface", interfaceName },
                    { "port_name", portName ?? "" },
                    { "expected_provider", expectedProvider.Name },
                    { "expected_ips", expectedProvider.DnsIps },
                    { "actual_servers", mismatchedServers }
                }
            });
        }
    }

    private void AddDnsOrderIssues(DnsSecurityResult result)
    {
        foreach (var wanInterface in result.WanInterfaces.Where(w => w.MatchesDoH && !w.OrderCorrect))
        {
            var displayName = NetworkFormatHelpers.FormatWanInterfaceName(wanInterface.InterfaceName, wanInterface.PortName);
            var ips = string.Join(", ", wanInterface.DnsServers);
            var correctOrder = GetCorrectDnsOrder(wanInterface.DnsServers, wanInterface.ReverseDnsResults);

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsWanOrder,
                Severity = AuditSeverity.Recommended,
                Message = $"{displayName} DNS in wrong order: {ips}. Should be {correctOrder}",
                RecommendedAction = $"Swap DNS order to {correctOrder}",
                DeviceName = result.GatewayName,
                Port = NetworkFormatHelpers.FormatWanInterfaceName(wanInterface.InterfaceName, null),
                PortName = wanInterface.PortName,
                RuleId = "DNS-WAN-002",
                ScoreImpact = 2,
                Metadata = new Dictionary<string, object>
                {
                    { "interface", wanInterface.InterfaceName },
                    { "port_name", wanInterface.PortName ?? "" },
                    { "dns_servers", wanInterface.DnsServers }
                }
            });
        }
    }

    private void AddNoStaticDnsIssues(DnsSecurityResult result, List<string> interfacesWithNoDns)
    {
        if (!result.DohConfigured || !interfacesWithNoDns.Any())
            return;

        var providerName = result.ExpectedDnsProvider ?? "your DoH provider";
        var expectedIps = result.ConfiguredServers
            .Where(s => s.Enabled)
            .SelectMany(s => (s.StampInfo?.ProviderInfo?.DnsIps ?? s.Provider?.DnsIps)?.ToList() ?? new List<string>())
            .Take(2)
            .ToList();

        foreach (var interfaceName in interfacesWithNoDns)
        {
            var wanInterface = result.WanInterfaces.FirstOrDefault(w => w.InterfaceName == interfaceName);
            var displayName = NetworkFormatHelpers.FormatWanInterfaceName(interfaceName, wanInterface?.PortName);

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsWanNoStatic,
                Severity = AuditSeverity.Recommended,
                Message = $"WAN interface '{displayName}' has no static DNS configured. If DoH fails, DNS queries will leak to your ISP's DNS servers.",
                RecommendedAction = $"Configure static DNS on {displayName} to use {providerName} servers",
                DeviceName = result.GatewayName,
                Port = NetworkFormatHelpers.FormatWanInterfaceName(interfaceName, null),
                PortName = wanInterface?.PortName,
                RuleId = "DNS-WAN-002",
                ScoreImpact = 3,
                Metadata = new Dictionary<string, object>
                {
                    { "interface", interfaceName },
                    { "port_name", wanInterface?.PortName ?? "" },
                    { "ip_address", wanInterface?.IpAddress ?? "" }
                }
            });

            _logger.LogInformation("WAN interface '{Interface}' has no static DNS - using ISP DNS", displayName);
        }
    }

    private void AnalyzeDeviceDnsConfiguration(List<SwitchInfo> switches, List<NetworkInfo> networks, DnsSecurityResult result)
    {
        // Find the gateway device from switches list
        var gateway = switches.FirstOrDefault(s => s.IsGateway);
        if (gateway == null)
        {
            _logger.LogDebug("No gateway found for device DNS validation");
            return;
        }

        // Find management network (usually VLAN 1 or labeled management)
        var managementNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Management)
            ?? networks.FirstOrDefault(n => n.IsNative);

        // Use the internal gateway IP from the management network, not the WAN IP
        var expectedGatewayIp = managementNetwork?.Gateway
            ?? networks.FirstOrDefault(n => !string.IsNullOrEmpty(n.Gateway))?.Gateway;

        if (string.IsNullOrEmpty(expectedGatewayIp))
        {
            _logger.LogDebug("Could not determine expected internal gateway IP for device DNS validation");
            return;
        }

        _logger.LogDebug("Using internal gateway IP {GatewayIp} for device DNS validation", expectedGatewayIp);

        // Get all non-gateway devices from switches list
        // Note: This list includes switches but may not include APs (which don't have port_table)
        // For comprehensive DNS checking, we also need to analyze raw device data
        var allDevices = switches.Where(s => !s.IsGateway).ToList();

        _logger.LogDebug("Device DNS validation: {DeviceCount} non-gateway switches/routers found", allDevices.Count);

        // Separate devices by network config type
        var devicesWithStaticDns = allDevices.Where(s => !string.IsNullOrEmpty(s.ConfiguredDns1)).ToList();
        var devicesWithDhcp = allDevices.Where(s =>
            string.IsNullOrEmpty(s.ConfiguredDns1) &&
            (s.NetworkConfigType == "dhcp" || string.IsNullOrEmpty(s.NetworkConfigType))).ToList();

        _logger.LogDebug("Device DNS: {StaticCount} with static DNS, {DhcpCount} with DHCP",
            devicesWithStaticDns.Count, devicesWithDhcp.Count);

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

            if (result.TotalDevicesChecked > 0 && !result.DeviceDnsPointsToGateway)
            {
                var misconfigured = result.TotalDevicesChecked - result.DevicesWithCorrectDns;
                var deviceNames = result.DeviceDnsDetails
                    .Where(d => !d.PointsToGateway && !d.UsesDhcp)
                    .Select(d => d.DeviceName)
                    .ToList();

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsDeviceMisconfigured,
                    Severity = AuditSeverity.Informational,
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
    }

    /// <summary>
    /// Analyze DNS configuration for ALL devices (switches and APs) from raw device data.
    /// This includes APs which are not in the switches list.
    /// </summary>
    private void AnalyzeAllDeviceDnsConfiguration(JsonElement deviceData, List<NetworkInfo> networks, DnsSecurityResult result)
    {
        // Find management network to get expected gateway IP
        var managementNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Management)
            ?? networks.FirstOrDefault(n => n.IsNative);

        var expectedGatewayIp = managementNetwork?.Gateway
            ?? networks.FirstOrDefault(n => !string.IsNullOrEmpty(n.Gateway))?.Gateway;

        if (string.IsNullOrEmpty(expectedGatewayIp))
        {
            _logger.LogDebug("Could not determine expected internal gateway IP for device DNS validation");
            return;
        }

        _logger.LogDebug("Using internal gateway IP {GatewayIp} for device DNS validation", expectedGatewayIp);

        // Process ALL devices from raw device data
        foreach (var device in deviceData.UnwrapDataArray())
        {
            var deviceType = device.GetStringOrNull("type");
            var name = device.GetStringFromAny("name", "mac") ?? "Unknown";
            var ip = device.GetStringOrNull("ip");

            // Skip gateways - they're not expected to point to themselves
            if (FromUniFiApiType(deviceType).IsGateway())
                continue;

            // Get DNS configuration from config_network
            string? dns1 = null;
            string? networkConfigType = null;
            if (device.TryGetProperty("config_network", out var configNetwork))
            {
                dns1 = configNetwork.GetStringOrNull("dns1");
                networkConfigType = configNetwork.GetStringOrNull("type"); // "dhcp" or "static"
            }

            if (!string.IsNullOrEmpty(dns1))
            {
                // Device has static DNS configured
                var pointsToGateway = dns1 == expectedGatewayIp;
                result.TotalDevicesChecked++;

                result.DeviceDnsDetails.Add(new DeviceDnsInfo
                {
                    DeviceName = name,
                    DeviceType = deviceType ?? "unknown",
                    DeviceIp = ip,
                    ConfiguredDns = dns1,
                    ExpectedGateway = expectedGatewayIp,
                    PointsToGateway = pointsToGateway,
                    UsesDhcp = false
                });

                if (pointsToGateway)
                {
                    result.DevicesWithCorrectDns++;
                }
            }
            else if (networkConfigType == "dhcp" || string.IsNullOrEmpty(networkConfigType))
            {
                // Device uses DHCP - DNS comes from DHCP server (gateway)
                result.DhcpDeviceCount++;

                result.DeviceDnsDetails.Add(new DeviceDnsInfo
                {
                    DeviceName = name,
                    DeviceType = deviceType ?? "unknown",
                    DeviceIp = ip,
                    ConfiguredDns = null,
                    ExpectedGateway = expectedGatewayIp,
                    PointsToGateway = true, // Assumed correct via DHCP
                    UsesDhcp = true
                });
            }
        }

        _logger.LogDebug("Device DNS check: {StaticCount} static, {DhcpCount} DHCP, {CorrectCount} correct",
            result.TotalDevicesChecked, result.DhcpDeviceCount, result.DevicesWithCorrectDns);

        result.DeviceDnsPointsToGateway = result.DevicesWithCorrectDns == result.TotalDevicesChecked;

        // Generate summary notes and issues
        if (result.TotalDevicesChecked > 0 || result.DhcpDeviceCount > 0)
        {
            var summaryParts = new List<string>();

            if (result.TotalDevicesChecked > 0 && !result.DeviceDnsPointsToGateway)
            {
                var misconfigured = result.TotalDevicesChecked - result.DevicesWithCorrectDns;
                var deviceNames = result.DeviceDnsDetails
                    .Where(d => !d.PointsToGateway && !d.UsesDhcp)
                    .Select(d => d.DeviceName)
                    .ToList();

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsDeviceMisconfigured,
                    Severity = AuditSeverity.Informational,
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
    }

    /// <summary>
    /// Detect third-party LAN DNS servers (like Pi-hole) across networks
    /// </summary>
    private async Task AnalyzeThirdPartyDnsAsync(List<NetworkInfo> networks, DnsSecurityResult result, int? customPiholePort = null)
    {
        var thirdPartyResults = await _thirdPartyDetector.DetectThirdPartyDnsAsync(networks, customPiholePort);

        if (thirdPartyResults.Any())
        {
            result.HasThirdPartyDns = true;
            result.ThirdPartyDnsServers.AddRange(thirdPartyResults);

            // Determine provider name (Pi-hole takes precedence)
            if (thirdPartyResults.Any(t => t.IsPihole))
            {
                result.ThirdPartyDnsProviderName = "Pi-hole";
                _logger.LogInformation("Pi-hole detected as third-party DNS on {Count} network(s)",
                    thirdPartyResults.Count(t => t.IsPihole));
            }
            else
            {
                result.ThirdPartyDnsProviderName = "Third-Party LAN DNS";
                _logger.LogInformation("Third-party LAN DNS detected on {Count} network(s)",
                    thirdPartyResults.Count);
            }

            // Check for DNS consistency across all DHCP-enabled networks
            CheckDnsConsistencyAcrossNetworks(networks, thirdPartyResults, result);
        }
    }

    /// <summary>
    /// Check if all DHCP-enabled networks use the same third-party DNS server.
    /// If a third-party DNS (like Pi-hole) is configured on some networks but not all,
    /// this creates a security gap where DNS filtering can be bypassed.
    /// </summary>
    private void CheckDnsConsistencyAcrossNetworks(
        List<NetworkInfo> networks,
        List<ThirdPartyDnsDetector.ThirdPartyDnsInfo> thirdPartyResults,
        DnsSecurityResult result)
    {
        // Get the unique third-party DNS IPs that were detected
        var thirdPartyDnsIps = thirdPartyResults
            .Select(r => r.DnsServerIp)
            .Distinct()
            .ToHashSet();

        // Get all DHCP-enabled networks
        var dhcpNetworks = networks.Where(n => n.DhcpEnabled).ToList();

        if (dhcpNetworks.Count == 0)
        {
            _logger.LogDebug("No DHCP-enabled networks found, skipping DNS consistency check");
            return;
        }

        // Get the networks where third-party DNS was detected
        var networksWithThirdPartyDns = thirdPartyResults
            .Select(r => r.NetworkName)
            .Distinct()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Get DHCP networks that are NOT using the third-party DNS
        var networksWithoutThirdPartyDns = dhcpNetworks
            .Where(n => !networksWithThirdPartyDns.Contains(n.Name))
            .ToList();

        if (networksWithoutThirdPartyDns.Any())
        {
            var providerName = result.ThirdPartyDnsProviderName ?? "Third-Party DNS";
            var missingNetworkNames = networksWithoutThirdPartyDns.Select(n => n.Name).ToList();
            var configuredNetworkNames = networksWithThirdPartyDns.ToList();
            var dnsServerIps = string.Join(", ", thirdPartyDnsIps);

            _logger.LogWarning(
                "DNS consistency issue: {ProviderName} ({DnsIps}) configured on {ConfiguredCount} networks but missing on {MissingCount} DHCP-enabled networks: {MissingNetworks}",
                providerName, dnsServerIps, configuredNetworkNames.Count, missingNetworkNames.Count, string.Join(", ", missingNetworkNames));

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsInconsistentConfig,
                Severity = AuditSeverity.Critical,
                DeviceName = result.GatewayName,
                Message = $"{providerName} is configured on {configuredNetworkNames.Count} network(s) but {missingNetworkNames.Count} DHCP-enabled network(s) are not using it: {string.Join(", ", missingNetworkNames)}. Devices on these networks can bypass DNS filtering.",
                RecommendedAction = $"Configure all DHCP-enabled networks to use {providerName} ({dnsServerIps}) for consistent DNS filtering",
                RuleId = "DNS-CONSISTENCY-001",
                ScoreImpact = 15,
                Metadata = new Dictionary<string, object>
                {
                    { "third_party_dns_ips", thirdPartyDnsIps.ToList() },
                    { "configured_networks", configuredNetworkNames },
                    { "missing_networks", missingNetworkNames },
                    { "provider_name", providerName }
                }
            });
        }
        else
        {
            _logger.LogInformation(
                "DNS consistency check passed: All {Count} DHCP-enabled networks use {ProviderName}",
                dhcpNetworks.Count, result.ThirdPartyDnsProviderName);
        }
    }

    /// <summary>
    /// Get a summary of DNS security status
    /// </summary>
    public DnsSecuritySummary GetSummary(DnsSecurityResult result)
    {
        var providerNames = result.ConfiguredServers
            .Where(s => s.Enabled)
            .Select(s => s.StampInfo?.ProviderInfo?.Name
                ?? s.Provider?.Name
                ?? DohProviderRegistry.IdentifyProviderFromName(s.ServerName)?.Name
                ?? s.ServerName)
            .Distinct()
            .ToList();

        return new DnsSecuritySummary
        {
            DohEnabled = result.DohConfigured,
            DohProviders = providerNames,
            DnsLeakProtection = result.HasDns53BlockRule,
            DotBlocked = result.HasDotBlockRule,
            DohBypassBlocked = result.HasDohBlockRule,
            DoqBypassBlocked = result.HasDoqBlockRule,
            FullyProtected = result.DohConfigured && result.HasDns53BlockRule && result.HasDotBlockRule && result.HasDohBlockRule && result.HasDoqBlockRule && result.WanDnsMatchesDoH && result.DeviceDnsPointsToGateway,
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

    // Gateway Info
    public string? GatewayName { get; set; }

    // WAN DNS Configuration
    public List<string> WanDnsServers { get; } = new();
    public List<WanInterfaceDns> WanInterfaces { get; } = new();
    public bool UsingIspDns { get; set; }
    public bool WanDnsMatchesDoH { get; set; }
    public bool WanDnsOrderCorrect => WanInterfaces.All(w => w.OrderCorrect);
    public List<string?> WanDnsPtrResults => WanInterfaces.SelectMany(w => w.ReverseDnsResults).ToList();
    public string? WanDnsProvider { get; set; }
    public string? ExpectedDnsProvider { get; set; }

    // Firewall Rules
    public bool HasDns53BlockRule { get; set; }
    public string? Dns53RuleName { get; set; }
    public bool HasDotBlockRule { get; set; }
    public string? DotRuleName { get; set; }
    public bool HasDohBlockRule { get; set; }
    public string? DohRuleName { get; set; }
    public bool HasDoqBlockRule { get; set; }
    public string? DoqRuleName { get; set; }
    public List<string> DohBlockedDomains { get; } = new();
    public List<string> DoqBlockedDomains { get; } = new();

    // Device DNS Configuration
    public bool DeviceDnsPointsToGateway { get; set; } = true;
    public int TotalDevicesChecked { get; set; }
    public int DevicesWithCorrectDns { get; set; }
    public int DhcpDeviceCount { get; set; }
    public List<DeviceDnsInfo> DeviceDnsDetails { get; } = new();

    // Third-Party DNS (Pi-hole, etc.)
    public bool HasThirdPartyDns { get; set; }
    public List<ThirdPartyDnsDetector.ThirdPartyDnsInfo> ThirdPartyDnsServers { get; } = new();
    public bool IsPiholeDetected => ThirdPartyDnsServers.Any(t => t.IsPihole);
    public string? ThirdPartyDnsProviderName { get; set; }

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
/// WAN interface DNS configuration details
/// </summary>
public class WanInterfaceDns
{
    public required string InterfaceName { get; init; }
    public string? PortName { get; init; }
    public string? IpAddress { get; init; }
    public bool IsUp { get; init; }
    public List<string> DnsServers { get; init; } = new();
    public bool HasStaticDns => DnsServers.Any();
    public bool MatchesDoH { get; set; }
    public bool OrderCorrect { get; set; } = true;
    public string? DetectedProvider { get; set; }
    /// <summary>
    /// PTR lookup results for each DNS server IP, in order
    /// </summary>
    public List<string?> ReverseDnsResults { get; set; } = new();
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
    public bool DoqBypassBlocked { get; init; }
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
