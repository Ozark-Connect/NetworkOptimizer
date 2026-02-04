using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Device discovery service using UniFi Controller API
/// Unlike SNMP-based discovery, this uses the controller as the source of truth
/// for all network devices and their configurations
/// </summary>
public class UniFiDiscovery
{
    private readonly UniFiApiClient _apiClient;
    private readonly ILogger<UniFiDiscovery> _logger;

    public UniFiDiscovery(UniFiApiClient apiClient, ILogger<UniFiDiscovery> logger)
    {
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <summary>
    /// Discovers all UniFi devices via controller API
    /// Returns devices with full metadata from controller
    /// </summary>
    public async Task<List<DiscoveredDevice>> DiscoverDevicesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting UniFi device discovery via API");

        // Fetch devices and network configs in parallel
        var devicesTask = _apiClient.GetDevicesAsync(cancellationToken);
        var networksTask = _apiClient.GetNetworkConfigsAsync(cancellationToken);

        await Task.WhenAll(devicesTask, networksTask);

        var devices = await devicesTask;
        var networks = await networksTask;

        if (devices == null || devices.Count == 0)
        {
            _logger.LogWarning("No devices discovered");
            return new List<DiscoveredDevice>();
        }

        _logger.LogInformation("Discovered {Count} UniFi devices", devices.Count);

        // Find the default LAN network gateway IP for gateways
        var defaultLanGatewayIp = GetDefaultLanGatewayIp(networks);

        // Collect all device MACs for uplink-based gateway detection
        var allDeviceMacs = new HashSet<string>(
            devices.Where(d => !string.IsNullOrEmpty(d.Mac)).Select(d => d.Mac.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        var discoveredDevices = devices.Select(d =>
        {
            var hardwareType = DeviceTypeExtensions.FromUniFiApiType(d.Type, d.Model);
            var effectiveType = DetermineDeviceType(d, allDeviceMacs, _logger);

            return new DiscoveredDevice
            {
                Id = d.Id,
                Mac = d.Mac,
                Name = d.Name,
                Type = effectiveType,
                HardwareType = hardwareType,
                Model = d.Model,
                Shortname = d.Shortname,
                IpAddress = d.Ip,
                // Set LAN IP for gateways from network config
                LanIpAddress = effectiveType.IsGateway() ? defaultLanGatewayIp : null,
                Firmware = d.DisplayableVersion ?? d.Version,
                Adopted = d.Adopted,
                State = d.State,
                Uptime = TimeSpan.FromSeconds(d.Uptime),
                LastSeen = DateTimeOffset.FromUnixTimeSeconds(d.LastSeen).DateTime,
                Upgradable = d.Upgradable,
                UpgradeToFirmware = d.UpgradeToFirmware,
                UplinkMac = d.Uplink?.UplinkMac,
                UplinkPort = d.Uplink?.UplinkRemotePort,
                LocalUplinkPort = d.Uplink?.PortIdx,
                IsUplinkConnected = d.Uplink?.Up ?? false,
                // For wireless uplinks, use tx_rate (Kbps -> Mbps); for wired, use speed (already Mbps)
                UplinkSpeedMbps = d.Uplink?.Type == "wireless" && d.Uplink.TxRate > 0
                    ? (int)(d.Uplink.TxRate / 1000)
                    : d.Uplink?.Speed ?? 0,
                // Wireless uplink rates in Kbps
                UplinkTxRateKbps = d.Uplink?.TxRate ?? 0,
                UplinkRxRateKbps = d.Uplink?.RxRate ?? 0,
                UplinkType = d.Uplink?.Type,
                UplinkRadioBand = d.Uplink?.RadioBand,
                UplinkChannel = d.Uplink?.Channel,
                UplinkSignalDbm = d.Uplink?.Signal,
                UplinkNoiseDbm = d.Uplink?.Noise,
                CpuUsage = d.SystemStats?.Cpu,
                MemoryUsage = d.SystemStats?.Mem,
                LoadAverage = d.SystemStats?.LoadAvg1?.ToString("F2"),
                TxBytes = d.Stats?.TxBytes ?? 0,
                RxBytes = d.Stats?.RxBytes ?? 0,
                PortCount = d.PortTable?.Count ?? 0
            };
        }).ToList();

        // Log wireless uplink details for debugging
        foreach (var d in devices.Where(d => d.Uplink?.Type == "wireless"))
        {
            _logger.LogDebug("Wireless uplink for {Name}: Radio={Radio}, TxRate={Tx}Kbps, RxRate={Rx}Kbps, Channel={Ch}, IsMlo={Mlo}",
                d.Name, d.Uplink?.RadioBand ?? "null", d.Uplink?.TxRate, d.Uplink?.RxRate, d.Uplink?.Channel, d.Uplink?.IsMlo);
        }

        return discoveredDevices;
    }

    /// <summary>
    /// Gets the gateway IP from the default LAN network configuration.
    /// This is the gateway's LAN-facing IP (not the WAN IP).
    /// </summary>
    private string? GetDefaultLanGatewayIp(List<UniFiNetworkConfig>? networks)
    {
        if (networks == null || networks.Count == 0)
            return null;

        // Find the default LAN network - typically:
        // 1. Purpose = "corporate" with no VLAN (the default LAN)
        // 2. Or the first "corporate" network if all have VLANs
        var defaultLan = networks
            .Where(n => n.Purpose == "corporate" && n.Enabled)
            .OrderBy(n => n.Vlan ?? 0) // Prefer no VLAN (0) first
            .FirstOrDefault();

        if (defaultLan == null)
            return null;

        // First try DhcpdGateway (explicitly configured gateway IP)
        if (!string.IsNullOrEmpty(defaultLan.DhcpdGateway))
        {
            _logger.LogDebug("Gateway LAN IP from DhcpdGateway: {Ip}", defaultLan.DhcpdGateway);
            return defaultLan.DhcpdGateway;
        }

        // Otherwise extract from ip_subnet (e.g., "192.168.1.1/24" -> "192.168.1.1")
        if (!string.IsNullOrEmpty(defaultLan.IpSubnet))
        {
            var ip = defaultLan.IpSubnet.Split('/')[0];
            _logger.LogDebug("Gateway LAN IP from IpSubnet: {Ip}", ip);
            return ip;
        }

        return null;
    }

    /// <summary>
    /// Discovers all connected clients via controller API
    /// Returns both wired and wireless clients
    /// </summary>
    public async Task<List<DiscoveredClient>> DiscoverClientsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting UniFi client discovery via API");

        var clients = await _apiClient.GetClientsAsync(cancellationToken);
        if (clients == null || clients.Count == 0)
        {
            _logger.LogWarning("No clients discovered");
            return new List<DiscoveredClient>();
        }

        _logger.LogInformation("Discovered {Count} connected clients", clients.Count);

        // Check if any clients are missing IPs after trying BestIp fallback (ip > last_ip > fixed_ip)
        var clientsMissingIps = clients.Where(c => string.IsNullOrEmpty(c.BestIp)).ToList();
        var macToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (clientsMissingIps.Count > 0)
        {
            _logger.LogDebug("{Count} clients missing IPs (after stat/sta fallbacks), fetching from active clients endpoint", clientsMissingIps.Count);
            var activeClients = await GetActiveClientsForEnrichmentAsync(cancellationToken);
            macToIp = ClientIpEnricher.BuildMacToIpLookup(activeClients);
        }

        // Log any MLO clients found
        var mloClients = clients.Where(c => c.IsMlo == true).ToList();
        if (mloClients.Any())
        {
            foreach (var c in mloClients)
            {
                var linksInfo = c.MloDetails != null
                    ? string.Join(", ", c.MloDetails.Select(m => $"{m.Radio ?? "?"} ch{m.Channel} {m.Signal}dBm {m.ChannelWidth}MHz"))
                    : "none";
                _logger.LogDebug("MLO client found: {Name} ({Mac}), Radio={Radio}, Links: [{Links}]",
                    c.Name ?? c.Hostname, c.Mac, c.Radio ?? "null", linksInfo);
            }
        }

        var discoveredClients = clients.Select(c =>
        {
            // Use stat/sta BestIp (ip > last_ip > fixed_ip), then active clients endpoint (UX/UX7 bug workaround)
            var ipAddress = ClientIpEnricher.GetEnrichedIp(c.BestIp, c.Mac, macToIp);
            if (string.IsNullOrEmpty(c.BestIp) && !string.IsNullOrEmpty(ipAddress))
            {
                _logger.LogDebug("Enriched IP for {Mac} from active clients: {Ip}", c.Mac, ipAddress);
            }

            return new DiscoveredClient
            {
                Id = c.Id,
                Mac = c.Mac,
                Hostname = c.Hostname,
                Name = c.Name,
                IpAddress = ipAddress ?? string.Empty,
            Network = c.Network,
            NetworkId = c.NetworkId,
            VirtualNetworkOverrideEnabled = c.VirtualNetworkOverrideEnabled,
            VirtualNetworkOverrideId = c.VirtualNetworkOverrideId,
            Vlan = c.Vlan,
            IsWired = c.IsWired,
            IsGuest = c.IsGuest,
            IsBlocked = c.Blocked,
            ConnectionType = DetermineConnectionType(c),
            ConnectedToDeviceMac = c.IsWired ? c.SwMac : c.ApMac,
            SwitchPort = c.SwPort,
            Uptime = TimeSpan.FromSeconds(c.Uptime),
            LastSeen = DateTimeOffset.FromUnixTimeSeconds(c.LastSeen).DateTime,
            FirstSeen = DateTimeOffset.FromUnixTimeSeconds(c.FirstSeen).DateTime,
            // Wireless-specific
            Essid = c.Essid,
            Channel = c.Channel,
            Rssi = c.Rssi,
            SignalStrength = c.Signal,
            NoiseLevel = c.Noise,
            RadioProtocol = c.RadioProto,
            Radio = c.Radio,
            // Wi-Fi 7 MLO
            IsMlo = c.IsMlo ?? false,
            MloLinks = c.MloDetails?.Select(m => new MloLink
            {
                Radio = m.Radio ?? "",
                Channel = m.Channel,
                ChannelWidth = m.ChannelWidth,
                SignalDbm = m.Signal,
                NoiseDbm = m.Noise,
                TxRateKbps = m.TxRate,
                RxRateKbps = m.RxRate
            }).ToList(),
            // Traffic stats
            TxBytes = c.TxBytes,
            RxBytes = c.RxBytes,
            TxPackets = c.TxPackets,
            RxPackets = c.RxPackets,
            TxRate = c.TxRate,
            RxRate = c.RxRate,
            TxBytesRate = c.TxBytesRate,
            RxBytesRate = c.RxBytesRate,
            // QoS
            Satisfaction = c.Satisfaction,
            HasFixedIp = c.UseFixedIp,
            FixedIp = c.FixedIp,
            Note = c.Note,
            Oui = c.Oui
        };
        }).ToList();

        return discoveredClients;
    }

    /// <summary>
    /// Fetches active clients for IP enrichment.
    /// Used to get IPs for UX/UX7 connected clients that are missing IPs in stat/sta.
    /// Gracefully returns empty list if the API fails.
    /// </summary>
    private async Task<List<Models.UniFiClientDetailResponse>> GetActiveClientsForEnrichmentAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _apiClient.GetActiveClientsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch active clients for IP enrichment, continuing without enrichment");
            return new List<Models.UniFiClientDetailResponse>();
        }
    }

    /// <summary>
    /// Gets comprehensive network topology including devices and their connections
    /// </summary>
    public async Task<NetworkTopology> DiscoverTopologyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting network topology discovery");

        var devicesTask = DiscoverDevicesAsync(cancellationToken);
        var clientsTask = DiscoverClientsAsync(cancellationToken);
        var networksTask = _apiClient.GetNetworkConfigsAsync(cancellationToken);

        await Task.WhenAll(devicesTask, clientsTask, networksTask);

        var devices = await devicesTask;
        var clients = await clientsTask;
        var networks = await networksTask;

        var topology = new NetworkTopology
        {
            Devices = devices,
            Clients = clients,
            Networks = networks?.Select(n => new NetworkInfo
            {
                Id = n.Id,
                Name = n.Name,
                Purpose = n.Purpose,
                Enabled = n.Enabled,
                VlanId = n.Vlan,
                IpSubnet = n.IpSubnet,
                IsDhcpEnabled = n.DhcpdEnabled,
                DhcpRange = n.DhcpdEnabled ? $"{n.DhcpdStart} - {n.DhcpdStop}" : null,
                Gateway = n.DhcpdGateway,
                IsNat = n.IsNat,
                WanUploadMbps = n.WanProviderCapabilities?.UploadMbps,
                WanDownloadMbps = n.WanProviderCapabilities?.DownloadMbps,
                WanNetworkgroup = n.WanNetworkgroup
            }).ToList() ?? new List<NetworkInfo>(),
            DiscoveredAt = DateTime.UtcNow
        };

        // Build device hierarchy (uplink relationships)
        BuildDeviceHierarchy(topology);

        _logger.LogInformation("Topology discovered: {DeviceCount} devices, {ClientCount} clients, {NetworkCount} networks",
            topology.Devices.Count, topology.Clients.Count, topology.Networks.Count);

        return topology;
    }

    /// <summary>
    /// Gets detailed firewall configuration
    /// </summary>
    public async Task<FirewallConfiguration> GetFirewallConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching firewall configuration");

        var rulesTask = _apiClient.GetFirewallRulesAsync(cancellationToken);
        var groupsTask = _apiClient.GetFirewallGroupsAsync(cancellationToken);

        await Task.WhenAll(rulesTask, groupsTask);

        var rules = await rulesTask;
        var groups = await groupsTask;

        var config = new FirewallConfiguration
        {
            Rules = rules ?? new List<UniFiFirewallRule>(),
            Groups = groups ?? new List<UniFiFirewallGroup>(),
            RetrievedAt = DateTime.UtcNow
        };

        _logger.LogInformation("Firewall config retrieved: {RuleCount} rules, {GroupCount} groups",
            config.Rules.Count, config.Groups.Count);

        return config;
    }

    /// <summary>
    /// Gets controller information including licensing fingerprint
    /// </summary>
    public async Task<ControllerInfo?> GetControllerInfoAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching controller information");

        var sysInfo = await _apiClient.GetSystemInfoAsync(cancellationToken);
        if (sysInfo == null)
        {
            _logger.LogWarning("Failed to retrieve controller information");
            return null;
        }

        var controllerInfo = new ControllerInfo
        {
            ControllerId = sysInfo.AnonymousControllerId ?? "unknown",
            DeviceId = sysInfo.AnonymousDeviceId,
            Uuid = sysInfo.Uuid,
            Name = sysInfo.Name,
            Hostname = sysInfo.Hostname,
            Version = sysInfo.Version,
            Build = sysInfo.Build,
            UpdateAvailable = sysInfo.UpdateAvailable,
            IpAddresses = sysInfo.IpAddrs,
            InformUrl = sysInfo.InformUrl,
            Timezone = sysInfo.Timezone,
            Uptime = TimeSpan.FromSeconds(sysInfo.Uptime),
            HardwareModel = sysInfo.HardwareModel,
            IsCloudKeyRunning = sysInfo.CloudKeyRunning,
            IsUnifiGoEnabled = sysInfo.UnifiGoEnabled
        };

        _logger.LogInformation("Controller info retrieved: {Name} v{Version} (ID: {Id})",
            controllerInfo.Name, controllerInfo.Version, controllerInfo.ControllerId);

        return controllerInfo;
    }

    /// <summary>
    /// Determines the device type, with special handling for UDM-family devices
    /// that may be operating as access points rather than gateways.
    /// </summary>
    /// <remarks>
    /// UX (Express) devices report type "udm" but may be configured as mesh APs
    /// rather than gateways. Detection uses uplink analysis: if a UDM-type device
    /// has an uplink to another UniFi device, it's acting as a mesh AP, not the gateway.
    /// The actual gateway either has no uplink or uplinks to a non-UniFi device (ISP modem).
    /// </remarks>
    internal static DeviceType DetermineDeviceType(
        UniFiDeviceResponse device,
        HashSet<string> allDeviceMacs,
        ILogger logger)
    {
        var baseType = DeviceTypeExtensions.FromUniFiApiType(device.Type, device.Model);

        // Only apply special handling to UDM-family devices (type = udm, uxg, ucg, etc.)
        if (baseType != DeviceType.Gateway)
        {
            return baseType;
        }

        // Check if this device has an uplink to another UniFi device
        var uplinkMac = device.Uplink?.UplinkMac;
        var hasUplinkToUniFiDevice = !string.IsNullOrEmpty(uplinkMac) &&
                                      allDeviceMacs.Contains(uplinkMac.ToLowerInvariant());

        // Log classification details for gateway-class devices (UDR, UX, UDM, etc.)
        logger.LogInformation(
            "Gateway-class device: {Name} ({Model}) - API type={ApiType}, IP={Ip}, " +
            "UplinkMac={UplinkMac}, UplinkToUniFi={HasUplinkToUniFi}, HasConfigNetworkLan={HasLan}",
            device.Name,
            device.Shortname ?? device.Model,
            device.Type,
            device.Ip,
            uplinkMac ?? "(none)",
            hasUplinkToUniFiDevice,
            device.ConfigNetworkLan != null);

        // If the gateway-class device has an uplink to another UniFi device,
        // it's acting as a mesh AP, not the network gateway (UDR/UX have integrated APs)
        if (hasUplinkToUniFiDevice)
        {
            logger.LogInformation(
                "Classifying {Name} as AccessPoint (uplinks to another UniFi device: {UplinkMac})",
                device.Name, uplinkMac);
            return DeviceType.AccessPoint;
        }

        return DeviceType.Gateway;
    }

    /// <summary>
    /// Gets the effective device type for a device, considering uplink topology.
    /// Use this when you have a list of devices and need to determine the correct
    /// type for each (e.g., UDR/UX devices with integrated APs acting as mesh APs).
    ///
    /// DEPRECATED: Prefer using GetDiscoveredDevicesAsync() which returns DiscoveredDevice
    /// with Type already set to the effective type.
    /// </summary>
    /// <param name="device">The device to classify</param>
    /// <param name="allDevices">All devices in the network (to check uplink relationships)</param>
    /// <returns>The effective device type</returns>
    public static DeviceType GetEffectiveDeviceType(UniFiDeviceResponse device, IEnumerable<UniFiDeviceResponse> allDevices)
    {
        var baseType = DeviceTypeExtensions.FromUniFiApiType(device.Type, device.Model);

        // Only apply special handling to gateway-class devices
        if (baseType != DeviceType.Gateway)
        {
            return baseType;
        }

        // Build set of all device MACs
        var allDeviceMacs = new HashSet<string>(
            allDevices.Select(d => d.Mac.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        // Check if this device has an uplink to another UniFi device
        var uplinkMac = device.Uplink?.UplinkMac;
        var hasUplinkToUniFiDevice = !string.IsNullOrEmpty(uplinkMac) &&
                                      allDeviceMacs.Contains(uplinkMac.ToLowerInvariant());

        // If the UDM-type device has an uplink to another UniFi device,
        // it's acting as a mesh AP, not the network gateway
        return hasUplinkToUniFiDevice ? DeviceType.AccessPoint : DeviceType.Gateway;
    }

    private string DetermineConnectionType(UniFiClientResponse client)
    {
        if (client.IsWired)
        {
            return "Wired";
        }

        if (!string.IsNullOrEmpty(client.RadioProto))
        {
            return client.RadioProto.ToUpperInvariant() switch
            {
                "NA" or "AC" or "AX" or "BE" => $"WiFi {client.RadioProto.ToUpper()}",
                _ => "WiFi"
            };
        }

        return "Wireless";
    }

    private void BuildDeviceHierarchy(NetworkTopology topology)
    {
        var deviceDict = topology.Devices.ToDictionary(d => d.Mac, d => d);

        foreach (var device in topology.Devices)
        {
            if (!string.IsNullOrEmpty(device.UplinkMac) && deviceDict.TryGetValue(device.UplinkMac, out var uplinkDevice))
            {
                device.UplinkDeviceName = uplinkDevice.Name;
                uplinkDevice.DownstreamDevices ??= new List<string>();
                uplinkDevice.DownstreamDevices.Add(device.Name);
            }
        }
    }
}

#region Discovery Result Models

public class DiscoveredDevice
{
    public string Id { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The effective device type considering network topology.
    /// For UDR/UX devices with integrated APs acting as mesh APs, this will be AccessPoint.
    /// </summary>
    public DeviceType Type { get; set; }

    /// <summary>
    /// The original hardware type from the UniFi API (before uplink-based adjustment).
    /// Use this to identify gateway-class hardware regardless of its current role.
    /// </summary>
    public DeviceType HardwareType { get; set; }

    /// <summary>
    /// True when this is a gateway-class device (UDR, UX, etc.) with HardwareType = Gateway
    /// that is acting as a mesh Access Point due to uplink to another UniFi device.
    /// </summary>
    public bool IsActingAsAccessPoint => HardwareType == DeviceType.Gateway && Type == DeviceType.AccessPoint;

    public string Model { get; set; } = string.Empty;
    public string? Shortname { get; set; }

    /// <summary>
    /// Best product name for display and image lookup.
    /// Uses the same logic as UniFiDeviceResponse.FriendlyModelName.
    /// </summary>
    public string FriendlyModelName =>
        UniFiProductDatabase.GetBestProductName(Model, Shortname);

    /// <summary>
    /// Whether this device can run iperf3 for LAN speed testing
    /// </summary>
    public bool CanRunIperf3 =>
        UniFiProductDatabase.CanRunIperf3(FriendlyModelName);

    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// LAN IP address for gateways (from network config).
    /// For non-gateway devices, this is null.
    /// </summary>
    public string? LanIpAddress { get; set; }

    /// <summary>
    /// Gets the best IP address for display purposes.
    /// For gateways, prefers LAN IP; for other devices, uses standard IP.
    /// </summary>
    public string DisplayIpAddress => !string.IsNullOrEmpty(LanIpAddress) ? LanIpAddress : IpAddress;

    public string Firmware { get; set; } = string.Empty;
    public bool Adopted { get; set; }
    public int State { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Upgradable { get; set; }
    public string? UpgradeToFirmware { get; set; }
    public string? UplinkMac { get; set; }
    /// <summary>Remote port on the upstream device that this device connects to.</summary>
    public int? UplinkPort { get; set; }
    /// <summary>Local port on this device that connects to the upstream device (wired only).</summary>
    public int? LocalUplinkPort { get; set; }
    public string? UplinkDeviceName { get; set; }
    public bool IsUplinkConnected { get; set; }
    public int UplinkSpeedMbps { get; set; }
    /// <summary>TX rate in Kbps for wireless uplinks</summary>
    public long UplinkTxRateKbps { get; set; }
    /// <summary>RX rate in Kbps for wireless uplinks</summary>
    public long UplinkRxRateKbps { get; set; }
    public string? UplinkType { get; set; }  // "wire" or "wireless"
    public string? UplinkRadioBand { get; set; }  // "ng" (2.4GHz), "na" (5GHz), "6e" (6GHz)
    public int? UplinkChannel { get; set; }
    public int? UplinkSignalDbm { get; set; }
    public int? UplinkNoiseDbm { get; set; }
    public List<string>? DownstreamDevices { get; set; }
    public string? CpuUsage { get; set; }
    public string? MemoryUsage { get; set; }
    public string? LoadAverage { get; set; }
    public long TxBytes { get; set; }
    public long RxBytes { get; set; }
    public int PortCount { get; set; }
}

public class DiscoveredClient
{
    public string Id { get; set; } = string.Empty;
    public string Mac { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Network { get; set; } = string.Empty;
    public string NetworkId { get; set; } = string.Empty;
    // Virtual network override (client assigned to different VLAN than SSID's native network)
    public bool VirtualNetworkOverrideEnabled { get; set; }
    public string? VirtualNetworkOverrideId { get; set; }
    /// <summary>
    /// The actual VLAN number the client is assigned to
    /// </summary>
    public int? Vlan { get; set; }
    /// <summary>
    /// Gets the effective network ID (considers virtual network override)
    /// </summary>
    public string EffectiveNetworkId =>
        VirtualNetworkOverrideEnabled && !string.IsNullOrEmpty(VirtualNetworkOverrideId)
            ? VirtualNetworkOverrideId
            : NetworkId;
    public bool IsWired { get; set; }
    public bool IsGuest { get; set; }
    public bool IsBlocked { get; set; }
    public string ConnectionType { get; set; } = string.Empty;
    public string? ConnectedToDeviceMac { get; set; }
    public int? SwitchPort { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTime LastSeen { get; set; }
    public DateTime FirstSeen { get; set; }
    // Wireless-specific
    public string? Essid { get; set; }
    public int? Channel { get; set; }
    public int? Rssi { get; set; }
    public int? SignalStrength { get; set; }
    public int? NoiseLevel { get; set; }
    public string? RadioProtocol { get; set; }
    public string? Radio { get; set; }  // "ng" (2.4GHz), "na" (5GHz), "6e" (6GHz)
    // Wi-Fi 7 MLO (Multi-Link Operation)
    public bool IsMlo { get; set; }
    public List<MloLink>? MloLinks { get; set; }
    // Traffic
    public long TxBytes { get; set; }
    public long RxBytes { get; set; }
    public long TxPackets { get; set; }
    public long RxPackets { get; set; }
    public long TxRate { get; set; }
    public long RxRate { get; set; }
    public double TxBytesRate { get; set; }
    public double RxBytesRate { get; set; }
    // QoS
    public int? Satisfaction { get; set; }
    public bool HasFixedIp { get; set; }
    public string? FixedIp { get; set; }
    public string? Note { get; set; }
    public string Oui { get; set; } = string.Empty;
}

/// <summary>
/// MLO link info for Wi-Fi 7 multi-link clients
/// </summary>
public class MloLink
{
    public string Radio { get; set; } = string.Empty;  // "ng", "na", "6e"
    public int? Channel { get; set; }
    public int? ChannelWidth { get; set; }  // 20, 40, 80, 160, 320
    public int? SignalDbm { get; set; }
    public int? NoiseDbm { get; set; }
    public long? TxRateKbps { get; set; }
    public long? RxRateKbps { get; set; }
}

public class NetworkTopology
{
    public List<DiscoveredDevice> Devices { get; set; } = new();
    public List<DiscoveredClient> Clients { get; set; } = new();
    public List<NetworkInfo> Networks { get; set; } = new();
    public DateTime DiscoveredAt { get; set; }
}

public class NetworkInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int? VlanId { get; set; }
    public string? IpSubnet { get; set; }
    public bool IsDhcpEnabled { get; set; }
    public string? DhcpRange { get; set; }
    public string? Gateway { get; set; }
    public bool IsNat { get; set; }

    /// <summary>WAN upload speed in Mbps (only for WAN networks)</summary>
    public int? WanUploadMbps { get; set; }

    /// <summary>WAN download speed in Mbps (only for WAN networks)</summary>
    public int? WanDownloadMbps { get; set; }

    /// <summary>WAN network group: "WAN" for primary, "WAN2", "WAN3" for secondary</summary>
    public string? WanNetworkgroup { get; set; }

    /// <summary>Whether this is a WAN network</summary>
    public bool IsWan => Purpose.Equals("wan", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this is the primary WAN (wan_networkgroup = "WAN")</summary>
    public bool IsPrimaryWan => WanNetworkgroup?.Equals("WAN", StringComparison.OrdinalIgnoreCase) == true;
}

public class FirewallConfiguration
{
    public List<UniFiFirewallRule> Rules { get; set; } = new();
    public List<UniFiFirewallGroup> Groups { get; set; } = new();
    public DateTime RetrievedAt { get; set; }
}

public class ControllerInfo
{
    public string ControllerId { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? Uuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Hostname { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Build { get; set; } = string.Empty;
    public bool UpdateAvailable { get; set; }
    public List<string> IpAddresses { get; set; } = new();
    public string InformUrl { get; set; } = string.Empty;
    public string Timezone { get; set; } = string.Empty;
    public TimeSpan Uptime { get; set; }
    public string? HardwareModel { get; set; }
    public bool IsCloudKeyRunning { get; set; }
    public bool IsUnifiGoEnabled { get; set; }
}

#endregion
