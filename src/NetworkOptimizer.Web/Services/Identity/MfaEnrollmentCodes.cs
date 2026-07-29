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

    /// <summary>
    /// How long a set stays retrievable after it is first read. The page renders twice - once
    /// prerendered over HTTP, once when the interactive circuit starts - and both passes read this
    /// store, so a strictly single-read window would let the prerender swallow the codes and leave
    /// the visible render empty.
    /// </summary>
    private static readonly TimeSpan GraceAfterFirstRead = TimeSpan.FromSeconds(30);

    private sealed record Entry(IReadOnlyList<string> Codes, DateTime ExpiresUtc)
    {
        public DateTime? FirstReadUtc { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> _pending = new();

    /// <summary>Holds a freshly generated set for the user, replacing any previous one.</summary>
    public void Stash(string userId, IReadOnlyList<string> codes)
        => _pending[userId] = new Entry(codes, DateTime.UtcNow.Add(Lifetime));

    /// <summary>Drops the user's pending codes outright, once they say they have saved them.</summary>
    public void Discard(string userId) => _pending.TryRemove(userId, out _);

    /// <summary>
    /// The user's pending codes, or null when there are none left. Readable for a short grace period
    /// after the first read so the prerender and interactive passes agree, then evicted - a later
    /// visit or refresh must not redisplay them.
    /// </summary>
    public IReadOnlyList<string>? Take(string userId)
    {
        if (!_pending.TryGetValue(userId, out var entry))
            return null;

        var now = DateTime.UtcNow;
        if (now > entry.ExpiresUtc)
        {
            _pending.TryRemove(userId, out _);
            return null;
        }

        if (entry.FirstReadUtc is null)
        {
            entry.FirstReadUtc = now;
            return entry.Codes;
        }

        if (now - entry.FirstReadUtc.Value <= GraceAfterFirstRead)
            return entry.Codes;

        _pending.TryRemove(userId, out _);
        return null;
    }
}
