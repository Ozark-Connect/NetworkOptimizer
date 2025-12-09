# NetworkOptimizer.UniFi

Full-featured UniFi Controller API client for network optimization and analysis.

## Features

- **Cookie-based Authentication** - Mimics browser login behavior
- **CSRF Token Handling** - Automatic extraction and header management
- **Automatic Re-authentication** - Handles 401/403 with transparent retry
- **Retry Logic with Polly** - Resilient to transient failures
- **Self-signed Certificate Support** - Works with default UniFi controller configs
- **Comprehensive API Coverage** - All major endpoints implemented

## API Coverage

### Device Management
- `GetDevicesAsync()` - Get all UniFi devices (APs, switches, gateways)
- `GetDeviceAsync(mac)` - Get specific device by MAC

### Client Management
- `GetClientsAsync()` - Get all connected clients (wireless + wired)
- `GetClientAsync(mac)` - Get specific client by MAC
- `GetAllKnownClientsAsync()` - Get all known users (including historical)

### Firewall Management
- `GetFirewallRulesAsync()` - Get all firewall rules
- `GetFirewallGroupsAsync()` - Get firewall groups (address/port groups)

### Network Configuration
- `GetNetworkConfigsAsync()` - Get all network/VLAN configurations
- `UpdateNetworkConfigAsync()` - Update network config (enable/disable VPNs, etc.)

### System Information
- `GetSystemInfoAsync()` - Get controller info **including licensing fingerprint**
- `GetSelfInfoAsync()` - Get current user info
- `GetSiteHealthAsync()` - Get site health metrics
- `GetSitesAsync()` - Get all accessible sites

### Traffic Management
- `GetTrafficRoutesAsync()` - Get traffic routes (UniFi Network v2 API)
- `UpdateTrafficRouteAsync()` - Update traffic route rules

### Statistics
- `GetHourlySiteStatsAsync()` - Get hourly site statistics

### Discovery Services
- `DiscoverDevicesAsync()` - Discover all devices via API
- `DiscoverClientsAsync()` - Discover all clients via API
- `DiscoverTopologyAsync()` - Get comprehensive network topology with device hierarchy
- `GetFirewallConfigurationAsync()` - Get complete firewall configuration
- `GetControllerInfoAsync()` - Get controller info with licensing fingerprint

## Usage

### Basic Setup

```csharp
using Microsoft.Extensions.Logging;
using NetworkOptimizer.UniFi;

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var apiLogger = loggerFactory.CreateLogger<UniFiApiClient>();
var discoveryLogger = loggerFactory.CreateLogger<UniFiDiscovery>();

// Create API client
var apiClient = new UniFiApiClient(
    logger: apiLogger,
    controllerHost: "192.168.1.1",  // or "unifi.local"
    username: "admin",
    password: "your-password",
    site: "default"  // optional, defaults to "default"
);

// Login (automatically called by API methods, but can be explicit)
await apiClient.LoginAsync();

// Create discovery service
var discovery = new UniFiDiscovery(apiClient, discoveryLogger);
```

### Discover Devices

```csharp
// Get all UniFi devices
var devices = await discovery.DiscoverDevicesAsync();

foreach (var device in devices)
{
    Console.WriteLine($"{device.Name} ({device.ModelDisplay}) - {device.IpAddress}");
    Console.WriteLine($"  Type: {device.Type}, Firmware: {device.Firmware}");
    Console.WriteLine($"  Uptime: {device.Uptime}, State: {device.State}");

    if (device.Upgradable)
    {
        Console.WriteLine($"  Upgrade available: {device.UpgradeToFirmware}");
    }
}
```

### Discover Clients

```csharp
// Get all connected clients
var clients = await discovery.DiscoverClientsAsync();

foreach (var client in clients)
{
    Console.WriteLine($"{client.Hostname} ({client.Name}) - {client.IpAddress}");
    Console.WriteLine($"  MAC: {client.Mac}, Connection: {client.ConnectionType}");

    if (!client.IsWired)
    {
        Console.WriteLine($"  SSID: {client.Essid}, Signal: {client.SignalStrength} dBm");
        Console.WriteLine($"  Channel: {client.Channel}, Protocol: {client.RadioProtocol}");
    }
    else
    {
        Console.WriteLine($"  Switch Port: {client.SwitchPort}");
    }

    var txMb = client.TxBytes / 1024.0 / 1024.0;
    var rxMb = client.RxBytes / 1024.0 / 1024.0;
    Console.WriteLine($"  Traffic: TX {txMb:F2} MB, RX {rxMb:F2} MB");
}
```

### Get Network Topology

```csharp
// Get complete network topology
var topology = await discovery.DiscoverTopologyAsync();

Console.WriteLine($"Network Topology (discovered at {topology.DiscoveredAt})");
Console.WriteLine($"  Devices: {topology.Devices.Count}");
Console.WriteLine($"  Clients: {topology.Clients.Count}");
Console.WriteLine($"  Networks: {topology.Networks.Count}");

// Show device hierarchy
var gateways = topology.Devices.Where(d => d.Type == DeviceType.Gateway);
foreach (var gateway in gateways)
{
    Console.WriteLine($"\n{gateway.Name} (Gateway)");

    if (gateway.DownstreamDevices != null)
    {
        foreach (var downstream in gateway.DownstreamDevices)
        {
            Console.WriteLine($"  └─ {downstream}");
        }
    }
}

// Show network configurations
foreach (var network in topology.Networks)
{
    Console.WriteLine($"\nNetwork: {network.Name} ({network.Purpose})");
    Console.WriteLine($"  Enabled: {network.Enabled}");
    Console.WriteLine($"  VLAN: {network.VlanId}");
    Console.WriteLine($"  Subnet: {network.IpSubnet}");

    if (network.IsDhcpEnabled)
    {
        Console.WriteLine($"  DHCP Range: {network.DhcpRange}");
    }
}
```

### Get Controller Information (with Licensing Fingerprint)

```csharp
// Get controller info including licensing fingerprint
var controllerInfo = await discovery.GetControllerInfoAsync();

if (controllerInfo != null)
{
    Console.WriteLine($"Controller: {controllerInfo.Name}");
    Console.WriteLine($"Version: {controllerInfo.Version} (build {controllerInfo.Build})");
    Console.WriteLine($"Hostname: {controllerInfo.Hostname}");
    Console.WriteLine($"Controller ID: {controllerInfo.ControllerId}");  // <- Licensing fingerprint
    Console.WriteLine($"Device ID: {controllerInfo.DeviceId}");
    Console.WriteLine($"UUID: {controllerInfo.Uuid}");
    Console.WriteLine($"Uptime: {controllerInfo.Uptime}");
    Console.WriteLine($"IP Addresses: {string.Join(", ", controllerInfo.IpAddresses)}");

    if (controllerInfo.UpdateAvailable)
    {
        Console.WriteLine("Controller update available!");
    }
}
```

### Get Firewall Configuration

```csharp
// Get complete firewall configuration
var firewallConfig = await discovery.GetFirewallConfigurationAsync();

Console.WriteLine($"Firewall Configuration (retrieved at {firewallConfig.RetrievedAt})");
Console.WriteLine($"  Rules: {firewallConfig.Rules.Count}");
Console.WriteLine($"  Groups: {firewallConfig.Groups.Count}");

// Show enabled rules
var enabledRules = firewallConfig.Rules.Where(r => r.Enabled);
foreach (var rule in enabledRules)
{
    Console.WriteLine($"\n{rule.Name} ({rule.Ruleset})");
    Console.WriteLine($"  Action: {rule.Action}");
    Console.WriteLine($"  Protocol: {rule.Protocol}");

    if (!string.IsNullOrEmpty(rule.SrcAddress))
        Console.WriteLine($"  Source: {rule.SrcAddress}");

    if (!string.IsNullOrEmpty(rule.DstAddress))
        Console.WriteLine($"  Destination: {rule.DstAddress}");

    if (!string.IsNullOrEmpty(rule.DstPort))
        Console.WriteLine($"  Port: {rule.DstPort}");
}

// Show firewall groups
foreach (var group in firewallConfig.Groups)
{
    Console.WriteLine($"\n{group.Name} ({group.GroupType})");
    Console.WriteLine($"  Members: {string.Join(", ", group.GroupMembers)}");
}
```

### Direct API Access

```csharp
// Get devices directly from API
var devices = await apiClient.GetDevicesAsync();

// Get clients
var clients = await apiClient.GetClientsAsync();

// Get firewall rules
var rules = await apiClient.GetFirewallRulesAsync();

// Get network configs
var networks = await apiClient.GetNetworkConfigsAsync();

// Get system info (includes licensing fingerprint)
var sysInfo = await apiClient.GetSystemInfoAsync();
Console.WriteLine($"Controller ID: {sysInfo?.AnonymousControllerId}");

// Update network config (e.g., enable/disable VPN)
var vpnConfig = networks.FirstOrDefault(n => n.Purpose == "remote-user-vpn");
if (vpnConfig != null)
{
    vpnConfig.Enabled = false;
    await apiClient.UpdateNetworkConfigAsync(vpnConfig.Id, vpnConfig);
}
```

## UniFi API Quirks

### 1. Cookie-Based Authentication
UniFi uses cookie-based sessions, not token-based auth. The client must:
- Maintain a `CookieContainer`
- Preserve cookies across requests
- Handle CSRF tokens from headers

### 2. Response Format
Most endpoints return data in this format:
```json
{
  "meta": {
    "rc": "ok"
  },
  "data": [ ... ]
}
```

### 3. Session Expiration
Sessions can expire, returning 401/403. The client automatically re-authenticates.

### 4. Self-Signed Certificates
UniFi controllers typically use self-signed certs. Certificate validation is disabled by default in this client.

### 5. Site Names
The default site is named "default". Multi-site controllers require specifying the site name.

### 6. MAC Address Format
MAC addresses are lowercase with colons: `aa:bb:cc:dd:ee:ff`

### 7. Unix Timestamps
Most timestamps are in Unix epoch seconds (not milliseconds).

### 8. State Codes
Device state codes:
- `0` - Disconnected
- `1` - Connected
- `2` - Pending
- `4` - Upgrading
- `5` - Provisioning

### 9. Traffic Routes API
The newer UniFi Network Application uses a v2 API at `/proxy/network/v2/api/...` for traffic routes.

### 10. Controller Fingerprinting
The `anonymous_controller_id` in sysinfo is the licensing fingerprint - unique per controller installation.

## Error Handling

All API methods return `null` or empty collections on failure. Check logs for details:

```csharp
var devices = await apiClient.GetDevicesAsync();
if (devices == null || devices.Count == 0)
{
    // Handle error - check logs for details
    Console.WriteLine("Failed to retrieve devices");
}
```

## Thread Safety

The `UniFiApiClient` uses a semaphore to ensure thread-safe authentication. Multiple concurrent API calls are supported.

## Disposal

Always dispose the client when done:

```csharp
using var apiClient = new UniFiApiClient(...);
// Use the client
await apiClient.LoginAsync();
// Client automatically disposed
```

Or explicitly:

```csharp
var apiClient = new UniFiApiClient(...);
try
{
    await apiClient.LoginAsync();
    // Use the client
}
finally
{
    apiClient.Dispose();
}
```

## Logging

The client uses `ILogger` for comprehensive logging:
- **Debug**: API calls, authentication steps, response parsing
- **Info**: Successful operations, data counts
- **Warning**: Retries, missing data, non-critical failures
- **Error**: Authentication failures, API errors

## Dependencies

- .NET 8.0
- Microsoft.Extensions.Logging.Abstractions
- Polly (retry policies)
- System.Text.Json

## References

This implementation is based on patterns from:
- **SeaTurtleGamingBuddy** - UniFi API authentication and traffic route management
- **SeaTurtleMonitor** - SNMP-based device discovery patterns

## License

Production-ready code for UniFi network optimization and analysis.
