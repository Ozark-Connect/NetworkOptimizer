using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// The public origin federation hands to an identity provider - OIDC redirect_uri, SAML EntityId and
/// ACS URL (design docs 02/03). Building these from the incoming request gives plain HTTP on 8042
/// behind a proxy: a URL that does not work and does not match what the operator registered.
///
/// The tiers live in <see cref="CanonicalBaseUrlProvider"/>, shared with the canonical-host redirect
/// and the agent URL, and this takes the callback rung - the whole ladder including HOST_IP. See that
/// class for why each caller stops where it does.
///
/// X-Forwarded-Proto / X-Forwarded-Host are deliberately NOT read. Forwarded headers are opt-in through
/// TRUSTED_PROXIES (Program.cs) precisely because an unvalidated forwarded host is attacker-chosen;
/// where they ARE trusted, UseForwardedHeaders has already rewritten Request.Scheme/Host by the time
/// this runs, so the request fallback picks them up having been validated rather than around the
/// validation. Reading the raw headers let a spoofed X-Forwarded-Host choose the SAML audience the SP
/// would accept.
/// </summary>
public interface ICanonicalOrigin
{
    /// <summary>Absolute origin, e.g. <c>https://optimizer.example.com</c> (no trailing slash).</summary>
    string Resolve(HttpContext context);

    /// <summary>Builds an absolute callback URI at <paramref name="path"/> under the canonical origin.</summary>
    string CallbackUri(HttpContext context, string path);

    /// <summary>
    /// The declared origin, for callers with no request to fall back on - the OIDC options factory.
    /// Null when nothing is declared, leaving the caller to let the request decide.
    /// </summary>
    string? Configured { get; }

    /// <summary>An absolute URL for a path under the declared origin, or null when none is declared.</summary>
    string? ConfiguredUriFor(string path);
}

/// <inheritdoc />
public sealed class CanonicalOrigin : ICanonicalOrigin
{
    private readonly CanonicalBaseUrlProvider _canonical;

    public CanonicalOrigin(CanonicalBaseUrlProvider canonical) => _canonical = canonical;

    /// <inheritdoc />
    public string? Configured => _canonical.UrlForCallbacks;

    /// <inheritdoc />
    public string? ConfiguredUriFor(string path) => _canonical.CallbackUriFor(path);

    /// <inheritdoc />
    public string Resolve(HttpContext context)
        => Configured ?? $"{context.Request.Scheme}://{context.Request.Host.Value}".TrimEnd('/');

    /// <inheritdoc />
    public string CallbackUri(HttpContext context, string path)
        => Resolve(context) + (path.StartsWith('/') ? path : "/" + path);
}
