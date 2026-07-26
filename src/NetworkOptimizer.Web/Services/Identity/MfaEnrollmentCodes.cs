using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Carries the recovery codes minted at the end of TOTP enrollment from the endpoint that generates
/// them to the page that shows them, exactly once. Enrollment completes over HTTP so the auth cookie
/// can be re-issued, which means a redirect sits between the two - and recovery codes must not travel
/// in a URL, where they would land in browser history, proxy logs, and the referrer header.
/// </summary>
public sealed class MfaEnrollmentCodes
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Codes, DateTime ExpiresUtc)> _pending = new();

    /// <summary>Holds a freshly generated set for the user, replacing any previous one.</summary>
    public void Stash(string userId, IReadOnlyList<string> codes)
        => _pending[userId] = (codes, DateTime.UtcNow.Add(Lifetime));

    /// <summary>
    /// Returns and removes the user's pending codes, or null when there are none (or they expired).
    /// Single use: a refresh of the page must not redisplay them.
    /// </summary>
    public IReadOnlyList<string>? Take(string userId)
    {
        if (!_pending.TryRemove(userId, out var entry))
            return null;

        return entry.ExpiresUtc >= DateTime.UtcNow ? entry.Codes : null;
    }
}
