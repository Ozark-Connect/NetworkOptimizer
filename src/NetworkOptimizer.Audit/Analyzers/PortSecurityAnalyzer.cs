using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.UniFi.Models;
using static NetworkOptimizer.Core.Enums.DeviceTypeExtensions;
using ProtectCameraCollection = NetworkOptimizer.Core.Models.ProtectCameraCollection;

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
    private ProtectCameraCollection? _protectCameras;

    /// <summary>
    /// The device type detection service used by this analyzer
    /// </summary>
    public DeviceTypeDetectionService? DetectionService => _detectionService;

    /// <summary>
    /// Set the Protect camera collection for network ID override.
    /// When a wireless client matches a Protect device, the Protect API's connection_network_id
    /// will be used instead of the Network API's network_id for VLAN determination.
    /// </summary>
    public void SetProtectCameras(ProtectCameraCollection? protectCameras)
    {
        _protectCameras = protectCameras;
        if (protectCameras != null && protectCameras.Count > 0)
        {
            _logger.LogDebug("PortSecurityAnalyzer: Protect camera collection set with {Count} devices for network override", protectCameras.Count);
        }
    }

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

        // Inject logger into rules that support it
        UnusedPortRule.SetLogger(_logger);
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
            new PortIsolationRule(),
            new WiredSubnetMismatchRule(),
            new AccessPortVlanRule()
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
            new WirelessCameraVlanRule(),
            new VlanSubnetMismatchRule()
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
    /// Set device allowance settings on all rules
    /// </summary>
    public void SetAllowanceSettings(DeviceAllowanceSettings settings)
    {
        foreach (var rule in _rules.OfType<AuditRuleBase>())
        {
            rule.SetAllowanceSettings(settings);
        }
        foreach (var rule in _wirelessRules.OfType<WirelessAuditRuleBase>())
        {
            rule.SetAllowanceSettings(settings);
        }
        _logger.LogDebug("Device allowance settings applied to audit rules");
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
        => ExtractSwitches(deviceData, networks, clients, clientHistory: null);

    /// <summary>
    /// Extract switch and port information from UniFi device JSON with client and history correlation
    /// </summary>
    /// <param name="deviceData">UniFi device JSON data</param>
    /// <param name="networks">Network configuration list</param>
    /// <param name="clients">Connected clients for port correlation (optional)</param>
    /// <param name="clientHistory">Historical clients for offline port correlation (optional)</param>
    public List<SwitchInfo> ExtractSwitches(JsonElement deviceData, List<NetworkInfo> networks, List<UniFiClientResponse>? clients, List<UniFiClientDetailResponse>? clientHistory)
        => ExtractSwitches(deviceData, networks, clients, clientHistory, portProfiles: null);

    /// <summary>
    /// Extract switch and port information from UniFi device JSON with client, history, and port profile correlation
    /// </summary>
    /// <param name="deviceData">UniFi device JSON data</param>
    /// <param name="networks">Network configuration list</param>
    /// <param name="clients">Connected clients for port correlation (optional)</param>
    /// <param name="clientHistory">Historical clients for offline port correlation (optional)</param>
    /// <param name="portProfiles">Port profiles for resolving portconf_id settings (optional)</param>
    public List<SwitchInfo> ExtractSwitches(JsonElement deviceData, List<NetworkInfo> networks, List<UniFiClientResponse>? clients, List<UniFiClientDetailResponse>? clientHistory, List<UniFiPortProfile>? portProfiles)
    {
        // Build port profile lookup by ID
        var profilesById = portProfiles?.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, UniFiPortProfile>(StringComparer.OrdinalIgnoreCase);
        if (profilesById.Count > 0)
        {
            _logger.LogDebug("Built port profile lookup with {Count} profiles", profilesById.Count);
        }
        var switches = new List<SwitchInfo>();

        // Build lookup for clients by switch MAC + port for O(1) correlation
        var clientsByPort = BuildClientPortLookup(clients);
        if (clientsByPort.Count > 0)
        {
            _logger.LogDebug("Built client lookup with {Count} wired clients for port correlation", clientsByPort.Count);
        }

        // Build lookup for historical clients by switch MAC + port for offline device detection
        var historyByPort = BuildClientHistoryPortLookup(clientHistory);
        if (historyByPort.Count > 0)
        {
            _logger.LogDebug("Built client history lookup with {Count} historical wired clients for port correlation", historyByPort.Count);
        }

        // Collect all device MACs for uplink-based gateway detection
        // and build lookup for device uplinks (to identify which ports have APs/switches connected)
        var allDeviceMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deviceUplinkLookup = new Dictionary<(string SwitchMac, int PortIndex), string>();
        foreach (var device in deviceData.UnwrapDataArray())
        {
            var mac = device.GetStringOrNull("mac");
            if (!string.IsNullOrEmpty(mac))
            {
                allDeviceMacs.Add(mac);
            }

            // Build uplink lookup: (switchMac, portIndex) -> device type
            if (device.TryGetProperty("uplink", out var uplink))
            {
                var uplinkMac = uplink.GetStringOrNull("uplink_mac");
                var deviceType = device.GetStringOrNull("type");

                // Get uplink_remote_port if present (nullable int)
                int? uplinkPort = null;
                if (uplink.TryGetProperty("uplink_remote_port", out var portProp) && portProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    uplinkPort = portProp.GetInt32();
                }

                if (!string.IsNullOrEmpty(uplinkMac) && uplinkPort.HasValue && !string.IsNullOrEmpty(deviceType))
                {
                    var key = (uplinkMac.ToLowerInvariant(), uplinkPort.Value);
                    if (!deviceUplinkLookup.ContainsKey(key))
                    {
                        deviceUplinkLookup[key] = deviceType;
                        _logger.LogDebug("Device uplink: {DeviceType} connected to {SwitchMac} port {Port}",
                            deviceType, uplinkMac, uplinkPort.Value);
                    }
                }
            }
        }

        if (deviceUplinkLookup.Count > 0)
        {
            _logger.LogDebug("Built device uplink lookup with {Count} UniFi device connections", deviceUplinkLookup.Count);
        }

        foreach (var device in deviceData.UnwrapDataArray())
        {
            var portTableItems = device.GetArrayOrEmpty("port_table").ToList();
            if (portTableItems.Count == 0)
                continue;

            // Skip APs with 1-2 ports (passthrough ports that can't be disabled)
            // 4+ port APs (in-wall with switch) may have manageable ports - TBD
            var deviceType = device.GetStringOrNull("type");
            if (deviceType == "uap" && portTableItems.Count <= 2)
            {
                var name = device.GetStringFromAny("name", "mac") ?? "Unknown";
                _logger.LogDebug("Skipping AP {Name} with {Count} passthrough port(s)", name, portTableItems.Count);
                continue;
            }

            var switchInfo = ParseSwitch(device, networks, clientsByPort, historyByPort, profilesById, allDeviceMacs, deviceUplinkLookup);
            if (switchInfo != null)
            {
                switches.Add(switchInfo);
                var clientCount = switchInfo.Ports.Count(p => p.ConnectedClient != null);
                var historyCount = switchInfo.Ports.Count(p => p.HistoricalClient != null);
                _logger.LogInformation("Discovered switch: {Name} with {PortCount} ports ({ClientCount} with client data, {HistoryCount} with history)",
                    switchInfo.Name, switchInfo.Ports.Count, clientCount, historyCount);
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
    /// Build lookup table for client history by switch MAC + port index.
    /// Returns most recently seen client per port for offline device correlation.
    /// Note: We check for LastUplinkRemotePort, not IsWired, because some devices
    /// that connected via switch have is_wired=false (e.g., devices capable of both).
    /// </summary>
    private Dictionary<(string, int), UniFiClientDetailResponse> BuildClientHistoryPortLookup(List<UniFiClientDetailResponse>? clientHistory)
    {
        var lookup = new Dictionary<(string, int), UniFiClientDetailResponse>();

        if (clientHistory == null)
            return lookup;

        foreach (var client in clientHistory)
        {
            // Need switch MAC and port number - this indicates a switch port connection
            // regardless of IsWired flag (some devices report is_wired=false even when wired)
            if (string.IsNullOrEmpty(client.LastUplinkMac) || !client.LastUplinkRemotePort.HasValue)
                continue;

            var key = (client.LastUplinkMac.ToLowerInvariant(), client.LastUplinkRemotePort.Value);
            var clientName = client.DisplayName ?? client.Name ?? client.Hostname ?? client.Mac;

            // Keep the most recently seen client per port
            if (lookup.TryGetValue(key, out var existing))
            {
                if (client.LastSeen > existing.LastSeen)
                {
                    _logger.LogDebug("Client history: {SwitchMac} port {Port} updated from '{OldName}' to '{NewName}'",
                        client.LastUplinkMac, client.LastUplinkRemotePort, existing.DisplayName ?? existing.Name, clientName);
                    lookup[key] = client;
                }
            }
            else
            {
                _logger.LogDebug("Client history: {SwitchMac} port {Port} = '{ClientName}' (MAC: {Mac})",
                    client.LastUplinkMac, client.LastUplinkRemotePort, clientName, client.Mac);
                lookup[key] = client;
            }
        }

        return lookup;
    }

    /// <summary>
    /// Parse a single switch from JSON
    /// </summary>
    private SwitchInfo? ParseSwitch(JsonElement device, List<NetworkInfo> networks, Dictionary<(string, int), UniFiClientResponse> clientsByPort)
        => ParseSwitch(device, networks, clientsByPort, new Dictionary<(string, int), UniFiClientDetailResponse>());

    /// <summary>
    /// Parse a single switch from JSON with client history
    /// </summary>
    private SwitchInfo? ParseSwitch(JsonElement device, List<NetworkInfo> networks, Dictionary<(string, int), UniFiClientResponse> clientsByPort, Dictionary<(string, int), UniFiClientDetailResponse> historyByPort)
        => ParseSwitch(device, networks, clientsByPort, historyByPort, new Dictionary<string, UniFiPortProfile>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Parse a single switch from JSON with client history and port profiles
    /// </summary>
    private SwitchInfo? ParseSwitch(
        JsonElement device,
        List<NetworkInfo> networks,
        Dictionary<(string, int), UniFiClientResponse> clientsByPort,
        Dictionary<(string, int), UniFiClientDetailResponse> historyByPort,
        Dictionary<string, UniFiPortProfile> portProfiles,
        HashSet<string>? allDeviceMacs = null,
        Dictionary<(string, int), string>? deviceUplinkLookup = null)
    {
        var deviceType = device.GetStringOrNull("type");
        var (isGateway, isAccessPoint) = DetermineDeviceRole(device, deviceType, allDeviceMacs);
        var name = device.GetStringFromAny("name", "mac") ?? "Unknown";

        var mac = device.GetStringOrNull("mac");
        var model = device.GetStringOrNull("model");
        var shortname = device.GetStringOrNull("shortname");
        var modelName = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(model, shortname);
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
            IsAccessPoint = isAccessPoint,
            Capabilities = capabilities
        };

        var ports = device.GetArrayOrEmpty("port_table")
            .Select(port => ParsePort(port, switchInfoPlaceholder, networks, clientsByPort, historyByPort, portProfiles, deviceUplinkLookup))
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
            IsAccessPoint = isAccessPoint,
            Capabilities = capabilities,
            Ports = ports
        };
    }

    /// <summary>
    /// Determine the effective device role using uplink-based detection.
    /// Gateway-class devices (UDR, UX, UDM, etc.) that uplink to another UniFi device
    /// are mesh APs, not gateways. UDR/UX devices have integrated APs.
    /// </summary>
    /// <remarks>
    /// This logic parallels UniFiDiscovery.DetermineDeviceType but works with raw JSON.
    /// The audit engine receives raw JSON instead of pre-classified DiscoveredDevice objects.
    /// </remarks>
    private (bool IsGateway, bool IsAccessPoint) DetermineDeviceRole(JsonElement device, string? deviceType, HashSet<string>? allDeviceMacs)
    {
        var baseType = FromUniFiApiType(deviceType);

        // Access points (type=uap) are always APs, not switches
        // This handles in-wall APs with integrated switch ports (4+ ports)
        if (baseType == DeviceType.AccessPoint)
            return (false, true);

        // Non-gateway types are switches (not gateway, not AP)
        if (!baseType.IsGateway())
            return (false, false);

        // If we don't have device MAC info, fall back to API type (assume gateway)
        if (allDeviceMacs == null || allDeviceMacs.Count == 0)
            return (true, false);

        // Check if this gateway-class device uplinks to another UniFi device
        // If so, it's acting as a mesh AP, not the network gateway
        string? uplinkMac = null;
        if (device.TryGetProperty("uplink", out var uplink))
        {
            uplinkMac = uplink.GetStringOrNull("uplink_mac");
        }

        if (!string.IsNullOrEmpty(uplinkMac) && allDeviceMacs.Contains(uplinkMac))
        {
            var name = device.GetStringFromAny("name", "mac") ?? "Unknown";
            _logger.LogInformation(
                "Gateway-class device {Name} uplinks to another UniFi device ({UplinkMac}), classifying as AP",
                name, uplinkMac);
            return (false, true); // It's an AP
        }

        return (true, false); // It's the gateway
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
    private PortInfo? ParsePort(JsonElement port, SwitchInfo switchInfo, List<NetworkInfo> networks, Dictionary<(string, int), UniFiClientResponse> clientsByPort, Dictionary<(string, int), UniFiClientDetailResponse>? historyByPort = null)
        => ParsePort(port, switchInfo, networks, clientsByPort, historyByPort, portProfiles: null, deviceUplinkLookup: null);

    /// <summary>
    /// Parse a single port from JSON with port profile resolution and device uplink detection
    /// </summary>
    private PortInfo? ParsePort(
        JsonElement port,
        SwitchInfo switchInfo,
        List<NetworkInfo> networks,
        Dictionary<(string, int), UniFiClientResponse> clientsByPort,
        Dictionary<(string, int), UniFiClientDetailResponse>? historyByPort,
        Dictionary<string, UniFiPortProfile>? portProfiles,
        Dictionary<(string, int), string>? deviceUplinkLookup)
    {
        var portIdx = port.GetIntOrDefault("port_idx", -1);
        if (portIdx < 0)
            return null;

        // Detect LAG child ports - they are assimilated into a parent LAG port
        // and their individual config is irrelevant for most audit rules.
        // A child port has both lag_idx (number) and aggregated_by (number = parent port index).
        var isLagChild = port.TryGetProperty("lag_idx", out var lagIdx) && lagIdx.ValueKind == JsonValueKind.Number &&
            port.TryGetProperty("aggregated_by", out var aggregatedBy) && aggregatedBy.ValueKind == JsonValueKind.Number;
        if (isLagChild)
        {
            _logger.LogDebug("LAG child port {Port} on {Switch} (aggregated by port {Parent}, LAG {LagIdx})",
                portIdx, switchInfo.Name, port.GetIntOrDefault("aggregated_by"), port.GetIntOrDefault("lag_idx"));
        }

        var portName = port.GetStringOrDefault("name", $"Port {portIdx}");
        var forwardMode = port.GetStringOrDefault("forward", "all");
        var taggedVlanMgmt = port.GetStringOrNull("tagged_vlan_mgmt");

        // Resolve port profile settings if a profile is assigned
        var portconfId = port.GetStringOrNull("portconf_id");
        UniFiPortProfile? assignedProfile = null;
        bool portSecurityEnabled = port.GetBoolOrDefault("port_security_enabled");
        List<string>? allowedMacAddresses = port.GetStringArrayOrNull("port_security_mac_address")?.ToList();
        string? nativeNetworkId = port.GetStringOrNull("native_networkconf_id");
        List<string>? excludedNetworkIds = port.GetStringArrayOrNull("excluded_networkconf_ids");
        bool isolationEnabled = port.GetBoolOrDefault("isolation");

        if (!string.IsNullOrEmpty(portconfId) && portProfiles != null && portProfiles.TryGetValue(portconfId, out var profile))
        {
            // Profile found - use profile's forward mode if set
            if (!string.IsNullOrEmpty(profile.Forward))
            {
                _logger.LogDebug("Port {Switch} port {Port}: resolving forward mode from profile '{ProfileName}': {PortForward} -> {ProfileForward}",
                    switchInfo.Name, portIdx, profile.Name, forwardMode, profile.Forward);
                forwardMode = profile.Forward;
            }

            // Use profile's native network ID if set (profile takes precedence over port's base value)
            if (!string.IsNullOrEmpty(profile.NativeNetworkId))
            {
                _logger.LogDebug("Port {Switch} port {Port}: resolving native_networkconf_id from profile '{ProfileName}': {PortValue} -> {ProfileNetworkId}",
                    switchInfo.Name, portIdx, profile.Name, nativeNetworkId ?? "(none)", profile.NativeNetworkId);
                nativeNetworkId = profile.NativeNetworkId;
            }

            // Use profile's excluded network IDs if set (for trunk VLAN configuration)
            if (profile.ExcludedNetworkConfIds != null)
            {
                _logger.LogDebug("Port {Switch} port {Port}: resolving excluded_networkconf_ids from profile '{ProfileName}': {Count} excluded",
                    switchInfo.Name, portIdx, profile.Name, profile.ExcludedNetworkConfIds.Count);
                excludedNetworkIds = profile.ExcludedNetworkConfIds;
            }

            // Use profile's port security settings
            if (profile.PortSecurityEnabled)
            {
                _logger.LogDebug("Port {Switch} port {Port}: resolving port_security_enabled from profile '{ProfileName}': {PortValue} -> {ProfileValue}",
                    switchInfo.Name, portIdx, profile.Name, portSecurityEnabled, profile.PortSecurityEnabled);
                portSecurityEnabled = profile.PortSecurityEnabled;
            }

            // Use profile's MAC address restrictions if set
            if (profile.PortSecurityMacAddresses?.Count > 0)
            {
                _logger.LogDebug("Port {Switch} port {Port}: resolving MAC restrictions from profile '{ProfileName}': {Count} MAC(s)",
                    switchInfo.Name, portIdx, profile.Name, profile.PortSecurityMacAddresses.Count);
                allowedMacAddresses = profile.PortSecurityMacAddresses;
            }

            // Use profile's isolation setting
            if (profile.Isolation)
            {
                _logger.LogDebug("Port {Switch} port {Port}: resolving isolation from profile '{ProfileName}': {PortValue} -> {ProfileValue}",
                    switchInfo.Name, portIdx, profile.Name, isolationEnabled, profile.Isolation);
                isolationEnabled = profile.Isolation;
            }

            assignedProfile = profile;
        }
        else if (!string.IsNullOrEmpty(portconfId))
        {
            // Profile ID present but not found in lookup - log warning
            _logger.LogWarning("Port {Switch} port {Port} has portconf_id '{PortconfId}' but profile not found in lookup",
                switchInfo.Name, portIdx, portconfId);
        }
        if (forwardMode == "customize")
            forwardMode = "custom";

        var networkName = port.GetStringOrNull("network_name")?.ToLowerInvariant();
        var isWan = networkName?.StartsWith("wan") ?? false;

        var poeEnable = port.GetBoolOrDefault("poe_enable");
        var portPoe = port.GetBoolOrDefault("port_poe");
        var poeMode = port.GetStringOrNull("poe_mode");

        // Look up connected client for this port
        UniFiClientResponse? connectedClient = null;
        UniFiClientDetailResponse? historicalClient = null;
        if (!string.IsNullOrEmpty(switchInfo.MacAddress))
        {
            var key = (switchInfo.MacAddress.ToLowerInvariant(), portIdx);
            clientsByPort.TryGetValue(key, out connectedClient);
            historyByPort?.TryGetValue(key, out historicalClient);

            if (historicalClient != null)
            {
                var histName = historicalClient.DisplayName ?? historicalClient.Name ?? historicalClient.Hostname;
                _logger.LogDebug("Port {Switch} port {Port}: matched historical client '{Name}' (MAC: {Mac})",
                    switchInfo.Name, portIdx, histName, historicalClient.Mac);
            }
        }

        // Extract last_connection info for down ports
        string? lastConnectionMac = null;
        long? lastConnectionSeen = null;
        if (port.TryGetProperty("last_connection", out var lastConnection))
        {
            lastConnectionMac = lastConnection.GetStringOrNull("mac");
            lastConnectionSeen = lastConnection.GetLongOrNull("last_seen");
        }

        // If no last_connection MAC but we have a historical client, use their MAC
        if (string.IsNullOrEmpty(lastConnectionMac) && historicalClient != null)
        {
            lastConnectionMac = historicalClient.Mac;
            lastConnectionSeen = historicalClient.LastSeen;
        }

        // Check if a UniFi device (AP, switch, etc.) is connected to this port
        string? connectedDeviceType = null;
        if (deviceUplinkLookup != null && !string.IsNullOrEmpty(switchInfo.MacAddress))
        {
            var uplinkKey = (switchInfo.MacAddress.ToLowerInvariant(), portIdx);
            deviceUplinkLookup.TryGetValue(uplinkKey, out connectedDeviceType);
        }

        return new PortInfo
        {
            PortIndex = portIdx,
            Name = portName,
            IsUp = port.GetBoolOrDefault("up"),
            Speed = port.GetIntOrDefault("speed"),
            ForwardMode = forwardMode,
            TaggedVlanMgmt = taggedVlanMgmt,
            IsUplink = port.GetBoolOrDefault("is_uplink"),
            IsWan = isWan,
            NativeNetworkId = nativeNetworkId,
            ExcludedNetworkIds = excludedNetworkIds,
            PortSecurityEnabled = portSecurityEnabled,
            AllowedMacAddresses = allowedMacAddresses,
            IsolationEnabled = isolationEnabled,
            PoeEnabled = poeEnable || portPoe,
            PoePower = port.GetDoubleOrDefault("poe_power"),
            PoeMode = poeMode,
            SupportsPoe = portPoe || !string.IsNullOrEmpty(poeMode),
            Switch = switchInfo,
            ConnectedClient = connectedClient,
            LastConnectionMac = lastConnectionMac,
            LastConnectionSeen = lastConnectionSeen,
            HistoricalClient = historicalClient,
            ConnectedDeviceType = connectedDeviceType,
            AssignedPortProfile = assignedProfile,
            IsLagChild = isLagChild
        };
    }


    /// <summary>
    /// Analyze all ports across all switches
    /// </summary>
    /// <param name="switches">Switches to analyze</param>
    /// <param name="networks">Enabled networks for most rules</param>
    /// <param name="allNetworks">All networks including disabled (for rules that check port config exposure)</param>
    public List<AuditIssue> AnalyzePorts(List<SwitchInfo> switches, List<NetworkInfo> networks, List<NetworkInfo>? allNetworks = null)
    {
        var issues = new List<AuditIssue>();

        foreach (var switchInfo in switches)
        {
            // Skip port-level audit issues for devices whose ports aren't manageable
            // in UniFi Port Manager (e.g., UX/UX7 in AP mode)
            if (switchInfo.HasUnmanageablePorts)
            {
                _logger.LogDebug("Skipping audit for {SwitchName} ({ModelName}) - ports not manageable in AP mode",
                    switchInfo.Name, switchInfo.ModelName);
                continue;
            }

            _logger.LogDebug("Analyzing {PortCount} ports on {SwitchName}",
                switchInfo.Ports.Count, switchInfo.Name);

            foreach (var port in switchInfo.Ports)
            {
                // Run all enabled rules against this port
                // LAG child ports are only evaluated by rules that opt in (e.g., unused port detection)
                foreach (var rule in _rules.Where(r => r.Enabled && (!port.IsLagChild || r.AppliesToLagChildPorts)))
                {
                    var issue = rule.Evaluate(port, networks, allNetworks);
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
                var modelName = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(model, shortname);

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

            // Determine effective network ID using this priority:
            // 1. Client's EffectiveNetworkId (handles virtual_network_override_id when override is enabled)
            // 2. Protect API's connection_network_id (for UniFi Protect cameras)
            // 3. Match by VLAN number if available
            var effectiveNetworkId = client.EffectiveNetworkId;

            // For UniFi Protect cameras, also check if Protect API has different network info
            if (_protectCameras?.TryGetNetworkId(client.Mac, out var protectNetworkId) == true)
            {
                if (protectNetworkId != effectiveNetworkId)
                {
                    _logger.LogDebug("Network override for {Mac}: Network API reported {NetworkApiId}, using Protect API's {ProtectApiId}",
                        client.Mac, effectiveNetworkId, protectNetworkId);
                    effectiveNetworkId = protectNetworkId;
                }
            }

            // Lookup network by effective NetworkId
            var network = networks.FirstOrDefault(n => n.Id == effectiveNetworkId);

            // If network not found by ID but we have a VLAN number, try matching by VLAN
            if (network == null && client.Vlan.HasValue)
            {
                network = networks.FirstOrDefault(n => n.VlanId == client.Vlan.Value);
                if (network != null)
                {
                    _logger.LogDebug("Matched client {Mac} to network {Network} by VLAN {Vlan}",
                        client.Mac, network.Name, client.Vlan.Value);
                }
            }

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

            _logger.LogDebug("Wireless client: {Name} ({Mac}) on {Network} - detected as {Category}, Radio={Radio}, Channel={Channel}",
                client.Name ?? client.Hostname ?? client.Mac,
                client.Mac,
                network?.Name ?? "Unknown",
                detection.CategoryName,
                client.Radio ?? "null",
                client.Channel?.ToString() ?? "null");
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
