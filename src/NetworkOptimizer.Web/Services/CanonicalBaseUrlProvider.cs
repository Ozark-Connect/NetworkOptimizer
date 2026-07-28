namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The address this install is reached at from outside, as the operator declared it. Anything that has
/// to hand an absolute URL to a third party - one it will be dialled back on - needs this rather than
/// the incoming request, because behind a reverse proxy the request arrives as plain HTTP on 8042 and
/// building from it produces a URL that does not work and, for OIDC, does not match what is registered
/// with the identity provider.
///
/// Same two tiers the canonical-host redirect in Program.cs uses, and deliberately the same order:
///
///   REVERSE_PROXIED_HOST_NAME  ->  https://host        (no port; 443 implied)
///   HOST_NAME                  ->  http://host:8042
///
/// HOST_IP is excluded, matching that redirect: it exists so an install stays reachable by any address,
/// so it is not a statement about the canonical one.
///
/// <see cref="AgentServerUrlProvider"/> deliberately implements only the first tier - agents require
/// HTTPS, so the plain-HTTP fallback is not a valid agent endpoint and it returns null instead. This
/// provider keeps the fallback, because a LAN install reached on http://host:8042 is a legitimate
/// deployment for everything that is not an agent.
/// </summary>
public sealed class CanonicalBaseUrlProvider
{
    /// <summary>The declared external base URL with no trailing slash, or null when none is declared.</summary>
    public string? Url { get; }

    public CanonicalBaseUrlProvider(IConfiguration configuration)
    {
        var proxied = configuration["REVERSE_PROXIED_HOST_NAME"]?.Trim();
        if (!string.IsNullOrEmpty(proxied))
        {
            // The setting is a bare host elsewhere in the app; tolerate an operator who included the
            // scheme anyway, the way AgentServerUrlProvider does.
            Url = (proxied.Contains("://") ? proxied : $"https://{proxied}").TrimEnd('/');
            return;
        }

        var host = configuration["HOST_NAME"]?.Trim();
        Url = string.IsNullOrEmpty(host)
            ? null
            : (host.Contains("://") ? host : $"http://{host}:8042").TrimEnd('/');
    }

    /// <summary>
    /// An absolute URL for a path on this install, or null when no canonical address is declared - in
    /// which case the caller should leave whatever it was going to do alone and let the request decide.
    /// </summary>
    public string? UrlFor(string path)
    {
        if (Url is null)
            return null;
        return string.IsNullOrEmpty(path) ? Url : $"{Url}/{path.TrimStart('/')}";
    }
}
