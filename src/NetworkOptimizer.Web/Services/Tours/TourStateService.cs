using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Identity;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Reads and writes per-subject tour state (TourStates) and the install-level version
/// stamps on AdminSettings. Always the main database via the singleton context factory:
/// a tour is about the product, not a site.
/// Source builds namespace every recorded id with "dev:" so a source-built test site
/// records and reads its own state without consuming what a release install would see.
/// A null subject (anonymous or system caller) reads an empty snapshot and writes nothing.
/// </summary>
public class TourStateService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly IDbContextFactory<AuthDbContext> _authDbFactory;
    private readonly ILogger<TourStateService> _logger;
    // One lock for every subject: it serializes each row's read-modify-write, prevents a
    // double insert on the unique Subject index, and covers the race on claiming the
    // legacy row. Never narrow it to a per-subject lock: two subjects can claim one row.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TourStateService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        IDbContextFactory<AuthDbContext> authDbFactory,
        ILogger<TourStateService> logger)
    {
        _dbFactory = dbFactory;
        _authDbFactory = authDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// The TourStates subject for <paramref name="caller"/>: the user id for a signed-in user,
    /// <see cref="TourState.DefaultSubject"/> when the install has authentication disabled, and
    /// null for anonymous and system callers. Anonymous never maps to the default row: on an
    /// install with authentication on, the login page would otherwise read and write it.
    /// </summary>
    public static string? SubjectFor(CallerInfo? caller)
    {
        if (caller == null || caller.IsSystem)
            return null;
        if (caller.AuthenticationDisabled)
            return TourState.DefaultSubject;
        return string.IsNullOrEmpty(caller.UserId) ? null : caller.UserId;
    }

    private const string DevPrefix = "dev:";

    private static string Ns(string id) => TourVersions.IsSourceBuild ? DevPrefix + id : id;

    private static string? StripNs(string id)
    {
        if (TourVersions.IsSourceBuild)
            return id.StartsWith(DevPrefix, StringComparison.Ordinal) ? id[DevPrefix.Length..] : null;
        return id.StartsWith(DevPrefix, StringComparison.Ordinal) ? null : id;
    }

    /// <summary>Snapshot of the subject's state with ids de-namespaced for the current build flavor.</summary>
    public class Snapshot
    {
        public HashSet<string> SeenStepIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DismissedTourIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tour id -> app versions in which it was included in an automatic modal offer.</summary>
        public Dictionary<string, List<string>> Offers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool ToursDisabled { get; init; }
    }

    public async Task<Snapshot> GetSnapshotAsync(string? subject)
    {
        if (subject == null)
            return new Snapshot();

        TourState? row;
        await using (var db = await _dbFactory.CreateDbContextAsync())
            row = await db.TourStates.AsNoTracking().FirstOrDefaultAsync(t => t.Subject == subject);
        // Claim on the first read too: the lone account's first offer decision must already
        // see the history it built before per-user state.
        if (row == null && subject != TourState.DefaultSubject)
            row = await ClaimOnReadAsync(subject);
        if (row == null)
            return new Snapshot();

        var snapshot = new Snapshot { ToursDisabled = row.ToursDisabled };
        foreach (var id in ParseArray(row.SeenTourSteps))
            if (StripNs(id) is { } s) snapshot.SeenStepIds.Add(s);
        foreach (var id in ParseArray(row.DismissedTours))
            if (StripNs(id) is { } s) snapshot.DismissedTourIds.Add(s);
        foreach (var (key, versions) in ParseMap(row.TourOffers))
            if (StripNs(key) is { } s) snapshot.Offers[s] = versions;
        return snapshot;
    }

    public Task RecordStepSeenAsync(string? subject, string stepId) => MutateAsync(subject, row =>
    {
        var seen = ParseArray(row.SeenTourSteps);
        if (seen.Add(Ns(stepId)))
            row.SeenTourSteps = JsonSerializer.Serialize(seen);
    });

    public Task RecordToursDismissedAsync(string? subject, IEnumerable<string> tourIds) => MutateAsync(subject, row =>
    {
        var dismissed = ParseArray(row.DismissedTours);
        foreach (var id in tourIds)
            dismissed.Add(Ns(id));
        row.DismissedTours = JsonSerializer.Serialize(dismissed);
    });

    public Task RecordOfferAsync(string? subject, IEnumerable<string> tourIds, string version) => MutateAsync(subject, row =>
    {
        var offers = ParseMap(row.TourOffers);
        foreach (var id in tourIds)
        {
            var key = Ns(id);
            if (!offers.TryGetValue(key, out var versions))
                offers[key] = versions = new List<string>();
            if (!versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                versions.Add(version);
        }
        row.TourOffers = JsonSerializer.Serialize(offers);
    });

    public Task SetToursDisabledAsync(string? subject, bool disabled) =>
        MutateAsync(subject, row => row.ToursDisabled = disabled);

    /// <summary>
    /// ?tour=reset: clears the subject's recorded tour state and the LastSeenAppVersion stamp
    /// so the automatic offer path (upgrade detection, merge, modal) fires again on the next
    /// Dashboard visit. FirstSeenVersion is deliberately preserved - it cannot be
    /// reconstructed, and losing it would permanently destroy Highlights eligibility.
    /// LastSeenAppVersion is install-level, so one user's reset re-arms upgrade detection
    /// for everyone; each other user's own offer history still decides what they are shown.
    /// </summary>
    public async Task ResetAsync(string? subject)
    {
        if (subject == null)
            return;
        await _writeLock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await FindOrClaimAsync(db, subject);
            if (row != null)
                db.TourStates.Remove(row);
            var admin = await db.AdminSettings.FirstOrDefaultAsync();
            if (admin != null)
            {
                admin.LastSeenAppVersion = null;
                admin.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();
            _logger.LogInformation("Guided tour state reset via ?tour=reset");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<string?> GetFirstSeenVersionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var admin = await db.AdminSettings.AsNoTracking().FirstOrDefaultAsync();
        return admin?.FirstSeenVersion;
    }

    private async Task MutateAsync(string? subject, Action<TourState> mutate)
    {
        if (subject == null)
            return;
        await _writeLock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await FindOrClaimAsync(db, subject);
            if (row == null)
            {
                // Subject set explicitly: the model default is DefaultSubject.
                row = new TourState { Subject = subject };
                db.TourStates.Add(row);
            }
            mutate(row);
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist tour state");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Claims the legacy row for <paramref name="subject"/> under the lock; null when it does not apply.</summary>
    private async Task<TourState?> ClaimOnReadAsync(string subject)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await FindOrClaimAsync(db, subject);
            if (row != null)
                await db.SaveChangesAsync();
            return row;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to claim tour state");
            return null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// The subject's tracked row, else the legacy <see cref="TourState.DefaultSubject"/> row renamed
    /// to it when the install holds exactly one account: that row is the lone account's history from
    /// before per-user state. Caller holds the lock and saves. Lazy on purpose: a startup sweep would
    /// take the row from an auth-disabled install that happens to hold one account row. Never seed a
    /// new subject from the legacy row otherwise, or every added user starts with everything seen.
    /// </summary>
    private async Task<TourState?> FindOrClaimAsync(NetworkOptimizerDbContext db, string subject)
    {
        var row = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == subject);
        if (row != null || subject == TourState.DefaultSubject)
            return row;

        var legacy = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == TourState.DefaultSubject);
        if (legacy == null)
            return null;
        await using (var auth = await _authDbFactory.CreateDbContextAsync())
        {
            var ids = await auth.Users.AsNoTracking().Select(u => u.Id).Take(2).ToListAsync();
            if (ids.Count != 1 || ids[0] != subject)
                return null;
        }
        legacy.Subject = subject;
        _logger.LogInformation("Guided tour state claimed by the install's only account");
        return legacy;
    }

    private static HashSet<string> ParseArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HashSet<string>>(json) ?? new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
    }

    private static Dictionary<string, List<string>> ParseMap(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new Dictionary<string, List<string>>();
        }
        catch
        {
            return new Dictionary<string, List<string>>();
        }
    }
}
