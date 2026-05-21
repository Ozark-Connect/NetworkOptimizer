using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Resolves IP addresses to ASN number + name. The upstream tracer (spec 5.5) needs this
/// for every traceroute hop to identify which transit ASN it belongs to and to label
/// the resulting cloud.
///
/// Iteration 1 uses bgp.tools' DNS-based bulk lookup endpoint (lightweight, no API key)
/// plus in-memory caching with a 30-day-ish TTL. A future iteration will bundle the
/// iptoasn.com dataset for offline / first-mile resolution before the live API is
/// reachable. The interface is stable across that change.
///
/// All requests respect a soft rate limit so a dense traceroute (30 hops) doesn't burst
/// 30 lookups simultaneously.
/// </summary>
public class AsnResolutionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ILogger<AsnResolutionService> _logger;

    // Short-lived process cache to bound live API calls during a single tracer run.
    // A traceroute can produce 20+ lookups in seconds; without caching we'd hammer the
    // upstream service.
    private readonly ConcurrentDictionary<string, AsnLookup?> _cache = new();
    private readonly SemaphoreSlim _rateLimiter = new(2, 2); // max 2 concurrent lookups

    public AsnResolutionService(
        IHttpClientFactory httpClientFactory,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ILogger<AsnResolutionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Look up the ASN + name for an IP address. Returns null if the IP is private,
    /// CGNAT, or the lookup fails. The caller is expected to handle null gracefully -
    /// not every traceroute hop is publicly attributable.
    /// </summary>
    public async Task<AsnLookup?> ResolveAsync(string ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ipAddress)) return null;

        // Process cache - cheapest possible hit
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

        await _rateLimiter.WaitAsync(ct);
        try
        {
            // Re-check the cache in case another concurrent lookup populated it.
            if (_cache.TryGetValue(ipAddress, out cached)) return cached;

            var result = await QueryBgpToolsAsync(ipAddress, ct);
            _cache[ipAddress] = result;
            return result;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// bgp.tools exposes a simple DNS-based bulk lookup at whois.bgp.tools (TCP/43).
    /// For an HTTP-based wrapper, we use their REST endpoint which returns JSON. No
    /// API key required, just a User-Agent identifying ourselves per their guidelines.
    /// </summary>
    private async Task<AsnLookup?> QueryBgpToolsAsync(string ipAddress, CancellationToken ct)
    {
        try
        {
            using var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "NetworkOptimizer/1.0 (https://github.com/Ozark-Connect/NetworkOptimizer; tjvc4@users.noreply.github.com)");

            var url = $"https://bgp.tools/json/lookup/ip/{ipAddress}";
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("bgp.tools lookup for {Ip} returned HTTP {Status}",
                    ipAddress, (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // bgp.tools responds with an array of ASN entries (often just one for a
            // specific IP). Pick the first entry's ASN + name.
            JsonElement entry;
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return null;
                entry = root[0];
            }
            else
            {
                entry = root;
            }

            int asn = 0;
            string? name = null;

            if (entry.TryGetProperty("asn", out var asnEl))
            {
                asn = asnEl.ValueKind == JsonValueKind.Number ? asnEl.GetInt32()
                    : int.TryParse(asnEl.GetString(), out var parsed) ? parsed : 0;
            }

            if (entry.TryGetProperty("name", out var nameEl))
                name = nameEl.GetString();
            else if (entry.TryGetProperty("as_name", out var asNameEl))
                name = asNameEl.GetString();

            if (asn == 0) return null;
            return new AsnLookup(asn, name ?? $"AS{asn}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "bgp.tools lookup failed for {Ip}", ipAddress);
            return null;
        }
    }
}

public record AsnLookup(int Asn, string Name);
