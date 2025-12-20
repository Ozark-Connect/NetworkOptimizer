using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Helpers;

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

        foreach (var device in deviceData.UnwrapDataArray())
        {
            var portTableItems = device.GetArrayOrEmpty("port_table").ToList();
            if (portTableItems.Count == 0)
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
        var deviceType = device.GetStringOrNull("type");
        var isGateway = UniFiDeviceTypes.IsGateway(deviceType);
        var rawName = device.GetStringFromAny("name", "mac") ?? "Unknown";
        var name = CleanDeviceName(rawName);

        var mac = device.GetStringOrNull("mac");
        var model = device.GetStringOrNull("model");
        var shortname = device.GetStringOrNull("shortname");
        var modelDisplay = device.GetStringOrNull("model_display");
        var modelName = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(model, shortname, modelDisplay);
        var ip = device.GetStringOrNull("ip");
        var capabilities = ParseSwitchCapabilities(device);

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

        var ports = device.GetArrayOrEmpty("port_table")
            .Select(port => ParsePort(port, switchInfoPlaceholder, networks))
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
