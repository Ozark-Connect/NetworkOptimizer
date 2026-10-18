namespace NetworkOptimizer.Threats.Interfaces;

/// <summary>
/// An HTTP client that reaches addresses inside a site: directly for a site this server sits in,
/// through the site's On-Site Agent tunnel for one it reaches that way. A site's WAF is read with
/// it, so "http://127.0.0.1:8044" names the WAF beside that site's agent. Implemented by the web
/// host, which owns the tunnel.
/// </summary>
public interface ISiteHttpClientProvider
{
    /// <summary>The client for a site (null = default site). Callers must not dispose it.</summary>
    Task<HttpClient> GetClientAsync(string? site, CancellationToken cancellationToken);
}
