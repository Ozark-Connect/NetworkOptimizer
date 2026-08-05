using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Utility methods for network operations (IP detection, etc.)
/// </summary>
public static class NetworkUtilities
{
    /// <summary>
    /// Well-known public anycast DNS resolvers UniFi Network uses as its default WAN SLA /
    /// internet-connectivity probe targets. Because the gateway constantly pings these, they
    /// can leave a neighbor-table entry on the WAN interface that masquerades as the L2 next
    /// hop, and users sometimes end up with them saved as Access ISP monitoring targets.
    /// Neither is ISP first-mile infrastructure, so upstream discovery rejects them as the L2
    /// neighbor and the target cleanup removes any that were saved.
    /// </summary>
    public static readonly IReadOnlySet<string> WanSlaProbeIps =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1.1.1.1", "8.8.8.8" };

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
    /// Every IPv4 unicast address this host holds, across all interfaces that are up.
    /// <para>
    /// Distinct from <see cref="DetectLocalIpFromInterfaces"/>, which picks the ONE address that
    /// best represents the host. That choice is arbitrary when something else has to recognise the
    /// host by an address it already knows: on a UniFi gateway the best-looking address can be an
    /// uplink the console never lists as the gateway's own, so a single-address comparison answers
    /// "is this that machine" with a false no.
    /// </para>
    /// <para>
    /// Bridges and virtual interfaces are INCLUDED here, unlike the single-address detection that
    /// skips them: a gateway's LAN address lives on a bridge, and it is one of the addresses a
    /// console does report. Loopback and link-local (169.254/16) are excluded - neither identifies
    /// a host, and both would be held by every machine, so comparing them could only produce a
    /// false match.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> LocalUnicastAddresses()
    {
        var addresses = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;
                    var text = address.ToString();
                    if (text.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    if (!addresses.Contains(text, StringComparer.OrdinalIgnoreCase))
                        addresses.Add(text);
                }
            }
        }
        catch
        {
            // Enumeration is best effort: the caller still has its single detected address.
        }
        return addresses;
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
    /// For an octet-aligned CIDR (/8, /16, /24), returns the IP prefix string suitable for
    /// SQL LIKE matching (e.g. "10." for /8, "192.168.1." for /24).
    /// Returns null for exact IPs (no slash), invalid input, /32, or non-octet-aligned masks.
    /// </summary>
    public static string? GetCidrLikePrefix(string? value)
    {
        if (value == null || !value.Contains('/')) return null;
        var slash = value.IndexOf('/');
        var ipPart = value[..slash];
        if (!int.TryParse(value[(slash + 1)..], out var bits)) return null;
        if (!IPAddress.TryParse(ipPart, out _)) return null;
        var octets = ipPart.Split('.');
        if (octets.Length != 4) return null;
        return bits switch
        {
            8 => $"{octets[0]}.",
            16 => $"{octets[0]}.{octets[1]}.",
            24 => $"{octets[0]}.{octets[1]}.{octets[2]}.",
            _ => null // /32 = exact match, others unsupported in SQL
        };
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
    /// Includes RFC1918, loopback, link-local, CGNAT, 0.0.0.0/8, multicast, and IPv6 equivalents.
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <returns>True if the IP is private/non-routable, false if public</returns>
    public static bool IsPrivateIpAddress(IPAddress ip)
    {
        // An IPv4 client accepted on a dual-stack socket arrives as ::ffff:a.b.c.d. Unwrap it
        // so the RFC1918/CGNAT/etc. byte checks below apply - otherwise it falls through the
        // IPv6 branch and a private LAN address is misreported as public. Real IPv6 is untouched.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // IPv6 loopback and link-local
        if (ip.IsIPv6LinkLocal || IPAddress.IsLoopback(ip))
            return true;

        // IPv6 Unique Local Address (fc00::/7, primarily fd00::/8 in practice)
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return IsIPv6UniqueLocal(ip);

        // Only do byte checks for IPv4
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 (RFC1918)
        if (IsRfc1918(ip))
            return true;

        var bytes = ip.GetAddressBytes();

        // 127.0.0.0/8 (Loopback)
        if (bytes[0] == 127)
            return true;

        // 169.254.0.0/16 (Link-local)
        if (bytes[0] == 169 && bytes[1] == 254)
            return true;

        // 100.64.0.0/10 (CGNAT / Carrier-grade NAT)
        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            return true;

        // 0.0.0.0/8
        if (bytes[0] == 0)
            return true;

        // 224.0.0.0/4 (Multicast + reserved)
        if (bytes[0] >= 224)
            return true;

        return false;
    }

    /// <summary>
    /// Collapses an IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) - produced when Kestrel
    /// accepts an IPv4 client on a dual-stack socket - back to its plain IPv4 form. Any other
    /// value (a real IPv6 address, a plain IPv4 address, null/empty, "unknown", or an
    /// unparseable string) is returned unchanged, so genuine IPv6 client identity is never
    /// altered. Apply at the point a client address is captured so downstream lookups,
    /// comparisons, storage, and display all see the same canonical form.
    /// </summary>
    /// <param name="ipAddress">Raw client IP string (may be null).</param>
    /// <returns>The plain IPv4 string if the input was IPv4-mapped; otherwise the input unchanged.</returns>
    public static string? NormalizeToIPv4String(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress))
            return ipAddress;
        if (!IPAddress.TryParse(ipAddress, out var ip))
            return ipAddress;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ipAddress;
    }

    /// <summary>
    /// Check if an IP address is in one of the three RFC1918 private IPv4 blocks
    /// (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16). Stricter than
    /// <see cref="IsPrivateIpAddress(IPAddress)"/>: loopback, link-local, CGNAT, and
    /// multicast are NOT RFC1918. IPv4-mapped IPv6 addresses are unwrapped first.
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <returns>True if the IP is in an RFC1918 block</returns>
    public static bool IsRfc1918(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    /// <summary>
    /// Check if an IP address is an IPv6 unique local address (fc00::/7, in practice fd00::/8).
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <returns>True if the IP is an IPv6 ULA</returns>
    public static bool IsIPv6UniqueLocal(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        return (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
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
    /// </summary>
    /// <param name="ip">Parsed IP address to check</param>
    /// <returns>True if the IP is public/routable, false if private</returns>
    public static bool IsPublicIpAddress(IPAddress ip)
    {
        // Only handle IPv4
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return false;

        return !IsPrivateIpAddress(ip);
    }

    /// <summary>
    /// Classify how routable an IPv4 address actually is. The monitoring subsystem's
    /// upstream tracer (spec 5.5) uses this to honestly surface CGNAT, double-NAT, and
    /// non-globally-routed "public" space rather than silently mis-handling them.
    /// </summary>
    public static PublicAddressClass ClassifyPublicAddress(string? ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress)) return PublicAddressClass.Unknown;
        if (!IPAddress.TryParse(ipAddress, out var ip)) return PublicAddressClass.Unknown;
        return ClassifyPublicAddress(ip);
    }

    public static PublicAddressClass ClassifyPublicAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return PublicAddressClass.IPv6;
        if (ip.AddressFamily != AddressFamily.InterNetwork)
            return PublicAddressClass.Unknown;

        var b = ip.GetAddressBytes();

        // 100.64.0.0/10 - CGNAT
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            return PublicAddressClass.Cgnat;

        // RFC1918 ranges on what's supposed to be a WAN: double-NAT scenario.
        if (b[0] == 10) return PublicAddressClass.DoubleNat;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return PublicAddressClass.DoubleNat;
        if (b[0] == 192 && b[1] == 168) return PublicAddressClass.DoubleNat;

        // Loopback / link-local: shouldn't appear as a WAN IP. Mark as misconfigured.
        if (b[0] == 127) return PublicAddressClass.Misconfigured;
        if (b[0] == 169 && b[1] == 254) return PublicAddressClass.Misconfigured;

        // 0.0.0.0/8 and the multicast / experimental / reserved upper ranges - "public"
        // looking but not globally routed.
        if (b[0] == 0) return PublicAddressClass.NonGloballyRouted;
        if (b[0] >= 224) return PublicAddressClass.NonGloballyRouted;

        return PublicAddressClass.PublicIPv4;
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
    /// Well-known port-to-service name map. Single source of truth used by
    /// threat dashboard, drilldowns, and any future port labeling.
    /// Returns the friendly service name, or null if the port is not in the map.
    /// </summary>
    public static string? GetPortServiceName(int port)
    {
        return port switch
        {
            20 => "FTP Data",
            21 => "FTP",
            22 => "SSH",
            23 => "Telnet",
            25 => "SMTP",
            53 => "DNS",
            80 => "HTTP",
            110 => "POP3",
            111 => "RPC",
            123 => "NTP",
            135 => "MS-RPC",
            137 => "NetBIOS-NS",
            138 => "NetBIOS-DGM",
            139 => "NetBIOS",
            143 => "IMAP",
            161 => "SNMP",
            162 => "SNMP Trap",
            389 => "LDAP",
            443 => "HTTPS",
            445 => "SMB",
            465 => "SMTPS",
            500 => "IKE",
            502 => "Modbus",
            587 => "SMTP Submit",
            636 => "LDAPS",
            993 => "IMAPS",
            995 => "POP3S",
            1080 => "SOCKS",
            1433 => "MSSQL",
            1434 => "MSSQL Browser",
            1521 => "Oracle",
            1723 => "PPTP",
            1883 => "MQTT",
            2049 => "NFS",
            2222 => "SSH-Alt",
            2375 => "Docker",
            2376 => "Docker TLS",
            3306 => "MySQL",
            3389 => "RDP",
            4500 => "IPSec NAT-T",
            5060 => "SIP",
            5405 => "Corosync",
            5406 => "Corosync",
            5432 => "PostgreSQL",
            5672 => "AMQP",
            5900 => "VNC",
            6379 => "Redis",
            6443 => "Kubernetes",
            8006 => "Proxmox",
            8080 => "HTTP-Alt",
            8086 => "InfluxDB",
            8443 => "HTTPS-Alt",
            8883 => "MQTT-TLS",
            9200 => "Elasticsearch",
            9300 => "Elasticsearch",
            11211 => "Memcached",
            27017 => "MongoDB",
            _ => null
        };
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
    /// Reduce a user-entered device host field to a bare host: when the input is URL-shaped
    /// (has a scheme or a path), extract just the hostname/IP. People paste the device's
    /// management page URL ("http://192.168.100.1/status") into fields that expect an IP or
    /// hostname. Plain hostnames/IPs pass through unchanged apart from trimming, including
    /// "host:port" values - only URL-shaped input is stripped (a URL's port is dropped with
    /// the rest of the URL; monitoring providers pick their own ports).
    /// </summary>
    public static string ExtractHostFromUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input ?? "";

        var value = input.Trim();
        var hasScheme = value.Contains("://", StringComparison.Ordinal);
        if (!hasScheme && !value.Contains('/'))
            return value;

        var candidate = hasScheme ? value : $"http://{value}";
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            return uri.Host;

        return value.TrimEnd('/');
    }

    /// <summary>
    /// Join a declared host with an optional declared port into a URL authority ("host" or
    /// "host:port"), so callers can keep interpolating a single value into "https://{authority}".
    ///
    /// The default port is omitted rather than written out, because it has to be: an origin string
    /// with an explicit :443 does not match the browser's own origin for CORS, and existing installs
    /// declare no port at all. An embedded port in <paramref name="host"/> wins over
    /// <paramref name="port"/> - operators who wrote "host:8443" in the hostname field mean it, and
    /// appending would produce "host:8443:8443".
    /// </summary>
    /// <param name="host">Declared hostname, optionally already carrying ":port".</param>
    /// <param name="port">Declared port, or null/empty for the scheme default.</param>
    /// <param name="defaultPort">The port the scheme implies, omitted from the result.</param>
    public static string? ComposeAuthority(string? host, string? port, int defaultPort)
    {
        var value = host?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        var declared = port?.Trim();
        if (string.IsNullOrEmpty(declared) || (int.TryParse(declared, out var parsed) && parsed == defaultPort))
            return value;

        return PortSeparatorIndex(value) >= 0 ? value : $"{value}:{declared}";
    }

    /// <summary>
    /// The host part of a URL authority, dropping any ":port". Null/empty in, null out.
    /// </summary>
    public static string? AuthorityHost(string? authority)
    {
        var value = authority?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;

        var separator = PortSeparatorIndex(value);
        return separator < 0 ? value : value[..separator];
    }

    /// <summary>
    /// Index of the colon introducing a port, or -1. Bracketed IPv6 literals keep their colons
    /// inside the brackets, so only a colon after the closing bracket counts.
    /// </summary>
    private static int PortSeparatorIndex(string authority) =>
        authority.IndexOf(':', authority.LastIndexOf(']') + 1);

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

    /// <summary>
    /// Picks the interface whose traffic counters best represent a WAN connection,
    /// given the physical port (e.g. "eth4") and the logical uplink (e.g. "eth4" for
    /// plain connections, "eth4.100" for VLAN-tagged, "ppp0" for PPPoE, "gre1" for
    /// LAN-tunneled cellular WANs).
    /// VLAN sub-interfaces double-count on some kernels, so when the uplink is a
    /// sub-interface of the physical port, the physical port wins. Any other logical
    /// uplink (ppp*, gre*, ...) wins: it carries exactly the WAN payload, while the
    /// physical port keeps counting too and over-counts (confirmed on a UCG-Fiber
    /// PPPoE WAN, where the physical port ran ~40% above ppp0 due to tunnel overhead
    /// and sibling VLANs on the same port).
    /// </summary>
    /// <param name="physicalIfName">Physical port interface name, if known</param>
    /// <param name="uplinkIfName">Logical uplink interface name, if known</param>
    /// <returns>The preferred interface name, or null when both inputs are null</returns>
    public static string? PreferredWanCounterInterface(string? physicalIfName, string? uplinkIfName)
    {
        if (string.IsNullOrEmpty(uplinkIfName)) return physicalIfName;
        if (string.IsNullOrEmpty(physicalIfName)) return uplinkIfName;
        if (uplinkIfName.Equals(physicalIfName, StringComparison.OrdinalIgnoreCase) ||
            uplinkIfName.StartsWith(physicalIfName + ".", StringComparison.OrdinalIgnoreCase))
            return physicalIfName;
        return uplinkIfName;
    }

    /// <summary>
    /// True when a WAN's data-path interface name is a PPPoE session interface - "ppp0",
    /// "ppp3", or the "pppoe0" form some stacks use. The kernel names a PPPoE session this
    /// way and nothing else, so the interface name alone identifies the encapsulation
    /// without asking the user or inspecting the session.
    ///
    /// Deliberately anchored: a plain "ppp" with no unit number, or any name that merely
    /// starts with those letters, is not a match. Pass the LOGICAL uplink (uplink_ifname),
    /// not the physical port - the physical port stays "eth6" while the session rides ppp0.
    /// </summary>
    public static bool IsPppoeInterface(string? uplinkIfName)
    {
        if (string.IsNullOrWhiteSpace(uplinkIfName)) return false;
        return Regex.IsMatch(uplinkIfName.Trim(), @"^ppp(oe)?\d+$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// True when a WAN's data-path interface name is the GRE tunnel a UniFi gateway uses to reach an
    /// attached UniFi Cellular Modem - "gre0", "gre1". Nothing else on a UniFi gateway presents a WAN
    /// that way, so a match identifies the medium as cellular without asking.
    ///
    /// The implication runs one way only. A third-party LTE/5G modem, a carrier router in bridge
    /// mode, or a USB dongle is every bit as cellular and appears as an ordinary Ethernet or PPPoE
    /// WAN, so FALSE says nothing about the medium - do not read it as "not cellular".
    ///
    /// Anchored for the same reason as <see cref="IsPppoeInterface"/>: "gretap0", or any name merely
    /// beginning with those letters, is not one of these. Pass the data-path interface (uplink_ifname,
    /// which a UniFi Cellular Modem WAN reports as gre1 for both it and ifname).
    /// </summary>
    public static bool IsUniFiCellularModemTunnel(string? dataPathIfName)
    {
        if (string.IsNullOrWhiteSpace(dataPathIfName)) return false;
        return Regex.IsMatch(dataPathIfName.Trim(), @"^gre\d+$", RegexOptions.IgnoreCase);
    }
}
