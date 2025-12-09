using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Main security audit engine for port and switch configuration analysis
/// </summary>
public class SecurityAuditEngine
{
    private readonly ILogger<SecurityAuditEngine> _logger;
    private readonly List<IAuditRule> _rules;

    public SecurityAuditEngine(ILogger<SecurityAuditEngine> logger)
    {
        _logger = logger;
        _rules = InitializeRules();
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
    {
        var switches = new List<SwitchInfo>();

        // Handle both single device and array of devices
        var devices = deviceData.ValueKind == JsonValueKind.Array
            ? deviceData.EnumerateArray().ToList()
            : new List<JsonElement> { deviceData };

        // Handle wrapped response with "data" property
        if (deviceData.ValueKind == JsonValueKind.Object && deviceData.TryGetProperty("data", out var dataArray))
        {
            devices = dataArray.EnumerateArray().ToList();
        }

        foreach (var device in devices)
        {
            // Check if device has port_table
            if (!device.TryGetProperty("port_table", out var portTable) || portTable.ValueKind != JsonValueKind.Array)
                continue;

            var portArray = portTable.EnumerateArray().ToList();
            if (portArray.Count == 0)
                continue;

            var switchInfo = ParseSwitch(device, networks);
            if (switchInfo != null)
            {
                switches.Add(switchInfo);
                _logger.LogInformation("Discovered switch: {Name} with {PortCount} ports",
                    switchInfo.Name, switchInfo.Ports.Count);
            }
        }

        // Sort: gateway first, then by name
        return switches.OrderBy(s => s.IsGateway ? 0 : 1).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Parse a single switch from JSON
    /// </summary>
    private SwitchInfo? ParseSwitch(JsonElement device, List<NetworkInfo> networks)
    {
        // Get device type
        var deviceType = device.TryGetProperty("type", out var typeProp)
            ? typeProp.GetString()
            : null;

        var isGateway = deviceType is "udm" or "ugw" or "uxg";

        // Get device name
        var rawName = device.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()
            : device.TryGetProperty("mac", out var macProp)
                ? macProp.GetString()
                : "Unknown";

        var name = CleanDeviceName(rawName ?? "Unknown");

        // Get MAC address
        var mac = device.TryGetProperty("mac", out var macAddrProp)
            ? macAddrProp.GetString()
            : null;

        // Get model
        var model = device.TryGetProperty("model", out var modelProp)
            ? modelProp.GetString()
            : null;

        var modelName = GetFriendlyModelName(model);

        // Get IP
        var ip = device.TryGetProperty("ip", out var ipProp)
            ? ipProp.GetString()
            : null;

        // Get switch capabilities
        var capabilities = ParseSwitchCapabilities(device);

        // Parse ports
        var ports = new List<PortInfo>();
        if (device.TryGetProperty("port_table", out var portTable) && portTable.ValueKind == JsonValueKind.Array)
        {
            var switchInfoPlaceholder = new SwitchInfo
            {
                Name = name,
                MacAddress = mac,
                Model = model,
                ModelName = modelName,
                Type = deviceType,
                IpAddress = ip,
                IsGateway = isGateway,
                Capabilities = capabilities
            };

            foreach (var port in portTable.EnumerateArray())
            {
                var portInfo = ParsePort(port, switchInfoPlaceholder, networks);
                if (portInfo != null)
                    ports.Add(portInfo);
            }
        }

        return new SwitchInfo
        {
            Name = name,
            MacAddress = mac,
            Model = model,
            ModelName = modelName,
            Type = deviceType,
            IpAddress = ip,
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
    private PortInfo? ParsePort(JsonElement port, SwitchInfo switchInfo, List<NetworkInfo> networks)
    {
        // Get port index
        if (!port.TryGetProperty("port_idx", out var portIdxProp))
            return null;

        var portIdx = portIdxProp.GetInt32();

        // Get port name
        var portName = port.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString()
            : $"Port {portIdx}";

        // Get link status
        var isUp = port.TryGetProperty("up", out var upProp) && upProp.GetBoolean();

        // Get speed
        var speed = port.TryGetProperty("speed", out var speedProp) && speedProp.ValueKind == JsonValueKind.Number
            ? speedProp.GetInt32()
            : 0;

        // Get forward mode
        var forwardMode = port.TryGetProperty("forward", out var forwardProp)
            ? forwardProp.GetString()
            : "all";

        if (forwardMode == "customize")
            forwardMode = "custom";

        // Check if uplink
        var isUplink = port.TryGetProperty("is_uplink", out var uplinkProp) && uplinkProp.GetBoolean();

        // Check if WAN
        var isWan = false;
        if (port.TryGetProperty("network_name", out var netNameProp))
        {
            var networkName = netNameProp.GetString()?.ToLowerInvariant();
            isWan = networkName?.StartsWith("wan") ?? false;
        }

        // Get native network
        var nativeNetworkId = port.TryGetProperty("native_networkconf_id", out var nativeNetProp)
            ? nativeNetProp.GetString()
            : null;

        // Get excluded networks
        List<string>? excludedNetworks = null;
        if (port.TryGetProperty("excluded_networkconf_ids", out var excludedProp) &&
            excludedProp.ValueKind == JsonValueKind.Array)
        {
            excludedNetworks = excludedProp.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Get port security
        var portSecurityEnabled = port.TryGetProperty("port_security_enabled", out var portSecProp) &&
            portSecProp.GetBoolean();

        List<string>? allowedMacs = null;
        if (port.TryGetProperty("port_security_mac_address", out var macsProp) &&
            macsProp.ValueKind == JsonValueKind.Array)
        {
            allowedMacs = macsProp.EnumerateArray()
                .Where(m => m.ValueKind == JsonValueKind.String)
                .Select(m => m.GetString()!)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();
        }

        // Get isolation
        var isolation = port.TryGetProperty("isolation", out var isolationProp) && isolationProp.GetBoolean();

        // Get PoE info
        var poeEnable = port.TryGetProperty("poe_enable", out var poeEnableProp) && poeEnableProp.GetBoolean();
        var portPoe = port.TryGetProperty("port_poe", out var portPoeProp) && portPoeProp.GetBoolean();

        var poePower = 0.0;
        if (port.TryGetProperty("poe_power", out var poePowerProp))
        {
            if (poePowerProp.ValueKind == JsonValueKind.Number)
                poePower = poePowerProp.GetDouble();
            else if (poePowerProp.ValueKind == JsonValueKind.String)
                double.TryParse(poePowerProp.GetString(), out poePower);
        }

        var poeMode = port.TryGetProperty("poe_mode", out var poeModeProp)
            ? poeModeProp.GetString()
            : null;

        return new PortInfo
        {
            PortIndex = portIdx,
            Name = portName,
            IsUp = isUp,
            Speed = speed,
            ForwardMode = forwardMode,
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = nativeNetworkId,
            ExcludedNetworkIds = excludedNetworks,
            PortSecurityEnabled = portSecurityEnabled,
            AllowedMacAddresses = allowedMacs,
            IsolationEnabled = isolation,
            PoeEnabled = poeEnable || portPoe,
            PoePower = poePower,
            PoeMode = poeMode,
            SupportsPoe = portPoe || !string.IsNullOrEmpty(poeMode),
            Switch = switchInfo
        };
    }

    /// <summary>
    /// Clean device name (remove redundant prefixes)
    /// </summary>
    private string CleanDeviceName(string name)
    {
        if (name.StartsWith("[Gateway] "))
            return name.Substring(10);
        if (name.StartsWith("[Switch] "))
            return name.Substring(9);
        return name;
    }

    /// <summary>
    /// Get friendly model name
    /// </summary>
    private string GetFriendlyModelName(string? modelCode)
    {
        if (string.IsNullOrEmpty(modelCode))
            return "Unknown";

        var modelMap = new Dictionary<string, string>
        {
            { "UCG-Ultra", "UCG-Ultra" },
            { "UCGULTRA", "UCG-Ultra" },
            { "UDMA6A8", "UCG-Ultra" },
            { "USW-Enterprise-8-PoE", "USW-Enterprise-8-PoE" },
            { "USWENTERPRISE8POE", "USW-Enterprise-8-PoE" },
            { "USWED76", "USW-Enterprise-8-PoE" },
            { "USW-Flex-Mini", "USW-Flex-Mini" },
            { "USWFLEXMINI", "USW-Flex-Mini" },
            { "USMINI", "USW-Flex-Mini" },
            { "USW-Lite-8-PoE", "USW-Lite-8-PoE" },
            { "USWLITE8POE", "USW-Lite-8-PoE" },
            { "USL8LPB", "USW-Lite-8-PoE" },
            { "USW-Pro-24-PoE", "USW-Pro-24-PoE" },
            { "USW-Pro-48-PoE", "USW-Pro-48-PoE" }
        };

        return modelMap.TryGetValue(modelCode, out var friendlyName) ? friendlyName : modelCode;
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
    private bool IsCameraDeviceName(string? portName)
    {
        if (string.IsNullOrEmpty(portName))
            return false;

        var nameLower = portName.ToLowerInvariant();
        var cameraHints = new[] { "cam", "camera", "ptz", "nvr", "protect" };
        return cameraHints.Any(hint => nameLower.Contains(hint));
    }
}
