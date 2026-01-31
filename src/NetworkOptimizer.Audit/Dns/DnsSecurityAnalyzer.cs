using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;
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
    /// Analyze DNS security from settings and firewall rules
    /// </summary>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, List<FirewallRule>? firewallRules)
        => AnalyzeAsync(settingsData, firewallRules, switches: null, networks: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall rules, and device configuration
    /// </summary>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, List<FirewallRule>? firewallRules, List<SwitchInfo>? switches, List<NetworkInfo>? networks)
        => AnalyzeAsync(settingsData, firewallRules, switches, networks, deviceData: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall rules, device configuration, and raw device data
    /// </summary>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, List<FirewallRule>? firewallRules, List<SwitchInfo>? switches, List<NetworkInfo>? networks, JsonElement? deviceData)
        => AnalyzeAsync(settingsData, firewallRules, switches, networks, deviceData, customDnsManagementPort: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall rules, device configuration, and raw device data
    /// </summary>
    /// <param name="customDnsManagementPort">Optional custom port for third-party DNS management interface (Pi-hole, AdGuard Home, etc.)</param>
    public Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, List<FirewallRule>? firewallRules, List<SwitchInfo>? switches, List<NetworkInfo>? networks, JsonElement? deviceData, int? customDnsManagementPort)
        => AnalyzeAsync(settingsData, firewallRules, switches, networks, deviceData, customDnsManagementPort, natRulesData: null);

    /// <summary>
    /// Analyze DNS security from settings, firewall rules, device configuration, raw device data, and NAT rules
    /// </summary>
    /// <param name="customDnsManagementPort">Optional custom port for third-party DNS management interface (Pi-hole, AdGuard Home, etc.)</param>
    /// <param name="natRulesData">Optional NAT rules data for DNAT DNS detection</param>
    /// <param name="dnatExcludedVlanIds">Optional VLAN IDs to exclude from DNAT coverage checks</param>
    /// <param name="externalZoneId">Optional External/WAN zone ID for validating firewall rule destinations</param>
    /// <param name="zoneLookup">Optional firewall zone lookup for DMZ/Hotspot network identification</param>
    public async Task<DnsSecurityResult> AnalyzeAsync(JsonElement? settingsData, List<FirewallRule>? firewallRules, List<SwitchInfo>? switches, List<NetworkInfo>? networks, JsonElement? deviceData, int? customDnsManagementPort, JsonElement? natRulesData, List<int>? dnatExcludedVlanIds = null, string? externalZoneId = null, Services.FirewallZoneLookup? zoneLookup = null)
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
        if (firewallRules != null && firewallRules.Count > 0)
        {
            AnalyzeFirewallRules(firewallRules, networks, result, externalZoneId);
        }
        else
        {
            _logger.LogWarning("No firewall rules available for DNS security analysis");
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

        // Detect third-party LAN DNS (Pi-hole, AdGuard Home, etc.)
        if (networks?.Any() == true)
        {
            await AnalyzeThirdPartyDnsAsync(networks, result, customDnsManagementPort, zoneLookup);
        }

        // Analyze DNAT DNS rules (alternative to firewall blocking)
        if (natRulesData.HasValue && networks?.Any() == true)
        {
            AnalyzeDnatDnsRules(natRulesData.Value, networks, result, dnatExcludedVlanIds);
        }

        // Generate issues based on findings (includes async WAN DNS validation)
        await GenerateAuditIssuesAsync(result, networks, zoneLookup);

        _logger.LogDebug("DNS security analysis complete: DoH={DoHState}, Firewall rules found: DNS53={Dns53}, DoT={DoT}, DoH={DoHBlock}, DoQ={DoQBlock}, DoH3={DoH3Block}, DeviceDns={DeviceDnsOk}, WanDns={WanDnsCount}",
            result.DohState, result.HasDns53BlockRule, result.HasDotBlockRule, result.HasDohBlockRule, result.HasDoqBlockRule, result.HasDoh3BlockRule, result.DeviceDnsPointsToGateway, result.WanDnsServers.Count);

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

        // DoH is configured only if state is not off/disabled AND there are enabled servers
        // UniFi API uses both "disabled" and "off" for the disabled state
        var isDisabledState = result.DohState == "disabled" || result.DohState == "off";
        result.DohConfigured = !isDisabledState && result.ConfiguredServers.Any(s => s.Enabled);
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

    private void AnalyzeFirewallRules(List<FirewallRule> firewallRules, List<NetworkInfo>? networks, DnsSecurityResult result, string? externalZoneId)
    {
        // Analyze parsed firewall rules to find DNS-related rules
        foreach (var rule in firewallRules)
        {
            var name = rule.Name ?? "";
            if (!rule.Enabled)
                continue;

            var protocol = rule.Protocol?.ToLowerInvariant() ?? "all";
            var matchOppositeProtocol = rule.MatchOppositeProtocol;

            // Get source info for coverage tracking
            var sourceMatchingTarget = rule.SourceMatchingTarget;
            var sourceNetworkIds = rule.SourceNetworkIds;
            var sourceMatchOppositeNetworks = rule.SourceMatchOppositeNetworks;

            // Get destination info (port group resolution already done during parsing)
            var destPort = rule.DestinationPort;
            var destZoneId = rule.DestinationZoneId;
            var matchingTarget = rule.DestinationMatchingTarget;
            var webDomains = rule.WebDomains;
            var matchOppositePorts = rule.DestinationMatchOppositePorts;

            // DNS leak prevention rules must target the External zone.
            // If we have an External zone ID, validate the destination zone matches.
            // Rules targeting other zones (e.g., LAN) don't prevent DNS leaks to the internet.
            // Rules without a destination zone are assumed to target all zones (including external).
            var targetsExternalZone = string.IsNullOrEmpty(externalZoneId) ||
                                      string.IsNullOrEmpty(destZoneId) ||
                                      string.Equals(destZoneId, externalZoneId, StringComparison.OrdinalIgnoreCase);

            // For legacy systems, LAN_IN rules can also block external traffic (traffic passes through
            // LAN_IN before reaching WAN_OUT). However, LAN_IN is only acceptable for DoT/DoH blocking,
            // NOT for UDP 53 - because the gateway's own DNS queries would be blocked by LAN_IN.
            var ruleset = rule.Ruleset?.ToUpperInvariant();
            var isLegacyLanIn = ruleset == "LAN_IN";

            var isBlockAction = rule.ActionType.IsBlockAction();

            // Debug logging for zone matching (helps diagnose DNS detection issues)
            _logger.LogDebug("Rule '{Name}': action={Action}, isBlock={IsBlock}, destZone={DestZone}, externalZone={ExternalZone}, targetsExternal={TargetsExternal}, matchingTarget={MatchingTarget}",
                name, rule.Action, isBlockAction, destZoneId ?? "(null)", externalZoneId ?? "(null)", targetsExternalZone, matchingTarget ?? "(null)");

            // If match_opposite_ports is true, the rule blocks everything EXCEPT the specified ports
            // So we should NOT count it as blocking those ports
            if (matchOppositePorts)
            {
                _logger.LogDebug("Rule {Name} has match_opposite_ports=true, ports {Ports} are EXCLUDED from blocking", name, destPort);
                continue;
            }

            // Determine effective protocol blocking considering match_opposite_protocol
            // If match_opposite_protocol=true, the rule blocks everything EXCEPT the specified protocol
            // AllowsProtocol returns true if the protocol matches the rule specification
            var blocksUdp = FirewallGroupHelper.AllowsProtocol(protocol, matchOppositeProtocol, "udp");
            var blocksTcp = FirewallGroupHelper.AllowsProtocol(protocol, matchOppositeProtocol, "tcp");

            // Check for DNS port 53 blocking - must include UDP (DNS is primarily UDP) and target External zone
            if (isBlockAction && targetsExternalZone && FirewallGroupHelper.IncludesPort(destPort, "53"))
            {
                if (blocksUdp)
                {
                    result.HasDns53BlockRule = true;
                    result.Dns53RuleName = name;
                    _logger.LogDebug("Found DNS53 block rule: {Name} (protocol={Protocol}, opposite={Opposite}, zone={Zone})",
                        name, protocol, matchOppositeProtocol, destZoneId ?? "any");

                    // Track network coverage for this rule
                    if (networks != null)
                    {
                        AddCoveredNetworks(networks, sourceMatchingTarget, sourceNetworkIds, sourceMatchOppositeNetworks, result.Dns53CoveredNetworkIds);
                    }
                }
                else
                {
                    _logger.LogDebug("Skipping DNS53 rule {Name}: protocol {Protocol} (opposite={Opposite}) doesn't block UDP", name, protocol, matchOppositeProtocol);
                }
            }
            else if (isBlockAction && !targetsExternalZone && FirewallGroupHelper.IncludesPort(destPort, "53"))
            {
                _logger.LogDebug("Skipping DNS53 rule {Name}: destination zone {Zone} is not the External zone {ExternalZone}",
                    name, destZoneId, externalZoneId);
            }

            // Check for DNS over TLS (port 853) blocking - TCP only, must target External zone
            // Check for DNS over QUIC (port 853) blocking - UDP only (RFC 9250), must target External zone
            // For legacy systems, LAN_IN is also acceptable (gateway uses DoH, not DoT/DoQ for upstream)
            if (isBlockAction && (targetsExternalZone || isLegacyLanIn) && FirewallGroupHelper.IncludesPort(destPort, "853"))
            {
                // DoT = TCP 853
                if (blocksTcp)
                {
                    result.HasDotBlockRule = true;
                    result.DotRuleName = name;
                    _logger.LogDebug("Found DoT block rule: {Name} (protocol={Protocol}, opposite={Opposite}, zone={Zone})",
                        name, protocol, matchOppositeProtocol, destZoneId ?? "any");
                }

                // DoQ = UDP 853 (RFC 9250 standard port)
                if (blocksUdp)
                {
                    result.HasDoqBlockRule = true;
                    result.DoqRuleName = name;
                    _logger.LogDebug("Found DoQ block rule: {Name} (protocol={Protocol}, opposite={Opposite}, zone={Zone})",
                        name, protocol, matchOppositeProtocol, destZoneId ?? "any");
                }
            }

            // Check for DoH/DoH3 blocking (port 443 with web domains containing DNS providers), must target External zone
            // DoH = TCP 443 (HTTP/2), DoH3 = UDP 443 (HTTP/3 over QUIC)
            // For legacy systems, LAN_IN is also acceptable (gateway's DoH goes to configured providers, not blocked IPs)
            if (isBlockAction && (targetsExternalZone || isLegacyLanIn) && FirewallGroupHelper.IncludesPort(destPort, "443") && matchingTarget == "WEB" && webDomains?.Count > 0)
            {
                // Check if web domains include DNS providers
                var dnsProviderDomains = webDomains.Where(d =>
                    DnsProviderPatterns.Any(pattern => d.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                ).ToList();

                if (dnsProviderDomains.Count > 0)
                {
                    // DoH blocking (TCP 443)
                    if (blocksTcp)
                    {
                        result.HasDohBlockRule = true;
                        foreach (var domain in dnsProviderDomains)
                        {
                            if (!result.DohBlockedDomains.Contains(domain))
                                result.DohBlockedDomains.Add(domain);
                        }
                        result.DohRuleName = name;
                        _logger.LogDebug("Found DoH block rule: {Name} (protocol={Protocol}, opposite={Opposite}, zone={Zone}) with {Count} DNS domains",
                            name, protocol, matchOppositeProtocol, destZoneId ?? "any", dnsProviderDomains.Count);
                    }

                    // DoH3 blocking (UDP 443 / HTTP/3 over QUIC)
                    if (blocksUdp)
                    {
                        result.HasDoh3BlockRule = true;
                        result.Doh3RuleName = name;
                        _logger.LogDebug("Found DoH3 block rule: {Name} (protocol={Protocol}, opposite={Opposite}, zone={Zone}) with {Count} DNS domains",
                            name, protocol, matchOppositeProtocol, destZoneId ?? "any", dnsProviderDomains.Count);
                    }
                }
            }

            // === App-based DNS blocking detection ===
            // App IDs are port-based under the hood, so app-based rules provide similar coverage to port-based rules
            // Legacy rules (from combined-traffic API) have no protocol field - assume ALL protocols
            var appIds = rule.AppIds;
            var isAppBasedRule = appIds?.Count > 0 && matchingTarget == "APP";

            if (isBlockAction && targetsExternalZone && isAppBasedRule)
            {
                // For legacy rules (protocol == "all" or null), assume all protocols blocked
                var legacyAllProtocols = string.IsNullOrEmpty(protocol) || protocol == "all";

                // DNS app (port 53) - blocks UDP DNS
                if (appIds!.Any(DnsAppIds.IsDns53App))
                {
                    if (legacyAllProtocols || blocksUdp)
                    {
                        result.HasDns53BlockRule = true;
                        result.Dns53RuleName ??= name;
                        _logger.LogDebug("Found app-based DNS53 block rule: {Name} (appIds={AppIds}, protocol={Protocol})",
                            name, string.Join(",", appIds!), protocol ?? "all");

                        // Track network coverage
                        if (networks != null)
                        {
                            AddCoveredNetworks(networks, sourceMatchingTarget, sourceNetworkIds, sourceMatchOppositeNetworks, result.Dns53CoveredNetworkIds);
                        }
                    }
                }

                // Port 853 app - covers DoT (TCP) and DoQ (UDP)
                if (appIds!.Any(DnsAppIds.IsPort853App))
                {
                    if (legacyAllProtocols || blocksTcp)
                    {
                        result.HasDotBlockRule = true;
                        result.DotRuleName ??= name;
                        _logger.LogDebug("Found app-based DoT block rule: {Name} (appIds={AppIds}, protocol={Protocol})",
                            name, string.Join(",", appIds!), protocol ?? "all");
                    }
                    if (legacyAllProtocols || blocksUdp)
                    {
                        result.HasDoqBlockRule = true;
                        result.DoqRuleName ??= name;
                        _logger.LogDebug("Found app-based DoQ block rule: {Name} (appIds={AppIds}, protocol={Protocol})",
                            name, string.Join(",", appIds!), protocol ?? "all");
                    }
                }

                // Port 443 app - covers DoH (TCP) and DoH3 (UDP/QUIC)
                if (appIds!.Any(DnsAppIds.IsPort443App))
                {
                    if (legacyAllProtocols || blocksTcp)
                    {
                        result.HasDohBlockRule = true;
                        result.DohRuleName ??= name;
                        _logger.LogDebug("Found app-based DoH block rule: {Name} (appIds={AppIds}, protocol={Protocol})",
                            name, string.Join(",", appIds!), protocol ?? "all");
                    }
                    if (legacyAllProtocols || blocksUdp)
                    {
                        result.HasDoh3BlockRule = true;
                        result.Doh3RuleName ??= name;
                        _logger.LogDebug("Found app-based DoH3 block rule: {Name} (appIds={AppIds}, protocol={Protocol})",
                            name, string.Join(",", appIds!), protocol ?? "all");
                    }
                }
            }
        }

        // Calculate DNS53 network coverage stats
        if (networks != null && result.HasDns53BlockRule)
        {
            CalculateDns53Coverage(networks, result);
        }
    }

    /// <summary>
    /// Add covered networks to the set based on source matching rules.
    /// Handles Match Opposite logic: when true, rule applies to all networks EXCEPT those listed.
    /// </summary>
    private static void AddCoveredNetworks(
        List<NetworkInfo> networks,
        string? sourceMatchingTarget,
        List<string>? sourceNetworkIds,
        bool matchOpposite,
        HashSet<string> coveredNetworkIds)
    {
        // If source matching target is ANY or not set with no network IDs, rule covers all networks
        if (string.IsNullOrEmpty(sourceMatchingTarget) ||
            sourceMatchingTarget.Equals("ANY", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var network in networks)
            {
                coveredNetworkIds.Add(network.Id);
            }
            return;
        }

        // If matching target is NETWORK, check the network IDs with Match Opposite handling
        if (sourceMatchingTarget.Equals("NETWORK", StringComparison.OrdinalIgnoreCase))
        {
            var networkIds = sourceNetworkIds ?? new List<string>();

            if (matchOpposite)
            {
                // Match Opposite: rule applies to all networks EXCEPT those listed
                foreach (var network in networks)
                {
                    if (!networkIds.Contains(network.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        coveredNetworkIds.Add(network.Id);
                    }
                }
            }
            else
            {
                // Normal: rule applies ONLY to networks listed
                foreach (var networkId in networkIds)
                {
                    coveredNetworkIds.Add(networkId);
                }
            }
            return;
        }

        // For other matching targets (IP, CLIENT, etc.), we can't determine network coverage
        // These are more targeted rules that don't provide broad network coverage
    }

    /// <summary>
    /// Calculate DNS53 coverage statistics after processing all rules
    /// </summary>
    private void CalculateDns53Coverage(List<NetworkInfo> networks, DnsSecurityResult result)
    {
        foreach (var network in networks)
        {
            if (result.Dns53CoveredNetworkIds.Contains(network.Id))
            {
                result.Dns53CoveredNetworks.Add(network.Name);
            }
            else
            {
                result.Dns53UncoveredNetworks.Add(network.Name);
            }
        }

        result.Dns53ProvidesFullCoverage = result.Dns53UncoveredNetworks.Count == 0;

        if (!result.Dns53ProvidesFullCoverage)
        {
            _logger.LogInformation("DNS53 blocking provides partial coverage. Covered: {Covered}, Uncovered: {Uncovered}",
                string.Join(", ", result.Dns53CoveredNetworks),
                string.Join(", ", result.Dns53UncoveredNetworks));
        }
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


    private async Task GenerateAuditIssuesAsync(DnsSecurityResult result, List<NetworkInfo>? networks = null, Services.FirewallZoneLookup? zoneLookup = null)
    {
        // Issue: DoH not configured
        if (!result.DohConfigured)
        {
            if (result.HasThirdPartyDns)
            {
                var dnsServerIps = result.ThirdPartyDnsServers.Select(t => t.DnsServerIp).Distinct().ToList();
                var networkNames = result.ThirdPartyDnsServers.Select(t => t.NetworkName).Distinct().ToList();

                // Known providers (Pi-hole, AdGuard Home) are trusted - neutral score impact
                // Unknown third-party DNS servers get a minor penalty since we can't verify their filtering
                var isKnownProvider = result.IsPiholeDetected || result.IsAdGuardHomeDetected;
                var scoreImpact = isKnownProvider ? 0 : 3; // Minor penalty for unknown providers
                var severity = isKnownProvider ? AuditSeverity.Informational : AuditSeverity.Recommended;
                var recommendedAction = isKnownProvider
                    ? "Verify third-party DNS provides adequate security and filtering. Consider enabling DNS firewall rules to prevent bypass."
                    : "Configure the third-party DNS management port in Settings to enable detection. Otherwise, consider a known DNS filtering solution (Pi-hole, AdGuard Home) or CyberSecure Encrypted DNS (DoH).";

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
                        { "is_adguard_home", result.IsAdGuardHomeDetected },
                        { "is_known_provider", isKnownProvider },
                        { "affected_networks", networkNames },
                        { "provider_name", result.ThirdPartyDnsProviderName ?? "Third-Party LAN DNS" },
                        { "configurable_setting", "Configure third-party DNS management port in Settings if detection fails" }
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
                    RecommendedAction = "Enable CyberSecure Encrypted DNS (DoH) in Network Settings or deploy a DNS filtering solution like Pi-hole or AdGuard Home",
                    RuleId = "DNS-UNKNOWN-001",
                    ScoreImpact = 0  // No score impact - shown alongside DNS_NO_DOH which carries the penalty
                });

                // Also add the standard DoH recommendation
                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsNoDoh,
                    Severity = AuditSeverity.Critical,
                    DeviceName = result.GatewayName,
                    Message = "DNS-over-HTTPS (DoH) is not configured. Network traffic uses unencrypted DNS which can be monitored or manipulated.",
                    RecommendedAction = "Enable CyberSecure Encrypted DNS (DoH) in Network Settings with a trusted provider like NextDNS or Cloudflare",
                    RuleId = "DNS-DOH-001",
                    ScoreImpact = 12
                });
            }
        }
        else if (result.DohState == "auto")
        {
            // DoH auto mode uses default providers whose privacy practices you may not have reviewed
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsDohAuto,
                Severity = AuditSeverity.Informational,
                DeviceName = result.GatewayName,
                Message = "DoH is using default providers whose privacy policies you may not have reviewed. Default providers may log queries and do not guarantee anonymity.",
                RecommendedAction = "Consider configuring custom DoH servers from privacy-focused providers if DNS query privacy is important",
                RuleId = "DNS-DOH-002",
                ScoreImpact = 3
            });
        }

        // Validate WAN DNS against DoH provider (uses PTR lookup)
        await ValidateWanDnsConfigurationAsync(result);

        // Issue: Networks using DNS servers outside configured subnets (bypasses local DNS filtering)
        if (result.HasExternalDns)
        {
            // Build a set of DMZ network names for quick lookup
            var dmzNetworkNames = (networks ?? Enumerable.Empty<NetworkInfo>())
                .Where(n => zoneLookup?.IsDmzZone(n.FirewallZoneId) == true)
                .Select(n => n.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Group by public vs private, then by provider
            var publicDns = result.ExternalDnsNetworks.Where(e => e.IsPublicDns).ToList();
            var privateDns = result.ExternalDnsNetworks.Where(e => !e.IsPublicDns).ToList();

            // Separate DMZ networks from regular networks for public DNS
            var publicDnsDmz = publicDns.Where(e => dmzNetworkNames.Contains(e.NetworkName)).ToList();
            var publicDnsRegular = publicDns.Where(e => !dmzNetworkNames.Contains(e.NetworkName)).ToList();

            // DMZ networks with public DNS - Informational (expected for isolated networks)
            foreach (var group in publicDnsDmz.GroupBy(e => e.ProviderName ?? "unknown provider"))
            {
                var netNames = group.Select(e => e.NetworkName).Distinct().ToList();
                var dnsIps = group.Select(e => e.DnsServerIp).Distinct().ToList();
                var providerName = group.Key;

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsExternalBypass,
                    Severity = AuditSeverity.Informational,
                    DeviceName = result.GatewayName,
                    Message = $"DMZ network(s) ({string.Join(", ", netNames)}) configured to use external public DNS ({providerName}: {string.Join(", ", dnsIps)}). This is expected for isolated DMZ networks.",
                    RecommendedAction = "If local DNS filtering is desired for DMZ networks, create firewall rules to allow DNS traffic from DMZ to your internal DNS server.",
                    RuleId = "DNS-EXT-BYPASS-DMZ",
                    ScoreImpact = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "affected_networks", netNames },
                        { "external_dns_servers", dnsIps },
                        { "provider_name", providerName },
                        { "is_public_dns", true },
                        { "is_dmz", true }
                    }
                });
            }

            // Regular networks with public DNS - Recommended (likely intentional but bypasses filtering)
            foreach (var group in publicDnsRegular.GroupBy(e => e.ProviderName ?? "unknown provider"))
            {
                var netNames = group.Select(e => e.NetworkName).Distinct().ToList();
                var dnsIps = group.Select(e => e.DnsServerIp).Distinct().ToList();
                var providerName = group.Key;

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsExternalBypass,
                    Severity = AuditSeverity.Recommended,
                    DeviceName = result.GatewayName,
                    Message = $"Network(s) ({string.Join(", ", netNames)}) configured to use external public DNS ({providerName}: {string.Join(", ", dnsIps)}). This bypasses local DNS filtering including gateway DoH and Pi-hole/AdGuard.",
                    RecommendedAction = "Remove custom DNS configuration from these networks to use gateway DNS, or point them to your local DNS filtering solution (e.g., Pi-hole, AdGuard Home). If intentional, create DNAT rules to redirect DNS traffic.",
                    RuleId = "DNS-EXT-BYPASS-001",
                    ScoreImpact = 8,
                    Metadata = new Dictionary<string, object>
                    {
                        { "affected_networks", netNames },
                        { "external_dns_servers", dnsIps },
                        { "provider_name", providerName },
                        { "is_public_dns", true }
                    }
                });
            }

            // Private DNS outside configured subnets - could be misconfiguration or unconfigured network
            if (privateDns.Any())
            {
                var netNames = privateDns.Select(e => e.NetworkName).Distinct().ToList();
                var dnsIps = privateDns.Select(e => e.DnsServerIp).Distinct().ToList();

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsExternalBypass,
                    Severity = AuditSeverity.Recommended,
                    DeviceName = result.GatewayName,
                    Message = $"Network(s) ({string.Join(", ", netNames)}) configured to use private DNS servers outside configured subnets ({string.Join(", ", dnsIps)}). These may be on an unconfigured network or misconfigured.",
                    RecommendedAction = "Verify these DNS servers are intentional. If they're local DNS servers (e.g., Pi-hole), ensure their subnet is configured in the network settings. Otherwise, remove the custom DNS configuration.",
                    RuleId = "DNS-EXT-BYPASS-002",
                    ScoreImpact = 6,
                    Metadata = new Dictionary<string, object>
                    {
                        { "affected_networks", netNames },
                        { "external_dns_servers", dnsIps },
                        { "is_public_dns", false }
                    }
                });
            }
        }

        // Issue: No DNS port 53 blocking (DNS leak prevention)
        // DNAT rules can be an alternative when DoH or third-party DNS is configured
        // and the redirect destination is correct
        var hasDnsControlSolution = result.DohConfigured || result.HasThirdPartyDns;
        var dnatIsValidAlternative = result.DnatProvidesFullCoverage
            && hasDnsControlSolution
            && result.DnatRedirectTargetIsValid
            && result.DnatDestinationFilterIsValid;

        // Partial DNAT coverage is better than nothing - don't double-penalize
        // The partial coverage issue (6 pts) is more actionable than the generic no-block issue (12 pts)
        var hasPartialDnatCoverage = result.HasDnatDnsRules
            && !result.DnatProvidesFullCoverage
            && hasDnsControlSolution
            && result.DnatRedirectTargetIsValid
            && result.DnatDestinationFilterIsValid;

        if (!result.HasDns53BlockRule && !dnatIsValidAlternative && !hasPartialDnatCoverage)
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsNo53Block,
                Severity = AuditSeverity.Critical,
                DeviceName = result.GatewayName,
                Message = "No firewall rule blocks external DNS (port 53). Devices can bypass network DNS settings and leak queries to untrusted servers.",
                RecommendedAction = "Create firewall rule: Block outbound UDP port 53 to Internet for all VLANs (except gateway), or configure DNAT rules to redirect DNS traffic",
                RuleId = "DNS-LEAK-001",
                ScoreImpact = 12
            });
        }

        // Issue: DNS53 firewall rules provide partial coverage (some networks not covered)
        // This happens when rules use source network restrictions with or without Match Opposite
        if (result.HasDns53BlockRule && !result.Dns53ProvidesFullCoverage && result.Dns53UncoveredNetworks.Any() && !dnatIsValidAlternative)
        {
            var totalNetworks = result.Dns53CoveredNetworks.Count + result.Dns53UncoveredNetworks.Count;
            var coverageRatio = totalNetworks > 0 ? (double)result.Dns53CoveredNetworks.Count / totalNetworks : 0;
            // If 2/3 or more networks are covered, use Recommended severity; otherwise Critical
            var severity = coverageRatio >= 2.0 / 3.0 ? AuditSeverity.Recommended : AuditSeverity.Critical;
            var scoreImpact = severity == AuditSeverity.Recommended ? 6 : 10;

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.Dns53PartialCoverage,
                Severity = severity,
                DeviceName = result.GatewayName,
                Message = $"DNS port 53 blocking rules provide partial network coverage. Uncovered networks: {string.Join(", ", result.Dns53UncoveredNetworks)}. Devices on these networks can bypass DNS settings.",
                RecommendedAction = "Update firewall rules to cover all networks, or create separate rules for uncovered networks, or configure DNAT rules as an alternative",
                RuleId = "DNS-LEAK-002",
                ScoreImpact = scoreImpact,
                Metadata = new Dictionary<string, object>
                {
                    { "covered_networks", result.Dns53CoveredNetworks },
                    { "uncovered_networks", result.Dns53UncoveredNetworks },
                    { "coverage_ratio", coverageRatio }
                }
            });
        }

        // Add hardening note if DNAT provides full coverage as alternative
        if (dnatIsValidAlternative && !result.HasDns53BlockRule)
        {
            result.HardeningNotes.Add($"DNS leak prevention via DNAT redirect to {result.DnatRedirectTarget ?? "gateway"} (covers all DHCP networks)");
        }

        // Issue: DNAT provides partial coverage (some networks not covered)
        // If DNS53 firewall blocking provides full coverage, downgrade to Informational (DNAT is redundant/supplementary)
        // Otherwise, severity depends on coverage ratio
        // Special handling for DMZ and Guest networks - they get Info issues instead of Recommended/Critical
        if (result.HasDnatDnsRules && !result.DnatProvidesFullCoverage && result.DnatUncoveredNetworks.Any())
        {
            // Build lookup of network name -> NetworkInfo for zone identification
            var networksByName = networks?
                .Where(n => !string.IsNullOrEmpty(n.Name))
                .ToDictionary(n => n.Name, n => n, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, NetworkInfo>(StringComparer.OrdinalIgnoreCase);

            // Separate DMZ networks and Guest networks with 3rd party LAN DNS from regular uncovered networks
            var dmzNetworks = new List<string>();
            var guestNetworksWithThirdPartyDns = new List<string>();
            var regularUncoveredNetworks = new List<string>();

            foreach (var networkName in result.DnatUncoveredNetworks)
            {
                if (networksByName.TryGetValue(networkName, out var network))
                {
                    // Check if this is a DMZ network (by firewall zone)
                    var isDmz = zoneLookup?.IsDmzZone(network.FirewallZoneId) ?? false;

                    // Check if this is a Guest network with 3rd party LAN DNS
                    // Guest networks are identified by IsUniFiGuestNetwork or Purpose == Guest
                    var isGuest = network.IsUniFiGuestNetwork || network.Purpose == NetworkPurpose.Guest;
                    var hasThirdPartyLanDns = result.HasThirdPartyDns && result.IsSiteWideThirdPartyDns;

                    if (isDmz)
                    {
                        dmzNetworks.Add(networkName);
                    }
                    else if (isGuest && hasThirdPartyLanDns)
                    {
                        guestNetworksWithThirdPartyDns.Add(networkName);
                    }
                    else
                    {
                        regularUncoveredNetworks.Add(networkName);
                    }
                }
                else
                {
                    // Network not found in lookup - treat as regular
                    regularUncoveredNetworks.Add(networkName);
                }
            }

            // Create Info issue for DMZ networks that need firewall rules for internal DNS
            if (dmzNetworks.Any())
            {
                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsDmzNetworkInfo,
                    Severity = AuditSeverity.Informational,
                    DeviceName = result.GatewayName,
                    Message = $"DMZ network(s) ({string.Join(", ", dmzNetworks)}) are not covered by DNS DNAT rules. This is expected for DMZ networks.",
                    RecommendedAction = "If internal DNS resolution or filtering is desired for DMZ networks, create firewall rules to allow DNS traffic from DMZ to your internal DNS server or gateway",
                    RuleId = "DNS-DMZ-001",
                    ScoreImpact = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "dmz_networks", dmzNetworks },
                        { "network_type", "dmz" }
                    }
                });
            }

            // Create Info issue for Guest networks with 3rd party LAN DNS
            if (guestNetworksWithThirdPartyDns.Any())
            {
                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsGuestThirdPartyInfo,
                    Severity = AuditSeverity.Informational,
                    DeviceName = result.GatewayName,
                    Message = $"Guest network(s) ({string.Join(", ", guestNetworksWithThirdPartyDns)}) are using third-party LAN DNS and are not covered by DNS DNAT rules.",
                    RecommendedAction = "If internal DNS resolution or filtering is desired for Guest networks using third-party DNS, create firewall rules to allow DNS traffic from the Guest network to your internal DNS server",
                    RuleId = "DNS-GUEST-001",
                    ScoreImpact = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        { "guest_networks", guestNetworksWithThirdPartyDns },
                        { "network_type", "guest" },
                        { "third_party_dns", result.ThirdPartyDnsProviderName ?? "unknown" }
                    }
                });
            }

            // Only create the regular partial coverage issue if there are non-DMZ/Guest uncovered networks
            if (regularUncoveredNetworks.Any())
            {
                var totalNetworks = result.DnatCoveredNetworks.Count + regularUncoveredNetworks.Count;
                var coverageRatio = totalNetworks > 0 ? (double)result.DnatCoveredNetworks.Count / totalNetworks : 0;

                // Determine severity based on whether DNS53 blocking is the primary protection
                AuditSeverity severity;
                int scoreImpact;
                string message;
                string action;

                if (result.HasDns53BlockRule && result.Dns53ProvidesFullCoverage)
                {
                    // DNS53 blocking provides full coverage - DNAT partial coverage is just informational
                    severity = AuditSeverity.Informational;
                    scoreImpact = 0;
                    message = $"DNAT DNS rules don't cover all networks ({string.Join(", ", regularUncoveredNetworks)} not covered). Your firewall already blocks external DNS for all networks, so this is just for your awareness.";
                    action = "If you intend to use DNAT as primary DNS control, add rules for uncovered networks. Otherwise, this can be ignored.";
                }
                else if (result.HasDns53BlockRule)
                {
                    // DNS53 blocking exists but partial - DNAT partial is lower priority
                    severity = AuditSeverity.Recommended;
                    scoreImpact = 3;
                    message = $"DNAT DNS rules provide partial coverage. Networks without DNAT coverage: {string.Join(", ", regularUncoveredNetworks)}. Firewall port 53 blocking is also present.";
                    action = "Consider whether you want to use firewall blocking or DNAT as your primary DNS control method, then ensure full coverage for your chosen approach";
                }
                else
                {
                    // No DNS53 blocking - DNAT is primary protection, partial coverage is significant
                    severity = coverageRatio >= 2.0 / 3.0 ? AuditSeverity.Recommended : AuditSeverity.Critical;
                    scoreImpact = severity == AuditSeverity.Recommended ? 6 : 10;
                    message = $"DNAT DNS rules provide partial coverage. Networks without DNAT coverage: {string.Join(", ", regularUncoveredNetworks)}. Devices on these networks can bypass DNS settings.";
                    action = "Add DNAT rules for the remaining networks, create a firewall rule to block outbound UDP port 53, or exclude intentionally uncovered networks in Settings";
                }

                result.Issues.Add(new AuditIssue
                {
                    Type = IssueTypes.DnsDnatPartialCoverage,
                    Severity = severity,
                    DeviceName = result.GatewayName,
                    Message = message,
                    RecommendedAction = action,
                    RuleId = "DNS-DNAT-001",
                    ScoreImpact = scoreImpact,
                    Metadata = new Dictionary<string, object>
                    {
                        { "covered_networks", result.DnatCoveredNetworks.ToList() },
                        { "uncovered_networks", regularUncoveredNetworks },
                        { "dmz_networks_excluded", dmzNetworks },
                        { "guest_networks_excluded", guestNetworksWithThirdPartyDns },
                        { "redirect_target", result.DnatRedirectTarget ?? "" },
                        { "coverage_ratio", coverageRatio },
                        { "has_dns53_block", result.HasDns53BlockRule },
                        { "dns53_full_coverage", result.Dns53ProvidesFullCoverage },
                        { "configurable_setting", "Exclude VLANs from coverage checks in Settings → Audit Settings → DNAT DNS Coverage: Excluded VLANs" }
                    }
                });
            }
        }

        // Issue: Single IP DNAT rules (abnormal configuration)
        if (result.DnatSingleIpRules.Any())
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsDnatSingleIp,
                Severity = AuditSeverity.Informational,
                DeviceName = result.GatewayName,
                Message = $"DNAT DNS rules target single IP addresses instead of network ranges: {string.Join(", ", result.DnatSingleIpRules)}. This provides limited coverage and may indicate misconfiguration.",
                RecommendedAction = "Configure DNAT rules to use network references or CIDR ranges for complete coverage",
                RuleId = "DNS-DNAT-002",
                ScoreImpact = 2,
                Metadata = new Dictionary<string, object>
                {
                    { "single_ip_sources", result.DnatSingleIpRules.ToList() }
                }
            });
        }

        // Issue: DNAT redirects to wrong translated IP
        if (result.HasDnatDnsRules && !result.DnatRedirectTargetIsValid && result.InvalidDnatRules.Any())
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsDnatWrongDestination,
                Severity = AuditSeverity.Critical,
                DeviceName = result.GatewayName,
                Message = $"DNAT DNS rules have incorrect translated IP address. {string.Join("; ", result.InvalidDnatRules)}.",
                RecommendedAction = result.IsSiteWideThirdPartyDns
                    ? "Update the translated IP address in DNAT rules to your Pi-hole/DNS server IP"
                    : "Update the translated IP address in DNAT rules to a gateway IP",
                RuleId = "DNS-DNAT-003",
                ScoreImpact = 10,
                Metadata = new Dictionary<string, object>
                {
                    { "invalid_rules", result.InvalidDnatRules.ToList() },
                    { "expected_destinations", result.ExpectedDnatDestinations.ToList() },
                    { "is_site_wide_third_party_dns", result.IsSiteWideThirdPartyDns }
                }
            });
        }

        // Issue: DNAT has restricted destination filter (only catches some bypass attempts)
        if (result.HasDnatDnsRules && !result.DnatDestinationFilterIsValid && result.RestrictedDestinationRules.Any())
        {
            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsDnatRestrictedDestination,
                Severity = AuditSeverity.Recommended,
                DeviceName = result.GatewayName,
                Message = $"DNAT DNS rules have restricted destination filters that only catch some bypass attempts. {string.Join("; ", result.RestrictedDestinationRules)}.",
                RecommendedAction = "Set destination to 'Any' or use 'invert address' to match traffic NOT going to your DNS server",
                RuleId = "DNS-DNAT-004",
                ScoreImpact = 5,
                Metadata = new Dictionary<string, object>
                {
                    { "restricted_rules", result.RestrictedDestinationRules.ToList() }
                }
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
                Message = "No firewall rule blocks DNS over QUIC (DoQ). Devices can bypass your DNS filtering using QUIC-based DNS on UDP port 853.",
                RecommendedAction = "Create firewall rule: Block outbound UDP port 853 to Internet for all VLANs",
                RuleId = "DNS-LEAK-004",
                ScoreImpact = 4
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
            var protocols = "DNS53, DoT, DoH, DoQ";
            if (result.HasDoh3BlockRule)
                protocols += ", DoH3";
            result.HardeningNotes.Add($"DNS leak prevention fully configured with DoH and firewall blocking ({protocols})");
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
    /// Detect third-party LAN DNS servers (like Pi-hole, AdGuard Home) across networks
    /// </summary>
    private async Task AnalyzeThirdPartyDnsAsync(List<NetworkInfo> networks, DnsSecurityResult result, int? customPort = null, Services.FirewallZoneLookup? zoneLookup = null)
    {
        var thirdPartyResults = await _thirdPartyDetector.DetectThirdPartyDnsAsync(networks, customPort);

        if (thirdPartyResults.Any())
        {
            result.HasThirdPartyDns = true;
            result.ThirdPartyDnsServers.AddRange(thirdPartyResults);

            // Determine provider name (Pi-hole takes precedence, then AdGuard Home)
            if (thirdPartyResults.Any(t => t.IsPihole))
            {
                result.ThirdPartyDnsProviderName = "Pi-hole";
                _logger.LogInformation("Pi-hole detected as third-party DNS on {Count} network(s)",
                    thirdPartyResults.Count(t => t.IsPihole));
            }
            else if (thirdPartyResults.Any(t => t.IsAdGuardHome))
            {
                result.ThirdPartyDnsProviderName = "AdGuard Home";
                _logger.LogInformation("AdGuard Home detected as third-party DNS on {Count} network(s)",
                    thirdPartyResults.Count(t => t.IsAdGuardHome));
            }
            else
            {
                result.ThirdPartyDnsProviderName = "Third-Party LAN DNS";
                _logger.LogInformation("Third-party LAN DNS detected on {Count} network(s)",
                    thirdPartyResults.Count);
            }

            // Determine if this is a site-wide DNS solution or just specialized corporate DNS
            // Site-wide = configured on at least one non-Corporate network
            var networkNamesWithThirdPartyDns = thirdPartyResults
                .Select(r => r.NetworkName)
                .Distinct()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var nonCorporateNetworksWithThirdPartyDns = networks
                .Where(n => networkNamesWithThirdPartyDns.Contains(n.Name))
                .Where(n => n.Purpose != NetworkPurpose.Corporate)
                .ToList();

            result.IsSiteWideThirdPartyDns = nonCorporateNetworksWithThirdPartyDns.Count > 0;

            if (result.IsSiteWideThirdPartyDns)
            {
                // Populate the site-wide DNS IPs
                var siteWideDnsIps = thirdPartyResults
                    .Select(r => r.DnsServerIp)
                    .Distinct()
                    .ToList();
                result.SiteWideDnsServerIps.AddRange(siteWideDnsIps);

                _logger.LogInformation("Third-party DNS is site-wide (configured on non-Corporate networks): {Ips}",
                    string.Join(", ", siteWideDnsIps));

                // Check for DNS consistency across all DHCP-enabled networks
                CheckDnsConsistencyAcrossNetworks(networks, thirdPartyResults, result, zoneLookup);
            }
            else
            {
                _logger.LogInformation("Third-party DNS only on Corporate networks - treating as specialized internal DNS, not site-wide");
            }
        }

        // Detect networks using external public DNS (bypasses all local DNS filtering)
        var externalDnsResults = _thirdPartyDetector.DetectExternalDns(networks);
        if (externalDnsResults.Any())
        {
            result.HasExternalDns = true;
            result.ExternalDnsNetworks.AddRange(externalDnsResults);
            _logger.LogWarning("Found {Count} network(s) using external public DNS (bypasses local filtering): {Networks}",
                externalDnsResults.Count,
                string.Join(", ", externalDnsResults.Select(e => $"{e.NetworkName} ({e.DnsServerIp})")));
        }
    }

    /// <summary>
    /// Check if all DHCP-enabled networks use the same third-party DNS server.
    /// If a third-party DNS (like Pi-hole) is configured on some networks but not all,
    /// this creates a security gap where DNS filtering can be bypassed.
    /// Note: This is only called if IsSiteWideThirdPartyDns is true (already determined by caller).
    /// </summary>
    private void CheckDnsConsistencyAcrossNetworks(
        List<NetworkInfo> networks,
        List<ThirdPartyDnsDetector.ThirdPartyDnsInfo> thirdPartyResults,
        DnsSecurityResult result,
        Services.FirewallZoneLookup? zoneLookup = null)
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
        // Exempt Corporate networks - they may legitimately use internal corporate DNS servers
        var networksWithoutThirdPartyDns = dhcpNetworks
            .Where(n => !networksWithThirdPartyDns.Contains(n.Name))
            .Where(n => n.Purpose != NetworkPurpose.Corporate)
            .ToList();

        // Separate DMZ networks - they require manual firewall rules for DNS
        var dmzNetworks = networksWithoutThirdPartyDns
            .Where(n => zoneLookup?.IsDmzZone(n.FirewallZoneId) == true)
            .ToList();

        // Separate Guest networks with third-party DNS configured elsewhere
        // (they also require manual firewall rules since gateway doesn't auto-punch holes for third-party DNS)
        var guestNetworksWithoutThirdParty = networksWithoutThirdPartyDns
            .Where(n => n.IsUniFiGuestNetwork || n.Purpose == NetworkPurpose.Guest)
            .Where(n => !dmzNetworks.Contains(n)) // Don't double-count if somehow both
            .ToList();

        // Remove DMZ and Guest networks from the standard consistency check
        networksWithoutThirdPartyDns = networksWithoutThirdPartyDns
            .Except(dmzNetworks)
            .Except(guestNetworksWithoutThirdParty)
            .ToList();

        // Create Info issues for DMZ networks
        if (dmzNetworks.Any())
        {
            var providerName = result.ThirdPartyDnsProviderName ?? "Third-Party DNS";
            var dnsServerIps = string.Join(", ", thirdPartyDnsIps);
            var dmzNetworkNames = dmzNetworks.Select(n => n.Name).ToList();

            _logger.LogInformation(
                "DMZ network(s) not using {ProviderName}: {Networks}. Firewall rules required for DNS filtering.",
                providerName, string.Join(", ", dmzNetworkNames));

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsDmzNetworkInfo,
                Severity = AuditSeverity.Informational,
                DeviceName = result.GatewayName,
                Message = $"DMZ network(s) ({string.Join(", ", dmzNetworkNames)}) are not configured to use {providerName}. DMZ networks are isolated from the gateway by design.",
                RecommendedAction = $"If DNS filtering is desired for DMZ network(s), create firewall rules to allow traffic from the DMZ zone to {providerName} ({dnsServerIps}) on port 53.",
                RuleId = "DNS-DMZ-INFO-001",
                ScoreImpact = 0,
                Metadata = new Dictionary<string, object>
                {
                    { "dmz_networks", dmzNetworkNames },
                    { "third_party_dns_ips", thirdPartyDnsIps.ToList() },
                    { "provider_name", providerName }
                }
            });
        }

        // Create Info issues for Guest networks with third-party DNS configured elsewhere
        if (guestNetworksWithoutThirdParty.Any())
        {
            var providerName = result.ThirdPartyDnsProviderName ?? "Third-Party DNS";
            var dnsServerIps = string.Join(", ", thirdPartyDnsIps);
            var guestNetworkNames = guestNetworksWithoutThirdParty.Select(n => n.Name).ToList();

            _logger.LogInformation(
                "Guest network(s) not using {ProviderName}: {Networks}. Firewall rules required for DNS filtering with third-party DNS.",
                providerName, string.Join(", ", guestNetworkNames));

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsGuestThirdPartyInfo,
                Severity = AuditSeverity.Informational,
                DeviceName = result.GatewayName,
                Message = $"Guest network(s) ({string.Join(", ", guestNetworkNames)}) are not configured to use {providerName}. The gateway automatically allows DNS to itself (for DoH/CyberSecure), but third-party LAN DNS servers require explicit firewall rules.",
                RecommendedAction = $"If DNS filtering via {providerName} is desired for guest network(s), create firewall rules to allow traffic from the Hotspot zone to {providerName} ({dnsServerIps}) on port 53.",
                RuleId = "DNS-GUEST-THIRDPARTY-INFO-001",
                ScoreImpact = 0,
                Metadata = new Dictionary<string, object>
                {
                    { "guest_networks", guestNetworkNames },
                    { "third_party_dns_ips", thirdPartyDnsIps.ToList() },
                    { "provider_name", providerName }
                }
            });
        }

        if (networksWithoutThirdPartyDns.Any())
        {
            var providerName = result.ThirdPartyDnsProviderName ?? "Third-Party DNS";
            var missingNetworkNames = networksWithoutThirdPartyDns.Select(n => n.Name).ToList();
            var configuredNetworkNames = networksWithThirdPartyDns.ToList();
            var dnsServerIps = string.Join(", ", thirdPartyDnsIps);

            _logger.LogWarning(
                "DNS consistency issue: {ProviderName} ({DnsIps}) configured on {ConfiguredCount} networks but missing on {MissingCount} DHCP-enabled networks: {MissingNetworks}",
                providerName, dnsServerIps, configuredNetworkNames.Count, missingNetworkNames.Count, string.Join(", ", missingNetworkNames));

            // Adjust message based on whether DoH is configured
            var message = result.DohConfigured
                ? $"{providerName} is configured on {configuredNetworkNames.Count} network(s) but {missingNetworkNames.Count} DHCP-enabled network(s) are using CyberSecure DoH instead: {string.Join(", ", missingNetworkNames)}."
                : $"{providerName} is configured on {configuredNetworkNames.Count} network(s) but {missingNetworkNames.Count} DHCP-enabled network(s) are not using it: {string.Join(", ", missingNetworkNames)}. Devices on these networks can bypass DNS filtering.";

            var recommendation = result.DohConfigured
                ? $"Configure all DHCP-enabled networks to use {providerName} ({dnsServerIps}) for consistent filtering, or keep CyberSecure DoH for those networks"
                : $"Configure all DHCP-enabled networks to use {providerName} ({dnsServerIps}) for consistent DNS filtering, or verify this is intentional";

            result.Issues.Add(new AuditIssue
            {
                Type = IssueTypes.DnsInconsistentConfig,
                Severity = AuditSeverity.Recommended,
                DeviceName = result.GatewayName,
                Message = message,
                RecommendedAction = recommendation,
                RuleId = "DNS-CONSISTENCY-001",
                ScoreImpact = 5,
                Metadata = new Dictionary<string, object>
                {
                    { "third_party_dns_ips", thirdPartyDnsIps.ToList() },
                    { "configured_networks", configuredNetworkNames },
                    { "missing_networks", missingNetworkNames },
                    { "provider_name", providerName },
                    { "doh_configured", result.DohConfigured }
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
    /// Analyze DNAT rules for DNS port 53 coverage.
    /// DNAT rules that redirect UDP port 53 to a trusted DNS server (gateway, Pi-hole)
    /// can be an alternative to firewall blocking when DoH or third-party DNS is configured.
    /// </summary>
    private void AnalyzeDnatDnsRules(JsonElement natRulesData, List<NetworkInfo> networks, DnsSecurityResult result, List<int>? excludedVlanIds = null)
    {
        var dnatAnalyzer = new DnatDnsAnalyzer();
        var coverageResult = dnatAnalyzer.Analyze(natRulesData, networks, excludedVlanIds);

        result.HasDnatDnsRules = coverageResult.HasDnatDnsRules;
        result.DnatProvidesFullCoverage = coverageResult.HasFullCoverage;
        result.DnatRedirectTarget = coverageResult.RedirectTargetIp;
        result.DnatCoveredNetworks.AddRange(coverageResult.CoveredNetworkNames);
        result.DnatUncoveredNetworks.AddRange(coverageResult.UncoveredNetworkNames);
        result.DnatSingleIpRules.AddRange(coverageResult.SingleIpRules);

        if (coverageResult.HasDnatDnsRules)
        {
            _logger.LogInformation(
                "DNAT DNS rules detected: {RuleCount} rules, full coverage: {FullCoverage}, redirect target: {Target}",
                coverageResult.Rules.Count, coverageResult.HasFullCoverage, coverageResult.RedirectTargetIp);

            if (!coverageResult.HasFullCoverage)
            {
                _logger.LogWarning(
                    "DNAT DNS rules provide partial coverage. Covered: {Covered}, Uncovered: {Uncovered}",
                    string.Join(", ", coverageResult.CoveredNetworkNames),
                    string.Join(", ", coverageResult.UncoveredNetworkNames));
            }

            if (coverageResult.SingleIpRules.Any())
            {
                _logger.LogWarning(
                    "DNAT DNS rules with single IP sources detected (abnormal configuration): {Ips}",
                    string.Join(", ", coverageResult.SingleIpRules));
            }
        }

        // Validate redirect destinations
        ValidateDnatRedirectTargets(coverageResult, result, networks);

        // Validate destination filters (should be Any or inverted)
        ValidateDnatDestinationFilters(coverageResult, result);
    }

    /// <summary>
    /// Validate that DNAT redirect destinations point to the correct DNS server.
    /// - With site-wide third-party DNS (Pi-hole on non-Corporate networks): must redirect to the third-party server IP
    /// - With DoH (no site-wide third-party DNS): must redirect to native VLAN gateway OR the specific VLAN gateway
    /// Note: Third-party DNS only on Corporate networks is NOT considered site-wide and falls through to DoH/gateway validation.
    /// </summary>
    private void ValidateDnatRedirectTargets(
        DnatCoverageResult coverageResult,
        DnsSecurityResult result,
        List<NetworkInfo> networks)
    {
        if (!coverageResult.HasDnatDnsRules)
            return;

        if (result.IsSiteWideThirdPartyDns)
        {
            // Site-wide third-party DNS: DNAT must point to the third-party server(s)
            // Also accept gateway IPs that are configured as DNS servers (common dual-DNS setup)
            var validDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ip in result.SiteWideDnsServerIps)
                validDestinations.Add(ip);

            // Also include any gateway IPs that are configured as DHCP DNS servers
            // This handles the common case where DHCP DNS 1 = gateway and DNS 2 = Pi-hole
            foreach (var network in networks)
            {
                if (network.DnsServers == null) continue;
                foreach (var dnsServer in network.DnsServers)
                {
                    if (!string.IsNullOrEmpty(dnsServer) && dnsServer == network.Gateway)
                    {
                        validDestinations.Add(dnsServer);
                    }
                }
            }

            result.ExpectedDnatDestinations.AddRange(validDestinations);

            foreach (var rule in coverageResult.Rules)
            {
                if (string.IsNullOrEmpty(rule.RedirectIp))
                    continue;

                if (!IsValidRedirectTarget(rule.RedirectIp, validDestinations))
                {
                    result.DnatRedirectTargetIsValid = false;
                    result.InvalidDnatRules.Add(
                        $"Rule '{rule.Description ?? rule.Id}' redirects to {rule.RedirectIp}");
                }
            }
        }
        else
        {
            // No site-wide third-party DNS: validate each rule against its network's DHCP DNS servers
            // If a network has DHCP DNS configured, DNAT should redirect to those servers
            // If no DHCP DNS but DoH is configured, validate against gateways
            // If neither, skip validation

            // Build lookup of network ID to DNS servers and gateway
            var networkDnsMap = networks
                .ToDictionary(
                    n => n.Id,
                    n => new { DnsServers = n.DnsServers ?? new List<string>(), Gateway = n.Gateway ?? string.Empty },
                    StringComparer.OrdinalIgnoreCase);

            // Find native VLAN gateway for DoH fallback
            var nativeNetwork = networks.FirstOrDefault(n => n.IsNative || n.VlanId == 1);
            var nativeGateway = nativeNetwork?.Gateway;

            // Track all valid destinations for reporting
            var allValidDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rule in coverageResult.Rules)
            {
                if (string.IsNullOrEmpty(rule.RedirectIp))
                    continue;

                // Build valid destinations for THIS rule based on the network's DHCP DNS settings
                var ruleValidDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var ruleNetworkId = rule.InInterface ?? rule.NetworkId;
                if (!string.IsNullOrEmpty(ruleNetworkId) && networkDnsMap.TryGetValue(ruleNetworkId, out var networkConfig))
                {
                    // If network has DHCP DNS servers configured, those are the valid destinations
                    if (networkConfig.DnsServers.Any(s => !string.IsNullOrEmpty(s)))
                    {
                        foreach (var dns in networkConfig.DnsServers.Where(s => !string.IsNullOrEmpty(s)))
                        {
                            ruleValidDestinations.Add(dns);
                            allValidDestinations.Add(dns);
                        }
                    }
                    else if (result.DohConfigured)
                    {
                        // No DHCP DNS configured but DoH is enabled - validate against gateways
                        // Native VLAN gateway is always valid
                        if (!string.IsNullOrEmpty(nativeGateway))
                        {
                            ruleValidDestinations.Add(nativeGateway);
                            allValidDestinations.Add(nativeGateway);
                        }
                        // Network's own gateway is also valid
                        if (!string.IsNullOrEmpty(networkConfig.Gateway))
                        {
                            ruleValidDestinations.Add(networkConfig.Gateway);
                            allValidDestinations.Add(networkConfig.Gateway);
                        }
                    }
                    // else: No DHCP DNS and no DoH - skip validation for this rule
                }

                if (ruleValidDestinations.Count == 0)
                {
                    // Can't determine expected destination - skip validation for this rule
                    continue;
                }

                if (!IsValidRedirectTarget(rule.RedirectIp, ruleValidDestinations))
                {
                    result.DnatRedirectTargetIsValid = false;
                    var expectedDns = string.Join(" or ", ruleValidDestinations);
                    result.InvalidDnatRules.Add(
                        $"Rule '{rule.Description ?? rule.Id}' redirects to {rule.RedirectIp} (expected {expectedDns})");
                }
            }

            result.ExpectedDnatDestinations.AddRange(allValidDestinations);
        }

        if (!result.DnatRedirectTargetIsValid)
        {
            _logger.LogWarning(
                "DNAT DNS rules redirect to incorrect destinations. Invalid rules: {InvalidRules}. Expected: {Expected}",
                string.Join("; ", result.InvalidDnatRules),
                string.Join(", ", result.ExpectedDnatDestinations));
        }
    }

    /// <summary>
    /// Validate that DNAT destination filters are not restricted to specific IPs.
    /// Valid configurations:
    /// - No destination address (Any)
    /// - Destination address with invert_address=true (matches traffic NOT going to DNS server)
    /// Invalid:
    /// - Specific destination address without invert (only catches some bypass attempts)
    /// </summary>
    private void ValidateDnatDestinationFilters(
        DnatCoverageResult coverageResult,
        DnsSecurityResult result)
    {
        if (!coverageResult.HasDnatDnsRules)
            return;

        foreach (var rule in coverageResult.Rules)
        {
            if (rule.HasRestrictedDestination)
            {
                result.DnatDestinationFilterIsValid = false;
                result.RestrictedDestinationRules.Add(
                    $"Rule '{rule.Description ?? rule.Id}' only matches traffic to {rule.DestinationAddress}");
            }
        }

        if (!result.DnatDestinationFilterIsValid)
        {
            _logger.LogWarning(
                "DNAT DNS rules have restricted destination filters: {Rules}",
                string.Join("; ", result.RestrictedDestinationRules));
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
            DnsLeakProtection = result.HasDns53BlockRule || (result.DnatProvidesFullCoverage && result.DnatRedirectTargetIsValid && result.DnatDestinationFilterIsValid),
            HasDns53BlockRule = result.HasDns53BlockRule,
            DnatProvidesFullCoverage = result.DnatProvidesFullCoverage && result.DnatRedirectTargetIsValid && result.DnatDestinationFilterIsValid,
            DotBlocked = result.HasDotBlockRule,
            DohBypassBlocked = result.HasDohBlockRule,
            DoqBypassBlocked = result.HasDoqBlockRule,
            FullyProtected = result.DohConfigured && (result.HasDns53BlockRule || (result.DnatProvidesFullCoverage && result.DnatRedirectTargetIsValid && result.DnatDestinationFilterIsValid)) && result.HasDotBlockRule && result.HasDohBlockRule && result.HasDoqBlockRule && result.WanDnsMatchesDoH && result.DeviceDnsPointsToGateway,
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

    /// <summary>
    /// Parse an IP address or IP range into a list of individual IPs.
    /// Delegates to NetworkUtilities.ExpandIpRange.
    /// </summary>
    public static List<string> ParseIpOrRange(string? ipOrRange)
        => NetworkUtilities.ExpandIpRange(ipOrRange);

    /// <summary>
    /// Check if a redirect IP (which may be a range) is valid against the set of expected destinations.
    /// For ranges, ALL IPs in the range must be valid destinations.
    /// Uses normalized IP comparison to handle IPv6 address format variations.
    /// </summary>
    public static bool IsValidRedirectTarget(string? redirectIp, HashSet<string> validDestinations)
    {
        if (string.IsNullOrEmpty(redirectIp))
            return true; // No redirect IP to validate

        var ips = ParseIpOrRange(redirectIp);
        if (ips.Count == 0)
            return true;

        // All IPs in the range must be valid destinations
        // Use IP address comparison to handle IPv6 format variations
        return ips.All(ip => IsIpInValidDestinations(ip, validDestinations));
    }

    /// <summary>
    /// Check if an IP address is in the set of valid destinations.
    /// Uses normalized IP comparison to handle IPv6 address format variations.
    /// </summary>
    private static bool IsIpInValidDestinations(string ip, HashSet<string> validDestinations)
    {
        // First try simple string match (fast path for most cases)
        if (validDestinations.Contains(ip))
            return true;

        // For IPv6, try normalized comparison in case of format differences
        foreach (var validDest in validDestinations)
        {
            if (NetworkUtilities.IpAddressesAreEqual(ip, validDest))
                return true;
        }

        return false;
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
    public bool HasDoh3BlockRule { get; set; }
    public string? Doh3RuleName { get; set; }
    public List<string> DohBlockedDomains { get; } = new();
    public List<string> DoqBlockedDomains { get; } = new();

    // DNS53 Firewall Rule Network Coverage
    /// <summary>
    /// Whether DNS53 blocking rules provide full coverage across all networks
    /// </summary>
    public bool Dns53ProvidesFullCoverage { get; set; }
    /// <summary>
    /// Network IDs covered by DNS53 blocking rules
    /// </summary>
    public HashSet<string> Dns53CoveredNetworkIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Network names covered by DNS53 blocking rules
    /// </summary>
    public List<string> Dns53CoveredNetworks { get; } = new();
    /// <summary>
    /// Network names NOT covered by DNS53 blocking rules
    /// </summary>
    public List<string> Dns53UncoveredNetworks { get; } = new();

    // Device DNS Configuration
    public bool DeviceDnsPointsToGateway { get; set; } = true;
    public int TotalDevicesChecked { get; set; }
    public int DevicesWithCorrectDns { get; set; }
    public int DhcpDeviceCount { get; set; }
    public List<DeviceDnsInfo> DeviceDnsDetails { get; } = new();

    // Third-Party DNS (Pi-hole, AdGuard Home, etc.)
    public bool HasThirdPartyDns { get; set; }
    public List<ThirdPartyDnsDetector.ThirdPartyDnsInfo> ThirdPartyDnsServers { get; } = new();
    public bool IsPiholeDetected => ThirdPartyDnsServers.Any(t => t.IsPihole);
    public bool IsAdGuardHomeDetected => ThirdPartyDnsServers.Any(t => t.IsAdGuardHome);
    public string? ThirdPartyDnsProviderName { get; set; }

    /// <summary>
    /// Whether third-party DNS is configured as a site-wide solution (on at least one non-Corporate network).
    /// If third-party DNS is ONLY on Corporate networks, it's considered specialized internal DNS,
    /// not intended for all networks, and won't be used as the expected DNAT destination.
    /// </summary>
    public bool IsSiteWideThirdPartyDns { get; set; }

    /// <summary>
    /// The IPs of the site-wide third-party DNS servers (only populated if IsSiteWideThirdPartyDns is true)
    /// </summary>
    public List<string> SiteWideDnsServerIps { get; } = new();

    // External Public DNS Detection
    public bool HasExternalDns { get; set; }
    public List<ThirdPartyDnsDetector.ExternalDnsInfo> ExternalDnsNetworks { get; } = new();

    // DNAT DNS Coverage
    public bool HasDnatDnsRules { get; set; }
    public bool DnatProvidesFullCoverage { get; set; }
    public string? DnatRedirectTarget { get; set; }
    public List<string> DnatCoveredNetworks { get; } = new();
    public List<string> DnatUncoveredNetworks { get; } = new();
    public List<string> DnatSingleIpRules { get; } = new();

    // DNAT Redirect Destination Validation
    public bool DnatRedirectTargetIsValid { get; set; } = true;
    public List<string> InvalidDnatRules { get; } = new();
    public List<string> ExpectedDnatDestinations { get; } = new();

    // DNAT Destination Filter Validation
    public bool DnatDestinationFilterIsValid { get; set; } = true;
    public List<string> RestrictedDestinationRules { get; } = new();

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
    public bool HasDns53BlockRule { get; init; }
    public bool DnatProvidesFullCoverage { get; init; }
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
