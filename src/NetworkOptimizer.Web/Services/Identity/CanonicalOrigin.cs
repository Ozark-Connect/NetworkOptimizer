namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Resolves the canonical public origin (scheme + host) used for federation redirect/callback URIs and
/// cookie decisions behind a reverse proxy (design docs 02/03). Prefers an explicit configured origin
/// (the <c>REVERSE_PROXIED_HOST_NAME</c> pattern), then the forwarded headers, then the request itself.
/// </summary>
public interface ICanonicalOrigin
{
    /// <summary>Absolute origin, e.g. <c>https://optimizer.example.com</c> (no trailing slash).</summary>
    string Resolve(HttpContext context);

    /// <summary>Builds an absolute callback URI at <paramref name="path"/> under the canonical origin.</summary>
    string CallbackUri(HttpContext context, string path);
}

/// <inheritdoc />
public sealed class CanonicalOrigin : ICanonicalOrigin
{
    private readonly ISystemSettingsService _settings;

    public CanonicalOrigin(ISystemSettingsService settings) => _settings = settings;

    public string Resolve(HttpContext context)
    {
        // Explicit configured origin wins (fleet/reverse-proxy installs set this).
        var configured = _settings.GetGlobalAsync("app.canonical_origin").GetAwaiter().GetResult();
        if (!string.IsNullOrEmpty(configured))
            return configured.TrimEnd('/');

        var envHost = Environment.GetEnvironmentVariable("REVERSE_PROXIED_HOST_NAME");
        if (!string.IsNullOrEmpty(envHost))
            return $"https://{envHost}".TrimEnd('/');

        var scheme = context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? context.Request.Scheme;
        var host = context.Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? context.Request.Host.Value;
        return $"{scheme}://{host}".TrimEnd('/');
    }

    public string CallbackUri(HttpContext context, string path)
        => Resolve(context) + (path.StartsWith('/') ? path : "/" + path);
}
