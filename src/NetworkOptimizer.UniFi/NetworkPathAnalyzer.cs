using System.Net.Sockets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
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
/// Interface for network path analysis operations
/// </summary>
public interface INetworkPathAnalyzer
{
    void InvalidateTopologyCache();
    Task<ServerPosition?> DiscoverServerPositionAsync(string? sourceIp = null, CancellationToken cancellationToken = default);
    Task<NetworkPath> CalculatePathAsync(string targetHost, string? sourceIp = null, CancellationToken cancellationToken = default);
    PathAnalysisResult AnalyzeSpeedTest(NetworkPath path, double fromDeviceMbps, double toDeviceMbps, int fromDeviceRetransmits = 0, int toDeviceRetransmits = 0, long fromDeviceBytes = 0, long toDeviceBytes = 0);
}

/// <summary>
/// Analyzes network paths between the iperf3 server and target devices.
/// Discovers L2/L3 paths, calculates theoretical bottlenecks, and grades speed test results.
/// </summary>
public class NetworkPathAnalyzer : INetworkPathAnalyzer
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
    private static readonly TimeSpan TopologyCacheDuration = TimeSpan.FromMinutes(1);
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
        { 2500, 2390 },    // 2.5 GbE: ~2.39 Gbps practical max
        { 1000, 960 },     // 1 GbE: ~960 Mbps practical max
        { 100, 94 },       // 100 Mbps: ~94% typical
    };

    // Fallback overhead factor for unknown link speeds
    private const double FallbackOverheadFactor = 0.94;

    // WiFi overhead factor - real TCP throughput is ~60% of PHY rate due to MAC overhead, contention, etc.
    private const double WifiOverheadFactor = 0.60;

    /// <summary>
    /// Known gateway inter-VLAN routing throughput limits (Mbps).
    /// These are empirical values from real-world testing.
    /// </summary>
    private static readonly Dictionary<string, int> GatewayRoutingLimits = new(StringComparer.OrdinalIgnoreCase)
    {
        // USG series
        { "USG-3P", 850 },
        { "USG", 850 },
        { "UniFi Security Gateway", 850 },
        { "USG-Pro-4", 2400 },
        { "UniFi Security Gateway Pro", 2400 },

        // UDM series
        { "UDM", 960 },
        { "UniFi Dream Machine", 960 },
        { "UDM-Pro", 9500 },
        { "UniFi Dream Machine Pro", 9500 },
        { "UDM-SE", 9500 },
        { "UniFi Dream Machine SE", 9500 },

        // UCG series
        { "UCG-Ultra", 2400 },
        { "UniFi Cloud Gateway Ultra", 2400 },
        { "UCG-Max", 2400 },
        { "UniFi Cloud Gateway Max", 2400 },
        { "UCG-Fiber", 9800 },
        { "UniFi Cloud Gateway Fiber", 9800 },
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
    /// Invalidates the cached topology so the next call fetches fresh data from the API.
    /// </summary>
    public void InvalidateTopologyCache()
    {
        _cache.Remove(TopologyCacheKey);
        _cache.Remove(ServerPositionCacheKey);
        _cache.Remove(RawDevicesCacheKey);
        _logger.LogDebug("Topology cache invalidated");
    }

    /// <summary>
    /// Discovers the server's position in the network topology.
    /// The server is the machine running this application (the iperf3 server).
    /// </summary>
    /// <param name="sourceIp">Optional source IP (from iperf3 output). If provided, uses this directly.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<ServerPosition?> DiscoverServerPositionAsync(
        string? sourceIp = null,
        CancellationToken cancellationToken = default)
    {
        // If sourceIp is provided, don't use cache (it's specific to this test)
        // Otherwise check cache
        if (string.IsNullOrEmpty(sourceIp) && _cache.TryGetValue(ServerPositionCacheKey, out ServerPosition? cached))
        {
            return cached;
        }

        _logger.LogInformation("Discovering server position in network topology");

        // Determine which IP(s) to search for
        // Priority: HOST_IP env var > sourceIp from iperf3 > interface enumeration
        // HOST_IP takes priority because on Docker port-mapping mode (macOS), the iperf3
        // sourceIp will be the container's internal IP which isn't visible to UniFi
        List<string> localIps;
        var hostIpOverride = Environment.GetEnvironmentVariable("HOST_IP");
        if (!string.IsNullOrWhiteSpace(hostIpOverride))
        {
            // Admin has explicitly configured the server IP - use it
            localIps = new List<string> { hostIpOverride.Trim() };
            _logger.LogDebug("Using HOST_IP override: {Ip}", hostIpOverride);
        }
        else if (!string.IsNullOrEmpty(sourceIp))
        {
            // Use the specific source IP from iperf3 output - this is the actual IP used
            localIps = new List<string> { sourceIp };
            _logger.LogDebug("Using source IP from iperf3: {Ip}", sourceIp);
        }
        else
        {
            // Fall back to interface enumeration (shared utility handles HOST_IP check internally)
            localIps = NetworkUtilities.GetAllLocalIpAddresses();
            if (localIps.Count == 0)
            {
                _logger.LogWarning("Could not determine local IP addresses");
                return null;
            }
            _logger.LogDebug("Auto-detected local IP addresses: {Ips}", string.Join(", ", localIps));
        }

        // Get topology
        var topology = await GetTopologyAsync(cancellationToken);
        if (topology == null)
        {
            _logger.LogWarning("Could not retrieve network topology");
            return null;
        }

        // Find this server in the client list - search in priority order
        DiscoveredClient? serverClient = null;
        foreach (var ip in localIps)
        {
            serverClient = topology.Clients.FirstOrDefault(c =>
                c.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase));
            if (serverClient != null)
                break;
        }

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

        // Get network info (use effective network ID which considers virtual network override)
        var network = topology.Networks.FirstOrDefault(n =>
            n.Id == serverClient.EffectiveNetworkId || n.Name == serverClient.Network);

        // If network not found by ID but we have a VLAN number, try matching by VLAN
        if (network == null && serverClient.Vlan.HasValue)
        {
            network = topology.Networks.FirstOrDefault(n => n.VlanId == serverClient.Vlan.Value);
        }

        var position = new ServerPosition
        {
            IpAddress = serverClient.IpAddress,
            Mac = serverClient.Mac,
            Name = serverClient.Name,
            Hostname = serverClient.Hostname,
            SwitchMac = serverClient.ConnectedToDeviceMac,
            SwitchName = connectedSwitch?.Name,
            SwitchModel = connectedSwitch?.ModelDisplay ?? connectedSwitch?.Model,
            SwitchPort = serverClient.SwitchPort,
            NetworkId = serverClient.EffectiveNetworkId,
            NetworkName = network?.Name ?? serverClient.Network,
            VlanId = network?.VlanId,
            IsWired = serverClient.IsWired,
            DiscoveredAt = DateTime.UtcNow
        };

        _logger.LogInformation("Server position: {Ip} on {Switch} port {Port} ({Network})",
            position.IpAddress, position.SwitchName ?? "unknown", position.SwitchPort, position.NetworkName);

        // Only cache if we auto-detected (sourceIp was null) - specific IPs are per-test
        if (string.IsNullOrEmpty(sourceIp))
        {
            _cache.Set(ServerPositionCacheKey, position, ServerPositionCacheDuration);
        }

        return position;
    }

    /// <summary>
    /// Calculates the network path from the server to a target device or client.
    /// </summary>
    /// <param name="targetHost">Target hostname or IP</param>
    /// <param name="sourceIp">Optional source IP (from iperf3 output). If null, auto-detects.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<NetworkPath> CalculatePathAsync(
        string targetHost,
        string? sourceIp = null,
        CancellationToken cancellationToken = default)
    {
        var path = new NetworkPath
        {
            DestinationHost = targetHost
        };

        try
        {
            // Get server position - use provided sourceIp if available
            var serverPosition = await DiscoverServerPositionAsync(sourceIp, cancellationToken);
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

            // If not found, try DNS resolution and search by IP
            if (targetDevice == null && targetClient == null)
            {
                var resolvedIp = await ResolveHostnameAsync(targetHost);
                if (!string.IsNullOrEmpty(resolvedIp) && resolvedIp != targetHost)
                {
                    _logger.LogDebug("Resolved {Host} to {Ip}, searching topology by IP", targetHost, resolvedIp);
                    targetDevice = FindDevice(topology, resolvedIp);
                    targetClient = targetDevice == null ? FindClient(topology, resolvedIp) : null;
                }
            }

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
                // Try to find network by device IP
                var deviceNetwork = FindNetworkByIp(topology.Networks, targetDevice.IpAddress);
                if (deviceNetwork != null)
                {
                    path.DestinationVlanId = deviceNetwork.VlanId;
                    path.DestinationNetworkName = deviceNetwork.Name;
                }
            }
            else if (targetClient != null)
            {
                path.DestinationMac = targetClient.Mac;
                // Use effective network ID which considers virtual network override
                var clientNetwork = topology.Networks.FirstOrDefault(n =>
                    n.Id == targetClient.EffectiveNetworkId || n.Name == targetClient.Network);
                // If not found by ID, try matching by VLAN number
                if (clientNetwork == null && targetClient.Vlan.HasValue)
                {
                    clientNetwork = topology.Networks.FirstOrDefault(n => n.VlanId == targetClient.Vlan.Value);
                }
                path.DestinationVlanId = clientNetwork?.VlanId ?? targetClient.Vlan;
                path.DestinationNetworkName = clientNetwork?.Name ?? targetClient.Network;
            }

            // Detect inter-VLAN routing
            // Check by VLAN ID if both are set, or by network name if different,
            // or by IP subnet if source and destination are on different subnets
            bool differentVlans = path.SourceVlanId.HasValue && path.DestinationVlanId.HasValue &&
                                  path.SourceVlanId != path.DestinationVlanId;
            bool differentNetworks = !string.IsNullOrEmpty(path.SourceNetworkName) &&
                                     !string.IsNullOrEmpty(path.DestinationNetworkName) &&
                                     !path.SourceNetworkName.Equals(path.DestinationNetworkName, StringComparison.OrdinalIgnoreCase);
            bool differentSubnets = AreDifferentSubnets(path.SourceHost, path.DestinationHost);

            if (differentVlans || differentNetworks || differentSubnets)
            {
                path.RequiresRouting = true;
                var gateway = topology.Devices.FirstOrDefault(d => d.Type == DeviceType.Gateway);
                if (gateway != null)
                {
                    path.GatewayDevice = gateway.Name;
                    path.GatewayModel = gateway.ModelDisplay ?? gateway.Model;
                }

                _logger.LogInformation("Inter-VLAN routing detected: {SrcNetwork} (VLAN {SrcVlan}) -> {DstNetwork} (VLAN {DstVlan})",
                    path.SourceNetworkName, path.SourceVlanId, path.DestinationNetworkName, path.DestinationVlanId);
            }

            // Mark target device type (affects insight generation)
            path.TargetIsGateway = targetDevice?.Type == DeviceType.Gateway;
            path.TargetIsAccessPoint = targetDevice?.Type == DeviceType.AccessPoint;
            path.TargetIsCellularModem = targetDevice?.Type == DeviceType.CellularModem;

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
    /// <param name="path">The network path to the target device</param>
    /// <param name="fromDeviceMbps">Measured upload speed (device to server) in Mbps</param>
    /// <param name="toDeviceMbps">Measured download speed (server to device) in Mbps</param>
    /// <param name="fromDeviceRetransmits">TCP retransmits in upload direction (optional)</param>
    /// <param name="toDeviceRetransmits">TCP retransmits in download direction (optional)</param>
    /// <param name="fromDeviceBytes">Bytes transferred from device (optional, for retransmit %)</param>
    /// <param name="toDeviceBytes">Bytes transferred to device (optional, for retransmit %)</param>
    public PathAnalysisResult AnalyzeSpeedTest(
        NetworkPath path,
        double fromDeviceMbps,
        double toDeviceMbps,
        int fromDeviceRetransmits = 0,
        int toDeviceRetransmits = 0,
        long fromDeviceBytes = 0,
        long toDeviceBytes = 0)
    {
        var result = new PathAnalysisResult
        {
            Path = path,
            MeasuredFromDeviceMbps = fromDeviceMbps,
            MeasuredToDeviceMbps = toDeviceMbps,
            FromDeviceRetransmits = fromDeviceRetransmits,
            ToDeviceRetransmits = toDeviceRetransmits,
            FromDeviceBytes = fromDeviceBytes,
            ToDeviceBytes = toDeviceBytes
        };

        if (path.IsValid && path.RealisticMaxMbps > 0)
        {
            result.CalculateEfficiency();
            result.GenerateInsights();
        }
        else
        {
            result.Insights.Add("Path analysis unavailable - cannot grade performance");
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

    /// <summary>
    /// Resolves a hostname to an IP address via DNS.
    /// Tries bare hostname first, then with common local domain suffixes.
    /// </summary>
    private async Task<string?> ResolveHostnameAsync(string hostname)
    {
        // Skip if it's already an IP address
        if (System.Net.IPAddress.TryParse(hostname, out _))
        {
            return hostname;
        }

        // Try the hostname as-is first, then with common local domain suffixes
        var namesToTry = new List<string> { hostname };
        if (!hostname.Contains('.'))
        {
            // Add common local domain suffixes for bare hostnames
            namesToTry.Add($"{hostname}.local");
            namesToTry.Add($"{hostname}.lan");
            namesToTry.Add($"{hostname}.home");
            namesToTry.Add($"{hostname}.localdomain");
        }

        foreach (var name in namesToTry)
        {
            try
            {
                var addresses = await System.Net.Dns.GetHostAddressesAsync(name);
                var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ipv4 != null)
                {
                    _logger.LogDebug("DNS resolved {Hostname} to {Ip}", name, ipv4);
                    return ipv4.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("DNS resolution failed for {Hostname}: {Error}", name, ex.Message);
            }
        }

        _logger.LogWarning("Could not resolve hostname {Hostname} via DNS", hostname);
        return null;
    }

    private static DiscoveredDevice? FindDevice(NetworkTopology topology, string hostOrIp)
    {
        // Direct match on IP, name, or MAC
        var device = topology.Devices.FirstOrDefault(d =>
            d.IpAddress.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            d.Name.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            d.Mac.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase));

        if (device != null)
            return device;

        // Special case: Gateway devices often have their LAN gateway IPs (192.168.x.1, 10.x.x.1)
        // as DNS entries, but the UniFi API reports a different management IP.
        // If the IP looks like a gateway address, check if there's a gateway device.
        if (System.Net.IPAddress.TryParse(hostOrIp, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            // Check for common gateway patterns: x.x.x.1 (last octet = 1)
            if (bytes.Length == 4 && bytes[3] == 1)
            {
                var gateway = topology.Devices.FirstOrDefault(d => d.Type == DeviceType.Gateway);
                if (gateway != null)
                    return gateway;
            }
        }

        return null;
    }

    private static DiscoveredClient? FindClient(NetworkTopology topology, string hostOrIp)
    {
        return topology.Clients.FirstOrDefault(c =>
            c.IpAddress.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            c.Name.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            c.Hostname.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase) ||
            c.Mac.Equals(hostOrIp, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds which network contains the given IP address based on subnet.
    /// </summary>
    private static NetworkInfo? FindNetworkByIp(List<NetworkInfo> networks, string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress) || !System.Net.IPAddress.TryParse(ipAddress, out var ip))
            return null;

        foreach (var network in networks)
        {
            if (string.IsNullOrEmpty(network.IpSubnet))
                continue;

            // Parse subnet (e.g., "192.168.99.0/24" or "192.168.99.1/24")
            var parts = network.IpSubnet.Split('/');
            if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var subnetIp) ||
                !int.TryParse(parts[1], out var prefixLength))
                continue;

            if (IsInSubnet(ip, subnetIp, prefixLength))
                return network;
        }

        return null;
    }

    /// <summary>
    /// Checks if an IP address is within a subnet.
    /// </summary>
    private static bool IsInSubnet(System.Net.IPAddress ip, System.Net.IPAddress subnetIp, int prefixLength)
    {
        var ipBytes = ip.GetAddressBytes();
        var subnetBytes = subnetIp.GetAddressBytes();

        if (ipBytes.Length != subnetBytes.Length)
            return false;

        // Create mask
        int fullBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes && i < ipBytes.Length; i++)
        {
            if (ipBytes[i] != subnetBytes[i])
                return false;
        }

        if (fullBytes < ipBytes.Length && remainingBits > 0)
        {
            byte mask = (byte)(0xFF << (8 - remainingBits));
            if ((ipBytes[fullBytes] & mask) != (subnetBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if two IP addresses are on different /24 subnets.
    /// This is a fallback for detecting inter-VLAN routing when network metadata isn't available.
    /// </summary>
    private static bool AreDifferentSubnets(string ip1, string ip2)
    {
        if (!System.Net.IPAddress.TryParse(ip1, out var addr1) ||
            !System.Net.IPAddress.TryParse(ip2, out var addr2))
            return false;

        var bytes1 = addr1.GetAddressBytes();
        var bytes2 = addr2.GetAddressBytes();

        // Compare first 3 octets (assumes /24 networks, which is typical for home/SMB)
        if (bytes1.Length != 4 || bytes2.Length != 4)
            return false;

        return bytes1[0] != bytes2[0] || bytes1[1] != bytes2[1] || bytes1[2] != bytes2[2];
    }

    internal void BuildHopList(
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
            var deviceModel = UniFiProductDatabase.GetBestProductName(targetDevice.Model, targetDevice.Shortname, targetDevice.ModelDisplay);
            _logger.LogDebug("Target device model resolution: Model={Model}, Shortname={Shortname}, ModelDisplay={ModelDisplay} => DeviceModel={DeviceModel}",
                targetDevice.Model, targetDevice.Shortname, targetDevice.ModelDisplay, deviceModel);
            var deviceHop = new NetworkHop
            {
                Order = 0,
                Type = GetHopType(targetDevice.Type),
                DeviceMac = targetDevice.Mac,
                DeviceName = targetDevice.Name,
                DeviceModel = deviceModel,
                DeviceFirmware = targetDevice.Firmware,
                DeviceIp = targetDevice.IpAddress,
                IngressPort = targetDevice.UplinkPort,
                EgressPort = targetDevice.UplinkPort,
                Notes = "Target device"
            };

            // Get uplink speed - use device's uplink speed for wireless mesh, otherwise port speed
            if (targetDevice.UplinkType?.Equals("wireless", StringComparison.OrdinalIgnoreCase) == true
                && targetDevice.UplinkSpeedMbps > 0)
            {
                // Wireless mesh uplink - use the reported uplink speed
                deviceHop.IngressSpeedMbps = targetDevice.UplinkSpeedMbps;
                deviceHop.EgressSpeedMbps = targetDevice.UplinkSpeedMbps;
                deviceHop.IngressPortName = "wireless mesh";
                deviceHop.EgressPortName = "wireless mesh";
                deviceHop.IsWirelessIngress = true;
                deviceHop.IsWirelessEgress = true;
                deviceHop.WirelessIngressBand = targetDevice.UplinkRadioBand;
                deviceHop.WirelessEgressBand = targetDevice.UplinkRadioBand;
                deviceHop.WirelessChannel = targetDevice.UplinkChannel;
                deviceHop.WirelessSignalDbm = targetDevice.UplinkSignalDbm;
                deviceHop.WirelessNoiseDbm = targetDevice.UplinkNoiseDbm;
                deviceHop.WirelessTxRateMbps = targetDevice.UplinkTxRateKbps > 0
                    ? (int)(targetDevice.UplinkTxRateKbps / 1000)
                    : null;
                deviceHop.WirelessRxRateMbps = targetDevice.UplinkRxRateKbps > 0
                    ? (int)(targetDevice.UplinkRxRateKbps / 1000)
                    : null;
                _logger.LogDebug("Wireless mesh device {Name}: UplinkType={UplinkType}, TxRate={Tx}Mbps, RxRate={Rx}Mbps, Band={Band}, Ch={Ch}, Signal={Sig}dBm",
                    targetDevice.Name, targetDevice.UplinkType,
                    deviceHop.WirelessTxRateMbps, deviceHop.WirelessRxRateMbps,
                    targetDevice.UplinkRadioBand ?? "null", targetDevice.UplinkChannel, targetDevice.UplinkSignalDbm);
            }
            else if (!string.IsNullOrEmpty(currentMac) && currentPort.HasValue)
            {
                // Wired uplink - get port speed from upstream switch
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

            // Warn if wireless client has no AP MAC - indicates stale data from UniFi API
            if (!targetClient.IsWired && string.IsNullOrEmpty(currentMac))
            {
                _logger.LogWarning("Wireless client {Name} ({Ip}) has no AP MAC - UniFi API data may be stale. Path will be incomplete.",
                    targetClient.Name ?? targetClient.Hostname, targetClient.IpAddress);
                path.IsValid = false;
                path.ErrorMessage = "Wireless client connection data not yet available from UniFi";
                return; // Don't build incomplete path - caller should retry
            }

            var hop = new NetworkHop
            {
                Order = 0,
                Type = targetClient.IsWired ? HopType.Client : HopType.WirelessClient,
                DeviceMac = targetClient.Mac,
                DeviceName = targetClient.Name ?? targetClient.Hostname,
                DeviceIp = targetClient.IpAddress,
                Notes = targetClient.IsWired ? "Target client (wired)" : $"Target client ({targetClient.ConnectionType})"
            };

            if (!targetClient.IsWired)
            {
                int txMbps, rxMbps;

                // For MLO clients, sum speeds from all links
                if (targetClient.IsMlo && targetClient.MloLinks?.Count > 0)
                {
                    txMbps = (int)(targetClient.MloLinks.Sum(l => l.TxRateKbps ?? 0) / 1000);
                    rxMbps = (int)(targetClient.MloLinks.Sum(l => l.RxRateKbps ?? 0) / 1000);
                    _logger.LogDebug("MLO client {Name}: Summed TxRate={Tx}Mbps, RxRate={Rx}Mbps from {Links} links",
                        targetClient.Name ?? targetClient.IpAddress, txMbps, rxMbps, targetClient.MloLinks.Count);
                }
                else
                {
                    // Single-link wireless - use reported rates
                    txMbps = (int)(targetClient.TxRate / 1000);
                    rxMbps = (int)(targetClient.RxRate / 1000);
                }

                var maxRate = Math.Max(txMbps, rxMbps);
                hop.EgressSpeedMbps = maxRate;
                hop.IngressSpeedMbps = maxRate;
                hop.IsWirelessEgress = true;
                hop.IsWirelessIngress = true;
                hop.WirelessEgressBand = targetClient.Radio;
                hop.WirelessIngressBand = targetClient.Radio;
                _logger.LogDebug("Wireless client {Name}: TxRate={Tx}Mbps, RxRate={Rx}Mbps, Radio={Radio}, MLO={IsMlo}",
                    targetClient.Name ?? targetClient.IpAddress, txMbps, rxMbps, targetClient.Radio ?? "null", targetClient.IsMlo);
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

        // Build server's uplink chain (for finding path from gateway to server)
        var serverChain = new List<(DiscoveredDevice device, int? port)>();
        if (!string.IsNullOrEmpty(serverPosition.SwitchMac))
        {
            string? chainMac = serverPosition.SwitchMac;
            int? chainPort = serverPosition.SwitchPort;
            int chainHops = 0;

            while (!string.IsNullOrEmpty(chainMac) && chainHops < 10)
            {
                if (deviceDict.TryGetValue(chainMac, out var chainDevice))
                {
                    serverChain.Add((chainDevice, chainPort));
                    chainMac = chainDevice.UplinkMac;
                    chainPort = chainDevice.UplinkPort;
                    chainHops++;
                }
                else
                {
                    break;
                }
            }
        }

        // Special case: target IS the gateway - add server chain directly
        bool targetIsGateway = targetDevice?.Type == DeviceType.Gateway;
        if (targetIsGateway)
        {
            // Gateway is the target, add path from gateway to server
            int hopOrder = 1;
            if (serverChain.Count > 0)
            {
                for (int i = serverChain.Count - 1; i >= 0; i--)
                {
                    var (chainDevice, chainPort) = serverChain[i];

                    // Skip if it's the gateway (already added as target)
                    if (chainDevice.Type == DeviceType.Gateway)
                        continue;

                    var hop = new NetworkHop
                    {
                        Order = hopOrder++,
                        Type = GetHopType(chainDevice.Type),
                        DeviceMac = chainDevice.Mac,
                        DeviceName = chainDevice.Name,
                        DeviceModel = UniFiProductDatabase.GetBestProductName(chainDevice.Model, chainDevice.Shortname, chainDevice.ModelDisplay),
                        DeviceFirmware = chainDevice.Firmware,
                        DeviceIp = chainDevice.IpAddress,
                        IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, chainPort),
                        IngressPort = chainPort,
                        IngressPortName = GetPortName(rawDevices, chainDevice.Mac, chainPort),
                        Notes = "Path from gateway"
                    };

                    // Set egress to server's port if this is server's switch
                    if (chainDevice.Mac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase))
                    {
                        hop.EgressPort = serverPosition.SwitchPort;
                        hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, serverPosition.SwitchPort);
                        hop.EgressPortName = GetPortName(rawDevices, chainDevice.Mac, serverPosition.SwitchPort);
                    }

                    hops.Add(hop);
                }
            }
        }
        // Check if both server and target are on the same switch AND same VLAN
        // Inter-VLAN traffic must go through gateway even if on same physical switch
        else if (!string.IsNullOrEmpty(currentMac) &&
                 currentMac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase) &&
                 !path.RequiresRouting)
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
                    DeviceModel = UniFiProductDatabase.GetBestProductName(switchDevice.Model, switchDevice.Shortname, switchDevice.ModelDisplay),
                    DeviceFirmware = switchDevice.Firmware,
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
            // Trace uplinks from target
            int hopOrder = 1;
            int maxHops = 10;
            bool reachedGateway = false;
            int commonAncestorIndex = -1; // Index in serverChain where we found the common ancestor

            // Build a set of server chain MACs for O(1) lookup
            var serverChainMacs = new HashSet<string>(
                serverChain.Select(s => s.device.Mac),
                StringComparer.OrdinalIgnoreCase);

            while (!string.IsNullOrEmpty(currentMac) && hopOrder < maxHops)
            {
                if (!deviceDict.TryGetValue(currentMac, out var device))
                    break;

                // Check if we've reached the server's switch or gateway
                bool isServerSwitch = currentMac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase);
                bool isGateway = device.Type == DeviceType.Gateway;

                // Check if this device is anywhere in the server's uplink chain (common ancestor)
                // This handles the daisy-chain scenario where server is downstream from client's switch
                bool isInServerChain = serverChainMacs.Contains(currentMac);
                if (isInServerChain && !isServerSwitch)
                {
                    // Find the index in the server chain
                    commonAncestorIndex = serverChain.FindIndex(s =>
                        s.device.Mac.Equals(currentMac, StringComparison.OrdinalIgnoreCase));
                }

                // For inter-VLAN routing: don't stop at server's switch or common ancestor, continue to gateway
                // Traffic must go to gateway for L3 routing even if it passes through server's switch
                bool stopAtServerSwitch = isServerSwitch && !path.RequiresRouting;
                bool stopAtCommonAncestor = commonAncestorIndex >= 0 && !path.RequiresRouting;

                // Check if this device has a wireless uplink (for egress, not ingress)
                bool isWirelessUplink = device.UplinkType?.Equals("wireless", StringComparison.OrdinalIgnoreCase) == true
                    && device.UplinkSpeedMbps > 0;

                // Ingress speed comes from the port/connection from the PREVIOUS hop, not this device's uplink
                // For APs after a wireless client, ingress is the client's wireless connection (handled by client hop's egress)
                int ingressSpeed = GetPortSpeedFromRawDevices(rawDevices, currentMac, currentPort);
                string? ingressPortName = GetPortName(rawDevices, currentMac, currentPort);

                var hop = new NetworkHop
                {
                    Order = hopOrder,
                    Type = GetHopType(device.Type),
                    DeviceMac = device.Mac,
                    DeviceName = device.Name,
                    DeviceModel = UniFiProductDatabase.GetBestProductName(device.Model, device.Shortname, device.ModelDisplay),
                    DeviceFirmware = device.Firmware,
                    DeviceIp = device.IpAddress,
                    IngressPort = currentPort,
                    IngressPortName = ingressPortName,
                    IngressSpeedMbps = ingressSpeed
                    // Note: IsWirelessIngress is set by the PREVIOUS hop's egress, not this device's uplink
                };

                if (stopAtServerSwitch)
                {
                    // Same VLAN: traffic exits to server from this switch
                    hop.EgressPort = serverPosition.SwitchPort;
                    hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, currentMac, serverPosition.SwitchPort);
                    hop.EgressPortName = GetPortName(rawDevices, currentMac, serverPosition.SwitchPort);
                }
                else if (stopAtCommonAncestor)
                {
                    // Common ancestor in daisy-chain: traffic goes down to server's switch
                    // Find the next device in the server chain (which uplinks to this device)
                    if (commonAncestorIndex > 0)
                    {
                        var nextInChain = serverChain[commonAncestorIndex - 1];
                        hop.EgressPort = nextInChain.device.UplinkPort;
                        hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, currentMac, nextInChain.device.UplinkPort);
                        hop.EgressPortName = GetPortName(rawDevices, currentMac, nextInChain.device.UplinkPort);
                    }
                }
                else if (!string.IsNullOrEmpty(device.UplinkMac))
                {
                    // Continue up the chain - check if THIS device has a wireless uplink to its uplink device
                    if (isWirelessUplink)
                    {
                        // This device connects to its uplink via wireless mesh
                        hop.EgressPort = device.UplinkPort;
                        hop.EgressSpeedMbps = device.UplinkSpeedMbps;
                        hop.EgressPortName = "wireless mesh";
                        hop.IsWirelessEgress = true;
                        hop.WirelessEgressBand = device.UplinkRadioBand;
                        // Signal stats come from the device with the wireless uplink
                        hop.WirelessChannel = device.UplinkChannel;
                        hop.WirelessSignalDbm = device.UplinkSignalDbm;
                        hop.WirelessNoiseDbm = device.UplinkNoiseDbm;
                        hop.WirelessTxRateMbps = device.UplinkTxRateKbps > 0
                            ? (int)(device.UplinkTxRateKbps / 1000)
                            : null;
                        hop.WirelessRxRateMbps = device.UplinkRxRateKbps > 0
                            ? (int)(device.UplinkRxRateKbps / 1000)
                            : null;
                    }
                    else
                    {
                        hop.EgressPort = device.UplinkPort;
                        hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, device.UplinkMac, device.UplinkPort);
                        hop.EgressPortName = GetPortName(rawDevices, device.UplinkMac, device.UplinkPort);
                    }
                }

                hops.Add(hop);

                // Debug: log hop wireless info
                if (hop.IsWirelessIngress || hop.IsWirelessEgress)
                {
                    _logger.LogDebug("Hop {Name}: IsWirelessIngress={WI}, IngressBand={IB}, IsWirelessEgress={WE}, EgressBand={EB}",
                        hop.DeviceName, hop.IsWirelessIngress, hop.WirelessIngressBand ?? "null",
                        hop.IsWirelessEgress, hop.WirelessEgressBand ?? "null");
                }

                if (stopAtServerSwitch)
                    break;

                // Stop at common ancestor for L2 traffic (same VLAN, daisy-chain topology)
                if (stopAtCommonAncestor)
                    break;

                if (isGateway)
                {
                    reachedGateway = true;
                    // Add known gateway routing limits
                    if (path.RequiresRouting)
                    {
                        hop.Notes = "L3 routing (inter-VLAN)";
                        if (GatewayRoutingLimits.TryGetValue(device.ModelDisplay ?? "", out int limit) ||
                            GatewayRoutingLimits.TryGetValue(device.Model ?? "", out limit))
                        {
                            hop.IngressSpeedMbps = limit;
                            hop.EgressSpeedMbps = limit;
                            hop.Notes = $"L3 routing (inter-VLAN) - {limit / 1000.0:F1} Gbps capacity";
                        }
                    }
                    break;
                }

                // Move to next hop
                currentMac = device.UplinkMac;
                currentPort = device.UplinkPort;
                hopOrder++;
            }

            // For L2 daisy-chain: after stopping at common ancestor, add path down to server's switch
            if (commonAncestorIndex >= 0 && !path.RequiresRouting && serverChain.Count > 0)
            {
                // Add server chain from common ancestor down to server's switch
                // Start from commonAncestorIndex - 1 (common ancestor already added) down to 0
                for (int i = commonAncestorIndex - 1; i >= 0; i--)
                {
                    var (chainDevice, chainPort) = serverChain[i];
                    hopOrder++;

                    var hop = new NetworkHop
                    {
                        Order = hopOrder,
                        Type = GetHopType(chainDevice.Type),
                        DeviceMac = chainDevice.Mac,
                        DeviceName = chainDevice.Name,
                        DeviceModel = UniFiProductDatabase.GetBestProductName(chainDevice.Model, chainDevice.Shortname, chainDevice.ModelDisplay),
                        DeviceFirmware = chainDevice.Firmware,
                        DeviceIp = chainDevice.IpAddress,
                        IngressPort = chainPort,
                        IngressPortName = GetPortName(rawDevices, chainDevice.Mac, chainPort),
                        IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, chainPort)
                    };

                    // Set egress based on position in chain
                    if (chainDevice.Mac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase))
                    {
                        // This is server's switch - egress to server
                        hop.EgressPort = serverPosition.SwitchPort;
                        hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, serverPosition.SwitchPort);
                        hop.EgressPortName = GetPortName(rawDevices, chainDevice.Mac, serverPosition.SwitchPort);
                    }
                    else if (i > 0)
                    {
                        // There's another switch below - egress to next in chain
                        var nextInChain = serverChain[i - 1];
                        hop.EgressPort = nextInChain.device.UplinkPort;
                        hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, nextInChain.device.UplinkPort);
                        hop.EgressPortName = GetPortName(rawDevices, chainDevice.Mac, nextInChain.device.UplinkPort);
                    }

                    hops.Add(hop);
                }
            }

            // For inter-VLAN: after reaching gateway, add path from gateway to server
            if (reachedGateway && path.RequiresRouting && serverChain.Count > 0)
            {
                // Add server chain in reverse (from gateway down to server's switch)
                // Note: We DON'T skip devices that appear in target path (except gateway)
                // because traffic actually traverses them twice in inter-VLAN routing
                for (int i = serverChain.Count - 1; i >= 0; i--)
                {
                    var (chainDevice, chainPort) = serverChain[i];

                    // Only skip the gateway (already added)
                    if (chainDevice.Type == DeviceType.Gateway)
                        continue;

                    hopOrder++;

                    var hop = new NetworkHop
                    {
                        Order = hopOrder,
                        Type = GetHopType(chainDevice.Type),
                        DeviceMac = chainDevice.Mac,
                        DeviceName = chainDevice.Name,
                        DeviceModel = UniFiProductDatabase.GetBestProductName(chainDevice.Model, chainDevice.Shortname, chainDevice.ModelDisplay),
                        DeviceFirmware = chainDevice.Firmware,
                        DeviceIp = chainDevice.IpAddress,
                        IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, chainPort),
                        IngressPort = chainPort,
                        IngressPortName = GetPortName(rawDevices, chainDevice.Mac, chainPort),
                        Notes = "Return path from gateway"
                    };

                    // Set egress to server's port if this is server's switch
                    if (chainDevice.Mac.Equals(serverPosition.SwitchMac, StringComparison.OrdinalIgnoreCase))
                    {
                        hop.EgressPort = serverPosition.SwitchPort;
                        hop.EgressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, chainDevice.Mac, serverPosition.SwitchPort);
                        hop.EgressPortName = GetPortName(rawDevices, chainDevice.Mac, serverPosition.SwitchPort);
                    }

                    hops.Add(hop);
                }
            }
        }

        // Add server as final endpoint
        // Use name from UniFi, fall back to hostname, then "This Server"
        var serverName = !string.IsNullOrEmpty(serverPosition.Name) ? serverPosition.Name
                       : !string.IsNullOrEmpty(serverPosition.Hostname) ? serverPosition.Hostname
                       : "This Server";
        var serverHop = new NetworkHop
        {
            Order = hops.Count,
            Type = HopType.Server,
            DeviceMac = serverPosition.Mac,
            DeviceName = serverName,
            DeviceIp = serverPosition.IpAddress,
            IngressPort = serverPosition.SwitchPort,
            IngressPortName = GetPortName(rawDevices, serverPosition.SwitchMac, serverPosition.SwitchPort),
            IngressSpeedMbps = GetPortSpeedFromRawDevices(rawDevices, serverPosition.SwitchMac, serverPosition.SwitchPort),
            Notes = "Speed test server"
        };
        hops.Add(serverHop);

        // Check if we need to prepend a VPN hop (Teleport or Tailscale)
        var vpnHop = DetectAndCreateVpnHop(path.DestinationHost, topology, rawDevices);
        if (vpnHop != null)
        {
            // VPN hop becomes first (order -1 sorts before 0)
            vpnHop.Order = -1;
            hops.Add(vpnHop);
            _logger.LogDebug("Prepended {VpnType} hop for client {ClientIp}",
                vpnHop.Type, path.DestinationHost);
        }

        // Sort hops by order
        path.Hops = hops.OrderBy(h => h.Order).ToList();
    }

    /// <summary>
    /// Detects if the client IP is coming through a VPN (Teleport or Tailscale)
    /// and creates an appropriate hop to prepend to the path.
    /// </summary>
    private NetworkHop? DetectAndCreateVpnHop(
        string clientIp,
        NetworkTopology topology,
        Dictionary<string, UniFiDeviceResponse> rawDevices)
    {
        if (string.IsNullOrEmpty(clientIp) || !System.Net.IPAddress.TryParse(clientIp, out _))
            return null;

        // Get WAN speeds for VPN bottleneck calculation
        // VPN traffic traverses the WAN, so WAN speed is the limiting factor
        var (wanDownloadMbps, wanUploadMbps) = GetWanSpeed(topology, rawDevices);

        // Check for Tailscale CGNAT range: 100.64.0.0/10 (100.64.x.x - 100.127.x.x)
        if (clientIp.StartsWith("100."))
        {
            var parts = clientIp.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int secondOctet))
            {
                if (secondOctet >= 64 && secondOctet <= 127)
                {
                    return new NetworkHop
                    {
                        Type = HopType.Tailscale,
                        DeviceName = "Tailscale",
                        DeviceIp = clientIp,
                        // WAN upload = server can send to client (client download)
                        // WAN download = server can receive from client (client upload)
                        IngressSpeedMbps = wanDownloadMbps,
                        EgressSpeedMbps = wanUploadMbps,
                        Notes = wanUploadMbps > 0
                            ? $"Tailscale VPN (WAN: {wanDownloadMbps}/{wanUploadMbps} Mbps)"
                            : "Tailscale VPN mesh"
                    };
                }
            }
        }

        // Check for Teleport: 192.168.x.x that's NOT in any known UniFi network
        if (clientIp.StartsWith("192.168."))
        {
            // Check if this IP is in any known UniFi network
            var isInKnownNetwork = topology.Networks.Any(n =>
                !string.IsNullOrEmpty(n.IpSubnet) && IsIpInSubnetCidr(clientIp, n.IpSubnet));

            if (!isInKnownNetwork)
            {
                return new NetworkHop
                {
                    Type = HopType.Teleport,
                    DeviceName = "Teleport",
                    DeviceIp = clientIp,
                    // WAN upload = server can send to client (client download)
                    // WAN download = server can receive from client (client upload)
                    IngressSpeedMbps = wanDownloadMbps,
                    EgressSpeedMbps = wanUploadMbps,
                    Notes = wanUploadMbps > 0
                        ? $"Teleport VPN (WAN: {wanDownloadMbps}/{wanUploadMbps} Mbps)"
                        : "Teleport VPN gateway"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Gets WAN speed from provider capabilities, or falls back to WAN interface link speed.
    /// </summary>
    private (int downloadMbps, int uploadMbps) GetWanSpeed(
        NetworkTopology topology,
        Dictionary<string, UniFiDeviceResponse> rawDevices)
    {
        // First try: WAN provider capabilities (ISP speed configuration)
        var primaryWan = topology.Networks.FirstOrDefault(n => n.IsWan && n.WanDownloadMbps > 0);
        if (primaryWan != null && primaryWan.WanDownloadMbps > 0)
        {
            return (primaryWan.WanDownloadMbps ?? 0, primaryWan.WanUploadMbps ?? 0);
        }

        // Fallback: WAN interface link speed from gateway's port table
        var gateway = topology.Devices.FirstOrDefault(d => d.Type == DeviceType.Gateway);
        if (gateway != null && rawDevices.TryGetValue(gateway.Mac, out var gatewayDevice))
        {
            // Find WAN port(s) - look for is_uplink flag or port name containing "WAN"
            var wanPort = gatewayDevice.PortTable?
                .Where(p => p.IsUplink || p.Name.Contains("WAN", StringComparison.OrdinalIgnoreCase))
                .Where(p => p.Up && p.Speed > 0)
                .OrderByDescending(p => p.Speed)
                .FirstOrDefault();

            if (wanPort != null)
            {
                // Link speed is symmetric for interface rate
                return (wanPort.Speed, wanPort.Speed);
            }
        }

        return (0, 0);
    }

    /// <summary>
    /// Checks if an IP address is within a CIDR subnet (e.g., "192.168.1.0/24").
    /// </summary>
    private static bool IsIpInSubnetCidr(string ipAddress, string cidrSubnet)
    {
        if (!System.Net.IPAddress.TryParse(ipAddress, out var ip))
            return false;

        var parts = cidrSubnet.Split('/');
        if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var subnetIp) ||
            !int.TryParse(parts[1], out var prefixLength))
            return false;

        return IsInSubnet(ip, subnetIp, prefixLength);
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
    private static int GetRealisticMax(int theoreticalMbps, bool isWireless = false)
    {
        // WiFi has much higher overhead than wired (~60% efficiency vs ~94%)
        if (isWireless)
        {
            return (int)(theoreticalMbps * WifiOverheadFactor);
        }

        if (RealisticMaxByLinkSpeed.TryGetValue(theoreticalMbps, out int realistic))
        {
            return realistic;
        }

        // Fallback: use 94% for unknown wired speeds
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

        // Collect all link speeds in the path
        var allSpeeds = new List<int>();
        int minSpeed = int.MaxValue;
        int maxSpeed = 0;
        NetworkHop? bottleneckHop = null;
        string? bottleneckPort = null;
        bool isBottleneckWireless = false;

        foreach (var hop in path.Hops)
        {
            // Check ingress
            if (hop.IngressSpeedMbps > 0)
            {
                allSpeeds.Add(hop.IngressSpeedMbps);
                if (hop.IngressSpeedMbps > maxSpeed) maxSpeed = hop.IngressSpeedMbps;
                if (hop.IngressSpeedMbps < minSpeed)
                {
                    minSpeed = hop.IngressSpeedMbps;
                    bottleneckHop = hop;
                    bottleneckPort = GetPortDescription(hop.IngressPortName, hop.IngressPort, hop.IsWirelessIngress);
                    isBottleneckWireless = hop.IsWirelessIngress;
                }
            }

            // Check egress
            if (hop.EgressSpeedMbps > 0)
            {
                allSpeeds.Add(hop.EgressSpeedMbps);
                if (hop.EgressSpeedMbps > maxSpeed) maxSpeed = hop.EgressSpeedMbps;
                if (hop.EgressSpeedMbps < minSpeed)
                {
                    minSpeed = hop.EgressSpeedMbps;
                    bottleneckHop = hop;
                    bottleneckPort = GetPortDescription(hop.EgressPortName, hop.EgressPort, hop.IsWirelessEgress);
                    isBottleneckWireless = hop.IsWirelessEgress;
                }
            }
        }

        if (minSpeed == int.MaxValue)
        {
            // No speed data available - assume 1 Gbps
            minSpeed = 1000;
        }

        path.TheoreticalMaxMbps = minSpeed;
        path.RealisticMaxMbps = GetRealisticMax(minSpeed, isBottleneckWireless);

        // Only mark as bottleneck if there's actually a slower link than others
        path.HasRealBottleneck = allSpeeds.Count > 0 && minSpeed < maxSpeed;

        if (bottleneckHop != null)
        {
            bottleneckHop.IsBottleneck = path.HasRealBottleneck;

            // Only set description if there's a real bottleneck
            if (path.HasRealBottleneck)
            {
                if (minSpeed < 1000)
                {
                    path.BottleneckDescription = $"{minSpeed} Mbps link at {bottleneckHop.DeviceName} ({bottleneckPort})";
                }
                else
                {
                    var gbps = minSpeed / 1000.0;
                    var gbpsStr = gbps % 1 == 0 ? $"{(int)gbps}" : $"{gbps:F1}";
                    path.BottleneckDescription = $"{gbpsStr} Gbps link at {bottleneckHop.DeviceName} ({bottleneckPort})";
                }
            }
        }
    }

    /// <summary>
    /// Get a human-readable description for a port/link
    /// </summary>
    private static string GetPortDescription(string? portName, int? portNumber, bool isWireless)
    {
        // If we have a port name (e.g., "wireless mesh", "WAN"), use it
        if (!string.IsNullOrEmpty(portName))
            return portName;

        // For wireless links without a port name, just say "wireless"
        if (isWireless)
            return "wireless";

        // For wired links with a port number
        if (portNumber.HasValue)
            return $"port {portNumber}";

        // Fallback
        return "unknown";
    }
}

/// <summary>
/// Represents this server's position in the network.
/// </summary>
public class ServerPosition
{
    public string IpAddress { get; set; } = "";
    public string Mac { get; set; } = "";
    public string? Name { get; set; }
    public string? Hostname { get; set; }
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
