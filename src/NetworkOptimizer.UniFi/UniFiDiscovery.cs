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

        var discoveredDevices = devices.Select(d =>
        {
            var deviceType = DetermineDeviceType(d.Type);
            return new DiscoveredDevice
            {
                Id = d.Id,
                Mac = d.Mac,
                Name = d.Name,
                Type = deviceType,
                Model = d.Model,
                Shortname = d.Shortname,
                ModelDisplay = d.ModelDisplay,
                IpAddress = d.Ip,
                // Set LAN IP for gateways from network config
                LanIpAddress = deviceType.IsGateway() ? defaultLanGatewayIp : null,
                Firmware = d.Version,
                Adopted = d.Adopted,
                State = d.State,
                Uptime = TimeSpan.FromSeconds(d.Uptime),
                LastSeen = DateTimeOffset.FromUnixTimeSeconds(d.LastSeen).DateTime,
                Upgradable = d.Upgradable,
                UpgradeToFirmware = d.UpgradeToFirmware,
                UplinkMac = d.Uplink?.UplinkMac,
                UplinkPort = d.Uplink?.UplinkRemotePort,
                IsUplinkConnected = d.Uplink?.Up ?? false,
                // For wireless uplinks, use tx_rate (Kbps -> Mbps); for wired, use speed (already Mbps)
                UplinkSpeedMbps = d.Uplink?.Type == "wireless" && d.Uplink.TxRate > 0
                    ? (int)(d.Uplink.TxRate / 1000)
                    : d.Uplink?.Speed ?? 0,
                UplinkType = d.Uplink?.Type,
                UplinkRadioBand = d.Uplink?.RadioBand,
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

        var discoveredClients = clients.Select(c => new DiscoveredClient
        {
            Id = c.Id,
            Mac = c.Mac,
            Hostname = c.Hostname,
            Name = c.Name,
            IpAddress = c.Ip,
            Network = c.Network,
            NetworkId = c.NetworkId,
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
        }).ToList();

        return discoveredClients;
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
                IsNat = n.IsNat
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

    private static DeviceType DetermineDeviceType(string typeString) =>
        DeviceTypeExtensions.FromUniFiApiType(typeString);

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
    public DeviceType Type { get; set; }
    public string Model { get; set; } = string.Empty;
    public string? Shortname { get; set; }
    public string ModelDisplay { get; set; } = string.Empty;

    /// <summary>
    /// Best product name for display and image lookup.
    /// Uses the same logic as UniFiDeviceResponse.FriendlyModelName.
    /// </summary>
    public string FriendlyModelName =>
        UniFiProductDatabase.GetBestProductName(Model, Shortname, ModelDisplay);

    /// <summary>
    /// Whether this device uses MIPS architecture and cannot run iperf3
    /// </summary>
    public bool IsMipsArchitecture =>
        UniFiProductDatabase.IsMipsArchitecture(FriendlyModelName);

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
    public int? UplinkPort { get; set; }
    public string? UplinkDeviceName { get; set; }
    public bool IsUplinkConnected { get; set; }
    public int UplinkSpeedMbps { get; set; }
    public string? UplinkType { get; set; }  // "wire" or "wireless"
    public string? UplinkRadioBand { get; set; }  // "ng" (2.4GHz), "na" (5GHz), "6e" (6GHz)
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
