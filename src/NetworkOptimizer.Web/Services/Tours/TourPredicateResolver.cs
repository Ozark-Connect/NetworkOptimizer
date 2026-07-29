using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Evaluates step predicates across every site the user can see, not just the active
/// one: on a multi-site install the default site may lack gateway SSH while another has
/// it, and judging only the active site would permanently hide those features. A step
/// qualifies when some visible site satisfies all its predicates, and the driver stamps
/// that site onto the step URL.
/// Adding a predicate is one entry in <see cref="EvaluateSiteAsync"/> or the globals below.
/// </summary>
public class TourPredicateResolver
{
    public const string GatewaySsh = "gateway-ssh";
    public const string MultiSite = "multi-site";
    public const string HasAgent = "has-agent";

    /// <summary>
    /// The site has ISP Health to show: at least one enabled Access ISP target, which is what the
    /// ISP Network card lists. Without it the tab has no report and Monitoring opens on Setup, so a
    /// step needing it must be filtered out BEFORE the driver navigates - "optional" only skips the
    /// step once you have already been taken there.
    /// </summary>
    public const string IspHealth = "isp-health";

    private readonly SiteManagementService _siteManagement;
    private readonly GatewaySshRegistry _gatewaySshRegistry;
    private readonly AgentEnrollmentService _agentEnrollment;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<TourPredicateResolver> _logger;

    public TourPredicateResolver(
        SiteManagementService siteManagement,
        GatewaySshRegistry gatewaySshRegistry,
        AgentEnrollmentService agentEnrollment,
        SiteDbContextFactory siteDbFactory,
        ILogger<TourPredicateResolver> logger)
    {
        _siteManagement = siteManagement;
        _gatewaySshRegistry = gatewaySshRegistry;
        _agentEnrollment = agentEnrollment;
        _siteDbFactory = siteDbFactory;
        _logger = logger;
    }

    public class PredicateContext
    {
        public required bool MultiSiteEnabled { get; init; }
        public required List<Site> Sites { get; init; }
        /// <summary>Predicate name -> slugs of the sites where it holds. Global predicates map to all sites.</summary>
        public required Dictionary<string, HashSet<string>> QualifyingSites { get; init; }

        /// <summary>
        /// True when some visible site satisfies every predicate in <paramref name="requires"/>.
        /// <paramref name="siteSlug"/> is a site where they all hold, preferring
        /// <paramref name="preferredSlug"/> (the active site) when it qualifies.
        /// </summary>
        public bool Satisfies(IReadOnlyList<string> requires, string preferredSlug, out string siteSlug)
        {
            siteSlug = preferredSlug;
            if (requires.Count == 0)
                return true;

            HashSet<string>? intersection = null;
            foreach (var name in requires)
            {
                if (!QualifyingSites.TryGetValue(name, out var slugs))
                    return false;
                intersection = intersection == null
                    ? new HashSet<string>(slugs, StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(intersection.Intersect(slugs, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                if (intersection.Count == 0)
                    return false;
            }

            siteSlug = intersection!.Contains(preferredSlug)
                ? preferredSlug
                : Sites.Select(s => s.Slug).First(intersection.Contains);
            return true;
        }
    }

    public async Task<PredicateContext> ResolveAsync()
    {
        var multiSite = await _siteManagement.IsMultiSiteEnabledAsync();
        var sites = multiSite
            ? await _siteManagement.GetSitesAsync()
            : new List<Site> { new() { Slug = SiteManagementService.DefaultSiteSlug, IsDefault = true } };
        var allSlugs = sites.Select(s => s.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var qualifying = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Global predicates: hold everywhere or nowhere.
        if (multiSite && sites.Count > 1)
            qualifying[MultiSite] = allSlugs;
        try
        {
            var agents = await _agentEnrollment.GetAllAgentsAsync();
            if (agents.Any(a => a.EnrolledAt != null))
                qualifying[HasAgent] = allSlugs;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed", HasAgent);
        }

        // Per-site predicates. Each is evaluated on its own, so one throwing cannot take the
        // others down with it - a site whose database is unreachable simply qualifies for neither.
        var gatewaySshSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ispHealthSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            try
            {
                if (await EvaluateSiteAsync(site.Slug))
                    gatewaySshSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", GatewaySsh, site.Slug);
            }

            try
            {
                if (await HasIspHealthAsync(site.Slug, site.IsDefault))
                    ispHealthSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", IspHealth, site.Slug);
            }
        }
        if (gatewaySshSites.Count > 0)
            qualifying[GatewaySsh] = gatewaySshSites;
        if (ispHealthSites.Count > 0)
            qualifying[IspHealth] = ispHealthSites;

        return new PredicateContext
        {
            MultiSiteEnabled = multiSite,
            Sites = sites,
            QualifyingSites = qualifying,
        };
    }

    private async Task<bool> EvaluateSiteAsync(string slug)
    {
        var settings = await _gatewaySshRegistry.GetFor(slug).GetSettingsAsync();
        return settings != null && !string.IsNullOrEmpty(settings.Host) && settings.HasCredentials && settings.Enabled;
    }

    /// <summary>
    /// Whether the site has an enabled Access ISP target. Deliberately a row check rather than
    /// asking IspHealthService: a report is computed on demand and computing one to decide whether
    /// to offer a tour step would be an expensive answer to a cheap question.
    /// </summary>
    private async Task<bool> HasIspHealthAsync(string slug, bool isDefault)
    {
        using var db = _siteDbFactory.CreateForSite(slug, isDefault);
        return await db.MonitoringTargets.AsNoTracking()
            .AnyAsync(t => t.Enabled && t.TargetType == MonitoringTargetType.AccessIsp);
    }
}
