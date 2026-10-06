using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Writes a band's recommended channels to the access points in UniFi Network. Site Admin, like
/// Pin Channel on the same table: it changes the site's radios, not just what it is advised.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IChannelPlanApplyService
{
    /// <summary>
    /// Starts applying on the server and returns at once. APs that hear each other move in
    /// different waves, and each wave finishes before the next starts. False when a run is
    /// already in progress on this site.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WiFiChannelPlanApplied, Category = AuditCategories.Action, TargetType = "channel_plan")]
    Task<bool> StartAsync(
        RadioBand band,
        IReadOnlyList<ChannelApplyItem> items,
        IReadOnlyList<ChannelApplyOutcome> notApplied,
        IReadOnlyDictionary<string, HashSet<string>>? hearingNeighbors);

    /// <summary>This site's current or most recent run, or null.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ChannelApplyRunSnapshot?> GetRunAsync();

    /// <summary>Closes a finished run's results. A running one is kept.</summary>
    [RequireRole(Roles.Admin)]
    Task DismissAsync();
}

/// <inheritdoc cref="IChannelPlanApplyService" />
public class ChannelPlanApplyService : IChannelPlanApplyService
{
    private readonly ChannelPlanApplyRunner _runner;
    private readonly IAuditContext _auditContext;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly string _siteSlug;

    /// <param name="runner">Runs applies on the server, per site.</param>
    /// <param name="auditContext">Carries the planned moves into the audit entry.</param>
    /// <param name="licenseState">Writes are refused on a site whose license is not operational.</param>
    /// <param name="siteContext">The site this scope serves.</param>
    public ChannelPlanApplyService(
        ChannelPlanApplyRunner runner,
        IAuditContext auditContext,
        Licensing.LicenseStateService licenseState,
        SiteContextService siteContext)
    {
        _runner = runner;
        _auditContext = auditContext;
        _licenseState = licenseState;
        _siteSlug = siteContext.Slug;
    }

    /// <inheritdoc />
    public Task<bool> StartAsync(
        RadioBand band,
        IReadOnlyList<ChannelApplyItem> items,
        IReadOnlyList<ChannelApplyOutcome> notApplied,
        IReadOnlyDictionary<string, HashSet<string>>? hearingNeighbors)
    {
        Licensing.LicenseGuard.EnsureOperational(_licenseState, _siteSlug);

        var bandItems = items.Where(i => i.Band == band).ToList();
        var waves = ChannelPlanApply.Waves(bandItems, hearingNeighbors);
        if (bandItems.Count == 0 || !_runner.TryStart(_siteSlug, band, waves, notApplied.ToList()))
        {
            _auditContext.SuppressNoChange();
            return Task.FromResult(false);
        }

        _auditContext.SetTarget($"{bandItems.Count} radio(s)", band.ToDisplayString());
        _auditContext.SetDetails(new
        {
            Band = band.ToDisplayString(),
            Waves = waves.Count,
            Radios = waves.SelectMany((w, i) => w.Select(item => new
            {
                Ap = item.ApName,
                item.ApMac,
                From = $"Ch {item.CurrentChannel} / {item.CurrentWidth} MHz",
                To = $"Ch {item.Channel} / {item.Width} MHz",
                Wave = i + 1
            })).ToList()
        });
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<ChannelApplyRunSnapshot?> GetRunAsync() => Task.FromResult(_runner.Get(_siteSlug));

    /// <inheritdoc />
    public Task DismissAsync()
    {
        _runner.Dismiss(_siteSlug);
        return Task.CompletedTask;
    }
}
