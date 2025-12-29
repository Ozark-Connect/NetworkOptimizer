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

        // Analyze firewall rules
        if (firewallData.HasValue)
        {
            AnalyzeFirewallRules(firewallData.Value, result);
        }
        else
        {
            _logger.LogWarning("No firewall data available for DNS security analysis");
        }

        // Generate issues based on findings
        GenerateAuditIssues(result);

        _logger.LogInformation("DNS security analysis complete: DoH={DoHState}, Firewall rules found: DNS53={Dns53}, DoT={DoT}, DoH={DoHBlock}",
            result.DohState, result.HasDns53BlockRule, result.HasDotBlockRule, result.HasDohBlockRule);

        return result;
    }

    private void AnalyzeDohConfiguration(JsonElement settings, DnsSecurityResult result)
    {
        // Look for DoH configuration in settings array
        foreach (var setting in settings.UnwrapDataArray())
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
            FullyProtected = result.DohConfigured && result.HasDns53BlockRule && result.HasDotBlockRule && result.HasDohBlockRule,
            IssueCount = result.Issues.Count,
            CriticalIssueCount = result.Issues.Count(i => i.Severity == AuditSeverity.Critical)
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

    // Firewall Rules
    public bool HasDns53BlockRule { get; set; }
    public string? Dns53RuleName { get; set; }
    public bool HasDotBlockRule { get; set; }
    public string? DotRuleName { get; set; }
    public bool HasDohBlockRule { get; set; }
    public string? DohRuleName { get; set; }
    public List<string> DohBlockedDomains { get; } = new();
    public bool HasQuicBlockRule { get; set; }

    // Audit Issues
    public List<AuditIssue> Issues { get; } = new();
    public List<string> HardeningNotes { get; } = new();
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
}
