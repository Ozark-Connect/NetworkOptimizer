namespace NetworkOptimizer.UniFi;

/// <summary>
/// Thrown when the UniFi Console rejects a request with <c>api.err.NoPermission</c> (HTTP 403) -
/// the credentials are valid but the account/API key lacks the required access level. Re-authenticating
/// does not help, so callers that make mutative requests (e.g. triggering an RF spectrum scan) can
/// catch this and surface an actionable message instead of a generic failure.
/// </summary>
public class UniFiPermissionException : Exception
{
    public UniFiPermissionException(string message)
        : base(message)
    {
    }
}
