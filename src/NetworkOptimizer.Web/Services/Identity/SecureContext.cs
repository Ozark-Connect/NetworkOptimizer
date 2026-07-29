namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// WebAuthn secure-context detection (design doc 02): passkeys require HTTPS or localhost. Many LAN
/// installs run plain <c>http://host:8042</c>, where the passkey UI must explain and point at the
/// reverse-proxy docs rather than half-render a broken ceremony. TOTP remains the MFA floor there.
/// </summary>
public static class SecureContext
{
    /// <summary>True when the request is a WebAuthn-eligible secure context (HTTPS, or a loopback host).</summary>
    public static bool IsSecure(HttpContext context)
    {
        if (context.Request.IsHttps)
            return true;

        // If a reverse proxy terminated TLS, honor the forwarded scheme.
        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
        if (string.Equals(forwardedProto, "https", StringComparison.OrdinalIgnoreCase))
            return true;

        var host = context.Request.Host.Host;
        return host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    /// <summary>The canonical RP ID (host only) used for passkey registration/assertion.</summary>
    public static string RpId(HttpContext context) => context.Request.Host.Host;
}
