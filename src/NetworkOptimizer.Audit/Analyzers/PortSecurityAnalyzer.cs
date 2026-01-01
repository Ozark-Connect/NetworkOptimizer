using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Analyzes port and switch configuration for security issues.
/// Evaluates VLAN placement, MAC restrictions, port isolation, and unused ports.
/// </summary>
public class PortSecurityAnalyzer
{
    private readonly ILogger<PortSecurityAnalyzer> _logger;
    private readonly List<IAuditRule> _rules;
    private readonly List<IWirelessAuditRule> _wirelessRules;
    private readonly DeviceTypeDetectionService? _detectionService;

    public PortSecurityAnalyzer(ILogger<PortSecurityAnalyzer> logger)
        : this(logger, null)
    {
    }

    public PortSecurityAnalyzer(
        ILogger<PortSecurityAnalyzer> logger,
        DeviceTypeDetectionService? detectionService)
    {
        _logger = logger;
        _detectionService = detectionService;
        _rules = InitializeRules();
        _wirelessRules = InitializeWirelessRules();

        // Inject detection service into rules
        if (_detectionService != null)
        {
            foreach (var rule in _rules.OfType<AuditRuleBase>())
            {
                rule.SetDetectionService(_detectionService);
            }
            _logger.LogInformation("Enhanced device detection enabled for audit rules");
        }
    }

    /// <summary>
    /// Initialize all audit rules
    /// </summary>
    private List<IAuditRule> InitializeRules()
    {
        return new List<IAuditRule>
        {
            new IotVlanRule(),
            new CameraVlanRule(),
            new MacRestrictionRule(),
            new UnusedPortRule(),
            new PortIsolationRule()
        };
    }

    /// <summary>
    /// Initialize wireless audit rules
    /// </summary>
    private List<IWirelessAuditRule> InitializeWirelessRules()
    {
        return new List<IWirelessAuditRule>
        {
            new WirelessIotVlanRule(),
            new WirelessCameraVlanRule()
        };
    }

    /// <summary>
    /// Add a custom rule to the engine
    /// </summary>
    public void AddRule(IAuditRule rule)
    {
        _rules.Add(rule);
    }

    /// <summary>
    /// Extract switch and port information from UniFi device JSON
    /// </summary>
    public List<SwitchInfo> ExtractSwitches(JsonElement deviceData, List<NetworkInfo> networks)
        => ExtractSwitches(deviceData, networks, clients: null);

    /// <summary>
    /// Extract switch and port information from UniFi device JSON with client correlation
    /// </summary>
    /// <param name="deviceData">UniFi device JSON data</param>
    /// <param name="networks">Network configuration list</param>
    /// <param name="clients">Connected clients for port correlation (optional)</param>
    public List<SwitchInfo> ExtractSwitches(JsonElement deviceData, List<NetworkInfo> networks, List<UniFiClientResponse>? clients)
    {
        var switches = new List<SwitchInfo>();

        // Build lookup for clients by switch MAC + port for O(1) correlation
        var clientsByPort = BuildClientPortLookup(clients);
        if (clientsByPort.Count > 0)
        {
            _logger.LogDebug("Built client lookup with {Count} wired clients for port correlation", clientsByPort.Count);
        }

        foreach (var device in deviceData.UnwrapDataArray())
        {
            var portTableItems = device.GetArrayOrEmpty("port_table").ToList();
            if (portTableItems.Count == 0)
                continue;

            var switchInfo = ParseSwitch(device, networks, clientsByPort);
            if (switchInfo != null)
            {
                switches.Add(switchInfo);
                var clientCount = switchInfo.Ports.Count(p => p.ConnectedClient != null);
                _logger.LogInformation("Discovered switch: {Name} with {PortCount} ports ({ClientCount} with client data)",
                    switchInfo.Name, switchInfo.Ports.Count, clientCount);
            }
        }

        // Sort: gateway first, then by name
        return switches.OrderBy(s => s.IsGateway ? 0 : 1).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Build lookup dictionary for clients by switch MAC and port index
    /// </summary>
    private Dictionary<(string SwitchMac, int PortIndex), UniFiClientResponse> BuildClientPortLookup(List<UniFiClientResponse>? clients)
    {
        var lookup = new Dictionary<(string, int), UniFiClientResponse>();
        if (clients == null) return lookup;

        foreach (var client in clients)
        {
            // Only wired clients have switch port info
            if (client.IsWired && !string.IsNullOrEmpty(client.SwMac) && client.SwPort.HasValue)
            {
                var key = (client.SwMac.ToLowerInvariant(), client.SwPort.Value);
                // If multiple clients on same port (shouldn't happen normally), keep first
                if (!lookup.ContainsKey(key))
                {
                    lookup[key] = client;
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Parse a single switch from JSON
    /// </summary>
    private SwitchInfo? ParseSwitch(JsonElement device, List<NetworkInfo> networks, Dictionary<(string, int), UniFiClientResponse> clientsByPort)
    {
        var deviceType = device.GetStringOrNull("type");
        var isGateway = UniFiDeviceTypes.IsGateway(deviceType);
        var name = device.GetStringFromAny("name", "mac") ?? "Unknown";

        var mac = device.GetStringOrNull("mac");
        var model = device.GetStringOrNull("model");
        var shortname = device.GetStringOrNull("shortname");
        var modelDisplay = device.GetStringOrNull("model_display");
        var modelName = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(model, shortname, modelDisplay);
        var ip = device.GetStringOrNull("ip");
        var capabilities = ParseSwitchCapabilities(device);

        // Extract DNS configuration from config_network
        string? dns1 = null;
        string? dns2 = null;
        string? networkConfigType = null;
        if (device.TryGetProperty("config_network", out var configNetwork))
        {
            dns1 = configNetwork.GetStringOrNull("dns1");
            dns2 = configNetwork.GetStringOrNull("dns2");
            networkConfigType = configNetwork.GetStringOrNull("type"); // dhcp or static
        }

        var switchInfoPlaceholder = new SwitchInfo
        {
            Name = name,
            MacAddress = mac,
            Model = model,
            ModelName = modelName,
            Type = deviceType,
            IpAddress = ip,
            ConfiguredDns1 = dns1,
            ConfiguredDns2 = dns2,
            NetworkConfigType = networkConfigType,
            IsGateway = isGateway,
            Capabilities = capabilities
        };

        var ports = device.GetArrayOrEmpty("port_table")
            .Select(port => ParsePort(port, switchInfoPlaceholder, networks, clientsByPort))
            .Where(p => p != null)
            .Cast<PortInfo>()
            .ToList();

        return new SwitchInfo
        {
            Name = name,
            MacAddress = mac,
            Model = model,
            ModelName = modelName,
            Type = deviceType,
            IpAddress = ip,
            ConfiguredDns1 = dns1,
            ConfiguredDns2 = dns2,
            NetworkConfigType = networkConfigType,
            IsGateway = isGateway,
            Capabilities = capabilities,
            Ports = ports
        };
    }

    /// <summary>
    /// Parse switch capabilities
    /// </summary>
    private SwitchCapabilities ParseSwitchCapabilities(JsonElement device)
    {
        var caps = new SwitchCapabilities();

        if (device.TryGetProperty("switch_caps", out var switchCaps))
        {
            if (switchCaps.TryGetProperty("max_custom_mac_acls", out var maxAclsProp))
            {
                return new SwitchCapabilities
                {
                    MaxCustomMacAcls = maxAclsProp.GetInt32()
                };
            }
        }

        return caps;
    }

    /// <summary>
    /// Parse a single port from JSON
    /// </summary>
    private PortInfo? ParsePort(JsonElement port, SwitchInfo switchInfo, List<NetworkInfo> networks, Dictionary<(string, int), UniFiClientResponse> clientsByPort)
    {
        var portIdx = port.GetIntOrDefault("port_idx", -1);
        if (portIdx < 0)
            return null;

        var portName = port.GetStringOrDefault("name", $"Port {portIdx}");
        var forwardMode = port.GetStringOrDefault("forward", "all");
        if (forwardMode == "customize")
            forwardMode = "custom";

        var networkName = port.GetStringOrNull("network_name")?.ToLowerInvariant();
        var isWan = networkName?.StartsWith("wan") ?? false;

        var poeEnable = port.GetBoolOrDefault("poe_enable");
        var portPoe = port.GetBoolOrDefault("port_poe");
        var poeMode = port.GetStringOrNull("poe_mode");

        // Look up connected client for this port
        UniFiClientResponse? connectedClient = null;
        if (!string.IsNullOrEmpty(switchInfo.MacAddress))
        {
            var key = (switchInfo.MacAddress.ToLowerInvariant(), portIdx);
            clientsByPort.TryGetValue(key, out connectedClient);
        }

        return new PortInfo
        {
            PortIndex = portIdx,
            Name = portName,
            IsUp = port.GetBoolOrDefault("up"),
            Speed = port.GetIntOrDefault("speed"),
            ForwardMode = forwardMode,
            IsUplink = port.GetBoolOrDefault("is_uplink"),
            IsWan = isWan,
            NativeNetworkId = port.GetStringOrNull("native_networkconf_id"),
            ExcludedNetworkIds = port.GetStringArrayOrNull("excluded_networkconf_ids"),
            PortSecurityEnabled = port.GetBoolOrDefault("port_security_enabled"),
            AllowedMacAddresses = port.GetStringArrayOrNull("port_security_mac_address"),
            IsolationEnabled = port.GetBoolOrDefault("isolation"),
            PoeEnabled = poeEnable || portPoe,
            PoePower = port.GetDoubleOrDefault("poe_power"),
            PoeMode = poeMode,
            SupportsPoe = portPoe || !string.IsNullOrEmpty(poeMode),
            Switch = switchInfo,
            ConnectedClient = connectedClient
        };
    }


    /// <summary>
    /// Analyze all ports across all switches
    /// </summary>
    public List<AuditIssue> AnalyzePorts(List<SwitchInfo> switches, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        foreach (var switchInfo in switches)
        {
            _logger.LogDebug("Analyzing {PortCount} ports on {SwitchName}",
                switchInfo.Ports.Count, switchInfo.Name);

            foreach (var port in switchInfo.Ports)
            {
                // Run all enabled rules against this port
                foreach (var rule in _rules.Where(r => r.Enabled))
                {
                    var issue = rule.Evaluate(port, networks);
                    if (issue != null)
                    {
                        issues.Add(issue);
                        _logger.LogDebug("Rule {RuleId} found issue on {Switch} port {Port}: {Message}",
                            rule.RuleId, switchInfo.Name, port.PortIndex, issue.Message);
                    }
                }
            }
        }

        _logger.LogInformation("Found {IssueCount} issues across {SwitchCount} switches",
            issues.Count, switches.Count);

        return issues;
    }

    /// <summary>
    /// Analyze hardening measures already in place
    /// </summary>
    public List<string> AnalyzeHardening(List<SwitchInfo> switches, List<NetworkInfo> networks)
    {
        var measures = new List<string>();

        var totalPorts = switches.Sum(s => s.Ports.Count);
        var disabledPorts = switches.Sum(s => s.Ports.Count(p => p.ForwardMode == "disabled"));
        var securityEnabledPorts = switches.Sum(s => s.Ports.Count(p => p.PortSecurityEnabled));
        var macRestrictedPorts = switches.Sum(s => s.Ports.Count(p => p.AllowedMacAddresses?.Any() ?? false));
        var isolatedPorts = switches.Sum(s => s.Ports.Count(p => p.IsolationEnabled));

        // Check for disabled ports
        if (disabledPorts > 0)
        {
            var percentage = (double)disabledPorts / totalPorts * 100;
            measures.Add($"{disabledPorts} unused ports disabled ({percentage:F0}% of total ports)");
        }

        // Check for port security
        if (securityEnabledPorts > 0)
        {
            measures.Add($"Port security enabled on {securityEnabledPorts} ports");
        }

        // Check for MAC restrictions
        if (macRestrictedPorts > 0)
        {
            measures.Add($"MAC restrictions configured on {macRestrictedPorts} access ports");
        }

        // Check for cameras on Security VLAN
        var cameraPorts = switches.SelectMany(s => s.Ports)
            .Where(p => IsCameraDeviceName(p.Name) && p.IsUp)
            .ToList();

        if (cameraPorts.Any())
        {
            var securityNetwork = networks.FirstOrDefault(n => n.Purpose == NetworkPurpose.Security);
            if (securityNetwork != null)
            {
                var camerasOnSecurityVlan = cameraPorts.Count(p => p.NativeNetworkId == securityNetwork.Id);
                if (camerasOnSecurityVlan > 0)
                {
                    measures.Add($"{camerasOnSecurityVlan} cameras properly isolated on Security VLAN");
                }
            }
        }

        // Check for isolated security devices
        if (isolatedPorts > 0)
        {
            var isolatedCameras = switches.SelectMany(s => s.Ports)
                .Count(p => p.IsolationEnabled && IsCameraDeviceName(p.Name));

            if (isolatedCameras > 0)
            {
                measures.Add($"{isolatedCameras} security devices have port isolation enabled");
            }
        }

        return measures;
    }

    /// <summary>
    /// Calculate statistics for the audit
    /// </summary>
    public AuditStatistics CalculateStatistics(List<SwitchInfo> switches)
    {
        var stats = new AuditStatistics();

        stats.TotalPorts = switches.Sum(s => s.Ports.Count);
        stats.DisabledPorts = switches.Sum(s => s.Ports.Count(p => p.ForwardMode == "disabled"));
        stats.ActivePorts = switches.Sum(s => s.Ports.Count(p => p.IsUp));
        stats.MacRestrictedPorts = switches.Sum(s => s.Ports.Count(p => p.AllowedMacAddresses?.Any() ?? false));
        stats.PortSecurityEnabledPorts = switches.Sum(s => s.Ports.Count(p => p.PortSecurityEnabled));
        stats.IsolatedPorts = switches.Sum(s => s.Ports.Count(p => p.IsolationEnabled));

        // Calculate unprotected active ports
        stats.UnprotectedActivePorts = switches.Sum(s => s.Ports.Count(p =>
            p.IsUp &&
            p.ForwardMode == "native" &&
            !p.IsUplink &&
            !p.IsWan &&
            !(p.AllowedMacAddresses?.Any() ?? false) &&
            !p.PortSecurityEnabled));

        return stats;
    }

    /// <summary>
    /// Helper to check if port name is a camera
    /// </summary>
    private static bool IsCameraDeviceName(string? portName) => DeviceNameHints.IsCameraDeviceName(portName);

    /// <summary>
    /// Access point info for lookup
    /// </summary>
    public record ApInfo(string Name, string? Model, string? ModelName);

    /// <summary>
    /// Extract access points from device data for AP name lookup
    /// </summary>
    public Dictionary<string, string> ExtractAccessPointLookup(JsonElement deviceData)
    {
        // Return simple name lookup for backwards compatibility
        return ExtractAccessPointInfoLookup(deviceData)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extract access points with full info (name, model) from device data
    /// </summary>
    public Dictionary<string, ApInfo> ExtractAccessPointInfoLookup(JsonElement deviceData)
    {
        var apsByMac = new Dictionary<string, ApInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in deviceData.UnwrapDataArray())
        {
            var deviceType = device.GetStringOrNull("type");
            var isAccessPoint = device.GetBoolOrDefault("is_access_point", false);

            // Include both type=uap devices and devices with is_access_point=true
            if (deviceType == "uap" || isAccessPoint)
            {
                var mac = device.GetStringOrNull("mac");
                var name = device.GetStringFromAny("name", "mac") ?? "Unknown AP";
                var model = device.GetStringOrNull("model");
                var shortname = device.GetStringOrNull("shortname");
                var modelDisplay = device.GetStringOrNull("model_display");
                var modelName = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(model, shortname, modelDisplay);

                if (!string.IsNullOrEmpty(mac) && !apsByMac.ContainsKey(mac))
                {
                    apsByMac[mac] = new ApInfo(name, model, modelName);
                    _logger.LogDebug("Found AP: {Name} ({Mac}) - {ModelName}", name, mac, modelName);
                }
            }
        }

        _logger.LogInformation("Extracted {Count} access points for lookup", apsByMac.Count);
        return apsByMac;
    }

    /// <summary>
    /// Extract wireless clients from client list for audit analysis
    /// </summary>
    /// <param name="clients">All connected clients</param>
    /// <param name="networks">Network configuration list</param>
    /// <param name="apLookup">AP MAC to name lookup dictionary</param>
    /// <returns>Wireless clients with detection results</returns>
    public List<WirelessClientInfo> ExtractWirelessClients(
        List<UniFiClientResponse>? clients,
        List<NetworkInfo> networks,
        Dictionary<string, string>? apLookup = null)
        => ExtractWirelessClients(clients, networks,
            apLookup?.ToDictionary(kvp => kvp.Key, kvp => new ApInfo(kvp.Value, null, null), StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Extract wireless clients from client list for audit analysis with full AP info
    /// </summary>
    public List<WirelessClientInfo> ExtractWirelessClients(
        List<UniFiClientResponse>? clients,
        List<NetworkInfo> networks,
        Dictionary<string, ApInfo>? apInfoLookup)
    {
        var wirelessClients = new List<WirelessClientInfo>();
        if (clients == null) return wirelessClients;

        var apsByMac = apInfoLookup ?? new Dictionary<string, ApInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var client in clients)
        {
            // Only process wireless clients
            if (client.IsWired)
                continue;

            // Run device detection
            var detection = _detectionService?.DetectDeviceType(client)
                ?? DeviceDetectionResult.Unknown;

            // Skip Unknown devices - no point auditing what we can't identify
            if (detection.Category == ClientDeviceCategory.Unknown)
                continue;

            // Lookup network by client's NetworkId
            var network = networks.FirstOrDefault(n => n.Id == client.NetworkId);

            // Lookup AP info
            ApInfo? apInfo = null;
            if (!string.IsNullOrEmpty(client.ApMac))
            {
                apsByMac.TryGetValue(client.ApMac.ToLowerInvariant(), out apInfo);
            }

            wirelessClients.Add(new WirelessClientInfo
            {
                Client = client,
                Network = network,
                Detection = detection,
                AccessPointName = apInfo?.Name,
                AccessPointMac = client.ApMac,
                AccessPointModel = apInfo?.Model,
                AccessPointModelName = apInfo?.ModelName
            });

            _logger.LogDebug("Wireless client: {Name} ({Mac}) on {Network} - detected as {Category}",
                client.Name ?? client.Hostname ?? client.Mac,
                client.Mac,
                network?.Name ?? "Unknown",
                detection.CategoryName);
        }

        _logger.LogInformation("Extracted {Count} wireless clients for audit analysis", wirelessClients.Count);
        return wirelessClients;
    }

    /// <summary>
    /// Analyze wireless clients for VLAN placement issues
    /// </summary>
    public List<AuditIssue> AnalyzeWirelessClients(List<WirelessClientInfo> wirelessClients, List<NetworkInfo> networks)
    {
        var issues = new List<AuditIssue>();

        foreach (var client in wirelessClients)
        {
            foreach (var rule in _wirelessRules.Where(r => r.Enabled))
            {
                var issue = rule.Evaluate(client, networks);
                if (issue != null)
                {
                    issues.Add(issue);
                    _logger.LogDebug("Wireless rule {RuleId} found issue for {Client}: {Message}",
                        rule.RuleId, client.DisplayName, issue.Message);
                }
            }
        }

        _logger.LogInformation("Found {IssueCount} wireless client issues", issues.Count);
        return issues;
    }
}
