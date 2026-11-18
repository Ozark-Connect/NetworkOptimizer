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
    private readonly TourDefinitionService _definitions;
    private readonly ILogger<TourStateService> _logger;
    // One lock for every subject: it serializes each row's read-modify-write, prevents a
    // double insert on the unique Subject index, and covers the race on claiming the
    // legacy row. Never narrow it to a per-subject lock: two subjects can claim one row.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public TourStateService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        IDbContextFactory<AuthDbContext> authDbFactory,
        TourDefinitionService definitions,
        ILogger<TourStateService> logger)
    {
        _dbFactory = dbFactory;
        _authDbFactory = authDbFactory;
        _definitions = definitions;
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
        // Claim on the first read too: the first offer decision must already see the history
        // the same person built under the other subject.
        if (row == null)
            row = await ClaimOnReadAsync(subject);
        if (row == null)
            return new Snapshot
            {
                ToursDisabled = await InheritedToursDisabledAsync(subject),
                DismissedTourIds = BaselineDismissedTourIds(subject),
            };

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
    /// ?tour=reset: clears the subject's recorded tour state, so the automatic offer fires again
    /// on the next Dashboard visit with every tour since FirstSeenVersion, and the
    /// LastSeenAppVersion stamp. Other users' rows are untouched. FirstSeenVersion is deliberately preserved - it cannot be reconstructed, and
    /// losing it would permanently destroy Highlights eligibility.
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
            if (subject == TourState.DefaultSubject)
            {
                if (row != null)
                    db.TourStates.Remove(row);
            }
            else
            {
                // An empty row, not none: a missing row reads as the clean-start baseline, and a
                // reset must re-arm every tour since FirstSeenVersion, not just this minor's.
                row ??= db.TourStates.Add(new TourState { Subject = subject }).Entity;
                row.SeenTourSteps = "[]";
                row.DismissedTours = "[]";
                row.TourOffers = "{}";
                row.ToursDisabled = false;
                row.UpdatedAt = DateTime.UtcNow;
            }
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
                // Subject set explicitly: the model default is DefaultSubject. The seed is the
                // baseline the read path already applied, so the row agrees with the first offer.
                row = new TourState
                {
                    Subject = subject,
                    ToursDisabled = await InheritedToursDisabledAsync(subject),
                    DismissedTours = JsonSerializer.Serialize(BaselineDismissedTourIds(subject).Select(Ns)),
                };
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
    /// The subject's tracked row, else the other subject's row renamed to it when the install holds
    /// exactly one account, since both are then the same person: the only account takes the
    /// <see cref="TourState.DefaultSubject"/> row (history from before per-user state, or from while
    /// authentication was off), and <see cref="TourState.DefaultSubject"/> takes the account's row back
    /// when authentication is turned off. Caller holds the lock and saves. Lazy on purpose: a startup
    /// sweep would take the row from an auth-disabled install that happens to hold one account row.
    /// Never seed seen state from another row otherwise, or every added user starts with everything seen.
    /// </summary>
    private async Task<TourState?> FindOrClaimAsync(NetworkOptimizerDbContext db, string subject)
    {
        var row = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == subject);
        if (row != null)
            return row;

        if (subject == TourState.DefaultSubject)
        {
            if (await OnlyAccountIdAsync() is not { } account)
                return null;
            var own = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == account);
            if (own == null)
                return null;
            own.Subject = TourState.DefaultSubject;
            _logger.LogInformation("Guided tour state handed back to the install with authentication disabled");
            return own;
        }

        var legacy = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == TourState.DefaultSubject);
        if (legacy == null || await OnlyAccountIdAsync() != subject)
            return null;
        legacy.Subject = subject;
        _logger.LogInformation("Guided tour state claimed by the install's only account");
        return legacy;
    }

    /// <summary>The install's single Identity account, or null when it holds none or several.</summary>
    private async Task<string?> OnlyAccountIdAsync()
    {
        await using var auth = await _authDbFactory.CreateDbContextAsync();
        var ids = await auth.Users.AsNoTracking().Select(u => u.Id).Take(2).ToListAsync();
        return ids.Count == 1 ? ids[0] : null;
    }

    /// <summary>
    /// What a subject that starts clean (no row, nothing claimed) reads as dismissed: every
    /// what's-new tour from before the current minor line. A new account is offered this minor's
    /// tour, not every release since the install. The default subject keeps the full backlog, and
    /// Highlights tours are never in it, so the new-install welcome is unchanged.
    /// </summary>
    private HashSet<string> BaselineDismissedTourIds(string subject)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (subject == TourState.DefaultSubject)
            return ids;
        var current = _definitions.CurrentEffectiveVersion();
        foreach (var tour in _definitions.GetTours())
            if (!tour.IsHighlights && tour.ParsedVersion is { } v
                && (v.Major < current.Major || (v.Major == current.Major && v.Minor < current.Minor)))
                ids.Add(tour.Id);
        return ids;
    }

    /// <summary>
    /// The opt-out a user without a row starts with. Before per-user state, "Don't offer tours
    /// automatically" on the <see cref="TourState.DefaultSubject"/> row covered every account, so an
    /// account that existed while that row was last written keeps it. An account added later starts
    /// with tours on: the opt-out was someone else's choice.
    /// </summary>
    private async Task<bool> InheritedToursDisabledAsync(string subject)
    {
        if (subject == TourState.DefaultSubject)
            return false;
        DateTime legacyWritten;
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            var legacy = await db.TourStates.AsNoTracking()
                .Where(t => t.Subject == TourState.DefaultSubject && t.ToursDisabled)
                .Select(t => (DateTime?)t.UpdatedAt)
                .FirstOrDefaultAsync();
            if (legacy == null)
                return false;
            legacyWritten = legacy.Value;
        }
        await using var auth = await _authDbFactory.CreateDbContextAsync();
        return await auth.Users.AsNoTracking().AnyAsync(u => u.Id == subject && u.CreatedAt <= legacyWritten);
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
