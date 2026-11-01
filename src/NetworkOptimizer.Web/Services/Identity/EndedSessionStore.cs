using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models.Identity;

namespace NetworkOptimizer.Web.Services.Identity;

/// <summary>
/// Signed-out logins, asked on every authenticated request. The application cookie is a
/// self-contained ticket, so signing out only deletes the browser's own copy; a copy elsewhere would
/// keep authenticating for the rest of its lifetime unless the server refuses its login here.
/// <para>
/// Held in memory for the per-request check and kept in the auth database so a restart does not
/// bring signed-out logins back. Rows outlive every cookie of their login and are then pruned.
/// </para>
/// </summary>
public sealed class EndedSessionStore
{
    private readonly IDbContextFactory<AuthDbContext> _dbFactory;
    private readonly ILogger<EndedSessionStore> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _ended = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private volatile bool _loaded;

    public EndedSessionStore(IDbContextFactory<AuthDbContext> dbFactory, ILogger<EndedSessionStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>
    /// Whether this login was signed out. A login with no id (none recorded yet) is never ended. If the
    /// database cannot be read, this answers no for the request and tries again on the next: a database
    /// hiccup must not sign out every user, and the security stamp still guards the rest.
    /// </summary>
    public async ValueTask<bool> IsEndedAsync(string? loginId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(loginId)) return false;
        if (!await TryLoadAsync(ct)) return false;
        return _ended.TryGetValue(loginId, out var expires) && expires > DateTime.UtcNow;
    }

    /// <summary>Ends a login: every cookie carrying it is refused from now on, wherever it is.</summary>
    /// <param name="expiresAt">When no cookie of this login could still be valid.</param>
    public async Task EndAsync(string loginId, string userId, DateTime expiresAt, CancellationToken ct = default)
    {
        await TryLoadAsync(ct);
        _ended[loginId] = expiresAt;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        if (await db.EndedSessions.FindAsync([loginId], ct) is null)
            db.EndedSessions.Add(new EndedSession { LoginId = loginId, UserId = userId, EndedAt = now, ExpiresAt = expiresAt });
        // Pruned here rather than on a timer: sign-outs are rare, and a stale row is harmless.
        await db.EndedSessions.Where(s => s.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Loads the persisted ended logins once. False when the database could not be read.</summary>
    private async Task<bool> TryLoadAsync(CancellationToken ct)
    {
        if (_loaded) return true;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded) return true;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var row in await db.EndedSessions.AsNoTracking().Where(s => s.ExpiresAt >= now).ToListAsync(ct))
                _ended[row.LoginId] = DateTime.SpecifyKind(row.ExpiresAt, DateTimeKind.Utc);
            _loaded = true;
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not load ended sessions; retrying on the next request");
            return false;
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
