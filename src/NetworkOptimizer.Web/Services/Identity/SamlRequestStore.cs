using Microsoft.Extensions.Caching.Memory;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Remembers the SAML AuthnRequests this server issued, and the assertions it has already accepted,
/// so a response can be tied to a request we actually made and no assertion can be used twice.
///
/// Server-side rather than cookie-only for two reasons. The correlation cookie needs SameSite=None,
/// which needs Secure, which needs HTTPS - so on a plain-HTTP install there is no cookie and the
/// check it carried simply did not happen. And a cookie proves the browser started a flow; it cannot
/// prove the ASSERTION has not been presented before, which is what the SAML profile requires of a
/// bearer assertion. This store answers both, on every install shape.
///
/// Entries are short-lived by design: an AuthnRequest that has not been answered within the window is
/// abandoned, and an assertion older than the window has already failed its own NotOnOrAfter.
/// </summary>
public sealed class SamlRequestStore
{
    private readonly IMemoryCache _cache;

    /// <summary>
    /// Long enough for a user to authenticate at the IdP (including an MFA prompt), short enough that
    /// an abandoned request does not stay usable. Matches the correlation cookie's lifetime.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public SamlRequestStore(IMemoryCache cache) => _cache = cache;

    private static string RequestKey(string scheme, string id) => $"saml:req:{scheme}:{id}";

    private static string AssertionKey(string scheme, string id) => $"saml:asrt:{scheme}:{id}";

    /// <summary>Records an AuthnRequest we are about to send, so its response can be recognised.</summary>
    public void RememberRequest(string scheme, string requestId)
    {
        if (!string.IsNullOrEmpty(requestId))
            _cache.Set(RequestKey(scheme, requestId), true, Window);
    }

    /// <summary>
    /// True when <paramref name="inResponseTo"/> names a request this server issued and has not
    /// already answered. Consumes it, so the same solicited assertion cannot be presented twice.
    /// </summary>
    public bool ConsumeRequest(string scheme, string? inResponseTo)
    {
        if (string.IsNullOrEmpty(inResponseTo))
            return false;

        var key = RequestKey(scheme, inResponseTo);
        if (!_cache.TryGetValue(key, out _))
            return false;

        _cache.Remove(key);
        return true;
    }

    /// <summary>
    /// True the first time an assertion id is seen, false every time after. Covers the IdP-initiated
    /// case, where there is no request to correlate against and single use is the only replay defence.
    /// </summary>
    public bool TryMarkAssertionSeen(string scheme, string? assertionId)
    {
        // No id means nothing can be de-duplicated. Refuse rather than wave it through: every real IdP
        // stamps one, and its absence is either a malformed response or a deliberately stripped one.
        if (string.IsNullOrEmpty(assertionId))
            return false;

        var key = AssertionKey(scheme, assertionId);
        if (_cache.TryGetValue(key, out _))
            return false;

        _cache.Set(key, true, Window);
        return true;
    }
}
