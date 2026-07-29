using System.Security.Claims;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Who is making the current call - a specific user, or a background/system actor. Snapshot taken
/// once per HTTP request (middleware) or per circuit (circuit init), never re-read from
/// <c>IHttpContextAccessor</c> mid-circuit (which is null/stale after the WebSocket starts).
/// </summary>
public sealed record CallerInfo
{
    /// <summary>True for scheduler/poller/background work; such calls skip authorization.</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// True when the install has authentication turned off entirely (no admin password configured).
    /// There is no principal to authorize in that mode, and the app has always let the local operator
    /// do everything - so gated calls skip authorization but are still audited, as "local".
    /// </summary>
    public bool AuthenticationDisabled { get; init; }

    /// <summary>The signed-in principal for a user caller (used by the gated interceptor for authorization); null for system.</summary>
    public ClaimsPrincipal? Principal { get; init; }

    /// <summary>Local user id for a user caller; null for system.</summary>
    public string? UserId { get; init; }

    /// <summary>Display name snapshot (e.g. "admin" or "system:scheduler:sqm").</summary>
    public string ActorName { get; init; } = "";

    /// <summary>Authentication method (password | totp | passkey | oidc:&lt;scheme&gt; | saml:&lt;scheme&gt; | system | recovery).</summary>
    public string? AuthMethod { get; init; }

    public string? SourceIp { get; init; }
    public string? UserAgent { get; init; }
    public string? CorrelationId { get; init; }

    /// <summary>Builds a system caller for an explicit background scope.</summary>
    public static CallerInfo System(string systemActor) => new()
    {
        IsSystem = true,
        ActorName = $"system:{systemActor}",
        AuthMethod = "system",
    };

    /// <summary>
    /// Builds the caller used when authentication is disabled for the install: an unauthenticated
    /// local operator with full access (the pre-identity behavior), still attributed in the audit log.
    /// </summary>
    public static CallerInfo LocalNoAuth(string? sourceIp, string? userAgent, string? correlationId) => new()
    {
        AuthenticationDisabled = true,
        ActorName = "local",
        AuthMethod = "none",
        SourceIp = sourceIp,
        UserAgent = Truncate(userAgent, 512),
        CorrelationId = correlationId,
    };

    /// <summary>
    /// An unauthenticated caller. Anonymous is a real caller, not missing plumbing: the login page runs
    /// a circuit, and anything gated it touches must be DENIED by authorization rather than throwing
    /// "no caller context is set", which points whoever reads it at BeginSystemScope for a problem that
    /// has nothing to do with background work.
    /// </summary>
    public static CallerInfo Anonymous(
        ClaimsPrincipal? principal, string? sourceIp, string? userAgent, string? correlationId)
        => new()
        {
            IsSystem = false,
            Principal = principal,
            ActorName = "anonymous",
            SourceIp = sourceIp,
            UserAgent = Truncate(userAgent, 512),
            CorrelationId = correlationId,
        };

    /// <summary>Builds a user caller from a signed-in principal plus request metadata.</summary>
    public static CallerInfo ForUser(ClaimsPrincipal user, string? sourceIp, string? userAgent, string? correlationId)
        => new()
        {
            IsSystem = false,
            Principal = user,
            UserId = user.FindFirstValue(ClaimTypes.NameIdentifier),
            ActorName = user.Identity?.Name ?? "unknown",
            AuthMethod = user.FindFirstValue(NetOptClaims.AuthMethod) ?? "password",
            SourceIp = sourceIp,
            UserAgent = Truncate(userAgent, 512),
            CorrelationId = correlationId,
        };

    private static string? Truncate(string? value, int max)
        => value is not null && value.Length > max ? value[..max] : value;
}

/// <summary>
/// Scoped ambient caller context (design doc 06). Populated per request/circuit for user calls, or
/// entered explicitly for background work via <see cref="BeginSystemScope"/>. A gated call made with
/// no caller set is a programming error and throws (via <see cref="Require"/>) - a forgotten system
/// scope is a loud failure, never a silent authorization bypass.
/// </summary>
public interface ICallerContext
{
    /// <summary>The current caller, or null if none has been established in this scope.</summary>
    CallerInfo? Current { get; }

    /// <summary>Sets the user caller for this scope (called by the request middleware / circuit init).</summary>
    void SetUser(CallerInfo caller);

    /// <summary>Enters an explicit system/background scope; dispose to restore the previous caller.</summary>
    IDisposable BeginSystemScope(string systemActor);

    /// <summary>Returns the current caller or throws if none is set (used by the gated interceptor).</summary>
    CallerInfo Require();
}

/// <inheritdoc />
public sealed class CallerContext : ICallerContext
{
    private CallerInfo? _current;

    /// <inheritdoc />
    public CallerInfo? Current => _current;

    /// <inheritdoc />
    public void SetUser(CallerInfo caller) => _current = caller;

    /// <inheritdoc />
    public IDisposable BeginSystemScope(string systemActor)
    {
        var previous = _current;
        _current = CallerInfo.System(systemActor);
        return new Restore(() => _current = previous);
    }

    /// <inheritdoc />
    public CallerInfo Require() => _current
        ?? throw new InvalidOperationException(
            "No caller context is set for a gated call. Background/system work must run inside " +
            "ICallerContext.BeginSystemScope(\"...\"); user calls are populated by the request/circuit.");

    private sealed class Restore : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;
        public Restore(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}
