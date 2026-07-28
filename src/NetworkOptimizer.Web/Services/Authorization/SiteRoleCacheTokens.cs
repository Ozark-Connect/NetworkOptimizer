using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Authorization;

/// <summary>
/// Owns the cancellation tokens every cached effective-role and authorized-slug entry is tied to, so
/// anything that changes the answer can drop them without knowing which sites or users are cached.
///
/// It is its own singleton rather than state on <see cref="EffectiveSiteRoleResolver"/> because the
/// resolver depends on <see cref="Identity.IAuthPolicyOptions"/>, and the site restriction toggle -
/// which changes the answer for every non-Admin on every site at once - lives there. Depending on the
/// resolver from the policy options would be a cycle; depending on the tokens is not.
/// </summary>
public sealed class SiteRoleCacheTokens
{
    /// <summary>
    /// Acquiring a token has to be ATOMIC. <c>IMemoryCache.GetOrCreate</c> is not: it reads, runs the
    /// factory, then commits, so two callers that both miss each build a source and each keeps its
    /// own, while only the last to commit is stored. Every entry written against the loser is then
    /// tied to a source nothing can ever find - which is how a revoked site went on being listed for
    /// a user until the ten-minute expiry, while the gate checks, written outside that burst, refused
    /// it correctly. The burst is not hypothetical: a membership change invalidates and then
    /// broadcasts, and every open circuit and in-flight request rebuilds this user's set at once.
    ///
    /// GetOrAdd may run its factory more than once under contention, but it returns the SAME stored
    /// source to every caller, which is the property that was missing.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new();

    private const string RegistryTokenKey = "siterole-token:site-registry";

    private static string UserTokenKey(string userId) => $"siterole-token:{userId}";

    /// <summary>The token every entry for one user is tied to.</summary>
    public CancellationToken ForUser(string userId) => Token(UserTokenKey(userId));

    /// <summary>The token every cached entry is tied to, whoever it belongs to.</summary>
    public CancellationToken ForRegistry() => Token(RegistryTokenKey);

    /// <summary>Drops everything cached for one user (their memberships changed).</summary>
    public void InvalidateUser(string userId) => Expire(UserTokenKey(userId));

    /// <summary>
    /// Drops everything cached for everyone. Used when the site registry changes, and when the site
    /// restriction is toggled - that one setting decides whether a global Operator or Viewer role
    /// reaches every site, so leaving the cache alone means the change appears to do nothing at all
    /// until the ten-minute expiry.
    /// </summary>
    public void InvalidateAll() => Expire(RegistryTokenKey);

    private CancellationToken Token(string key)
        => _tokens.GetOrAdd(key, _ => new CancellationTokenSource()).Token;

    private void Expire(string tokenKey)
    {
        if (!_tokens.TryRemove(tokenKey, out var source))
            return;

        source.Cancel();
        source.Dispose();
    }
}
