using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Resolves a hostname the way a client on the site would see it: from the site's own
/// vantage (the on-site agent when it owns path measurement, otherwise this server), the
/// same lookup Network Tools runs. The central server's resolver is the wrong view for a
/// LAN name - split-horizon DNS, or a name that only the site's resolver knows.
/// Answers are cached per site and name so a lookup does not ride along with every
/// speed test result.
/// </summary>
public class SiteVantageDnsResolver
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(1);
    // A lookup rides inside a speed test result post; a name the vantage cannot resolve must not
    // hold that for the OS resolver's own timeout.
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly SiteAgentCoverage _agentCoverage;
    private readonly AgentProbeService _agentProbe;
    private readonly LocalProbeExecutor _local;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SiteVantageDnsResolver> _logger;
    private readonly ConcurrentDictionary<(string Slug, string Host), (string? Ip, DateTime Expires)> _cache = new();

    public SiteVantageDnsResolver(
        SiteAgentCoverage agentCoverage,
        AgentProbeService agentProbe,
        LocalProbeExecutor local,
        ILoggerFactory loggerFactory)
    {
        _agentCoverage = agentCoverage;
        _agentProbe = agentProbe;
        _local = local;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SiteVantageDnsResolver>();
    }

    /// <summary>
    /// The IPv4 address <paramref name="host"/> resolves to from the site's vantage, or null
    /// when it does not resolve there. A literal IP is returned as-is.
    /// </summary>
    public async Task<string?> ResolveAsync(string slug, string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        host = host.Trim();
        if (IPAddress.TryParse(host, out _)) return host;

        var key = (slug, host.ToLowerInvariant());
        if (_cache.TryGetValue(key, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Ip;

        var ip = await LookupAsync(slug, host, ct);
        _cache[key] = (ip, DateTime.UtcNow + (ip != null ? HitTtl : MissTtl));
        return ip;
    }

    private async Task<string?> LookupAsync(string slug, string host, CancellationToken ct)
    {
        IProbeExecutor executor = _agentCoverage.AgentOwnsPathMeasurement(slug)
            ? new AgentProbeExecutor(_agentProbe, slug, _loggerFactory.CreateLogger<AgentProbeExecutor>())
            : _local;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);
            var result = await executor.LookupAsync(new ProbeTarget(host, ProbeMode.Icmp), ct: timeout.Token);
            // Kind is null when an old agent answered a lookup with a ping; that is not a DNS answer.
            if (result.Kind == null || !result.Success)
            {
                _logger.LogDebug("Site {Site} vantage {Vantage} did not resolve {Host}: {Error}",
                    slug, executor.Vantage, host, result.ErrorMessage ?? (result.NotFound ? "not found" : "no addresses"));
                return null;
            }
            var addresses = result.Addresses
                .Select(a => IPAddress.TryParse(a, out var parsed) ? parsed : null)
                .Where(a => a != null)
                .ToList();
            var ipv4 = addresses.FirstOrDefault(a => a!.AddressFamily == AddressFamily.InterNetwork);
            return (ipv4 ?? addresses.FirstOrDefault())?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Site {Site} lookup of {Host} failed", slug, host);
            return null;
        }
    }
}
