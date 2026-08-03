namespace NetworkOptimizer.Web.Services;

internal static class CanonicalHostRedirectPolicy
{
    /// <summary>
    /// Machine-to-machine endpoints must remain reachable on the local listener even when the
    /// browser-facing application redirects other requests to its configured canonical host.
    /// </summary>
    internal static bool ShouldBypass(HttpRequest request)
    {
        if (request.Path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
            return true;

        return request.ContentType?.StartsWith(
            "application/grpc",
            StringComparison.OrdinalIgnoreCase) == true;
    }
}
