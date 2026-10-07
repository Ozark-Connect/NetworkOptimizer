using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Evaluates step predicates across every site the user can see, not just the active
/// one: on a multi-site install the default site may lack gateway SSH while another has
/// it, and judging only the active site would permanently hide those features. A step
/// qualifies when some visible site satisfies all its predicates, and the driver stamps
/// that site onto the step URL.
/// Only the predicates a caller asks for are evaluated. Adding a database-backed predicate is
/// one entry in <see cref="SiteDbPredicates"/>.
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

    /// <summary>
    /// The site has a cellular modem configured. Without one the radio steps describe hardware the
    /// user does not own, which is worse than saying nothing.
    /// </summary>
    public const string Cellular = "cellular";

    /// <summary>
    /// The site has a cable modem configured. Without one CM Stats is a setup prompt, so a step
    /// pointing at its cards has nothing to spotlight and must be filtered out BEFORE the driver
    /// navigates.
    /// </summary>
    public const string CableModem = "cable-modem";

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

    /// <summary>Predicates answered from the site's own database, all from one context per site.</summary>
    private static readonly (string Name, Func<NetworkOptimizerDbContext, Task<bool>> Holds)[] SiteDbPredicates =
    {
        (IspHealth, HasIspHealthAsync),
        (HasTargets, HasMonitoringTargetsAsync),
        (SqmEnabled, HasSqmEnabledAsync),
        (Starlink, HasStarlinkAsync),
        (Cellular, HasCellularAsync),
        (CableModem, HasCableModemAsync),
    };

    public class PredicateContext
    {
        public required bool MultiSiteEnabled { get; init; }
        public required List<Site> Sites { get; init; }
        /// <summary>Predicate name -> slugs of the sites where it holds. Global predicates map to all sites.</summary>
        public required Dictionary<string, HashSet<string>> QualifyingSites { get; init; }

        /// <summary>
        /// The predicates this context was asked to evaluate. Absence from
        /// <see cref="QualifyingSites"/> means "holds nowhere" only for these.
        /// </summary>
        public required IReadOnlySet<string> Evaluated { get; init; }

        /// <summary>
        /// True when some visible site satisfies every predicate in <paramref name="requires"/>.
        /// <paramref name="siteSlug"/> is a site where they all hold, preferring
        /// <paramref name="preferredSlug"/> (the active site) when it qualifies.
        /// Throws for a predicate outside <see cref="Evaluated"/>: reading it as "holds nowhere"
        /// would silently drop the step when a caller's needed set is incomplete.
        /// </summary>
        public bool Satisfies(IReadOnlyList<string> requires, string preferredSlug, out string siteSlug)
        {
            siteSlug = preferredSlug;
            if (requires.Count == 0)
                return true;

            HashSet<string>? intersection = null;
            foreach (var name in requires)
            {
                if (!Evaluated.Contains(name))
                    throw new InvalidOperationException($"Tour predicate '{name}' was not evaluated for this context");
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

    /// <summary>
    /// Evaluates the predicates in <paramref name="needed"/> on every visible site, and nothing else.
    /// <see cref="PredicateContext.Sites"/> and <see cref="MultiSite"/> are always filled, since step
    /// URL stamping needs them whatever the steps require.
    /// </summary>
    public virtual async Task<PredicateContext> ResolveAsync(IReadOnlySet<string> needed)
    {
        var evaluated = new HashSet<string>(needed, StringComparer.OrdinalIgnoreCase) { MultiSite };
        var multiSite = await _siteManagement.IsMultiSiteEnabledAsync();
        var sites = multiSite
            ? await _siteManagement.GetSitesAsync()
            : new List<Site> { new() { Slug = SiteManagementService.DefaultSiteSlug, IsDefault = true } };
        var allSlugs = sites.Select(s => s.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var qualifying = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // Global predicates: hold everywhere or nowhere.
        if (multiSite && sites.Count > 1)
            qualifying[MultiSite] = allSlugs;
        if (evaluated.Contains(HasAgent))
        {
            try
            {
                var agents = await ReadAgentsAsync();
                if (agents.Any(a => a.EnrolledAt != null))
                    qualifying[HasAgent] = allSlugs;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed", HasAgent);
            }
        }

        var dbPredicates = SiteDbPredicates.Where(p => evaluated.Contains(p.Name)).ToList();
        var needsWans = evaluated.Contains(MultiWan) || evaluated.Contains(SmartQueues);

        // Per-site predicates. Each is evaluated on its own, so one throwing cannot take the
        // others down with it - a site whose database is unreachable simply qualifies for neither.
        foreach (var site in sites)
        {
            if (evaluated.Contains(GatewaySsh))
                await TryAddAsync(qualifying, GatewaySsh, site.Slug, () => HasGatewaySshAsync(site.Slug));

            if (dbPredicates.Count > 0)
            {
                NetworkOptimizerDbContext? db = null;
                try
                {
                    db = OpenSiteDb(site);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tour predicates could not open the database for site {Slug}", site.Slug);
                }
                if (db != null)
                {
                    using (db)
                    {
                        foreach (var (name, holds) in dbPredicates)
                            await TryAddAsync(qualifying, name, site.Slug, () => holds(db));
                    }
                }
            }

            if (needsWans)
            {
                // One fetch serves both WAN predicates; a failed one qualifies the site for neither.
                List<UniFiNetworkConfig>? wans = null;
                try
                {
                    wans = await ReadWanConfigsAsync(site.Slug);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tour predicates could not read the WAN configs for site {Slug}", site.Slug);
                }
                if (wans != null)
                {
                    if (evaluated.Contains(MultiWan) && HasMultipleWans(wans))
                        Add(qualifying, MultiWan, site.Slug);
                    if (evaluated.Contains(SmartQueues) && HasSmartQueues(wans))
                        Add(qualifying, SmartQueues, site.Slug);
                }
            }
        }

        return new PredicateContext
        {
            MultiSiteEnabled = multiSite,
            Sites = sites,
            QualifyingSites = qualifying,
            Evaluated = evaluated,
        };
    }

    private async Task TryAddAsync(Dictionary<string, HashSet<string>> qualifying, string name, string slug, Func<Task<bool>> holds)
    {
        try
        {
            if (await holds())
                Add(qualifying, name, slug);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tour predicate {Predicate} evaluation failed for site {Slug}", name, slug);
        }
    }

    private static void Add(Dictionary<string, HashSet<string>> qualifying, string name, string slug)
    {
        if (!qualifying.TryGetValue(name, out var slugs))
            qualifying[name] = slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        slugs.Add(slug);
    }

    /// <summary>Every agent on a site the caller can see. Virtual so tests can count the reads.</summary>
    protected virtual Task<List<SiteAgent>> ReadAgentsAsync() => _agentEnrollment.GetAllAgentsAsync();

    /// <summary>The site's own database. Virtual so tests can count the opens.</summary>
    protected virtual NetworkOptimizerDbContext OpenSiteDb(Site site) =>
        _siteDbFactory.CreateForSite(site.Slug, site.IsDefault);

    /// <summary>
    /// The site's WAN configs from its UniFi Console, or null when the site is not connected.
    /// Virtual so tests can stand in for the console.
    /// </summary>
    protected virtual async Task<List<UniFiNetworkConfig>?> ReadWanConfigsAsync(string slug)
    {
        var connection = _siteConnections.GetFor(slug);
        if (!connection.IsConnected || connection.Client == null)
            return null;
        return await connection.Client.GetWanConfigsAsync();
    }

    /// <summary>Whether the site's gateway SSH is set up and on. Virtual so tests can stand in for the settings.</summary>
    protected virtual async Task<bool> HasGatewaySshAsync(string slug)
    {
        var settings = await _gatewaySshRegistry.GetFor(slug).GetSettingsAsync();
        return settings != null && !string.IsNullOrEmpty(settings.Host) && settings.HasCredentials && settings.Enabled;
    }

    /// <summary>
    /// Whether the site has an enabled Access ISP target. Deliberately a row check rather than
    /// asking IspHealthService: a report is computed on demand and computing one to decide whether
    /// to offer a tour step would be an expensive answer to a cheap question.
    /// </summary>
    private static Task<bool> HasIspHealthAsync(NetworkOptimizerDbContext db) =>
        db.MonitoringTargets.AsNoTracking()
            .AnyAsync(t => t.Enabled && t.TargetType == MonitoringTargetType.AccessIsp);

    /// <summary>
    /// Whether the site is monitoring anything: the feature switched on, and at least one enabled
    /// target of any type. Both halves matter - targets left behind by a site that has since turned
    /// monitoring off would otherwise qualify it for steps whose tab is a setup prompt.
    /// </summary>
    private static async Task<bool> HasMonitoringTargetsAsync(NetworkOptimizerDbContext db)
    {
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
    private static Task<bool> HasSqmEnabledAsync(NetworkOptimizerDbContext db) =>
        db.SqmWanConfigurations.AsNoTracking().AnyAsync(c => c.Enabled);

    /// <summary>
    /// Whether the site has more than one enabled WAN. Asked of the console, because that is what
    /// populates the WAN filter bars this step points at - a predicate reading anything else can
    /// disagree with what is on screen. WanProfiles in particular cannot answer it: rows are written
    /// as a side effect of computing an ISP Health report, so a site whose second WAN has never been
    /// graded has no row for it, while a WAN since removed keeps the one it had.
    /// Affordable for the same reason the Smart Queues check is: predicates resolve only for a tour
    /// that is actually due. A site that is not connected does not qualify.
    /// </summary>
    private static bool HasMultipleWans(List<UniFiNetworkConfig> wans) => wans.Count(w => w.Enabled) > 1;

    /// <summary>
    /// Whether the site has an enabled Starlink terminal. A disabled one is a dish the user has
    /// stopped monitoring, and its alerts would describe hardware they are no longer watching.
    /// </summary>
    private static Task<bool> HasStarlinkAsync(NetworkOptimizerDbContext db) =>
        db.StarlinkConfigurations.AsNoTracking().AnyAsync(c => c.Enabled);

    /// <summary>
    /// Whether the site has an enabled cellular modem. A disabled one is a modem the user has but
    /// is not watching, and its card carries no live state to point at.
    /// </summary>
    private static Task<bool> HasCellularAsync(NetworkOptimizerDbContext db) =>
        db.ModemConfigurations.AsNoTracking().AnyAsync(c => c.Enabled);

    /// <summary>
    /// Whether the site has an enabled cable modem. A disabled one is not polled, so CM Stats has
    /// no channel data for it to point at.
    /// </summary>
    private static Task<bool> HasCableModemAsync(NetworkOptimizerDbContext db) =>
        db.CmConfigurations.AsNoTracking().AnyAsync(c => c.Enabled);

    /// <summary>
    /// Whether the site has UniFi's Smart Queues turned on for at least one enabled WAN. This one
    /// has to ask the console - nothing stores UniFi's own toggle locally - which is affordable
    /// only because predicates resolve just for a tour that is actually due, never on the ordinary
    /// Dashboard visit. A site that isn't connected simply does not qualify.
    /// </summary>
    private static bool HasSmartQueues(List<UniFiNetworkConfig> wans) => wans.Any(w => w.Enabled && w.WanSmartqEnabled);
}
