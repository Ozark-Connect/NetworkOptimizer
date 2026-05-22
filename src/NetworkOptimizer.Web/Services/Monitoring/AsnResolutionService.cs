using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NetworkOptimizer.Threats.Enrichment;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Resolves IP addresses to ASN number + name. The upstream tracer (spec 5.5) needs this
/// for every traceroute hop to identify which transit ASN it belongs to and to label
/// the resulting cloud.
///
/// Primary source is the offline GeoLite2-ASN MaxMind database (already loaded by
/// <see cref="GeoEnrichmentService"/> for threat enrichment). In-memory lookup, no
/// network dependency, no rate limit, covers essentially every routed IPv4 prefix.
///
/// Fallback is bgp.tools' bulk WHOIS service on TCP/43 (the only programmatic IP-to-ASN
/// interface they actually publish - https://bgp.tools/kb/api). Used when GeoLite2
/// can't answer (very new prefix, or the bundled DB hasn't refreshed) or hasn't been
/// loaded at all.
/// </summary>
public class AsnResolutionService
{
    private readonly GeoEnrichmentService _geo;
    private readonly ILogger<AsnResolutionService> _logger;

    // Per-process cache - a single tracer run can produce 20+ lookups in seconds.
    private readonly ConcurrentDictionary<string, AsnLookup?> _cache = new();

    // Soft rate limiter on the whois fallback. The TCP/43 service has no documented
    // limit, but we still don't want a 30-hop trace bursting 30 concurrent sockets.
    private readonly SemaphoreSlim _whoisLimiter = new(2, 2);

    public AsnResolutionService(GeoEnrichmentService geo, ILogger<AsnResolutionService> logger)
    {
        _geo = geo;
        _logger = logger;
    }

    /// <summary>
    /// Look up the ASN + name for an IP address. Returns null if the IP is private,
    /// CGNAT, or both the offline DB and online fallback fail. The caller is expected
    /// to handle null gracefully - not every traceroute hop is publicly attributable.
    /// </summary>
    public async Task<AsnLookup?> ResolveAsync(string ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ipAddress)) return null;
        if (_cache.TryGetValue(ipAddress, out var cached)) return cached;

        // Skip non-public addresses - they can't have ASN attribution and we'd just
        // burn an upstream call.
        var classified = NetworkOptimizer.Core.Helpers.NetworkUtilities
            .ClassifyPublicAddress(ipAddress);
        if (classified != NetworkOptimizer.Core.Helpers.PublicAddressClass.PublicIPv4
            && classified != NetworkOptimizer.Core.Helpers.PublicAddressClass.Cgnat)
        {
            _cache[ipAddress] = null;
            return null;
        }

        // Primary: offline GeoLite2-ASN. In-memory, free of rate-limit / network risk.
        if (_geo.IsAsnAvailable)
        {
            var enriched = _geo.Enrich(ipAddress);
            if (enriched.Asn.HasValue && enriched.Asn.Value > 0)
            {
                var name = AsnNameCleanup.Clean(enriched.AsnOrg) ?? $"AS{enriched.Asn.Value}";
                var hit = new AsnLookup(enriched.Asn.Value, name);
                _cache[ipAddress] = hit;
                return hit;
            }
        }

        // Fallback: bgp.tools whois on TCP/43.
        await _whoisLimiter.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(ipAddress, out cached)) return cached;
            var result = await QueryBgpToolsWhoisAsync(ipAddress, ct);
            _cache[ipAddress] = result;
            return result;
        }
        finally
        {
            _whoisLimiter.Release();
        }
    }

    /// <summary>
    /// Query bgp.tools' single-IP whois (TCP/43). Sends " -v &lt;ip&gt;" (leading space
    /// matters - the dash isn't a CLI flag, it's part of the wire payload) and parses
    /// the pipe-delimited row.
    /// Example response:
    ///   AS      | IP       | BGP Prefix  | CC | Registry | Allocated  | AS Name
    ///   13335   | 1.1.1.1  | 1.1.1.0/24  | US | arin     | 2010-07-14 | CLOUDFLARENET
    /// </summary>
    private async Task<AsnLookup?> QueryBgpToolsWhoisAsync(string ipAddress, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync("bgp.tools", 43, connectCts.Token);

            using var stream = tcp.GetStream();
            var query = Encoding.ASCII.GetBytes($" -v {ipAddress}\r\n");
            await stream.WriteAsync(query, ct);

            using var reader = new StreamReader(stream, Encoding.ASCII);
            string? line;
            // Skip the header line; first data row carries the answer.
            bool headerSkipped = false;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!headerSkipped)
                {
                    headerSkipped = true;
                    continue;
                }
                var parts = line.Split('|');
                if (parts.Length < 7) continue;
                if (!int.TryParse(parts[0].Trim(), out var asn) || asn <= 0) continue;
                var name = AsnNameCleanup.Clean(parts[6]);
                return new AsnLookup(asn, string.IsNullOrEmpty(name) ? $"AS{asn}" : name);
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "bgp.tools whois lookup failed for {Ip}", ipAddress);
            return null;
        }
    }
}

public record AsnLookup(int Asn, string Name);

internal static class AsnNameCleanup
{
    // Strip the most common corporate-form suffixes off the tail of an ASN name
    // ("Cloudflare, Inc." -> "Cloudflare", "Akamai International B.V." -> "Akamai
    // International"). Handles a trailing comma before the suffix and the most
    // common US / EU / UK / Nordic forms. Run iteratively because some names
    // have stacked suffixes (e.g. "Foo Holdings Ltd LLC").
    private static readonly Regex SuffixPattern = new(
        @"\s*,?\s+(LLC|L\.L\.C\.?|Inc\.?|Incorporated|Corp\.?|Corporation|Co\.?|Company|Ltd\.?|Limited|B\.V\.?|BV|AB|AG|GmbH|S\.A\.?S?\.?|S\.r\.l\.?|SA|PLC|Pte\.?|N\.V\.?|NV|OY|OYJ)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = raw.Trim();
        for (var i = 0; i < 4; i++) // bounded loop, stops naturally when nothing matches
        {
            var next = SuffixPattern.Replace(s, string.Empty).TrimEnd(',', ' ');
            if (next.Length == 0 || next == s) break;
            s = next;
        }
        return s;
    }
}
