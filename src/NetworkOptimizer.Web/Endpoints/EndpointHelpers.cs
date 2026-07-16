using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Shared helpers used across endpoint groups.
/// </summary>
public static class EndpointHelpers
{
    /// <summary>
    /// Extracts client IP from request, handling X-Forwarded-For for proxied requests.
    /// An IPv4 client on a dual-stack socket arrives as ::ffff:a.b.c.d; it's normalized to
    /// plain IPv4 so it matches the UniFi client list and stores/displays cleanly.
    /// </summary>
    public static string GetClientIp(HttpContext context)
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            clientIp = forwardedFor.Split(',')[0].Trim();
        }
        return NetworkUtilities.NormalizeToIPv4String(clientIp) ?? "unknown";
    }
}
