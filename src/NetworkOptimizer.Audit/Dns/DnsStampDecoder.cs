using System.Text;

namespace NetworkOptimizer.Audit.Dns;

/// <summary>
/// Decodes DNS Stamps (SDNS format) used by secure DNS protocols
/// Based on DNS Stamp specification: https://dnscrypt.info/stamps/
/// </summary>
public static class DnsStampDecoder
{
    private const string StampPrefix = "sdns://";

    /// <summary>
    /// DNS Stamp protocol types
    /// </summary>
    public enum DnsProtocol : byte
    {
        DNSCrypt = 0x01,
        DoH = 0x02,
        DoT = 0x03,
        DoQ = 0x04,
        ODoH = 0x05,
        DNSCryptRelay = 0x81,
        ODoHRelay = 0x85
    }

    /// <summary>
    /// Decode a DNS stamp and return its components
    /// </summary>
    public static DnsStampInfo? Decode(string stamp)
    {
        if (string.IsNullOrEmpty(stamp))
            return null;

        try
        {
            // Remove prefix if present
            var base64 = stamp.StartsWith(StampPrefix, StringComparison.OrdinalIgnoreCase)
                ? stamp.Substring(StampPrefix.Length)
                : stamp;

            // Decode base64 (handles URL-safe base64)
            var bytes = DecodeBase64Url(base64);
            if (bytes.Length < 2)
                return null;

            var offset = 0;
            Console.WriteLine($"[DnsStamp] Raw bytes ({bytes.Length}): {BitConverter.ToString(bytes.Take(Math.Min(50, bytes.Length)).ToArray())}");

            // First byte is protocol type
            var protocol = (DnsProtocol)bytes[offset++];

            // Second byte is properties (for DoH/DoT/DoQ)
            var props = bytes[offset++];
            var dnssecEnabled = (props & 0x01) != 0;
            var noLog = (props & 0x02) != 0;
            var noFilter = (props & 0x04) != 0;

            string? hostname = null;
            string? path = null;
            string? ipAddress = null;
            int? port = null;

            switch (protocol)
            {
                case DnsProtocol.DoH:
                    // DoH stamp format: [1-byte protocol][1-byte props][VLP addr][VLP hashes][VLP hostname][VLP path]
                    // Some stamps (like NextDNS) may have extra padding/zeros - try standard parsing first
                    ipAddress = ReadVlpString(bytes, ref offset);
                    var hashes = ReadVlpData(bytes, ref offset); // Skip hashes
                    hostname = ReadVlpString(bytes, ref offset);
                    path = ReadVlpString(bytes, ref offset);

                    // Fallback: if hostname is empty, scan for it (some stamps have extra zeros)
                    if (string.IsNullOrEmpty(hostname))
                    {
                        (hostname, path) = ScanForHostnameAndPath(bytes);
                        Console.WriteLine($"[DnsStamp] DoH fallback scan found: hostname={hostname}, path={path}");
                    }
                    break;

                case DnsProtocol.DoT:
                    // DoT stamp format: [1-byte protocol][1-byte props][VLP addr][VLP hashes][VLP hostname]
                    ipAddress = ReadVlpString(bytes, ref offset);
                    hashes = ReadVlpData(bytes, ref offset); // Skip hashes
                    hostname = ReadVlpString(bytes, ref offset);
                    port = 853;
                    break;

                case DnsProtocol.DNSCrypt:
                    // DNSCrypt has different format
                    ipAddress = ReadVlpString(bytes, ref offset);
                    var publicKey = ReadVlpData(bytes, ref offset);
                    var providerName = ReadVlpString(bytes, ref offset);
                    hostname = providerName;
                    break;

                case DnsProtocol.DoQ:
                    // DoQ (DNS over QUIC)
                    ipAddress = ReadVlpString(bytes, ref offset);
                    hashes = ReadVlpData(bytes, ref offset);
                    hostname = ReadVlpString(bytes, ref offset);
                    port = 8853;
                    break;
            }

            // Parse port from IP address if present (e.g., "1.2.3.4:443")
            if (!string.IsNullOrEmpty(ipAddress) && ipAddress.Contains(':'))
            {
                var parts = ipAddress.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort))
                {
                    ipAddress = parts[0];
                    port = parsedPort;
                }
            }

            // Identify provider
            var provider = !string.IsNullOrEmpty(hostname)
                ? DohProviderRegistry.IdentifyProvider(hostname)
                : null;

            return new DnsStampInfo
            {
                Protocol = protocol,
                ProtocolName = GetProtocolName(protocol),
                Hostname = hostname,
                Path = path,
                IpAddress = ipAddress,
                Port = port,
                DnssecEnabled = dnssecEnabled,
                NoLogging = noLog,
                NoFiltering = noFilter,
                ProviderInfo = provider,
                RawStamp = stamp
            };
        }
        catch
        {
            // Invalid stamp format
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string base64Url)
    {
        // Convert URL-safe base64 to standard base64
        var base64 = base64Url
            .Replace('-', '+')
            .Replace('_', '/');

        // Add padding if needed
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        return Convert.FromBase64String(base64);
    }

    private static string ReadVlpString(byte[] bytes, ref int offset)
    {
        if (offset >= bytes.Length)
            return string.Empty;

        var length = bytes[offset++];
        if (length == 0 || offset + length > bytes.Length)
            return string.Empty;

        var str = Encoding.UTF8.GetString(bytes, offset, length);
        offset += length;
        return str;
    }

    /// <summary>
    /// Read a VLP (variable length packed) vector - multiple length-prefixed items terminated by 0
    /// This is used for hashes which can have multiple entries
    /// </summary>
    private static byte[] ReadVlpVector(byte[] bytes, ref int offset)
    {
        var result = new List<byte>();

        while (offset < bytes.Length)
        {
            var length = bytes[offset++];
            if (length == 0)
                break; // 0 length = terminator

            if (offset + length > bytes.Length)
                break;

            // Add the length byte and data
            result.Add(length);
            for (int i = 0; i < length; i++)
                result.Add(bytes[offset++]);
        }

        return result.ToArray();
    }

    private static byte[] ReadVlpData(byte[] bytes, ref int offset)
    {
        if (offset >= bytes.Length)
            return Array.Empty<byte>();

        var length = bytes[offset++];
        if (length == 0 || offset + length > bytes.Length)
            return Array.Empty<byte>();

        var data = new byte[length];
        Array.Copy(bytes, offset, data, 0, length);
        offset += length;
        return data;
    }

    /// <summary>
    /// Fallback: scan bytes for hostname and path pattern when standard parsing fails.
    /// Looks for a length byte (5-50) followed by ASCII text containing a dot.
    /// </summary>
    private static (string? hostname, string? path) ScanForHostnameAndPath(byte[] bytes)
    {
        string? hostname = null;
        string? path = null;

        for (int i = 2; i < bytes.Length - 5; i++)
        {
            var len = bytes[i];
            // Look for reasonable hostname length (5-50 chars)
            if (len >= 5 && len <= 50 && i + 1 + len <= bytes.Length)
            {
                var candidate = Encoding.ASCII.GetString(bytes, i + 1, len);
                // Valid hostname should contain a dot and be printable ASCII
                if (candidate.Contains('.') && candidate.All(c => c >= 32 && c < 127))
                {
                    hostname = candidate;
                    var pathOffset = i + 1 + len;
                    if (pathOffset < bytes.Length)
                    {
                        var pathLen = bytes[pathOffset];
                        if (pathLen > 0 && pathLen < 100 && pathOffset + 1 + pathLen <= bytes.Length)
                        {
                            path = Encoding.ASCII.GetString(bytes, pathOffset + 1, pathLen);
                        }
                    }
                    break;
                }
            }
        }

        return (hostname, path);
    }

    private static string GetProtocolName(DnsProtocol protocol) => protocol switch
    {
        DnsProtocol.DNSCrypt => "DNSCrypt",
        DnsProtocol.DoH => "DNS-over-HTTPS",
        DnsProtocol.DoT => "DNS-over-TLS",
        DnsProtocol.DoQ => "DNS-over-QUIC",
        DnsProtocol.ODoH => "Oblivious DoH",
        DnsProtocol.DNSCryptRelay => "DNSCrypt Relay",
        DnsProtocol.ODoHRelay => "ODoH Relay",
        _ => "Unknown"
    };
}

/// <summary>
/// Decoded DNS stamp information
/// </summary>
public class DnsStampInfo
{
    public DnsStampDecoder.DnsProtocol Protocol { get; init; }
    public required string ProtocolName { get; init; }
    public string? Hostname { get; init; }
    public string? Path { get; init; }
    public string? IpAddress { get; init; }
    public int? Port { get; init; }
    public bool DnssecEnabled { get; init; }
    public bool NoLogging { get; init; }
    public bool NoFiltering { get; init; }
    public DohProviderInfo? ProviderInfo { get; init; }
    public required string RawStamp { get; init; }

    /// <summary>
    /// Get a display-friendly summary
    /// </summary>
    public string GetDisplaySummary()
    {
        var provider = ProviderInfo?.Name ?? Hostname ?? "Unknown";
        var features = new List<string>();

        if (DnssecEnabled) features.Add("DNSSEC");
        if (NoLogging) features.Add("No-Log");
        if (!NoFiltering && ProviderInfo?.SupportsFiltering == true) features.Add("Filtered");

        var featuresStr = features.Count > 0 ? $" [{string.Join(", ", features)}]" : "";
        return $"{provider} ({ProtocolName}){featuresStr}";
    }
}
