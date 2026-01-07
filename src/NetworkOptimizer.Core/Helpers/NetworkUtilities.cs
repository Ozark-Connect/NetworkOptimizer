using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Utility methods for network operations (IP detection, etc.)
/// </summary>
public static class NetworkUtilities
{
    /// <summary>
    /// Detect the best local IP address from network interfaces.
    /// Prioritizes: HOST_IP env var > Physical Ethernet > WiFi > Other.
    /// Skips virtual/container interfaces (Docker, Podman, Hyper-V, etc.).
    /// </summary>
    /// <returns>The best local IP address, or null if detection fails.</returns>
    public static string? DetectLocalIp()
    {
        // Check for HOST_IP environment variable override first
        var hostIp = Environment.GetEnvironmentVariable("HOST_IP");
        if (!string.IsNullOrWhiteSpace(hostIp))
        {
            return hostIp.Trim();
        }

        return DetectLocalIpFromInterfaces();
    }

    /// <summary>
    /// Detect local IP address from network interfaces (ignores HOST_IP env var).
    /// Prioritizes: Physical Ethernet > WiFi > Other.
    /// Skips virtual/container interfaces.
    /// </summary>
    /// <returns>The best local IP address from interfaces, or null if detection fails.</returns>
    public static string? DetectLocalIpFromInterfaces()
    {
        try
        {
            var interfaceIps = new List<(string Ip, int Priority)>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var name = ni.Name.ToLowerInvariant();
                var desc = ni.Description.ToLowerInvariant();

                // Skip virtual/bridge/tunnel/container interfaces
                if (IsVirtualInterface(name, desc))
                    continue;

                // Assign priority: lower = better (Ethernet > WiFi > Other)
                int priority = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or
                    NetworkInterfaceType.Ethernet3Megabit or
                    NetworkInterfaceType.FastEthernetT or
                    NetworkInterfaceType.FastEthernetFx or
                    NetworkInterfaceType.GigabitEthernet => 1,
                    NetworkInterfaceType.Wireless80211 => 2,
                    _ => 3
                };

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        interfaceIps.Add((addr.Address.ToString(), priority));
                    }
                }
            }

            return interfaceIps
                .OrderBy(x => x.Priority)
                .Select(x => x.Ip)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get all local IP addresses from network interfaces, sorted by priority.
    /// </summary>
    /// <returns>List of local IP addresses (Ethernet first, then WiFi, then others).</returns>
    public static List<string> GetAllLocalIpAddresses()
    {
        // Check for HOST_IP environment variable override first
        var hostIp = Environment.GetEnvironmentVariable("HOST_IP");
        if (!string.IsNullOrWhiteSpace(hostIp))
        {
            return new List<string> { hostIp.Trim() };
        }

        try
        {
            var interfaceIps = new List<(string Ip, int Priority, string Name)>();

            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                var name = ni.Name.ToLowerInvariant();
                var desc = ni.Description.ToLowerInvariant();

                // Skip virtual/bridge/tunnel/container interfaces
                if (IsVirtualInterface(name, desc))
                    continue;

                // Assign priority: lower = better (Ethernet > WiFi > Other)
                int priority = ni.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet or
                    NetworkInterfaceType.Ethernet3Megabit or
                    NetworkInterfaceType.FastEthernetT or
                    NetworkInterfaceType.FastEthernetFx or
                    NetworkInterfaceType.GigabitEthernet => 1,
                    NetworkInterfaceType.Wireless80211 => 2,
                    _ => 3
                };

                var props = ni.GetIPProperties();
                foreach (var addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        interfaceIps.Add((addr.Address.ToString(), priority, ni.Name));
                    }
                }
            }

            return interfaceIps
                .OrderBy(x => x.Priority)
                .Select(x => x.Ip)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Check if a network interface is virtual (Docker, Hyper-V, VirtualBox, etc.)
    /// </summary>
    private static bool IsVirtualInterface(string name, string description)
    {
        return name.Contains("docker") || description.Contains("docker") ||
               name.Contains("podman") || description.Contains("podman") ||
               name.Contains("macvlan") || description.Contains("macvlan") ||
               name.Contains("veth") || name.Contains("br-") ||
               name.Contains("virbr") || name.Contains("vbox") ||
               name.Contains("vmnet") || name.Contains("vmware") ||
               name.Contains("hyper-v") || description.Contains("hyper-v") ||
               name.Contains("virtualbox") || description.Contains("virtualbox") ||
               name.StartsWith("veth") || name.StartsWith("cni") ||
               name.StartsWith("gre") || name.StartsWith("ifb") ||
               name.StartsWith("wg");  // WireGuard
    }
}
