using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi.Tests.Fixtures;

/// <summary>
/// Static helper class for creating test data.
/// Uses RFC 5737 test IPs (192.0.2.x, 198.51.100.x, 203.0.113.x) per project guidelines.
/// </summary>
public static class NetworkTestData
{
    // Standard test MAC addresses
    public const string GatewayMac = "aa:bb:cc:00:00:01";
    public const string SwitchMac = "aa:bb:cc:00:00:02";
    public const string ApWiredMac = "aa:bb:cc:00:00:03";
    public const string ApMeshMac = "aa:bb:cc:00:00:04";
    public const string ClientWiredMac = "aa:bb:cc:00:01:01";
    public const string ClientWirelessMac = "aa:bb:cc:00:01:02";
    public const string ServerMac = "aa:bb:cc:00:02:01";

    // Daisy-chain topology MACs (Gateway -> Switch1 -> Switch2 -> Server)
    public const string Switch1Mac = "aa:bb:cc:00:00:10";  // ProXG equivalent
    public const string Switch2Mac = "aa:bb:cc:00:00:11";  // FlexXG equivalent
    public const string NasMac = "aa:bb:cc:00:01:10";      // NAS on Switch1

    // Standard test IPs (RFC 5737)
    public const string GatewayIp = "192.0.2.1";
    public const string SwitchIp = "192.0.2.2";
    public const string ApWiredIp = "192.0.2.3";
    public const string ApMeshIp = "192.0.2.4";
    public const string ClientWiredIp = "192.0.2.100";
    public const string ClientWirelessIp = "192.0.2.101";
    public const string ServerIp = "192.0.2.200";

    // Daisy-chain topology IPs
    public const string Switch1Ip = "192.0.2.10";
    public const string Switch2Ip = "192.0.2.11";
    public const string NasIp = "192.0.2.110";

    #region Device Creators

    /// <summary>
    /// Creates a gateway device (UDM, USG, etc.)
    /// </summary>
    public static DiscoveredDevice CreateGateway(
        string mac = GatewayMac,
        string ip = GatewayIp,
        string name = "Gateway",
        string model = "UDM-Pro",
        int lanSpeed = 1000)
    {
        return new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            LanIpAddress = ip,
            Name = name,
            Model = model,
            ModelDisplay = model,
            Type = DeviceType.Gateway,
            Adopted = true,
            State = 1,
            UplinkSpeedMbps = lanSpeed,
            IsUplinkConnected = true
        };
    }

    /// <summary>
    /// Creates a switch device
    /// </summary>
    public static DiscoveredDevice CreateSwitch(
        string mac = SwitchMac,
        string ip = SwitchIp,
        string name = "Switch",
        string model = "USW-24-PoE",
        string? uplinkMac = GatewayMac,
        int? uplinkPort = 1,
        int uplinkSpeed = 1000)
    {
        return new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            Name = name,
            Model = model,
            ModelDisplay = model,
            Type = DeviceType.Switch,
            Adopted = true,
            State = 1,
            UplinkMac = uplinkMac,
            UplinkPort = uplinkPort,
            UplinkSpeedMbps = uplinkSpeed,
            UplinkType = "wire",
            IsUplinkConnected = true,
            PortCount = 24
        };
    }

    /// <summary>
    /// Creates a wired access point (uplinked via Ethernet)
    /// </summary>
    public static DiscoveredDevice CreateWiredAccessPoint(
        string mac = ApWiredMac,
        string ip = ApWiredIp,
        string name = "AP-Wired",
        string model = "U6-Pro",
        string? uplinkMac = SwitchMac,
        int? uplinkPort = 5,
        int uplinkSpeed = 1000)
    {
        return new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            Name = name,
            Model = model,
            ModelDisplay = model,
            Type = DeviceType.AccessPoint,
            Adopted = true,
            State = 1,
            UplinkMac = uplinkMac,
            UplinkPort = uplinkPort,
            UplinkSpeedMbps = uplinkSpeed,
            UplinkType = "wire",
            IsUplinkConnected = true
        };
    }

    /// <summary>
    /// Creates a mesh access point (uplinked via wireless)
    /// </summary>
    public static DiscoveredDevice CreateMeshAccessPoint(
        string mac = ApMeshMac,
        string ip = ApMeshIp,
        string name = "AP-Mesh",
        string model = "U6-Mesh",
        string? uplinkMac = ApWiredMac,
        int txRateKbps = 866000,
        int rxRateKbps = 866000,
        string radioBand = "na",
        int channel = 36,
        int signalDbm = -55,
        int noiseDbm = -95)
    {
        return new DiscoveredDevice
        {
            Mac = mac,
            IpAddress = ip,
            Name = name,
            Model = model,
            ModelDisplay = model,
            Type = DeviceType.AccessPoint,
            Adopted = true,
            State = 1,
            UplinkMac = uplinkMac,
            UplinkPort = null,
            UplinkSpeedMbps = txRateKbps / 1000, // Mesh uses PHY rate
            UplinkType = "wireless",
            UplinkTxRateKbps = txRateKbps,
            UplinkRxRateKbps = rxRateKbps,
            UplinkRadioBand = radioBand,
            UplinkChannel = channel,
            UplinkSignalDbm = signalDbm,
            UplinkNoiseDbm = noiseDbm,
            IsUplinkConnected = true
        };
    }

    #endregion

    #region Client Creators

    /// <summary>
    /// Creates a wired client
    /// </summary>
    public static DiscoveredClient CreateWiredClient(
        string mac = ClientWiredMac,
        string ip = ClientWiredIp,
        string hostname = "client-wired",
        string? connectedToMac = SwitchMac,
        int switchPort = 10,
        string network = "Default",
        int? vlanId = 1)
    {
        return new DiscoveredClient
        {
            Mac = mac,
            IpAddress = ip,
            Hostname = hostname,
            Name = hostname,
            IsWired = true,
            ConnectedToDeviceMac = connectedToMac,
            SwitchPort = switchPort,
            Network = network,
            NetworkId = vlanId?.ToString() ?? "1"
        };
    }

    /// <summary>
    /// Creates a wireless client with stale/missing AP data.
    /// This simulates the condition where UniFi API hasn't fully populated the client's connection info.
    /// </summary>
    public static DiscoveredClient CreateStaleWirelessClient(
        string mac = ClientWirelessMac,
        string ip = ClientWirelessIp,
        string hostname = "client-wifi-stale",
        string network = "Default",
        int? vlanId = 1)
    {
        return new DiscoveredClient
        {
            Mac = mac,
            IpAddress = ip,
            Hostname = hostname,
            Name = hostname,
            IsWired = false,
            ConnectedToDeviceMac = null, // Key: no AP MAC yet
            TxRate = 0,
            RxRate = 0,
            Radio = null, // No radio info
            Channel = null,
            SignalStrength = null,
            NoiseLevel = null,
            Network = network,
            NetworkId = vlanId?.ToString() ?? "1",
            IsMlo = false,
            RadioProtocol = null
        };
    }

    /// <summary>
    /// Creates a wireless client (single-link, non-MLO)
    /// </summary>
    public static DiscoveredClient CreateWirelessClient(
        string mac = ClientWirelessMac,
        string ip = ClientWirelessIp,
        string hostname = "client-wifi",
        string? connectedToMac = ApWiredMac,
        long txRateKbps = 866000,
        long rxRateKbps = 866000,
        string radio = "na",
        int channel = 36,
        int signalDbm = -55,
        int noiseDbm = -95,
        string network = "Default",
        int? vlanId = 1)
    {
        return new DiscoveredClient
        {
            Mac = mac,
            IpAddress = ip,
            Hostname = hostname,
            Name = hostname,
            IsWired = false,
            ConnectedToDeviceMac = connectedToMac,
            TxRate = txRateKbps,
            RxRate = rxRateKbps,
            Radio = radio,
            Channel = channel,
            SignalStrength = signalDbm,
            NoiseLevel = noiseDbm,
            Network = network,
            NetworkId = vlanId?.ToString() ?? "1",
            IsMlo = false,
            RadioProtocol = "AX"
        };
    }

    /// <summary>
    /// Creates an MLO (Wi-Fi 7 multi-link) client
    /// </summary>
    public static DiscoveredClient CreateMloClient(
        string mac = ClientWirelessMac,
        string ip = ClientWirelessIp,
        string hostname = "client-wifi7",
        string? connectedToMac = ApWiredMac,
        string network = "Default",
        int? vlanId = 1)
    {
        return new DiscoveredClient
        {
            Mac = mac,
            IpAddress = ip,
            Hostname = hostname,
            Name = hostname,
            IsWired = false,
            ConnectedToDeviceMac = connectedToMac,
            Network = network,
            NetworkId = vlanId?.ToString() ?? "1",
            IsMlo = true,
            RadioProtocol = "BE",
            MloLinks = new List<MloLink>
            {
                new MloLink
                {
                    Radio = "ng",
                    Channel = 6,
                    ChannelWidth = 40,
                    SignalDbm = -50,
                    NoiseDbm = -95,
                    TxRateKbps = 574000,
                    RxRateKbps = 574000
                },
                new MloLink
                {
                    Radio = "na",
                    Channel = 36,
                    ChannelWidth = 160,
                    SignalDbm = -55,
                    NoiseDbm = -95,
                    TxRateKbps = 2400000,
                    RxRateKbps = 2400000
                },
                new MloLink
                {
                    Radio = "6e",
                    Channel = 37,
                    ChannelWidth = 320,
                    SignalDbm = -52,
                    NoiseDbm = -95,
                    TxRateKbps = 5760000,
                    RxRateKbps = 5760000
                }
            }
        };
    }

    #endregion

    #region Path Creators

    /// <summary>
    /// Creates a simple wired path: Client -> Switch -> Gateway -> Server
    /// </summary>
    public static NetworkPath CreateWiredClientPath(int linkSpeedMbps = 1000)
    {
        return new NetworkPath
        {
            SourceHost = ServerIp,
            SourceMac = ServerMac,
            SourceVlanId = 1,
            SourceNetworkName = "Default",
            DestinationHost = ClientWiredIp,
            DestinationMac = ClientWiredMac,
            DestinationVlanId = 1,
            DestinationNetworkName = "Default",
            RequiresRouting = false,
            TheoreticalMaxMbps = linkSpeedMbps,
            RealisticMaxMbps = (int)(linkSpeedMbps * 0.94),
            IsValid = true,
            Hops = new List<NetworkHop>
            {
                new NetworkHop
                {
                    Order = 0,
                    Type = HopType.Client,
                    DeviceMac = ClientWiredMac,
                    DeviceName = "client-wired",
                    DeviceIp = ClientWiredIp,
                    IngressPort = 10,
                    IngressSpeedMbps = linkSpeedMbps,
                    EgressPort = 10,
                    EgressSpeedMbps = linkSpeedMbps
                },
                new NetworkHop
                {
                    Order = 1,
                    Type = HopType.Switch,
                    DeviceMac = SwitchMac,
                    DeviceName = "Switch",
                    DeviceModel = "USW-24-PoE",
                    DeviceIp = SwitchIp,
                    IngressPort = 10,
                    IngressSpeedMbps = linkSpeedMbps,
                    EgressPort = 1,
                    EgressSpeedMbps = linkSpeedMbps
                },
                new NetworkHop
                {
                    Order = 2,
                    Type = HopType.Gateway,
                    DeviceMac = GatewayMac,
                    DeviceName = "Gateway",
                    DeviceModel = "UDM-Pro",
                    DeviceIp = GatewayIp,
                    IngressPort = 1,
                    IngressSpeedMbps = linkSpeedMbps,
                    EgressPort = 1,
                    EgressSpeedMbps = linkSpeedMbps
                },
                new NetworkHop
                {
                    Order = 3,
                    Type = HopType.Server,
                    DeviceMac = ServerMac,
                    DeviceName = "Server",
                    DeviceIp = ServerIp,
                    IngressPort = 1,
                    IngressSpeedMbps = linkSpeedMbps
                }
            }
        };
    }

    /// <summary>
    /// Creates a wireless client path: WirelessClient -> AP -> Switch -> Gateway -> Server
    /// </summary>
    public static NetworkPath CreateWirelessClientPath(
        int wirelessRateMbps = 866,
        int wiredSpeedMbps = 1000)
    {
        var theoreticalMax = Math.Min(wirelessRateMbps, wiredSpeedMbps);
        return new NetworkPath
        {
            SourceHost = ServerIp,
            SourceMac = ServerMac,
            SourceVlanId = 1,
            SourceNetworkName = "Default",
            DestinationHost = ClientWirelessIp,
            DestinationMac = ClientWirelessMac,
            DestinationVlanId = 1,
            DestinationNetworkName = "Default",
            RequiresRouting = false,
            TheoreticalMaxMbps = theoreticalMax,
            RealisticMaxMbps = (int)(theoreticalMax * 0.60), // Wireless overhead
            IsValid = true,
            Hops = new List<NetworkHop>
            {
                new NetworkHop
                {
                    Order = 0,
                    Type = HopType.Client,
                    DeviceMac = ClientWirelessMac,
                    DeviceName = "client-wifi",
                    DeviceIp = ClientWirelessIp,
                    EgressSpeedMbps = wirelessRateMbps,
                    IsWirelessEgress = true,
                    IsWirelessIngress = true,
                    WirelessEgressBand = "na",
                    WirelessIngressBand = "na",
                    WirelessChannel = 36,
                    WirelessSignalDbm = -55,
                    WirelessTxRateMbps = wirelessRateMbps,
                    WirelessRxRateMbps = wirelessRateMbps
                },
                new NetworkHop
                {
                    Order = 1,
                    Type = HopType.AccessPoint,
                    DeviceMac = ApWiredMac,
                    DeviceName = "AP-Wired",
                    DeviceModel = "U6-Pro",
                    DeviceIp = ApWiredIp,
                    IngressSpeedMbps = wirelessRateMbps,
                    IsWirelessIngress = true,
                    WirelessIngressBand = "na",
                    EgressPort = 1,
                    EgressSpeedMbps = wiredSpeedMbps
                },
                new NetworkHop
                {
                    Order = 2,
                    Type = HopType.Switch,
                    DeviceMac = SwitchMac,
                    DeviceName = "Switch",
                    DeviceModel = "USW-24-PoE",
                    DeviceIp = SwitchIp,
                    IngressPort = 5,
                    IngressSpeedMbps = wiredSpeedMbps,
                    EgressPort = 1,
                    EgressSpeedMbps = wiredSpeedMbps
                },
                new NetworkHop
                {
                    Order = 3,
                    Type = HopType.Gateway,
                    DeviceMac = GatewayMac,
                    DeviceName = "Gateway",
                    DeviceModel = "UDM-Pro",
                    DeviceIp = GatewayIp,
                    IngressPort = 1,
                    IngressSpeedMbps = wiredSpeedMbps,
                    EgressPort = 1,
                    EgressSpeedMbps = wiredSpeedMbps
                },
                new NetworkHop
                {
                    Order = 4,
                    Type = HopType.Server,
                    DeviceMac = ServerMac,
                    DeviceName = "Server",
                    DeviceIp = ServerIp,
                    IngressPort = 1,
                    IngressSpeedMbps = wiredSpeedMbps
                }
            }
        };
    }

    /// <summary>
    /// Creates a mesh AP client path: WirelessClient -> MeshAP -> WiredAP -> Switch -> Gateway -> Server
    /// This tests the scenario where a client connects to a mesh AP.
    /// </summary>
    public static NetworkPath CreateMeshClientPath(
        int clientWirelessRateMbps = 866,
        int meshBackhaulRateMbps = 866,
        int wiredSpeedMbps = 1000)
    {
        var theoreticalMax = Math.Min(Math.Min(clientWirelessRateMbps, meshBackhaulRateMbps), wiredSpeedMbps);
        return new NetworkPath
        {
            SourceHost = ServerIp,
            SourceMac = ServerMac,
            SourceVlanId = 1,
            SourceNetworkName = "Default",
            DestinationHost = ClientWirelessIp,
            DestinationMac = ClientWirelessMac,
            DestinationVlanId = 1,
            DestinationNetworkName = "Default",
            RequiresRouting = false,
            TheoreticalMaxMbps = theoreticalMax,
            RealisticMaxMbps = (int)(theoreticalMax * 0.60), // Wireless overhead
            IsValid = true,
            Hops = new List<NetworkHop>
            {
                new NetworkHop
                {
                    Order = 0,
                    Type = HopType.Client,
                    DeviceMac = ClientWirelessMac,
                    DeviceName = "client-wifi",
                    DeviceIp = ClientWirelessIp,
                    EgressSpeedMbps = clientWirelessRateMbps,
                    IsWirelessEgress = true,
                    IsWirelessIngress = true,
                    WirelessEgressBand = "na",
                    WirelessIngressBand = "na",
                    WirelessChannel = 36,
                    WirelessSignalDbm = -55,
                    WirelessTxRateMbps = clientWirelessRateMbps,
                    WirelessRxRateMbps = clientWirelessRateMbps
                },
                new NetworkHop
                {
                    Order = 1,
                    Type = HopType.AccessPoint,
                    DeviceMac = ApMeshMac,
                    DeviceName = "AP-Mesh",
                    DeviceModel = "U6-Mesh",
                    DeviceIp = ApMeshIp,
                    IngressSpeedMbps = clientWirelessRateMbps,
                    IsWirelessIngress = true,
                    WirelessIngressBand = "na",
                    EgressSpeedMbps = meshBackhaulRateMbps,
                    IsWirelessEgress = true,
                    WirelessEgressBand = "na",
                    WirelessChannel = 36,
                    WirelessSignalDbm = -55,
                    WirelessTxRateMbps = meshBackhaulRateMbps,
                    WirelessRxRateMbps = meshBackhaulRateMbps
                },
                new NetworkHop
                {
                    Order = 2,
                    Type = HopType.AccessPoint,
                    DeviceMac = ApWiredMac,
                    DeviceName = "AP-Wired",
                    DeviceModel = "U6-Pro",
                    DeviceIp = ApWiredIp,
                    IngressSpeedMbps = meshBackhaulRateMbps,
                    IsWirelessIngress = true,
                    WirelessIngressBand = "na",
                    EgressPort = 1,
                    EgressSpeedMbps = wiredSpeedMbps
                },
                new NetworkHop
                {
                    Order = 3,
                    Type = HopType.Switch,
                    DeviceMac = SwitchMac,
                    DeviceName = "Switch",
                    DeviceModel = "USW-24-PoE",
                    DeviceIp = SwitchIp,
                    IngressPort = 5,
                    IngressSpeedMbps = wiredSpeedMbps,
                    EgressPort = 1,
                    EgressSpeedMbps = wiredSpeedMbps
                },
                new NetworkHop
                {
                    Order = 4,
                    Type = HopType.Gateway,
                    DeviceMac = GatewayMac,
                    DeviceName = "Gateway",
                    DeviceModel = "UDM-Pro",
                    DeviceIp = GatewayIp,
                    IngressPort = 1,
                    IngressSpeedMbps = wiredSpeedMbps,
                    EgressPort = 1,
                    EgressSpeedMbps = wiredSpeedMbps
                },
                new NetworkHop
                {
                    Order = 5,
                    Type = HopType.Server,
                    DeviceMac = ServerMac,
                    DeviceName = "Server",
                    DeviceIp = ServerIp,
                    IngressPort = 1,
                    IngressSpeedMbps = wiredSpeedMbps
                }
            }
        };
    }

    #endregion

    #region Topology Creators

    /// <summary>
    /// Creates a basic topology with gateway, switch, wired AP, and mesh AP
    /// </summary>
    public static NetworkTopology CreateBasicTopology()
    {
        return new NetworkTopology
        {
            Devices = new List<DiscoveredDevice>
            {
                CreateGateway(),
                CreateSwitch(),
                CreateWiredAccessPoint(),
                CreateMeshAccessPoint()
            },
            Clients = new List<DiscoveredClient>
            {
                CreateWiredClient(),
                CreateWirelessClient()
            },
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo
                {
                    Id = "1",
                    Name = "Default",
                    VlanId = 1,
                    IpSubnet = "192.0.2.0/24",
                    Purpose = "corporate"
                }
            }
        };
    }

    /// <summary>
    /// Creates a multi-VLAN topology for inter-VLAN routing tests
    /// </summary>
    public static NetworkTopology CreateMultiVlanTopology()
    {
        var topology = CreateBasicTopology();

        // Add IoT VLAN
        topology.Networks.Add(new NetworkInfo
        {
            Id = "10",
            Name = "IoT",
            VlanId = 10,
            IpSubnet = "198.51.100.0/24",
            Purpose = "corporate"
        });

        // Add IoT client
        topology.Clients.Add(new DiscoveredClient
        {
            Mac = "aa:bb:cc:00:01:03",
            IpAddress = "198.51.100.50",
            Hostname = "iot-device",
            Name = "iot-device",
            IsWired = true,
            ConnectedToDeviceMac = SwitchMac,
            SwitchPort = 15,
            Network = "IoT",
            NetworkId = "10"
        });

        return topology;
    }

    #endregion

    #region Daisy-Chain Topology Creators

    /// <summary>
    /// Creates a daisy-chain topology where switches are connected in series:
    /// Gateway -> Switch1 (ProXG) -> Switch2 (FlexXG) -> Server
    ///                           \-> NAS (client)
    ///
    /// This tests the scenario where:
    /// - Server is on Switch2
    /// - NAS (client) is on Switch1
    /// - Switch2 uplinks to Switch1
    /// - Switch1 uplinks to Gateway
    ///
    /// Expected L2 path for NAS -> Server: NAS -> Switch1 -> Switch2 -> Server
    /// (Gateway should NOT be in the path since they're on the same VLAN)
    /// </summary>
    public static NetworkTopology CreateDaisyChainTopology(int linkSpeedMbps = 10000)
    {
        return new NetworkTopology
        {
            Devices = new List<DiscoveredDevice>
            {
                // Gateway
                CreateGateway(lanSpeed: linkSpeedMbps),

                // Switch1 (ProXG) - uplinks to Gateway
                new DiscoveredDevice
                {
                    Mac = Switch1Mac,
                    IpAddress = Switch1Ip,
                    Name = "Switch1-ProXG",
                    Model = "USW-Pro-XG-8-PoE",
                    ModelDisplay = "USW Pro XG 8 PoE",
                    Type = DeviceType.Switch,
                    Adopted = true,
                    State = 1,
                    UplinkMac = GatewayMac,
                    UplinkPort = 1,
                    UplinkSpeedMbps = linkSpeedMbps,
                    UplinkType = "wire",
                    IsUplinkConnected = true,
                    PortCount = 8
                },

                // Switch2 (FlexXG) - uplinks to Switch1
                new DiscoveredDevice
                {
                    Mac = Switch2Mac,
                    IpAddress = Switch2Ip,
                    Name = "Switch2-FlexXG",
                    Model = "USW-Flex-XG",
                    ModelDisplay = "USW Flex XG",
                    Type = DeviceType.Switch,
                    Adopted = true,
                    State = 1,
                    UplinkMac = Switch1Mac,
                    UplinkPort = 2,
                    UplinkSpeedMbps = linkSpeedMbps,
                    UplinkType = "wire",
                    IsUplinkConnected = true,
                    PortCount = 4
                }
            },
            Clients = new List<DiscoveredClient>
            {
                // NAS client on Switch1
                new DiscoveredClient
                {
                    Mac = NasMac,
                    IpAddress = NasIp,
                    Hostname = "nas-mother",
                    Name = "NAS Mother",
                    IsWired = true,
                    ConnectedToDeviceMac = Switch1Mac,
                    SwitchPort = 3,
                    Network = "Default",
                    NetworkId = "1"
                }
            },
            Networks = new List<NetworkInfo>
            {
                new NetworkInfo
                {
                    Id = "1",
                    Name = "Default",
                    VlanId = 1,
                    IpSubnet = "192.0.2.0/24",
                    Purpose = "corporate"
                }
            }
        };
    }

    /// <summary>
    /// Creates a ServerPosition for testing where server is on Switch2 (FlexXG)
    /// </summary>
    public static ServerPosition CreateDaisyChainServerPosition()
    {
        return new ServerPosition
        {
            IpAddress = ServerIp,
            Mac = ServerMac,
            Name = "Server-Mac",
            SwitchMac = Switch2Mac,
            SwitchName = "Switch2-FlexXG",
            SwitchPort = 3,
            NetworkName = "Default",
            VlanId = 1,
            IsWired = true
        };
    }

    /// <summary>
    /// Creates a ServerPosition for testing where server is directly on the gateway
    /// </summary>
    public static ServerPosition CreateGatewayServerPosition()
    {
        return new ServerPosition
        {
            IpAddress = ServerIp,
            Mac = ServerMac,
            Name = "Server",
            SwitchMac = GatewayMac,
            SwitchName = "Gateway",
            SwitchPort = 1,
            NetworkName = "Default",
            VlanId = 1,
            IsWired = true
        };
    }

    /// <summary>
    /// Creates a ServerPosition for testing where server is on the same switch as client
    /// </summary>
    public static ServerPosition CreateSameSwitchServerPosition()
    {
        return new ServerPosition
        {
            IpAddress = ServerIp,
            Mac = ServerMac,
            Name = "Server",
            SwitchMac = Switch1Mac,
            SwitchName = "Switch1-ProXG",
            SwitchPort = 4,
            NetworkName = "Default",
            VlanId = 1,
            IsWired = true
        };
    }

    #endregion
}
