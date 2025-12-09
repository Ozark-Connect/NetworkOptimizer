using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.UniFi.Examples;

/// <summary>
/// Example usage of UniFiApiClient and UniFiDiscovery
/// Demonstrates common operations and patterns
/// </summary>
public class UniFiClientExample
{
    public static async Task RunExamplesAsync()
    {
        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole()
                   .SetMinimumLevel(LogLevel.Information);
        });

        var apiLogger = loggerFactory.CreateLogger<UniFiApiClient>();
        var discoveryLogger = loggerFactory.CreateLogger<UniFiDiscovery>();

        // Create API client
        // NOTE: In production, use secure credential storage (Windows Credential Manager, Azure Key Vault, etc.)
        var apiClient = new UniFiApiClient(
            logger: apiLogger,
            controllerHost: "192.168.1.1",  // Your UniFi controller IP or hostname
            username: "admin",
            password: "your-password-here",
            site: "default"
        );

        try
        {
            // Example 1: Basic authentication
            await Example1_BasicAuthenticationAsync(apiClient);

            // Example 2: Device discovery
            await Example2_DeviceDiscoveryAsync(apiClient, discoveryLogger);

            // Example 3: Client discovery
            await Example3_ClientDiscoveryAsync(apiClient, discoveryLogger);

            // Example 4: Network topology
            await Example4_NetworkTopologyAsync(apiClient, discoveryLogger);

            // Example 5: Controller information
            await Example5_ControllerInformationAsync(apiClient, discoveryLogger);

            // Example 6: Firewall configuration
            await Example6_FirewallConfigurationAsync(apiClient, discoveryLogger);

            // Example 7: Network management
            await Example7_NetworkManagementAsync(apiClient);

            // Example 8: Performance monitoring
            await Example8_PerformanceMonitoringAsync(apiClient);
        }
        finally
        {
            // Always logout when done (optional, but good practice)
            await apiClient.LogoutAsync();
            apiClient.Dispose();
        }
    }

    private static async Task Example1_BasicAuthenticationAsync(UniFiApiClient apiClient)
    {
        Console.WriteLine("\n=== Example 1: Basic Authentication ===");

        // Login is automatic on first API call, but can be explicit
        var success = await apiClient.LoginAsync();

        if (success)
        {
            Console.WriteLine("✓ Successfully authenticated with UniFi controller");
        }
        else
        {
            Console.WriteLine("✗ Authentication failed");
            throw new Exception("Cannot continue without authentication");
        }
    }

    private static async Task Example2_DeviceDiscoveryAsync(
        UniFiApiClient apiClient,
        ILogger<UniFiDiscovery> logger)
    {
        Console.WriteLine("\n=== Example 2: Device Discovery ===");

        var discovery = new UniFiDiscovery(apiClient, logger);
        var devices = await discovery.DiscoverDevicesAsync();

        Console.WriteLine($"Discovered {devices.Count} UniFi devices:");

        foreach (var device in devices)
        {
            Console.WriteLine($"\n{device.Name} ({device.ModelDisplay})");
            Console.WriteLine($"  MAC: {device.Mac}");
            Console.WriteLine($"  IP: {device.IpAddress}");
            Console.WriteLine($"  Type: {device.Type}");
            Console.WriteLine($"  Firmware: {device.Firmware}");
            Console.WriteLine($"  State: {device.State} (Adopted: {device.Adopted})");
            Console.WriteLine($"  Uptime: {device.Uptime.TotalHours:F1} hours");

            if (device.Upgradable)
            {
                Console.WriteLine($"  ⚠ Upgrade available: {device.UpgradeToFirmware}");
            }

            if (!string.IsNullOrEmpty(device.CpuUsage))
            {
                Console.WriteLine($"  CPU: {device.CpuUsage}%, Memory: {device.MemoryUsage}%");
            }

            if (!string.IsNullOrEmpty(device.UplinkMac))
            {
                Console.WriteLine($"  Uplink: {device.UplinkDeviceName ?? device.UplinkMac} " +
                                  $"(Port {device.UplinkPort}, Connected: {device.IsUplinkConnected})");
            }

            var txMB = device.TxBytes / 1024.0 / 1024.0;
            var rxMB = device.RxBytes / 1024.0 / 1024.0;
            Console.WriteLine($"  Traffic: TX {txMB:F2} MB, RX {rxMB:F2} MB");
        }
    }

    private static async Task Example3_ClientDiscoveryAsync(
        UniFiApiClient apiClient,
        ILogger<UniFiDiscovery> logger)
    {
        Console.WriteLine("\n=== Example 3: Client Discovery ===");

        var discovery = new UniFiDiscovery(apiClient, logger);
        var clients = await discovery.DiscoverClientsAsync();

        Console.WriteLine($"Discovered {clients.Count} connected clients:");

        var wiredClients = clients.Where(c => c.IsWired).ToList();
        var wirelessClients = clients.Where(c => !c.IsWired).ToList();

        Console.WriteLine($"  Wired: {wiredClients.Count}");
        Console.WriteLine($"  Wireless: {wirelessClients.Count}");

        // Show top 5 wireless clients by signal strength
        Console.WriteLine("\nTop 5 Wireless Clients by Signal Strength:");
        var topWireless = wirelessClients
            .Where(c => c.SignalStrength.HasValue)
            .OrderByDescending(c => c.SignalStrength)
            .Take(5);

        foreach (var client in topWireless)
        {
            Console.WriteLine($"  {client.Hostname ?? client.Name ?? client.Mac}");
            Console.WriteLine($"    SSID: {client.Essid}, Signal: {client.SignalStrength} dBm");
            Console.WriteLine($"    Protocol: {client.RadioProtocol}, Channel: {client.Channel}");
            Console.WriteLine($"    IP: {client.IpAddress}, Network: {client.Network}");
        }

        // Show top 5 clients by traffic
        Console.WriteLine("\nTop 5 Clients by Total Traffic:");
        var topTraffic = clients
            .OrderByDescending(c => c.TxBytes + c.RxBytes)
            .Take(5);

        foreach (var client in topTraffic)
        {
            var totalGB = (client.TxBytes + client.RxBytes) / 1024.0 / 1024.0 / 1024.0;
            var connectionType = client.IsWired ? "Wired" : "Wireless";

            Console.WriteLine($"  {client.Hostname ?? client.Name ?? client.Mac}");
            Console.WriteLine($"    Total: {totalGB:F2} GB, Type: {connectionType}");
            Console.WriteLine($"    Uptime: {client.Uptime.TotalHours:F1} hours");
        }
    }

    private static async Task Example4_NetworkTopologyAsync(
        UniFiApiClient apiClient,
        ILogger<UniFiDiscovery> logger)
    {
        Console.WriteLine("\n=== Example 4: Network Topology ===");

        var discovery = new UniFiDiscovery(apiClient, logger);
        var topology = await discovery.DiscoverTopologyAsync();

        Console.WriteLine($"Network Topology (discovered at {topology.DiscoveredAt})");
        Console.WriteLine($"  Devices: {topology.Devices.Count}");
        Console.WriteLine($"  Clients: {topology.Clients.Count}");
        Console.WriteLine($"  Networks: {topology.Networks.Count}");

        // Show device hierarchy
        Console.WriteLine("\nDevice Hierarchy:");
        var gateways = topology.Devices.Where(d => d.Type == DeviceType.Gateway);

        foreach (var gateway in gateways)
        {
            Console.WriteLine($"\n{gateway.Name} (Gateway) - {gateway.IpAddress}");

            if (gateway.DownstreamDevices != null && gateway.DownstreamDevices.Any())
            {
                foreach (var downstreamName in gateway.DownstreamDevices)
                {
                    var downstream = topology.Devices.FirstOrDefault(d => d.Name == downstreamName);
                    if (downstream != null)
                    {
                        Console.WriteLine($"  └─ {downstream.Name} ({downstream.Type}) - {downstream.IpAddress}");

                        // Show clients connected to this device
                        var deviceClients = topology.Clients
                            .Where(c => c.ConnectedToDeviceMac == downstream.Mac)
                            .ToList();

                        if (deviceClients.Any())
                        {
                            Console.WriteLine($"     {deviceClients.Count} client(s) connected");
                        }
                    }
                }
            }
        }

        // Show network configurations
        Console.WriteLine("\nNetwork Configurations:");
        foreach (var network in topology.Networks.Where(n => n.Enabled))
        {
            Console.WriteLine($"\n{network.Name} ({network.Purpose})");
            Console.WriteLine($"  VLAN: {network.VlanId?.ToString() ?? "none"}");
            Console.WriteLine($"  Subnet: {network.IpSubnet ?? "N/A"}");
            Console.WriteLine($"  Gateway: {network.Gateway ?? "N/A"}");

            if (network.IsDhcpEnabled)
            {
                Console.WriteLine($"  DHCP: {network.DhcpRange}");
            }

            Console.WriteLine($"  NAT: {network.IsNat}");

            // Count clients on this network
            var networkClients = topology.Clients.Where(c => c.NetworkId == network.Id).ToList();
            Console.WriteLine($"  Clients: {networkClients.Count}");
        }
    }

    private static async Task Example5_ControllerInformationAsync(
        UniFiApiClient apiClient,
        ILogger<UniFiDiscovery> logger)
    {
        Console.WriteLine("\n=== Example 5: Controller Information ===");

        var discovery = new UniFiDiscovery(apiClient, logger);
        var controllerInfo = await discovery.GetControllerInfoAsync();

        if (controllerInfo != null)
        {
            Console.WriteLine($"Controller: {controllerInfo.Name}");
            Console.WriteLine($"Hostname: {controllerInfo.Hostname}");
            Console.WriteLine($"Version: {controllerInfo.Version} (build {controllerInfo.Build})");
            Console.WriteLine($"\nLicensing Fingerprint:");
            Console.WriteLine($"  Controller ID: {controllerInfo.ControllerId}");
            Console.WriteLine($"  Device ID: {controllerInfo.DeviceId ?? "N/A"}");
            Console.WriteLine($"  UUID: {controllerInfo.Uuid ?? "N/A"}");
            Console.WriteLine($"\nNetwork:");
            Console.WriteLine($"  IP Addresses: {string.Join(", ", controllerInfo.IpAddresses)}");
            Console.WriteLine($"  Inform URL: {controllerInfo.InformUrl}");
            Console.WriteLine($"\nStatus:");
            Console.WriteLine($"  Uptime: {controllerInfo.Uptime.TotalDays:F1} days");
            Console.WriteLine($"  Timezone: {controllerInfo.Timezone}");

            if (!string.IsNullOrEmpty(controllerInfo.HardwareModel))
            {
                Console.WriteLine($"  Hardware: {controllerInfo.HardwareModel}");
            }

            if (controllerInfo.UpdateAvailable)
            {
                Console.WriteLine($"\n⚠ Controller update available!");
            }

            Console.WriteLine($"\nFeatures:");
            Console.WriteLine($"  Cloud Key Running: {controllerInfo.IsCloudKeyRunning}");
            Console.WriteLine($"  UniFi Go Enabled: {controllerInfo.IsUnifiGoEnabled}");
        }
    }

    private static async Task Example6_FirewallConfigurationAsync(
        UniFiApiClient apiClient,
        ILogger<UniFiDiscovery> logger)
    {
        Console.WriteLine("\n=== Example 6: Firewall Configuration ===");

        var discovery = new UniFiDiscovery(apiClient, logger);
        var firewallConfig = await discovery.GetFirewallConfigurationAsync();

        Console.WriteLine($"Firewall Configuration (retrieved at {firewallConfig.RetrievedAt})");
        Console.WriteLine($"  Total Rules: {firewallConfig.Rules.Count}");
        Console.WriteLine($"  Enabled Rules: {firewallConfig.Rules.Count(r => r.Enabled)}");
        Console.WriteLine($"  Groups: {firewallConfig.Groups.Count}");

        // Group rules by ruleset
        var rulesByRuleset = firewallConfig.Rules
            .Where(r => r.Enabled)
            .GroupBy(r => r.Ruleset);

        foreach (var group in rulesByRuleset)
        {
            Console.WriteLine($"\n{group.Key} Rules:");

            foreach (var rule in group.OrderBy(r => r.RuleIndex))
            {
                Console.WriteLine($"  [{rule.RuleIndex}] {rule.Name}");
                Console.WriteLine($"      Action: {rule.Action}, Protocol: {rule.Protocol}");

                if (!string.IsNullOrEmpty(rule.SrcAddress))
                    Console.WriteLine($"      Source: {rule.SrcAddress}");

                if (!string.IsNullOrEmpty(rule.DstAddress))
                    Console.WriteLine($"      Destination: {rule.DstAddress}");

                if (!string.IsNullOrEmpty(rule.DstPort))
                    Console.WriteLine($"      Port: {rule.DstPort}");

                if (rule.Logging)
                    Console.WriteLine($"      Logging: Enabled");
            }
        }

        // Show firewall groups
        if (firewallConfig.Groups.Any())
        {
            Console.WriteLine("\nFirewall Groups:");

            foreach (var group in firewallConfig.Groups)
            {
                Console.WriteLine($"  {group.Name} ({group.GroupType})");
                Console.WriteLine($"    Members: {string.Join(", ", group.GroupMembers)}");
            }
        }
    }

    private static async Task Example7_NetworkManagementAsync(UniFiApiClient apiClient)
    {
        Console.WriteLine("\n=== Example 7: Network Management ===");

        // Get all network configurations
        var networks = await apiClient.GetNetworkConfigsAsync();

        Console.WriteLine($"Network Configurations: {networks.Count}");

        foreach (var network in networks)
        {
            Console.WriteLine($"\n{network.Name} ({network.Purpose})");
            Console.WriteLine($"  ID: {network.Id}");
            Console.WriteLine($"  Enabled: {network.Enabled}");
            Console.WriteLine($"  VLAN: {network.Vlan?.ToString() ?? "none"}");

            if (network.Purpose == "remote-user-vpn")
            {
                Console.WriteLine($"  VPN Type: {network.VpnType}");
            }

            if (network.Purpose == "wan")
            {
                Console.WriteLine($"  WAN Type: {network.WanType}");
                Console.WriteLine($"  Smart Queue Enabled: {network.WanSmartqEnabled}");

                if (network.WanSmartqEnabled)
                {
                    Console.WriteLine($"  Upload Rate: {network.WanSmartqUpRate} kbps");
                    Console.WriteLine($"  Download Rate: {network.WanSmartqDownRate} kbps");
                }
            }
        }

        // Example: Toggle a VPN network (commented out for safety)
        /*
        var vpnNetwork = networks.FirstOrDefault(n => n.Purpose == "remote-user-vpn");
        if (vpnNetwork != null)
        {
            Console.WriteLine($"\nToggling VPN network '{vpnNetwork.Name}'...");
            vpnNetwork.Enabled = !vpnNetwork.Enabled;
            var success = await apiClient.UpdateNetworkConfigAsync(vpnNetwork.Id, vpnNetwork);
            Console.WriteLine(success ? "✓ VPN toggled successfully" : "✗ Failed to toggle VPN");
        }
        */
    }

    private static async Task Example8_PerformanceMonitoringAsync(UniFiApiClient apiClient)
    {
        Console.WriteLine("\n=== Example 8: Performance Monitoring ===");

        // Get site health
        var healthJson = await apiClient.GetSiteHealthAsync();

        if (healthJson != null)
        {
            Console.WriteLine("Site Health:");
            Console.WriteLine(healthJson.RootElement.GetRawText());
        }

        // Get hourly stats for last 24 hours
        var statsJson = await apiClient.GetHourlySiteStatsAsync(
            start: DateTime.UtcNow.AddHours(-24),
            end: DateTime.UtcNow);

        if (statsJson != null)
        {
            Console.WriteLine("\nHourly Site Statistics (last 24 hours):");

            if (statsJson.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
            {
                Console.WriteLine($"  Data points: {data.GetArrayLength()}");

                // Show latest data point
                var latest = data[data.GetArrayLength() - 1];
                if (latest.TryGetProperty("time", out var time))
                {
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(time.GetInt64());
                    Console.WriteLine($"  Latest: {timestamp.LocalDateTime}");
                }

                if (latest.TryGetProperty("num_sta", out var numSta))
                {
                    Console.WriteLine($"  Clients: {numSta.GetInt32()}");
                }

                if (latest.TryGetProperty("wan-tx_bytes", out var wanTx) &&
                    latest.TryGetProperty("wan-rx_bytes", out var wanRx))
                {
                    var txMB = wanTx.GetDouble() / 1024.0 / 1024.0;
                    var rxMB = wanRx.GetDouble() / 1024.0 / 1024.0;
                    Console.WriteLine($"  WAN Traffic: TX {txMB:F2} MB, RX {rxMB:F2} MB");
                }
            }
        }

        // Get all devices with stats
        var devices = await apiClient.GetDevicesAsync();

        Console.WriteLine("\nDevice Performance:");
        foreach (var device in devices)
        {
            Console.WriteLine($"\n{device.Name} ({device.Model})");

            if (device.SystemStats != null)
            {
                Console.WriteLine($"  CPU: {device.SystemStats.Cpu ?? "N/A"}");
                Console.WriteLine($"  Memory: {device.SystemStats.Mem ?? "N/A"}");
                Console.WriteLine($"  Load Average: {device.SystemStats.LoadAvg1 ?? "N/A"}");
            }

            if (device.Stats != null)
            {
                var txMbps = device.Stats.TxBytes * 8.0 / 1024.0 / 1024.0 / device.Uptime;
                var rxMbps = device.Stats.RxBytes * 8.0 / 1024.0 / 1024.0 / device.Uptime;
                Console.WriteLine($"  Avg Throughput: TX {txMbps:F2} Mbps, RX {rxMbps:F2} Mbps");
            }
        }
    }
}
