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
        var isCgnat = classified == NetworkOptimizer.Core.Helpers.PublicAddressClass.Cgnat;
        if (classified != NetworkOptimizer.Core.Helpers.PublicAddressClass.PublicIPv4 && !isCgnat)
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

        // CGNAT stops at the offline database. The lookup above costs nothing and does sometimes
        // know shared space a carrier has registered, so it is worth asking; the network call is
        // not, because RFC 6598 space is not announced in BGP and whois has nothing to say about
        // it. That distinction matters at scale: whole first miles are built from this space - a
        // Starlink trace is mostly 100.64/10 - and each distinct hop was previously its own query,
        // which is what turned one rate-limited provider into a stalled discovery.
        if (isCgnat)
        {
            _cache[ipAddress] = null;
            return null;
        }

        // Fallback: bgp.tools whois on TCP/43.
        await _whoisLimiter.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(ipAddress, out cached)) return cached;
            // Logged per lookup, with how long it took: the offline database answers everything it
            // can before this point, so each of these is a public address it did not know. A run
            // that reaches the network repeatedly, or slowly, is worth seeing - previously a whois
            // call left no trace at all unless it threw, which is why a hang looked like a silent
            // stall rather than a lookup.
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var result = await QueryBgpToolsWhoisAsync(ipAddress, ct);
            _cache[ipAddress] = result;
            _logger.LogDebug("ASN: whois fallback for {Ip} took {Ms} ms -> {Result}",
                ipAddress,
                (int)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                result == null ? "no attribution" : $"AS{result.Asn}");
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
            // One budget for the WHOLE exchange, not just the connect. A rate-limited bgp.tools
            // accepts the connection and then says nothing, and the read carried no deadline of its
            // own - so it waited on the caller's token, held one of the two permits, and blocked
            // every later lookup behind it until the run was abandoned. Observed with both permits
            // held by sockets sitting at 0 bytes in either direction. A timeout lands in the catch
            // below and returns null, which is already what an unattributable hop looks like.
            using var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            exchangeCts.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync("bgp.tools", 43, exchangeCts.Token);

            using var stream = tcp.GetStream();
            var query = Encoding.ASCII.GetBytes($" -v {ipAddress}\r\n");
            await stream.WriteAsync(query, exchangeCts.Token);

            using var reader = new StreamReader(stream, Encoding.ASCII);
            string? line;
            string? firstLine = null;
            // Skip the header line; first data row carries the answer.
            bool headerSkipped = false;
            while ((line = await reader.ReadLineAsync(exchangeCts.Token)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                firstLine ??= line;
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

            // Nothing parsed, and the reasons look identical from the outside: bgp.tools genuinely
            // has no row for the prefix, it answered with a notice instead of data, or the format
            // moved. The reply itself separates them, so keep the first line of it.
            _logger.LogDebug("bgp.tools returned no usable row for {Ip}; first line of the reply: {Reply}",
                ipAddress,
                firstLine == null ? "(empty response)"
                    : firstLine.Length > 200 ? firstLine[..200] + "..." : firstLine);
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

/// <summary>
/// The resolve/display ASN-name cleaner. Runs on every IP-&gt;ASN resolve (<see cref="AsnResolutionService"/>)
/// and again when ISP Health renders a "Networks on Your Path" card, so brand overrides apply
/// to already-stored names without re-discovery. Strips legal-entity suffixes (LLC, Inc, AB,
/// B.V. ...) and applies exact-match brand overrides for the cases suffix-stripping can't infer -
/// a geographic or rebrand word, e.g. "Arelion Sweden" -&gt; "Arelion".
///
/// This is the LIGHTER of the two ASN-name cleaners. The heavier industry-suffix pass
/// (Communications, Telecom, Networks, Parent ...) is <see cref="NetworkOptimizer.Core.Helpers.NetworkFormatHelpers.CleanOrgName"/>,
/// which runs once at STORAGE time (auto-discovery via UpstreamTracerService.CleanAsnName, and
/// manual add via LatencyTargetsCard). A stored AsnName has therefore been through both passes;
/// this one re-runs cheaply at display purely so the brand overrides stay applied.
/// </summary>
internal static class AsnNameCleanup
{
    // Strip the most common corporate-form suffixes off the tail of an ASN name
    // ("Cloudflare, Inc." -> "Cloudflare", "Akamai International B.V." -> "Akamai
    // International"). Handles a trailing comma before the suffix and the most
    // common US / EU / UK / Nordic forms. Run iteratively because some names
    // have stacked suffixes (e.g. "Foo Holdings Ltd LLC").
    private static readonly Regex SuffixPattern = new(
        @"\s*,?\s+(LLC|L\.L\.C\.?|L\.C\.?|LC|L\.P\.?|LP|Inc\.?|Incorporated|Corp\.?|Corporation|Co\.?|Company|Ltd\.?|Limited|B\.V\.?|BV|AB|AG|GmbH|S\.A\.?S?\.?|S\.r\.l\.?|SA|PLC|Pte\.?|N\.V\.?|NV|OY|OYJ)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Specific brand overrides, applied AFTER suffix stripping. This is the ONLY place a name is
    // canonicalized on a non-suffix word (geography, legacy brand, rebrand) - the suffix strippers
    // (here and CleanOrgName) can't infer those. Deliberately exact-match and not a generic
    // geographic strip: a real ISP could legitimately be named "<x> Sweden". Keys are the
    // post-suffix-strip form (e.g. "Arelion Sweden AB" -> strip "AB" -> match "Arelion Sweden").
    private static readonly Dictionary<string, string> NameOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // Arelion (AS1299, ex-Telia Carrier) resolves as "Arelion Sweden AB"; show just "Arelion".
        ["Arelion Sweden"] = "Arelion",
    };

    // Brand tokens: collapse the whole name to this brand when it appears as a standalone word.
    // Unlike NameOverrides (exact-match, for a single geographic/rebrand word on one entity), this
    // is for a brand spread across many regional legal entities under different names - e.g.
    // "TELECOM ITALIA SPARKLE S.p.A.", "TI Sparkle Turkey ...", "TTi Sparkle Greece SA" all
    // canonicalize to "Sparkle". Use only for tokens distinctive enough that no unrelated ISP
    // shares them as a whole word ("Sparkle" won't match Cable One's "Sparklight").
    private static readonly string[] BrandTokens = { "Sparkle" };

    private static readonly Regex BrandTokenPattern = new(
        @"\b(" + string.Join('|', BrandTokens) + @")\b",
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
        var brand = BrandTokenPattern.Match(s);
        if (brand.Success)
            return BrandTokens.First(t => string.Equals(t, brand.Value, StringComparison.OrdinalIgnoreCase));
        return NameOverrides.TryGetValue(s, out var canonical) ? canonical : s;
    }
}
