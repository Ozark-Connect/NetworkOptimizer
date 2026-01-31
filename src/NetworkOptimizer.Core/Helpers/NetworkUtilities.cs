using System.Net;
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

    /// <summary>
    /// Check if an IP address string is within a given subnet (CIDR notation like "192.168.1.0/24").
    /// </summary>
    /// <param name="ipAddress">IP address to check (e.g., "192.168.1.100")</param>
    /// <param name="cidrSubnet">Subnet in CIDR notation (e.g., "192.168.1.0/24")</param>
    /// <returns>True if the IP is within the subnet, false otherwise</returns>
    public static bool IsIpInSubnet(string ipAddress, string? cidrSubnet)
    {
        if (string.IsNullOrEmpty(cidrSubnet))
            return false;

        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        return IsIpInSubnet(ip, cidrSubnet);
    }

    /// <summary>
    /// Check if an IP address is within a given subnet (CIDR notation like "192.168.1.0/24" or "2001:db8::/32").
    /// Supports both IPv4 and IPv6 addresses.
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <param name="cidrSubnet">Subnet in CIDR notation (e.g., "192.168.1.0/24" or "2001:db8::/32")</param>
    /// <returns>True if the IP is within the subnet, false otherwise</returns>
    public static bool IsIpInSubnet(IPAddress ip, string cidrSubnet)
    {
        var parts = cidrSubnet.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefixLength))
            return false;

        if (!IPAddress.TryParse(parts[0], out var networkAddress))
            return false;

        // Addresses must be same family (both IPv4 or both IPv6)
        if (ip.AddressFamily != networkAddress.AddressFamily)
            return false;

        var ipBytes = ip.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();
        var byteCount = ipBytes.Length; // 4 for IPv4, 16 for IPv6

        // Create mask from prefix length for the appropriate address length
        var maskBytes = new byte[byteCount];
        var remainingBits = prefixLength;
        for (int i = 0; i < byteCount; i++)
        {
            if (remainingBits >= 8)
            {
                maskBytes[i] = 0xFF;
                remainingBits -= 8;
            }
            else if (remainingBits > 0)
            {
                maskBytes[i] = (byte)(0xFF << (8 - remainingBits));
                remainingBits = 0;
            }
            else
            {
                maskBytes[i] = 0;
            }
        }

        // Check if masked IP equals masked network
        for (int i = 0; i < byteCount; i++)
        {
            if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Check if an IP address is within any of the given subnets.
    /// </summary>
    /// <param name="ipAddress">IP address to check</param>
    /// <param name="cidrSubnets">Collection of subnets in CIDR notation</param>
    /// <returns>True if the IP is within any subnet, false otherwise</returns>
    public static bool IsIpInAnySubnet(string ipAddress, IEnumerable<string> cidrSubnets)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        foreach (var subnet in cidrSubnets)
        {
            if (!string.IsNullOrEmpty(subnet) && IsIpInSubnet(ip, subnet))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if an IP address is a private/non-routable address.
    /// Includes RFC1918, loopback, link-local, and CGNAT ranges.
    /// </summary>
    /// <param name="ipAddress">IP address string to check</param>
    /// <returns>True if the IP is private/non-routable, false if public or invalid</returns>
    public static bool IsPrivateIpAddress(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        return IsPrivateIpAddress(ip);
    }

    /// <summary>
    /// Check if an IP address is a private/non-routable address.
    /// For IPv4: Includes RFC1918, loopback, link-local, and CGNAT ranges.
    /// For IPv6: Includes loopback (::1), link-local (fe80::/10), and unique local (fc00::/7).
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <returns>True if the IP is private/non-routable, false if public</returns>
    public static bool IsPrivateIpAddress(IPAddress ip)
    {
        // Handle IPv6
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsPrivateIPv6Address(ip);
        }

        // Handle IPv4
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = ip.GetAddressBytes();

        // 10.0.0.0/8 (RFC1918)
        if (bytes[0] == 10)
            return true;

        // 172.16.0.0/12 (RFC1918)
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            return true;

        // 192.168.0.0/16 (RFC1918)
        if (bytes[0] == 192 && bytes[1] == 168)
            return true;

        // 127.0.0.0/8 (Loopback)
        if (bytes[0] == 127)
            return true;

        // 169.254.0.0/16 (Link-local)
        if (bytes[0] == 169 && bytes[1] == 254)
            return true;

        // 100.64.0.0/10 (CGNAT / Carrier-grade NAT)
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            return true;

        return false;
    }

    /// <summary>
    /// Check if an IPv6 address is a private/non-routable address.
    /// Includes loopback (::1), link-local (fe80::/10), and unique local addresses (fc00::/7).
    /// </summary>
    private static bool IsPrivateIPv6Address(IPAddress ip)
    {
        // Loopback (::1)
        if (IPAddress.IsLoopback(ip))
            return true;

        // Link-local (fe80::/10)
        if (ip.IsIPv6LinkLocal)
            return true;

        // Unique Local Addresses (fc00::/7) - includes fc00::/8 and fd00::/8
        var bytes = ip.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC) // First 7 bits are 1111110 (0xFC with mask 0xFE)
            return true;

        return false;
    }

    /// <summary>
    /// Check if an IP address is a public/routable address.
    /// </summary>
    /// <param name="ipAddress">IP address string to check</param>
    /// <returns>True if the IP is public/routable, false if private or invalid</returns>
    public static bool IsPublicIpAddress(string ipAddress)
    {
        if (!IPAddress.TryParse(ipAddress, out var ip))
            return false;

        return IsPublicIpAddress(ip);
    }

    /// <summary>
    /// Check if an IP address is a public/routable address.
    /// Supports both IPv4 and IPv6 addresses.
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <returns>True if the IP is public/routable, false if private</returns>
    public static bool IsPublicIpAddress(IPAddress ip)
    {
        // Handle IPv4
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return !IsPrivateIpAddress(ip);
        }

        // Handle IPv6
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return !IsPrivateIpAddress(ip);
        }

        // Unknown address family
        return false;
    }

    /// <summary>
    /// Check if a CIDR block completely covers another subnet.
    /// Supports both IPv4 and IPv6.
    /// </summary>
    /// <param name="outerCidr">The outer/larger CIDR (e.g., "192.168.0.0/16" or "2001:db8::/32")</param>
    /// <param name="innerSubnet">The inner/smaller subnet (e.g., "192.168.1.0/24" or "2001:db8:abcd::/48")</param>
    /// <returns>True if outerCidr completely covers innerSubnet</returns>
    public static bool CidrCoversSubnet(string outerCidr, string innerSubnet)
    {
        try
        {
            var (outerNetwork, outerPrefixLength) = ParseCidr(outerCidr);
            var (innerNetwork, innerPrefixLength) = ParseCidr(innerSubnet);

            if (outerNetwork == null || innerNetwork == null)
                return false;

            // Outer must have same or shorter prefix (larger network) to cover inner
            if (outerPrefixLength > innerPrefixLength)
                return false;

            // Must be same address family
            var outerBytes = outerNetwork.GetAddressBytes();
            var innerBytes = innerNetwork.GetAddressBytes();

            if (outerBytes.Length != innerBytes.Length)
                return false;

            // Compare network addresses masked by outer's prefix length
            var fullBytes = outerPrefixLength / 8;
            var remainingBits = outerPrefixLength % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                if (outerBytes[i] != innerBytes[i])
                    return false;
            }

            if (remainingBits > 0 && fullBytes < outerBytes.Length)
            {
                var mask = (byte)(0xFF << (8 - remainingBits));
                if ((outerBytes[fullBytes] & mask) != (innerBytes[fullBytes] & mask))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if any CIDR in a list covers the given subnet.
    /// </summary>
    /// <param name="cidrs">List of CIDRs to check</param>
    /// <param name="subnet">The subnet to check coverage for</param>
    /// <returns>True if any CIDR in the list covers the subnet</returns>
    public static bool AnyCidrCoversSubnet(IEnumerable<string>? cidrs, string? subnet)
    {
        if (cidrs == null || string.IsNullOrEmpty(subnet))
            return false;

        foreach (var cidr in cidrs)
        {
            if (string.IsNullOrEmpty(cidr))
                continue;

            // Check if this CIDR covers the network subnet
            if (CidrCoversSubnet(cidr, subnet))
                return true;

            // Also check if they're the same subnet
            if (string.Equals(cidr, subnet, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parse an IP address or IP range into a list of individual IPs.
    /// Supports formats: "192.168.1.1" (single) or "192.168.1.1-192.168.1.5" (range).
    /// For ranges, all IPs must be in the same /24 subnet and the range must be reasonable (max 256 IPs).
    /// </summary>
    /// <param name="ipOrRange">Single IP or IP range (e.g., "192.168.1.10-192.168.1.20")</param>
    /// <returns>List of individual IP addresses. Returns original value if parsing fails.</returns>
    public static List<string> ExpandIpRange(string? ipOrRange)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(ipOrRange))
            return result;

        var hyphenIndex = ipOrRange.IndexOf('-');
        if (hyphenIndex > 0 && hyphenIndex < ipOrRange.Length - 1)
        {
            var startIp = ipOrRange[..hyphenIndex];
            var endIp = ipOrRange[(hyphenIndex + 1)..];

            if (!IPAddress.TryParse(startIp, out var startAddr) ||
                !IPAddress.TryParse(endIp, out var endAddr))
            {
                result.Add(ipOrRange);
                return result;
            }

            var startBytes = startAddr.GetAddressBytes();
            var endBytes = endAddr.GetAddressBytes();

            // Only support IPv4 ranges in the same /24 subnet
            if (startBytes.Length != 4 || endBytes.Length != 4 ||
                startBytes[0] != endBytes[0] || startBytes[1] != endBytes[1] || startBytes[2] != endBytes[2])
            {
                result.Add(ipOrRange);
                return result;
            }

            var startOctet = startBytes[3];
            var endOctet = endBytes[3];

            if (startOctet > endOctet || endOctet - startOctet > 255)
            {
                result.Add(ipOrRange);
                return result;
            }

            for (var i = startOctet; i <= endOctet; i++)
            {
                result.Add($"{startBytes[0]}.{startBytes[1]}.{startBytes[2]}.{i}");
            }
        }
        else
        {
            result.Add(ipOrRange);
        }

        return result;
    }

    /// <summary>
    /// Parse CIDR notation into network address and prefix length.
    /// </summary>
    /// <param name="cidr">CIDR string (e.g., "192.168.1.0/24" or "2001:db8::/32")</param>
    /// <returns>Tuple of (network address, prefix length). Network is null if parsing fails.</returns>
    public static (IPAddress? Network, int PrefixLength) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return (null, 0);

        if (!IPAddress.TryParse(parts[0], out var address))
            return (null, 0);

        if (!int.TryParse(parts[1], out var prefixLength))
            return (null, 0);

        return (address, prefixLength);
    }

    /// <summary>
    /// Normalize an IP address to its canonical string form.
    /// For IPv6, this converts expanded/full form to compressed form (e.g., 2001:0db8::0001 becomes 2001:db8::1).
    /// For IPv4, returns the standard dotted-decimal form.
    /// Useful for comparing IP addresses that may be in different string formats.
    /// </summary>
    /// <param name="ipAddress">IP address string to normalize</param>
    /// <returns>Normalized IP address string, or the original string if parsing fails</returns>
    public static string NormalizeIpAddress(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return ipAddress ?? string.Empty;

        if (IPAddress.TryParse(ipAddress, out var ip))
        {
            return ip.ToString();
        }

        // If parsing fails, return the original
        return ipAddress;
    }

    /// <summary>
    /// Check if two IP addresses are equal, accounting for different string formats.
    /// Handles IPv6 compressed vs expanded forms and IPv4 with/without leading zeros.
    /// </summary>
    /// <param name="ip1">First IP address</param>
    /// <param name="ip2">Second IP address</param>
    /// <returns>True if both addresses represent the same IP, false otherwise</returns>
    public static bool IpAddressesAreEqual(string? ip1, string? ip2)
    {
        if (string.IsNullOrEmpty(ip1) || string.IsNullOrEmpty(ip2))
            return false;

        if (!IPAddress.TryParse(ip1, out var addr1) || !IPAddress.TryParse(ip2, out var addr2))
            return false;

        return addr1.Equals(addr2);
    }

    /// <summary>
    /// Normalize a controller URL: prepend https:// if needed, strip any path/query/fragment.
    /// E.g., "unifi.example.com/network/default/" becomes "https://unifi.example.com"
    /// </summary>
    /// <param name="url">The URL to normalize</param>
    /// <returns>Normalized URL with just scheme and host (and port if non-default)</returns>
    public static string NormalizeControllerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        url = url.Trim();

        // Prepend https:// if no scheme provided
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = $"https://{url}";
        }

        // Parse and extract just scheme + host (strip path, query, fragment)
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Include port if non-default
            var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
            return $"{uri.Scheme}://{uri.Host}{port}";
        }

        // Fallback: just trim trailing slashes
        return url.TrimEnd('/');
    }
}
