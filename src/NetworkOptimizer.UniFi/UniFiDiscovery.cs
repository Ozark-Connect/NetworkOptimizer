using Microsoft.Extensions.Logging;
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

        var devices = await _apiClient.GetDevicesAsync(cancellationToken);
        if (devices == null || devices.Count == 0)
        {
            _logger.LogWarning("No devices discovered");
            return new List<DiscoveredDevice>();
        }

        _logger.LogInformation("Discovered {Count} UniFi devices", devices.Count);

        var discoveredDevices = devices.Select(d => new DiscoveredDevice
        {
            Id = d.Id,
            Mac = d.Mac,
            Name = d.Name,
            Type = DetermineDeviceType(d.Type),
            Model = d.Model,
            ModelDisplay = d.ModelDisplay,
            IpAddress = d.Ip,
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
            CpuUsage = d.SystemStats?.Cpu,
            MemoryUsage = d.SystemStats?.Mem,
            LoadAverage = d.SystemStats?.LoadAvg1,
            TxBytes = d.Stats?.TxBytes ?? 0,
            RxBytes = d.Stats?.RxBytes ?? 0,
            PortCount = d.PortTable?.Count ?? 0
        }).ToList();

        return discoveredDevices;
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

    private DeviceType DetermineDeviceType(string typeString)
    {
        return typeString.ToLowerInvariant() switch
        {
            "ugw" or "ugw3" or "ugw4" or "usg" or "udm" or "udmpro" or "uxg" or "uxgpro" => DeviceType.Gateway,
            "usw" or "switch" => DeviceType.Switch,
            "uap" or "ap" or "u6" or "u7" or "uap-nanohd" or "uap-ac-lite" or "uap-ac-pro" => DeviceType.AccessPoint,
            _ => DeviceType.Unknown
        };
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
    public DeviceType Type { get; set; }
    public string Model { get; set; } = string.Empty;
    public string ModelDisplay { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
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
    // Traffic
    public long TxBytes { get; set; }
    public long RxBytes { get; set; }
    public long TxPackets { get; set; }
    public long RxPackets { get; set; }
    public long TxRate { get; set; }
    public long RxRate { get; set; }
    public long TxBytesRate { get; set; }
    public long RxBytesRate { get; set; }
    // QoS
    public int Satisfaction { get; set; }
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

public enum DeviceType
{
    Unknown,
    Gateway,
    Switch,
    AccessPoint
}

#endregion
