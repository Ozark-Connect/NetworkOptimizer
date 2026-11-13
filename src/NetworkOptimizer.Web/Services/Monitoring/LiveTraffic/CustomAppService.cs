using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// The site's custom apps: its own names for traffic UniFi Network's DPI does not recognize, matched
/// by protocol, port, and remote. Live Traffic (Live, Ended, History, and the cards that embed it)
/// applies them on the next refresh after a change. Reads are open to any role, as Live Traffic is;
/// changes are the site's admin's.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ICustomAppService
{
    /// <summary>The definitions in list order.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<CustomAppDefinition>> ListAsync(CancellationToken ct = default);

    /// <summary>Active flows each definition names now, by id; a definition naming none is absent.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyDictionary<int, int>> LiveCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// How an unsaved definition would land on the active flows. Null when it does not parse yet
    /// (the form is mid-edit).
    /// </summary>
    [RequireRole(Roles.Viewer)]
    Task<CustomAppPreview?> PreviewAsync(CustomAppDefinition draft, CancellationToken ct = default);

    /// <summary>
    /// Adds (<see cref="CustomAppDefinition.Id"/> 0) or replaces a definition. A new one goes last, or
    /// at <paramref name="position"/> (0-based) when given: Undo puts a deleted one back where it was.
    /// Throws <see cref="ArgumentException"/> with the form's message when it does not validate.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.LiveTrafficCustomAppSaved, TargetType = "custom_app")]
    Task<CustomAppDefinition> SaveAsync(CustomAppDefinition definition, int? position = null, CancellationToken ct = default);

    /// <summary>Moves a definition one place up (-1) or down (+1).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.LiveTrafficCustomAppMoved, TargetType = "custom_app")]
    Task MoveAsync(int id, int offset, CancellationToken ct = default);

    /// <summary>Deletes a definition. Returns its place in the list, for Undo; null when it was already gone.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.LiveTrafficCustomAppDeleted, TargetType = "custom_app")]
    Task<int?> DeleteAsync(int id, CancellationToken ct = default);
}

/// <inheritdoc cref="ICustomAppService" />
public sealed class CustomAppService : ICustomAppService
{
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _site;
    private readonly LiveTrafficRegistry _registry;
    private readonly IAuditContext _audit;
    private readonly ILogger<CustomAppService> _logger;

    public CustomAppService(SiteDbContextFactory siteDb, SiteContextService site, LiveTrafficRegistry registry, IAuditContext audit,
        ILogger<CustomAppService> logger)
    {
        _siteDb = siteDb;
        _site = site;
        _registry = registry;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Runs a write under the site's write lock.</summary>
    private async Task<T> WriteAsync<T>(Func<Task<T>> write, CancellationToken ct)
    {
        var gate = _registry.CustomAppsWriteLock(_site.Slug);
        await gate.WaitAsync(ct);
        try { return await write(); }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Reads the site's definitions into the registry once; later changes push them in directly. Never
    /// throws for a failed read: Live Traffic runs on without custom names and tries again in a minute.
    /// </summary>
    internal static async Task EnsureLoadedAsync(LiveTrafficRegistry registry, SiteDbContextFactory siteDb, string slug, ILogger logger, CancellationToken ct)
    {
        if (!registry.CustomAppsLoadDue(slug, DateTime.UtcNow)) return;
        try
        {
            await using var db = siteDb.CreateForSite(slug, slug == SiteManagementService.DefaultSiteSlug);
            var all = await db.CustomAppDefinitions.AsNoTracking().ToListAsync(ct);
            registry.LoadCustomApps(slug, CustomAppMatcher.ToRules(all));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            registry.CustomAppsLoadFailed(slug, DateTime.UtcNow);
            logger.LogWarning(ex, "Live Traffic: could not read custom apps for site {Site}", slug);
        }
    }

    private NetworkOptimizerDbContext OpenDb() =>
        _siteDb.CreateForSite(_site.Slug, _site.Slug == SiteManagementService.DefaultSiteSlug);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CustomAppDefinition>> ListAsync(CancellationToken ct = default)
    {
        await using var db = OpenDb();
        return await db.CustomAppDefinitions.AsNoTracking().OrderBy(d => d.SortOrder).ThenBy(d => d.Id).ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, int>> LiveCountsAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(_registry, _siteDb, _site.Slug, _logger, ct);
        return _registry.GetFor(_site.Slug).LiveCounts();
    }

    /// <inheritdoc />
    public async Task<CustomAppPreview?> PreviewAsync(CustomAppDefinition draft, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(_registry, _siteDb, _site.Slug, _logger, ct);
        // The name does not affect matching, and an empty one is the form's first state.
        var normalized = Normalized(draft);
        normalized.Name = "Preview";
        var rule = CustomAppMatcher.ToRule(normalized);
        return rule == null ? null : _registry.GetFor(_site.Slug).Preview(rule, draft.Id > 0 ? draft.Id : null);
    }

    /// <inheritdoc />
    public Task<CustomAppDefinition> SaveAsync(CustomAppDefinition definition, int? position = null, CancellationToken ct = default) =>
        WriteAsync(() => SaveLockedAsync(definition, position, ct), ct);

    private async Task<CustomAppDefinition> SaveLockedAsync(CustomAppDefinition definition, int? position, CancellationToken ct)
    {
        var d = Normalized(definition);
        if (Validate(d) is { } error) throw new ArgumentException(error);

        await using var db = OpenDb();
        var all = await db.CustomAppDefinitions.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        var now = DateTime.UtcNow;
        CustomAppDefinition saved;
        if (d.Id > 0)
        {
            saved = all.FirstOrDefault(x => x.Id == d.Id) ?? throw new ArgumentException("That custom app was deleted. Add it again.");
            if (saved.Name == d.Name && saved.Protocol == d.Protocol && saved.Ports == d.Ports && saved.PortOn == d.PortOn
                && saved.Scope == d.Scope && saved.Remote == d.Remote && saved.IconClass == d.IconClass)
            {
                _audit.SuppressNoChange();
                return saved;
            }
            saved.Name = d.Name;
            saved.Protocol = d.Protocol;
            saved.Ports = d.Ports;
            saved.PortOn = d.PortOn;
            saved.Scope = d.Scope;
            saved.Remote = d.Remote;
            saved.IconClass = d.IconClass;
            saved.UpdatedAt = now;
        }
        else
        {
            saved = new CustomAppDefinition
            {
                Name = d.Name, Protocol = d.Protocol, Ports = d.Ports, PortOn = d.PortOn, Scope = d.Scope,
                Remote = d.Remote, IconClass = d.IconClass, CreatedAt = now, UpdatedAt = now,
            };
            db.CustomAppDefinitions.Add(saved);
            all.Insert(Math.Clamp(position ?? all.Count, 0, all.Count), saved);
        }
        Renumber(all);
        await db.SaveChangesAsync(ct);

        _audit.SetTarget(saved.Id.ToString(), saved.Name);
        _audit.SetDetails(new
        {
            created = d.Id == 0,
            matches = CustomAppMatcher.ToRule(saved) is { } r ? CustomAppMatcher.Describe(r) : saved.Ports,
            icon = saved.IconClass,
        });
        Publish(all);
        return saved;
    }

    /// <inheritdoc />
    public Task MoveAsync(int id, int offset, CancellationToken ct = default) =>
        WriteAsync(async () => { await MoveLockedAsync(id, offset, ct); return true; }, ct);

    private async Task MoveLockedAsync(int id, int offset, CancellationToken ct)
    {
        await using var db = OpenDb();
        var all = await db.CustomAppDefinitions.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        var from = all.FindIndex(x => x.Id == id);
        var to = from + Math.Sign(offset);
        if (from < 0 || to < 0 || to >= all.Count)
        {
            _audit.SuppressNoChange();
            return;
        }
        (all[from], all[to]) = (all[to], all[from]);
        Renumber(all);
        await db.SaveChangesAsync(ct);
        _audit.SetTarget(id.ToString(), all[to].Name);
        _audit.SetDetails(new { position = to + 1, of = all.Count });
        Publish(all);
    }

    /// <inheritdoc />
    public Task<int?> DeleteAsync(int id, CancellationToken ct = default) =>
        WriteAsync(() => DeleteLockedAsync(id, ct), ct);

    private async Task<int?> DeleteLockedAsync(int id, CancellationToken ct)
    {
        await using var db = OpenDb();
        var all = await db.CustomAppDefinitions.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync(ct);
        var index = all.FindIndex(x => x.Id == id);
        if (index < 0)
        {
            _audit.SuppressNoChange();
            return null;
        }
        var gone = all[index];
        db.CustomAppDefinitions.Remove(gone);
        all.RemoveAt(index);
        Renumber(all);
        await db.SaveChangesAsync(ct);
        _audit.SetTarget(id.ToString(), gone.Name);
        Publish(all);
        return index;
    }

    /// <summary>The form's first problem with a definition, or null when it saves.</summary>
    public static string? Validate(CustomAppDefinition d)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return "Give the app a name.";
        if (d.Name.Trim().Length > 80) return "Keep the name to 80 characters.";
        if (!CustomAppMatcher.TryParsePorts(d.Ports, out _, out var portError)) return portError;
        return CustomAppMatcher.ValidateRemote(d.Remote);
    }

    /// <summary>Trimmed, with the ports written back in one form ("80, 8080", "5000-5010").</summary>
    private static CustomAppDefinition Normalized(CustomAppDefinition d)
    {
        var ports = CustomAppMatcher.TryParsePorts(d.Ports, out var parsed, out _) ? string.Join(", ", parsed) : d.Ports?.Trim() ?? "";
        return new CustomAppDefinition
        {
            Id = d.Id,
            Name = (d.Name ?? "").Trim(),
            Protocol = d.Protocol,
            Ports = ports,
            PortOn = d.PortOn,
            Scope = d.Scope,
            Remote = string.IsNullOrWhiteSpace(d.Remote) ? null : d.Remote.Trim(),
            IconClass = CustomAppMatcher.IconChoices.Contains(d.IconClass ?? "") ? d.IconClass : null,
        };
    }

    private static void Renumber(List<CustomAppDefinition> all)
    {
        for (var i = 0; i < all.Count; i++) all[i].SortOrder = i;
    }

    private void Publish(IEnumerable<CustomAppDefinition> all) =>
        _registry.SetCustomApps(_site.Slug, CustomAppMatcher.ToRules(all));
}
