using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi.Tests.Fixtures;

/// <summary>
/// Fluent builder for constructing network topologies used in BuildHopList tests.
/// Creates NetworkTopology, rawDevices dictionary, and ServerPosition from a
/// declarative topology description.
/// </summary>
public class TopologyBuilder
{
    private readonly List<DeviceEntry> _devices = new();
    private readonly List<ClientEntry> _clients = new();
    private readonly List<NetworkEntry> _networks = new();
    private ServerEntry? _server;

    #region Device entries

    private class DeviceEntry
    {
        public DiscoveredDevice Device { get; set; } = null!;
        public List<PortEntry> Ports { get; set; } = new();
        public List<LagEntry> Lags { get; set; } = new();
    }

    private class PortEntry
    {
        public int PortIdx { get; set; }
        public int Speed { get; set; }
        public bool Up { get; set; } = true;
        public string Name { get; set; } = "";
        public bool IsUplink { get; set; }
    }

    private class LagEntry
    {
        public int ParentPort { get; set; }
        public int[] ChildPorts { get; set; } = Array.Empty<int>();
    }

    private class ClientEntry
    {
        public DiscoveredClient Client { get; set; } = null!;
    }

    private class NetworkEntry
    {
        public NetworkInfo Network { get; set; } = null!;
    }

    private class ServerEntry
    {
        public string Ip { get; set; } = "";
        public string Mac { get; set; } = "";
        public string? Name { get; set; }
        public string SwitchMac { get; set; } = "";
        public int SwitchPort { get; set; }
        public string NetworkId { get; set; } = "";
        public int? VlanId { get; set; }
    }

    #endregion

    #region Gateway

    /// <summary>
    /// Adds a gateway device to the topology.
    /// </summary>
    /// <param name="mac">Device MAC address</param>
    /// <param name="name">Display name</param>
    /// <param name="wanPortIdx">WAN port index (marked as uplink in port table)</param>
    /// <param name="wanSpeed">WAN port speed in Mbps</param>
    /// <param name="ip">Device IP</param>
    /// <param name="model">Device model</param>
    /// <param name="lanPorts">Additional LAN port definitions as (portIdx, speed) tuples</param>
    public TopologyBuilder WithGateway(
        string mac,
        string name,
        int wanPortIdx = 5,
        int wanSpeed = 1000,
        string ip = "192.0.2.1",
        string model = "UDM-Pro",
        (int portIdx, int speed)[]? lanPorts = null)
    {
        var device = new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            LanIpAddress = ip,
            Name = name,
            Model = model,
            Type = DeviceType.Gateway,
            HardwareType = DeviceType.Gateway,
            Adopted = true,
            State = 1,
            // Gateway's UplinkMac points to ISP (outside UniFi), UplinkPort is 0 or null
            UplinkMac = "ff:ff:ff:00:00:01", // ISP MAC, not in rawDevices
            UplinkPort = 0,
            LocalUplinkPort = wanPortIdx,
            UplinkSpeedMbps = wanSpeed,
            UplinkType = "wire",
            IsUplinkConnected = true
        };

        var ports = new List<PortEntry>
        {
            new()
            {
                PortIdx = wanPortIdx,
                Speed = wanSpeed,
                Up = true,
                Name = $"WAN {wanPortIdx}",
                IsUplink = true
            }
        };

        if (lanPorts != null)
        {
            foreach (var (portIdx, speed) in lanPorts)
            {
                ports.Add(new PortEntry
                {
                    PortIdx = portIdx,
                    Speed = speed,
                    Up = true,
                    Name = $"Port {portIdx}"
                });
            }
        }

        _devices.Add(new DeviceEntry { Device = device, Ports = ports });
        return this;
    }

    #endregion

    #region Switch

    /// <summary>
    /// Adds a switch device to the topology.
    /// </summary>
    /// <param name="mac">Device MAC address</param>
    /// <param name="name">Display name</param>
    /// <param name="uplinkTo">MAC of upstream device</param>
    /// <param name="uplinkRemotePort">Port index on upstream device where this switch connects</param>
    /// <param name="localUplinkPort">Local port index used for the uplink</param>
    /// <param name="ports">Port definitions as (portIdx, speed) tuples</param>
    /// <param name="lag">LAG definitions as (parentPort, childPorts) tuples</param>
    /// <param name="ip">Device IP</param>
    /// <param name="model">Device model</param>
    public TopologyBuilder WithSwitch(
        string mac,
        string name,
        string uplinkTo,
        int uplinkRemotePort,
        int localUplinkPort,
        (int portIdx, int speed)[]? ports = null,
        (int parentPort, int[] childPorts)[]? lag = null,
        string? ip = null,
        string model = "USW-24-PoE")
    {
        ip ??= GenerateIp(mac);

        var device = new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            Name = name,
            Model = model,
            Type = DeviceType.Switch,
            HardwareType = DeviceType.Switch,
            Adopted = true,
            State = 1,
            UplinkMac = uplinkTo,
            UplinkPort = uplinkRemotePort,
            LocalUplinkPort = localUplinkPort,
            UplinkType = "wire",
            IsUplinkConnected = true,
            PortCount = ports?.Length ?? 24
        };

        var portEntries = new List<PortEntry>();
        if (ports != null)
        {
            foreach (var (portIdx, speed) in ports)
            {
                portEntries.Add(new PortEntry
                {
                    PortIdx = portIdx,
                    Speed = speed,
                    Up = true,
                    Name = $"Port {portIdx}"
                });
            }
        }

        // Also ensure uplink port in port table based on localUplinkPort
        EnsurePortInTable(portEntries, localUplinkPort, device.UplinkSpeedMbps);

        var lagEntries = new List<LagEntry>();
        if (lag != null)
        {
            foreach (var (parentPort, childPorts) in lag)
            {
                lagEntries.Add(new LagEntry { ParentPort = parentPort, ChildPorts = childPorts });
            }
        }

        _devices.Add(new DeviceEntry { Device = device, Ports = portEntries, Lags = lagEntries });

        // Set uplink speed on device from port table
        var uplinkPortEntry = portEntries.FirstOrDefault(p => p.PortIdx == localUplinkPort);
        if (uplinkPortEntry != null)
        {
            device.UplinkSpeedMbps = uplinkPortEntry.Speed;
        }

        return this;
    }

    #endregion

    #region Access Point

    /// <summary>
    /// Adds a wired access point to the topology.
    /// </summary>
    public TopologyBuilder WithAP(
        string mac,
        string name,
        string uplinkTo,
        int uplinkRemotePort,
        int localUplinkPort = 1,
        (int portIdx, int speed)[]? ports = null,
        string? ip = null,
        string model = "U6-Pro")
    {
        ip ??= GenerateIp(mac);

        var device = new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            Name = name,
            Model = model,
            Type = DeviceType.AccessPoint,
            HardwareType = DeviceType.AccessPoint,
            Adopted = true,
            State = 1,
            UplinkMac = uplinkTo,
            UplinkPort = uplinkRemotePort,
            LocalUplinkPort = localUplinkPort,
            UplinkType = "wire",
            IsUplinkConnected = true
        };

        var portEntries = new List<PortEntry>();
        if (ports != null)
        {
            foreach (var (portIdx, speed) in ports)
            {
                portEntries.Add(new PortEntry
                {
                    PortIdx = portIdx,
                    Speed = speed,
                    Up = true,
                    Name = $"Port {portIdx}"
                });
            }
        }

        var uplinkPortEntry = portEntries.FirstOrDefault(p => p.PortIdx == localUplinkPort);
        if (uplinkPortEntry != null)
        {
            device.UplinkSpeedMbps = uplinkPortEntry.Speed;
        }

        _devices.Add(new DeviceEntry { Device = device, Ports = portEntries });
        return this;
    }

    /// <summary>
    /// Adds a mesh (wireless uplink) access point to the topology.
    /// </summary>
    public TopologyBuilder WithMeshAP(
        string mac,
        string name,
        string parentApMac,
        int txRateKbps = 866000,
        int rxRateKbps = 866000,
        string band = "na",
        int channel = 36,
        int signal = -55,
        int noise = -95,
        string? ip = null,
        string model = "U6-Mesh")
    {
        ip ??= GenerateIp(mac);

        var device = new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            Name = name,
            Model = model,
            Type = DeviceType.AccessPoint,
            HardwareType = DeviceType.AccessPoint,
            Adopted = true,
            State = 1,
            UplinkMac = parentApMac,
            UplinkPort = null, // Wireless uplink has no remote port
            UplinkSpeedMbps = txRateKbps / 1000,
            UplinkType = "wireless",
            UplinkTxRateKbps = txRateKbps,
            UplinkRxRateKbps = rxRateKbps,
            UplinkRadioBand = band,
            UplinkChannel = channel,
            UplinkSignalDbm = signal,
            UplinkNoiseDbm = noise,
            IsUplinkConnected = true
        };

        // Mesh APs typically have no wired port table
        _devices.Add(new DeviceEntry { Device = device, Ports = new List<PortEntry>() });
        return this;
    }

    #endregion

    #region Clients

    /// <summary>
    /// Adds a wired client to the topology.
    /// </summary>
    public TopologyBuilder WithWiredClient(
        string mac,
        string ip,
        string connectedTo,
        int port,
        string network = "default-net",
        int? vlan = null)
    {
        var client = new DiscoveredClient
        {
            Mac = mac,
            IpAddress = ip,
            Hostname = $"client-{mac[^5..].Replace(":", "")}",
            Name = $"client-{mac[^5..].Replace(":", "")}",
            IsWired = true,
            ConnectedToDeviceMac = connectedTo,
            SwitchPort = port,
            Network = network,
            NetworkId = network
        };

        _clients.Add(new ClientEntry { Client = client });
        return this;
    }

    /// <summary>
    /// Adds a wireless client to the topology.
    /// </summary>
    public TopologyBuilder WithWirelessClient(
        string mac,
        string ip,
        string connectedTo,
        long txRateKbps = 866000,
        long rxRateKbps = 866000,
        string band = "na",
        int channel = 36,
        int signalDbm = -55,
        int noiseDbm = -95,
        string network = "default-net",
        string? hostname = null)
    {
        hostname ??= $"wifi-{mac[^5..].Replace(":", "")}";

        var client = new DiscoveredClient
        {
            Mac = mac,
            IpAddress = ip,
            Hostname = hostname,
            Name = hostname,
            IsWired = false,
            ConnectedToDeviceMac = connectedTo,
            TxRate = txRateKbps,
            RxRate = rxRateKbps,
            Radio = band,
            Channel = channel,
            SignalStrength = signalDbm,
            NoiseLevel = noiseDbm,
            Network = network,
            NetworkId = network,
            IsMlo = false,
            RadioProtocol = "AX"
        };

        _clients.Add(new ClientEntry { Client = client });
        return this;
    }

    #endregion

    #region Networks

    /// <summary>
    /// Adds a network definition to the topology.
    /// </summary>
    public TopologyBuilder WithNetwork(
        string id,
        string name,
        string purpose = "corporate",
        int? vlan = null,
        string? subnet = null)
    {
        var network = new NetworkInfo
        {
            Id = id,
            Name = name,
            Purpose = purpose,
            VlanId = vlan,
            IpSubnet = subnet,
            Enabled = true
        };

        _networks.Add(new NetworkEntry { Network = network });
        return this;
    }

    #endregion

    #region Server

    /// <summary>
    /// Configures the speed test server position in the topology.
    /// </summary>
    public TopologyBuilder WithServer(
        string ip,
        string connectedTo,
        int port,
        string network = "default-net",
        string? mac = null,
        string? name = null,
        int? vlan = null)
    {
        _server = new ServerEntry
        {
            Ip = ip,
            Mac = mac ?? "aa:bb:cc:00:ff:01",
            Name = name ?? "Test Server",
            SwitchMac = connectedTo,
            SwitchPort = port,
            NetworkId = network,
            VlanId = vlan
        };
        return this;
    }

    #endregion

    #region Build methods

    /// <summary>
    /// Builds the NetworkTopology from the configured devices, clients, and networks.
    /// </summary>
    public NetworkTopology BuildTopology()
    {
        return new NetworkTopology
        {
            Devices = _devices.Select(d => d.Device).ToList(),
            Clients = _clients.Select(c => c.Client).ToList(),
            Networks = _networks.Select(n => n.Network).ToList(),
            DiscoveredAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Builds the raw devices dictionary keyed by MAC (case-insensitive).
    /// Each entry has a PortTable with speeds and LAG configuration.
    /// </summary>
    public Dictionary<string, UniFiDeviceResponse> BuildRawDevices()
    {
        var dict = new Dictionary<string, UniFiDeviceResponse>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _devices)
        {
            var portTable = new List<SwitchPort>();

            // Build LAG lookup: childPort -> parentPort
            var lagChildToParent = new Dictionary<int, int>();
            int lagIdx = 1;
            var lagParentToIdx = new Dictionary<int, int>();

            foreach (var lag in entry.Lags)
            {
                lagParentToIdx[lag.ParentPort] = lagIdx;
                foreach (var childPort in lag.ChildPorts)
                {
                    lagChildToParent[childPort] = lag.ParentPort;
                    lagParentToIdx.TryAdd(childPort, lagIdx); // same lag_idx for children
                }
                lagIdx++;
            }

            foreach (var p in entry.Ports)
            {
                var sp = new SwitchPort
                {
                    PortIdx = p.PortIdx,
                    Speed = p.Speed,
                    Up = p.Up,
                    Name = p.Name,
                    Enable = true,
                    IsUplink = p.IsUplink
                };

                // Apply LAG annotations
                if (lagChildToParent.TryGetValue(p.PortIdx, out var parentPortIdx))
                {
                    sp.AggregatedBy = parentPortIdx;
                    if (lagParentToIdx.TryGetValue(p.PortIdx, out var li))
                        sp.LagIdx = li;
                }
                else if (lagParentToIdx.TryGetValue(p.PortIdx, out var parentLi) && !lagChildToParent.ContainsKey(p.PortIdx))
                {
                    // This is a parent port - set LagIdx but not AggregatedBy
                    sp.LagIdx = parentLi;
                }

                portTable.Add(sp);
            }

            var rawDevice = new UniFiDeviceResponse
            {
                Mac = entry.Device.Mac,
                Name = entry.Device.Name,
                Model = entry.Device.Model,
                PortTable = portTable
            };

            // Set the type string for the raw device
            rawDevice.Type = entry.Device.Type switch
            {
                DeviceType.Gateway => "ugw",
                DeviceType.Switch => "usw",
                DeviceType.AccessPoint => "uap",
                _ => "unknown"
            };

            dict[entry.Device.Mac] = rawDevice;
        }

        return dict;
    }

    /// <summary>
    /// Builds the ServerPosition from the configured server.
    /// </summary>
    public ServerPosition BuildServerPosition()
    {
        if (_server == null)
            throw new InvalidOperationException("No server configured. Call WithServer() first.");

        return new ServerPosition
        {
            IpAddress = _server.Ip,
            Mac = _server.Mac,
            Name = _server.Name,
            SwitchMac = _server.SwitchMac,
            SwitchPort = _server.SwitchPort,
            NetworkId = _server.NetworkId,
            VlanId = _server.VlanId,
            IsWired = true,
            DiscoveredAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Gets a DiscoveredDevice from the topology by MAC address.
    /// </summary>
    public DiscoveredDevice? GetDevice(string mac) =>
        _devices.FirstOrDefault(d => d.Device.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))?.Device;

    /// <summary>
    /// Gets a DiscoveredClient from the topology by MAC address.
    /// </summary>
    public DiscoveredClient? GetClient(string mac) =>
        _clients.FirstOrDefault(c => c.Client.Mac.Equals(mac, StringComparison.OrdinalIgnoreCase))?.Client;

    #endregion

    #region Helpers

    /// <summary>
    /// Ensures a port exists in the port table. If not present, adds it with the given speed.
    /// </summary>
    private static void EnsurePortInTable(List<PortEntry> ports, int portIdx, int defaultSpeed)
    {
        if (ports.All(p => p.PortIdx != portIdx))
        {
            ports.Add(new PortEntry
            {
                PortIdx = portIdx,
                Speed = defaultSpeed > 0 ? defaultSpeed : 1000,
                Up = true,
                Name = $"Port {portIdx}"
            });
        }
    }

    /// <summary>
    /// Generates a deterministic IP from a MAC address suffix for convenience.
    /// </summary>
    private static string GenerateIp(string mac)
    {
        // Use last two octets of MAC to generate an IP in 192.0.2.0/24
        var parts = mac.Split(':');
        if (parts.Length >= 6)
        {
            var lastOctet = Convert.ToInt32(parts[5], 16);
            // Avoid 0 and 255
            lastOctet = Math.Clamp(lastOctet, 1, 254);
            return $"192.0.2.{lastOctet}";
        }
        return "192.0.2.99";
    }

    #endregion
}
