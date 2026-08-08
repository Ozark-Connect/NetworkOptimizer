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

    /// <summary>
    /// The site is monitoring something: the feature is on AND at least one target is enabled, of
    /// any type. Deliberately looser than <see cref="IspHealth"/>, which needs an Access ISP target
    /// - a site watching nothing but its own switches and APs still has charts worth pointing at,
    /// and would be turned away by that one. With monitoring off the tab is a setup prompt, so a
    /// step must be filtered out BEFORE the driver navigates.
    /// </summary>
    public const string HasTargets = "has-targets";

    /// <summary>
    /// The site runs Adaptive SQM on at least one WAN. Without it the page is a setup prompt with no
    /// WAN cards at all, so a step pointing at a per-WAN control has nothing to spotlight and must be
    /// filtered out BEFORE the driver navigates - "optional" only skips the step once you are there.
    /// </summary>
    public const string SqmEnabled = "sqm-enabled";

    /// <summary>
    /// UniFi's own Smart Queues is on for at least one of the site's WANs. Not the same thing as
    /// <see cref="SqmEnabled"/>, which is our Adaptive SQM: a WAN can have UniFi's Smart Queues on
    /// without Adaptive SQM ever being deployed, and that is exactly the case the Smart Queues
    /// shaper check exists for.
    /// </summary>
    public const string SmartQueues = "smart-queues";

    /// <summary>
    /// The site has more than one enabled WAN, so the per-WAN filters and comparisons exist to be
    /// shown. A single-WAN site renders no WAN selector at all, so a step spotlighting one has
    /// nothing to point at and must be filtered out BEFORE the driver navigates.
    /// </summary>
    public const string MultiWan = "multi-wan";

    /// <summary>
    /// The site has a Starlink terminal configured. Without one the dish alerts describe hardware
    /// the user does not own, which is worse than saying nothing.
    /// </summary>
    public const string Starlink = "starlink";

    private readonly SiteManagementService _siteManagement;
    private readonly GatewaySshRegistry _gatewaySshRegistry;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly AgentEnrollmentService _agentEnrollment;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly ILogger<TourPredicateResolver> _logger;

    public TourPredicateResolver(
        SiteManagementService siteManagement,
        GatewaySshRegistry gatewaySshRegistry,
        SiteConnectionRegistry siteConnections,
        AgentEnrollmentService agentEnrollment,
        SiteDbContextFactory siteDbFactory,
        ILogger<TourPredicateResolver> logger)
    {
        _siteManagement = siteManagement;
        _gatewaySshRegistry = gatewaySshRegistry;
        _siteConnections = siteConnections;
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
        var hasTargetsSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sqmSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var smartQueuesSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var multiWanSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var starlinkSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

            try
            {
                if (await HasMonitoringTargetsAsync(site.Slug, site.IsDefault))
                    hasTargetsSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", HasTargets, site.Slug);
            }

            try
            {
                if (await HasSqmEnabledAsync(site.Slug, site.IsDefault))
                    sqmSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", SqmEnabled, site.Slug);
            }

            try
            {
                if (await HasSmartQueuesAsync(site.Slug))
                    smartQueuesSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", SmartQueues, site.Slug);
            }

            try
            {
                if (await HasMultipleWansAsync(site.Slug))
                    multiWanSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", MultiWan, site.Slug);
            }

            try
            {
                if (await HasStarlinkAsync(site.Slug, site.IsDefault))
                    starlinkSites.Add(site.Slug);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", Starlink, site.Slug);
            }
        }
        if (gatewaySshSites.Count > 0)
            qualifying[GatewaySsh] = gatewaySshSites;
        if (ispHealthSites.Count > 0)
            qualifying[IspHealth] = ispHealthSites;
        if (hasTargetsSites.Count > 0)
            qualifying[HasTargets] = hasTargetsSites;
        if (sqmSites.Count > 0)
            qualifying[SqmEnabled] = sqmSites;
        if (smartQueuesSites.Count > 0)
            qualifying[SmartQueues] = smartQueuesSites;
        if (multiWanSites.Count > 0)
            qualifying[MultiWan] = multiWanSites;
        if (starlinkSites.Count > 0)
            qualifying[Starlink] = starlinkSites;

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

    /// <summary>
    /// Whether the site is monitoring anything: the feature switched on, and at least one enabled
    /// target of any type. Both halves matter - targets left behind by a site that has since turned
    /// monitoring off would otherwise qualify it for steps whose tab is a setup prompt.
    /// </summary>
    private async Task<bool> HasMonitoringTargetsAsync(string slug, bool isDefault)
    {
        using var db = _siteDbFactory.CreateForSite(slug, isDefault);
        var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync();
        if (settings?.Enabled != true) return false;
        return await db.MonitoringTargets.AsNoTracking().AnyAsync(t => t.Enabled);
    }

    /// <summary>
    /// Whether the site has Adaptive SQM turned on for at least one WAN. A saved row check rather
    /// than asking the gateway what tc is currently doing: the question is whether the user has this
    /// feature configured, and reaching a gateway over SSH to answer it would put a network round
    /// trip on a path that runs on every Dashboard visit.
    /// </summary>
    private async Task<bool> HasSqmEnabledAsync(string slug, bool isDefault)
    {
        using var db = _siteDbFactory.CreateForSite(slug, isDefault);
        return await db.SqmWanConfigurations.AsNoTracking().AnyAsync(c => c.Enabled);
    }

    /// <summary>
    /// Whether the site has more than one enabled WAN. Asked of the console, because that is what
    /// populates the WAN filter bars this step points at - a predicate reading anything else can
    /// disagree with what is on screen. WanProfiles in particular cannot answer it: rows are written
    /// as a side effect of computing an ISP Health report, so a site whose second WAN has never been
    /// graded has no row for it, while a WAN since removed keeps the one it had.
    /// Affordable for the same reason the Smart Queues check is: predicates resolve only for a tour
    /// that is actually due. A site that is not connected does not qualify.
    /// </summary>
    private async Task<bool> HasMultipleWansAsync(string slug)
    {
        var connection = _siteConnections.GetFor(slug);
        if (!connection.IsConnected || connection.Client == null)
            return false;

        var wans = await connection.Client.GetWanConfigsAsync();
        return wans.Count(w => w.Enabled) > 1;
    }

    /// <summary>
    /// Whether the site has an enabled Starlink terminal. A disabled one is a dish the user has
    /// stopped monitoring, and its alerts would describe hardware they are no longer watching.
    /// </summary>
    private async Task<bool> HasStarlinkAsync(string slug, bool isDefault)
    {
        using var db = _siteDbFactory.CreateForSite(slug, isDefault);
        return await db.StarlinkConfigurations.AsNoTracking().AnyAsync(c => c.Enabled);
    }

    /// <summary>
    /// Whether the site has UniFi's Smart Queues turned on for at least one enabled WAN. This one
    /// has to ask the console - nothing stores UniFi's own toggle locally - which is affordable
    /// only because predicates resolve just for a tour that is actually due, never on the ordinary
    /// Dashboard visit. A site that isn't connected simply does not qualify.
    /// </summary>
    private async Task<bool> HasSmartQueuesAsync(string slug)
    {
        var connection = _siteConnections.GetFor(slug);
        if (!connection.IsConnected || connection.Client == null)
            return false;

        var wans = await connection.Client.GetWanConfigsAsync();
        return wans.Any(w => w.Enabled && w.WanSmartqEnabled);
    }
}
