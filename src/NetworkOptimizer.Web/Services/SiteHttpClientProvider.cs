using System.Collections.Concurrent;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// HTTP clients that reach addresses inside a site. A site whose devices are reached through its
/// agent gets a client whose every connection is dialed by that agent, so a URL like
/// http://127.0.0.1:8044 means the WAF beside the site's agent, not this server.
/// </summary>
public sealed class SiteHttpClientProvider : ISiteHttpClientProvider, IDisposable
{
    private readonly IHttpClientFactory _factory;
    private readonly SiteTunnelRouting _routing;
    private readonly ConcurrentDictionary<string, HttpClient> _tunneled = new();

    public SiteHttpClientProvider(IHttpClientFactory factory, SiteTunnelRouting routing)
    {
        _factory = factory;
        _routing = routing;
    }

    public async Task<HttpClient> GetClientAsync(string? site, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrEmpty(site) ? SiteManagementService.DefaultSiteSlug : site;
        if (!await _routing.IsViaAgentAsync(slug))
            return _factory.CreateClient("Waf");

        return _tunneled.GetOrAdd(slug, s =>
        {
            var dialer = _routing.DialerFor(s);
            var handler = new SocketsHttpHandler
            {
                // The agent dials the real host:port inside the site; nothing is resolved here.
                ConnectCallback = async (context, ct) =>
                    await dialer.DialAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            return new HttpClient(handler, disposeHandler: true);
        });
    }

    public void Dispose()
    {
        foreach (var client in _tunneled.Values)
            client.Dispose();
    }
}
