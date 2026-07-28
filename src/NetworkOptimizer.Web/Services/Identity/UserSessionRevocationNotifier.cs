namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Announces that one account's live sessions have been revoked - disabled, deleted, or signed out
/// everywhere - so the circuits belonging to it go now rather than at the next revalidation.
///
/// The security stamp already makes the cookie invalid the instant any of those happen, but a Blazor
/// circuit is a connection that outlives cookie checks: it keeps rendering until it revalidates, which
/// is a five-minute window. Losing a site already boots the circuit immediately through the site
/// registry broadcast, and losing the whole account is a stronger revocation than losing one site, so
/// it cannot be the slower of the two.
/// </summary>
public sealed class UserSessionRevocationNotifier
{
    /// <summary>
    /// Raised with the id of the account whose sessions have been revoked, and the session id spared -
    /// null when every session goes.
    /// </summary>
    public event Action<string, string?>? SessionsRevoked;

    /// <summary>
    /// Announces a revocation. Safe to call when nothing is listening.
    /// </summary>
    /// <param name="exceptSessionId">
    /// The <see cref="NetOptClaims.SessionId"/> of the session that asked for this, which keeps its
    /// place: a self-service revocation ends your OTHER sessions, not the one you are sitting in.
    /// An admin revoking someone else passes null - all of that account's sessions go.
    /// </param>
    public void NotifyRevoked(string userId, string? exceptSessionId = null)
        => SessionsRevoked?.Invoke(userId, exceptSessionId);
}
