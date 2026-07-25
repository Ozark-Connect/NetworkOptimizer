using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Reads and writes per-subject tour state (TourStates) and the install-level version
/// stamps on AdminSettings. Always the main database via the singleton context factory:
/// a tour is about the product, not a site.
/// Source builds namespace every recorded id with "dev:" so a source-built test site
/// records and reads its own state without consuming what a release install would see.
/// </summary>
public class TourStateService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ILogger<TourStateService> _logger;

    public TourStateService(IDbContextFactory<NetworkOptimizerDbContext> dbFactory, ILogger<TourStateService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    private const string DevPrefix = "dev:";

    private static string Ns(string id) => AppVersionInfo.IsSourceBuild ? DevPrefix + id : id;

    private static string? StripNs(string id)
    {
        if (AppVersionInfo.IsSourceBuild)
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

    public async Task<Snapshot> GetSnapshotAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.TourStates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Subject == TourState.DefaultSubject);
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

    public Task RecordStepSeenAsync(string stepId) => MutateAsync(row =>
    {
        var seen = ParseArray(row.SeenTourSteps);
        if (seen.Add(Ns(stepId)))
            row.SeenTourSteps = JsonSerializer.Serialize(seen);
    });

    public Task RecordToursDismissedAsync(IEnumerable<string> tourIds) => MutateAsync(row =>
    {
        var dismissed = ParseArray(row.DismissedTours);
        foreach (var id in tourIds)
            dismissed.Add(Ns(id));
        row.DismissedTours = JsonSerializer.Serialize(dismissed);
    });

    public Task RecordOfferAsync(IEnumerable<string> tourIds, string version) => MutateAsync(row =>
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

    public Task SetToursDisabledAsync(bool disabled) => MutateAsync(row => row.ToursDisabled = disabled);

    /// <summary>
    /// ?tour=reset: clears recorded tour state and the LastSeenAppVersion stamp so the
    /// automatic offer path (upgrade detection, merge, modal) fires again on the next
    /// Dashboard visit. FirstSeenVersion is deliberately preserved - it cannot be
    /// reconstructed, and losing it would permanently destroy Highlights eligibility.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == TourState.DefaultSubject);
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

    public async Task<string?> GetFirstSeenVersionAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var admin = await db.AdminSettings.AsNoTracking().FirstOrDefaultAsync();
        return admin?.FirstSeenVersion;
    }

    private async Task MutateAsync(Action<TourState> mutate)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var row = await db.TourStates.FirstOrDefaultAsync(t => t.Subject == TourState.DefaultSubject);
            if (row == null)
            {
                row = new TourState();
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
