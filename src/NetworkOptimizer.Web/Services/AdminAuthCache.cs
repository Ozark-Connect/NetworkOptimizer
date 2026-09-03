using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Process-wide cache of the resolved admin password hash and its source. Held as a
/// singleton so the 30s cache survives across requests - <see cref="AdminAuthService"/>
/// is request-scoped, so a per-instance cache reset every request and re-read the
/// database (and logged) on every authenticated call. The entry is swapped atomically
/// via a volatile reference so concurrent readers always see a consistent hash+source
/// pair; <see cref="RefreshGate"/> serializes refreshers so only one request hits the
/// database per expiry.
/// </summary>
public class AdminAuthCache
{
    private static readonly TimeSpan CacheTimeout = TimeSpan.FromSeconds(30);

    private sealed record Entry(string? Hash, AdminPasswordSource Source, DateTime RefreshedAt, bool Resolved);

    private volatile Entry _entry = new(null, AdminPasswordSource.None, DateTime.MinValue, false);

    /// <summary>Serializes cache refreshes so a stale cache triggers one DB read, not one per request.</summary>
    internal SemaphoreSlim RefreshGate { get; } = new(1, 1);

    /// <summary>The cached password hash (or "__ENV__" marker), null when unresolved.</summary>
    public string? PasswordHash => _entry.Hash;

    /// <summary>The cached password source.</summary>
    public AdminPasswordSource Source => _entry.Source;

    /// <summary>True while the cached entry is still within the refresh window.</summary>
    public bool IsFresh => DateTime.UtcNow - _entry.RefreshedAt < CacheTimeout;

    /// <summary>
    /// Whether a refresh has ever resolved a source. False means the settings could not be read
    /// at all, which is not the same as an install with no password - callers must not read an
    /// unresolved cache as "authentication disabled".
    /// </summary>
    public bool HasResolved => _entry.Resolved;

    /// <summary>Atomically replaces the cached entry and stamps it fresh.</summary>
    public void Store(string? hash, AdminPasswordSource source)
        => _entry = new Entry(hash, source, DateTime.UtcNow, true);

    /// <summary>Forces the next access to refresh (e.g. after the password is changed).</summary>
    public void Invalidate()
        => _entry = _entry with { RefreshedAt = DateTime.MinValue };
}
