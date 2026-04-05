namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Shared helpers used across endpoint groups.
/// </summary>
public static class EndpointHelpers
{
    /// <summary>
    /// Extracts client IP from request, handling X-Forwarded-For for proxied requests.
    /// </summary>
    public static string GetClientIp(HttpContext context)
    {
        var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
        {
            clientIp = forwardedFor.Split(',')[0].Trim();
        }
        return clientIp;
    }
}
