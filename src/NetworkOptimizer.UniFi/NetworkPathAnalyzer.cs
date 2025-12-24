using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Interface for providing access to the UniFi API client.
/// Implemented by UniFiConnectionService in the Web project.
/// </summary>
public interface IUniFiClientProvider
{
    bool IsConnected { get; }
    UniFiApiClient? Client { get; }
}

/// <summary>
/// Analyzes network paths between the iperf3 server and target devices.
/// Discovers L2/L3 paths, calculates theoretical bottlenecks, and grades speed test results.
/// </summary>
public class NetworkPathAnalyzer
{
    private readonly IUniFiClientProvider _clientProvider;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NetworkPathAnalyzer> _logger;
    private readonly ILoggerFactory _loggerFactory;

    // Cache keys
    private const string TopologyCacheKey = "NetworkTopology";
    private const string ServerPositionCacheKey = "ServerPosition";
    private const string RawDevicesCacheKey = "RawDevices";

    // Cache duration
    private static readonly TimeSpan TopologyCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ServerPositionCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RawDevicesCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Empirical realistic maximum throughput by link speed (Mbps).
    /// Based on real-world testing with iperf3.
    /// </summary>
    private static readonly Dictionary<int, int> RealisticMaxByLinkSpeed = new()
    {
        { 10000, 9910 },   // 10 GbE copper: ~9.91 Gbps practical max
        { 5000, 4850 },    // 5 GbE: ~97% (estimated, between 2.5G and 10G)
        { 2500, 2380 },    // 2.5 GbE: ~2.38 Gbps practical max
        { 1000, 960 },     // 1 GbE: ~960 Mbps practical max
        { 100, 94 },       // 100 Mbps: ~94% typical
    };

    // Fallback overhead factor for unknown link speeds
    private const double FallbackOverheadFactor = 0.94;

    /// <summary>
    /// Known gateway inter-VLAN routing throughput limits (Mbps).
    /// These are empirical values from real-world testing.
    /// </summary>
    private static readonly Dictionary<string, int> GatewayRoutingLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        // UCG-Fiber: Tested at 9.8+ Gbps inter-VLAN
        { "UCG-Fiber", 9800 },
        { "UniFi Cloud Gateway Fiber", 9800 },

        // Add other known gateways as we gather data
        // { "UDM-Pro", 3500 },
        // { "UDM-SE", 3500 },
    };

    public NetworkPathAnalyzer(
        IUniFiClientProvider clientProvider,
        IMemoryCache cache,
        ILoggerFactory loggerFactory)
    {
        _clientProvider = clientProvider;
        _cache = cache;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NetworkPathAnalyzer>();
    }

    /// <summary>
    /// Discovers the server's position in the network topology.
    /// The server is the machine running this application (the iperf3 server).
    /// </summary>
    public async Task<ServerPosition?> DiscoverServerPositionAsync(CancellationToken cancellationToken = default)
    {
        // Check cache first
        if (_cache.TryGetValue(ServerPositionCacheKey, out ServerPosition? cached))
        {
            return cached;
        }

        _logger.LogInformation("Discovering server position in network topology");

        // Get local IP addresses
        var localIps = GetLocalIpAddresses();
        if (localIps.Count == 0)
        {
            _logger.LogWarning("Could not determine local IP addresses");
            return null;
        }

        _logger.LogDebug("Local IP addresses: {Ips}", string.Join(", ", localIps));

        // Get topology
        var topology = await GetTopologyAsync(cancellationToken);
        if (topology == null)
        {
            _logger.LogWarning("Could not retrieve network topology");
            return null;
        }

        // Find this server in the client list
        var serverClient = topology.Clients.FirstOrDefault(c =>
            localIps.Contains(c.IpAddress, StringComparer.OrdinalIgnoreCase));

        if (serverClient == null)
        {
            _logger.LogWarning("Server not found in UniFi client list. Local IPs: {Ips}", string.Join(", ", localIps));
            return null;
        }

        // Find the switch it's connected to
        DiscoveredDevice? connectedSwitch = null;
        if (!string.IsNullOrEmpty(serverClient.ConnectedToDeviceMac))
        {
            connectedSwitch = topology.Devices.FirstOrDefault(d =>
                d.Mac.Equals(serverClient.ConnectedToDeviceMac, StringComparison.OrdinalIgnoreCase));
        }

        // Get network info
        var network = topology.Networks.FirstOrDefault(n =>
            n.Id == serverClient.NetworkId || n.Name == serverClient.Network);

        var position = new ServerPosition
        {
            IpAddress = serverClient.IpAddress,
            Mac = serverClient.Mac,
            SwitchMac = serverClient.ConnectedToDeviceMac,
            SwitchName = connectedSwitch?.Name,
            SwitchModel = connectedSwitch?.ModelDisplay ?? connectedSwitch?.Model,
            SwitchPort = serverClient.SwitchPort,
            NetworkId = serverClient.NetworkId,
            NetworkName = serverClient.Network,
            VlanId = network?.VlanId,
            IsWired = serverClient.IsWired,
            DiscoveredAt = DateTime.UtcNow
        };

        _logger.LogInformation("Server position: {Ip} on {Switch} port {Port} ({Network})",
            position.IpAddress, position.SwitchName ?? "unknown", position.SwitchPort, position.NetworkName);

        // Cache the result
        _cache.Set(ServerPositionCacheKey, position, ServerPositionCacheDuration);

        return position;
    }

    /// <summary>
    /// Calculates the network path from the server to a target device or client.
    /// </summary>
    public async Task<NetworkPath> CalculatePathAsync(
        string targetHost,
        CancellationToken cancellationToken = default)
    {
        var path = new NetworkPath
        {
            DestinationHost = targetHost
        };

        try
        {
            // Get server position
            var serverPosition = await DiscoverServerPositionAsync(cancellationToken);
            if (serverPosition == null)
            {
                path.IsValid = false;
                path.ErrorMessage = "Could not determine server position in network";
                return path;
            }

            path.SourceHost = serverPosition.IpAddress;
            path.SourceMac = serverPosition.Mac;
            path.SourceVlanId = serverPosition.VlanId;
            path.SourceNetworkName = serverPosition.NetworkName;

            // Get topology
            var topology = await GetTopologyAsync(cancellationToken);
            if (topology == null)
            {
                path.IsValid = false;
                path.ErrorMessage = "Could not retrieve network topology";
                return path;
            }

            // Find target - could be a UniFi device or a client
            var targetDevice = FindDevice(topology, targetHost);
            var targetClient = targetDevice == null ? FindClient(topology, targetHost) : null;

            if (targetDevice == null && targetClient == null)
            {
                path.IsValid = false;
                path.ErrorMessage = $"Target '{targetHost}' not found in network topology";
                return path;
            }

            // Set destination info
            if (targetDevice != null)
            {
                path.DestinationMac = targetDevice.Mac;
                // Devices don't have VLAN directly, they span VLANs
            }
            else if (targetClient != null)
            {
                path.DestinationMac = targetClient.Mac;
                var clientNetwork = topology.Networks.FirstOrDefault(n =>
                    n.Id == targetClient.NetworkId || n.Name == targetClient.Network);
                path.DestinationVlanId = clientNetwork?.VlanId;
                path.DestinationNetworkName = targetClient.Network;
            }

            // Detect inter-VLAN routing
            if (path.SourceVlanId.HasValue && path.DestinationVlanId.HasValue &&
                path.SourceVlanId != path.DestinationVlanId)
            {
                path.RequiresRouting = true;
                var gateway = topology.Devices.FirstOrDefault(d => d.Type == DeviceType.Gateway);
                if (gateway != null)
                {
                    path.GatewayDevice = gateway.Name;
                    path.GatewayModel = gateway.ModelDisplay ?? gateway.Model;
                }
            }

            // Get raw devices for port speed lookup
            var rawDevices = await GetRawDevicesAsync(cancellationToken);

            // Build the hop list
            BuildHopList(path, serverPosition, targetDevice, targetClient, topology, rawDevices);

            // Calculate bottleneck
            CalculateBottleneck(path);

            _logger.LogInformation("Path calculated: {Source} -> {Dest}, {HopCount} hops, max {MaxMbps} Mbps, routing: {Routing}",
                path.SourceHost, path.DestinationHost, path.Hops.Count,
                path.TheoreticalMaxMbps, path.RequiresRouting);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating path to {Target}", targetHost);
            path.IsValid = false;
            path.ErrorMessage = $"Error calculating path: {ex.Message}";
        }

        return path;
    }

    /// <summary>
    /// Analyzes a speed test result against the calculated network path.
    /// </summary>
    public PathAnalysisResult AnalyzeSpeedTest(NetworkPath path, double fromDeviceMbps, double toDeviceMbps)
    {
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = fromDeviceMbps,
            MeasuredToDeviceMbps = toDeviceMbps
        };

        if (path.IsValid && path.RealisticMaxMbps > 0)
        {
            result.CalculateEfficiency();
            result.GenerateInsights();
        }
        else
        {
            result.Insights.Add("Path analysis unavailable - cannot grade performance");
            if (!string.IsNullOrEmpty(path.ErrorMessage))
            {
                result.Insights.Add(path.ErrorMessage);
            }
        }

        return result;
    }

    private async Task<NetworkTopology?> GetTopologyAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(TopologyCacheKey, out NetworkTopology? cached))
        {
            return cached;
        }

        // Check if we have a connected client
        if (!_clientProvider.IsConnected || _clientProvider.Client == null)
        {
            _logger.LogWarning("Cannot get topology - not connected to UniFi controller");
            return null;
        }

        // Create a discovery instance with the current client
        var discovery = new UniFiDiscovery(
            _clientProvider.Client,
            _loggerFactory.CreateLogger<UniFiDiscovery>());

        var topology = await discovery.DiscoverTopologyAsync(cancellationToken);

        if (topology != null)
        {
            _cache.Set(TopologyCacheKey, topology, TopologyCacheDuration);
        }

        return topology;
    }

    /// <summary>
    /// Gets raw UniFi device responses with port table data.
    /// </summary>
    private async Task<Dictionary<string, UniFiDeviceResponse>> GetRawDevicesAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(RawDevicesCacheKey, out Dictionary<string, UniFiDeviceResponse>? cached))
        {
            return cached ?? new Dictionary<string, UniFiDeviceResponse>();
        }

        if (!_clientProvider.IsConnected || _clientProvider.Client == null)
        {
            return new Dictionary<string, UniFiDeviceResponse>();
        }

        var devices = await _clientProvider.Client.GetDevicesAsync(cancellationToken);
        var deviceDict = devices?
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .ToDictionary(d => d.Mac, d => d, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, UniFiDeviceResponse>();

        _cache.Set(RawDevicesCacheKey, deviceDict, RawDevicesCacheDuration);

        return deviceDict;
    }

    /// <summary>
    /// Gets the port speed for a specific port on a device.
    /// </summary>
    private int GetPortSpeedFromRawDevices(
        Dictionary<string, UniFiDeviceResponse> rawDevices,
        string? deviceMac,
        int? portIndex)
    {
        if (string.IsNullOrEmpty(deviceMac) || !portIndex.HasValue)
        {
            return 0;
        }

        if (!rawDevices.TryGetValue(deviceMac, out var device))
        {
            return 0;
        }

        var port = device.PortTable?.FirstOrDefault(p => p.PortIdx == portIndex.Value);
        if (port == null)
        {
            return 0;
        }

        // Speed is in Mbps in the API
        return port.Speed;
    }

    private static List<string> GetLocalIpAddresses()
    {
        var ips = new List<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        ips.Add(addr.Address.ToString());
                    }
                }
            }
        }
        catch
        {
            // Ignore network enumeration errors
        }

        return ips;
    }

    private static DiscoveredDevice? FindDevice(NetworkTopology topology, string hostOrIp)
    {
        return topology.Devices.FirstOrDefault(d =>
            d.IpAddress.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            d.Name.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            d.Mac.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase));
    }

    private static DiscoveredClient? FindClient(NetworkTopology topology, string hostOrIp)
    {
        return topology.Clients.FirstOrDefault(c =>
            c.IpAddress.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            c.Hostname.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            c.Mac.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase));
    }

    private void BuildHopList(
        NetworkPath path,
        ServerPosition serverPosition,
        DiscoveredDevice? targetDevice,
        DiscoveredClient? targetClient,
        NetworkTopology topology,
        Dictionary<string, UniFiDeviceResponse> rawDevices)
    {
        var hops = new List<NetworkHop>();
        var deviceDict = topology.Devices.ToDictionary(d => d.Mac, d => d, StringComparer.OrdinalIgnoreCase);

        // Start from target and trace back to server's switch
        string? currentMac;
        int? currentPort;

        if (targetDevice != null)
        {
            // Target is a UniFi device - use its uplink
            currentMac = targetDevice.UplinkMac;
            currentPort = targetDevice.UplinkPort;

            // Add target device as first hop
            var deviceHop = new NetworkHop
            {
                Order = 0,
                Type = GetHopType(targetDevice.Type),
                DeviceMac = targetDevice.Mac,
                DeviceName = targetDevice.Name,
                DeviceModel = targetDevice.ModelDisplay ?? targetDevice.Model,
                DeviceIp = targetDevice.IpAddress,
                IngressPort = targetDevice.UplinkPort,
                EgressPort = targetDevice.UplinkPort,
                Notes = "Target device"
            };

            // Get uplink speed from the port on the upstream switch
            if (!string.IsNullOrEmpty(currentMac) && currentPort.HasValue)
            {
                deviceHop.IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, currentMac, currentPort);
                deviceHop.EgressSpeedMbps = deviceHop.IngressSpeedMbps;
            }

            hops.Add(deviceHop);
        }
        else if (targetClient != null)
        {
            // Target is a client - start from its connected device
            currentMac = targetClient.ConnectedToDeviceMac;
            currentPort = targetClient.SwitchPort;

            var hop = new NetworkHop
            {
                Order = 0,
                Type = targetClient.IsWired ? HopType.Client : HopType.AccessPoint,
                DeviceMac = targetClient.Mac,
                DeviceName = targetClient.Name ?? targetClient.Hostname,
                DeviceIp = targetClient.IpAddress,
                Notes = targetClient.IsWired ? "Target client (wired)" : $"Target client ({targetClient.ConnectionType})"
            };

            if (!targetClient.IsWired)
            {
                // Wireless client - speed depends on link rate
                hop.EgressSpeedMbps = (int)(targetClient.TxRate / 1000); // kbps to Mbps
            }
            else if (!string.IsNullOrEmpty(currentMac) && currentPort.HasValue)
            {
                // Wired client - get port speed from switch
                int portSpeed = GetPortSpeedFromRawDevices(rawDevices, currentMac, currentPort);
                hop.EgressSpeedMbps = portSpeed;
                hop.IngressSpeedMbps = portSpeed;
            }

            hops.Add(hop);
        }
        else
        {
            return; // No target found
        }

        // Check if both server and target are on the same switch
        bool sameSwitch = !string.IsNullOrEmpty(currentMac) &&
                          currentMac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase);

        if (sameSwitch)
        {
            // Both endpoints on same switch - just add the switch as a single hop
            if (deviceDict.TryGetValue(currentMac!, out var switchDevice))
            {
                // Get server's port speed
                int serverPortSpeed = GetPortSpeedFromRawDevices(rawDevices, currentMac, serverPosition.SwitchPort);

                var switchHop = new NetworkHop
                {
                    Order = 1,
                    Type = HopType.Switch,
                    DeviceMac = switchDevice.Mac,
                    DeviceName = switchDevice.Name,
                    DeviceModel = switchDevice.ModelDisplay ?? switchDevice.Model,
                    DeviceIp = switchDevice.IpAddress,
                    IngressPort = currentPort,
                    IngressPortName = GetPortName(rawDevices, currentMac, currentPort),
                    IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, currentMac, currentPort),
                    EgressPort = serverPosition.SwitchPort,
                    EgressPortName = GetPortName(rawDevices, currentMac, serverPosition.SwitchPort),
                    EgressSpeedMbps = serverPortSpeed,
                    Notes = "Same switch (direct L2 path)"
                };

                hops.Add(switchHop);
            }
        }
        else
        {
            // Trace uplinks until we reach the server's switch
            int hopOrder = 1;
            int maxHops = 10; // Prevent infinite loops

            while (!string.IsNullOrEmpty(currentMac) && hopOrder < maxHops)
            {
                if (!deviceDict.TryGetValue(currentMac, out var device))
                    break;

                // Check if we've reached the server's switch
                bool isServerSwitch = currentMac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase);

                var hop = new NetworkHop
                {
                    Order = hopOrder,
                    Type = GetHopType(device.Type),
                    DeviceMac = device.Mac,
                    DeviceName = device.Name,
                    DeviceModel = device.ModelDisplay ?? device.Model,
                    DeviceIp = device.IpAddress,
                    IngressPort = currentPort,
                    IngressPortName = GetPortName(rawDevices, currentMac, currentPort),
                    IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, currentMac, currentPort),
                    EgressPort = isServerSwitch ? serverPosition.SwitchPort : device.UplinkPort
                };

                // Get egress port speed
                if (isServerSwitch)
                {
                    hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, currentMac, serverPosition.SwitchPort);
                    hop.EgressPortName = GetPortName(rawDevices, currentMac, serverPosition.SwitchPort);
                    hop.Notes = "Server's switch";
                }
                else if (!string.IsNullOrEmpty(device.UplinkMac))
                {
                    // Egress speed is the uplink speed - get from uplink switch's port
                    hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, device.UplinkMac, device.UplinkPort);
                    hop.EgressPortName = GetPortName(rawDevices, device.UplinkMac, device.UplinkPort);
                }

                hops.Add(hop);

                if (isServerSwitch)
                    break;

                // Move to next hop
                currentMac = device.UplinkMac;
                currentPort = device.UplinkPort;
                hopOrder++;
            }
        }

        // Add gateway hop if inter-VLAN routing is required
        if (path.RequiresRouting && !string.IsNullOrEmpty(path.GatewayDevice))
        {
            var gateway = topology.Devices.FirstOrDefault(d => d.Type == DeviceType.Gateway);
            if (gateway != null)
            {
                var gatewayHop = new NetworkHop
                {
                    Order = hops.Count,
                    Type = HopType.Gateway,
                    DeviceMac = gateway.Mac,
                    DeviceName = gateway.Name,
                    DeviceModel = gateway.ModelDisplay ?? gateway.Model,
                    DeviceIp = gateway.IpAddress,
                    Notes = "L3 routing (inter-VLAN)"
                };

                // Check for known gateway routing limits
                if (GatewayRoutingLimits.TryGetValue(gateway.ModelDisplay ?? "", out int limit) ||
                    GatewayRoutingLimits.TryGetValue(gateway.Model ?? "", out limit))
                {
                    gatewayHop.IngressSpeedMbps = limit;
                    gatewayHop.EgressSpeedMbps = limit;
                    gatewayHop.Notes = $"L3 routing (inter-VLAN) - {limit / 1000.0:F1} Gbps routing capacity";
                }

                hops.Add(gatewayHop);
            }
        }

        // Sort hops by order
        path.Hops = hops.OrderBy(h => h.Order).ToList();
    }

    private static HopType GetHopType(DeviceType deviceType) => deviceType switch
    {
        DeviceType.Gateway => HopType.Gateway,
        DeviceType.Switch => HopType.Switch,
        DeviceType.AccessPoint => HopType.AccessPoint,
        _ => HopType.Client
    };

    /// <summary>
    /// Gets the port name from raw device data.
    /// </summary>
    private static string? GetPortName(
        Dictionary<string, UniFiDeviceResponse> rawDevices,
        string? deviceMac,
        int? portIndex)
    {
        if (string.IsNullOrEmpty(deviceMac) || !portIndex.HasValue)
        {
            return null;
        }

        if (!rawDevices.TryGetValue(deviceMac, out var device))
        {
            return $"Port {portIndex}";
        }

        var port = device.PortTable?.FirstOrDefault(p => p.PortIdx == portIndex.Value);
        if (port != null && !string.IsNullOrEmpty(port.Name))
        {
            return port.Name;
        }

        return $"Port {portIndex}";
    }

    /// <summary>
    /// Gets the realistic maximum throughput for a given link speed.
    /// Uses empirical data where available, falls back to 94% overhead estimate.
    /// </summary>
    private static int GetRealisticMax(int theoreticalMbps)
    {
        if (RealisticMaxByLinkSpeed.TryGetValue(theoreticalMbps, out int realistic))
        {
            return realistic;
        }

        // Fallback: use 94% for unknown speeds
        return (int)(theoreticalMbps * FallbackOverheadFactor);
    }

    private void CalculateBottleneck(NetworkPath path)
    {
        if (path.Hops.Count == 0)
        {
            path.TheoreticalMaxMbps = 0;
            path.RealisticMaxMbps = 0;
            return;
        }

        int minSpeed = int.MaxValue;
        NetworkHop? bottleneckHop = null;
        string? bottleneckPort = null;

        foreach (var hop in path.Hops)
        {
            // Check ingress
            if (hop.IngressSpeedMbps > 0 && hop.IngressSpeedMbps < minSpeed)
            {
                minSpeed = hop.IngressSpeedMbps;
                bottleneckHop = hop;
                bottleneckPort = hop.IngressPortName ?? $"ingress port {hop.IngressPort}";
            }

            // Check egress
            if (hop.EgressSpeedMbps > 0 && hop.EgressSpeedMbps < minSpeed)
            {
                minSpeed = hop.EgressSpeedMbps;
                bottleneckHop = hop;
                bottleneckPort = hop.EgressPortName ?? $"egress port {hop.EgressPort}";
            }
        }

        if (minSpeed == int.MaxValue)
        {
            // No speed data available - assume 1 Gbps
            minSpeed = 1000;
        }

        path.TheoreticalMaxMbps = minSpeed;
        path.RealisticMaxMbps = GetRealisticMax(minSpeed);

        if (bottleneckHop != null)
        {
            bottleneckHop.IsBottleneck = true;

            if (minSpeed < 1000)
            {
                path.BottleneckDescription = $"{minSpeed} Mbps link on {bottleneckPort} of {bottleneckHop.DeviceName}";
            }
            else
            {
                var gbps = minSpeed / 1000.0;
                var gbpsStr = gbps % 1 == 0 ? $"{(int)gbps}" : $"{gbps:F1}";
                path.BottleneckDescription = $"{gbpsStr} Gbps link on {bottleneckPort} of {bottleneckHop.DeviceName}";
            }
        }
    }
}

/// <summary>
/// Represents the iperf3 server's position in the network.
/// </summary>
public class ServerPosition
{
    public string IpAddress { get; set; } = "";
    public string Mac { get; set; } = "";
    public string? SwitchMac { get; set; }
    public string? SwitchName { get; set; }
    public string? SwitchModel { get; set; }
    public int? SwitchPort { get; set; }
    public string? NetworkId { get; set; }
    public string? NetworkName { get; set; }
    public int? VlanId { get; set; }
    public bool IsWired { get; set; }
    public DateTime DiscoveredAt { get; set; }
}
