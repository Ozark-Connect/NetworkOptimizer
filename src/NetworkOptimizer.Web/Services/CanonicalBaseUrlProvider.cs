namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The address this install is reached at from outside, as the operator declared it. One tier ladder,
/// read from configuration at startup, with a named entry point per use - because the three callers
/// legitimately stop at different rungs, and re-deriving the ladder in each of them is how they drifted
/// apart in the first place.
///
///   REVERSE_PROXIED_HOST_NAME  ->  https://host        (no port; 443 implied)
///   HOST_NAME                  ->  http://host:8042
///   HOST_IP                    ->  http://ip:8042
/// </summary>
public sealed class CanonicalBaseUrlProvider
{
    /// <summary>
    /// Reverse-proxied only, so always HTTPS. Null otherwise, rather than degrading: agents require
    /// HTTPS, and http://host:8042 is not a valid agent endpoint.
    /// </summary>
    public string? HttpsUrl { get; }

    /// <summary>
    /// The declared host. Excludes HOST_IP, because the canonical-host redirect uses this and an
    /// install that sets only HOST_IP means to stay reachable at any address - forcing it onto one
    /// would be the opposite of what it asked for.
    /// </summary>
    public string? Url { get; }

    /// <summary>
    /// The whole ladder, HOST_IP included. For absolute URLs handed to a third party that will dial
    /// them back - OIDC redirect_uri, SAML EntityId and ACS. On an IP-only install the alternative is
    /// deriving them from whichever address a request arrived on, which varies and then does not match
    /// what the operator registered with their provider.
    /// </summary>
    public string? UrlForCallbacks { get; }

    public CanonicalBaseUrlProvider(IConfiguration configuration)
    {
        HttpsUrl = Normalize(configuration["REVERSE_PROXIED_HOST_NAME"], "https", port: null);
        Url = HttpsUrl ?? Normalize(configuration["HOST_NAME"], "http", port: "8042");
        UrlForCallbacks = Url ?? Normalize(configuration["HOST_IP"], "http", port: "8042");
    }

    /// <summary>An absolute callback URL for a path, or null when nothing is declared.</summary>
    public string? CallbackUriFor(string path)
    {
        if (UrlForCallbacks is null)
            return null;
        return string.IsNullOrEmpty(path)
            ? UrlForCallbacks
            : UrlForCallbacks + (path.StartsWith('/') ? path : "/" + path);
    }

    /// <summary>The settings are bare hosts; tolerate an operator who included the scheme anyway.</summary>
    private static string? Normalize(string? value, string scheme, string? port)
    {
        var host = value?.Trim();
        if (string.IsNullOrEmpty(host))
            return null;
        if (host.Contains("://"))
            return host.TrimEnd('/');
        return port is null ? $"{scheme}://{host}" : $"{scheme}://{host}:{port}";
    }
}
