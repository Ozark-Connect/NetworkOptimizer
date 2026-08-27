namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Decides which tour, if any, is due for the automatic offer on Dashboard, builds
/// merged catch-up tours, and resolves manual launches (Settings launcher, ?tour=id).
/// Scoped: predicate resolution and site stamping depend on the circuit's site context.
/// </summary>
public class TourService
{
    /// <summary>
    /// A tour is included in an automatic offer at most this many times across releases:
    /// the original offer plus two carries. After that it stays reachable from
    /// Settings - Application but never appears in a modal again.
    /// </summary>
    public const int MaxAutomaticOffers = 3;

    private readonly TourDefinitionService _definitions;
    private readonly TourStateService _state;
    private readonly TourPredicateResolver _predicates;
    private readonly SiteContextService _siteContext;
    private readonly TourUrlTokenResolver _tokens;
    private readonly ILogger<TourService> _logger;

    public TourService(
        TourDefinitionService definitions,
        TourStateService state,
        TourPredicateResolver predicates,
        SiteContextService siteContext,
        TourUrlTokenResolver tokens,
        ILogger<TourService> logger)
    {
        _definitions = definitions;
        _state = state;
        _predicates = predicates;
        _siteContext = siteContext;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>
    /// The automatic path: what the Dashboard offer modal should propose, or null when
    /// nothing is due. Highlights (new installs) wins over a merged what's-new tour;
    /// the loser stays due for the next visit so two modals never queue in one session.
    /// </summary>
    public async Task<TourOffer?> GetDueOfferAsync()
    {
        var tours = _definitions.GetTours();
        if (tours.Count == 0)
        {
            _logger.LogDebug("No tour due: no tour definitions loaded");
            return null;
        }

        var snapshot = await _state.GetSnapshotAsync();
        if (snapshot.ToursDisabled)
        {
            _logger.LogDebug("No tour due: tours disabled for this subject");
            return null;
        }

        var current = _definitions.CurrentEffectiveVersion();
        var firstSeen = TourVersions.Parse(await _state.GetFirstSeenVersionAsync());

        var eligible = tours.Where(t =>
                !t.IsHighlights
                && t.ParsedVersion <= current
                && (firstSeen == null || t.ParsedVersion > firstSeen)
                && !snapshot.DismissedTourIds.Contains(t.Id)
                && IsStillOfferable(snapshot, t.Id, current))
            .ToList();
        var highlightsPossible = firstSeen != null && tours.Any(t => t.IsHighlights);

        _logger.LogDebug("Tour eligibility: version={Current}, firstSeen={FirstSeen}, tours={Tours}, eligible={Eligible}, seenSteps={Seen}, dismissed={Dismissed}, offers={Offers}",
            current, firstSeen?.ToString() ?? "(null)", tours.Count, eligible.Count,
            snapshot.SeenStepIds.Count, snapshot.DismissedTourIds.Count, snapshot.Offers.Count);

        // Predicate resolution reads gateway settings per site; skip it entirely in the
        // common nothing-due case, which runs on every Dashboard visit.
        if (eligible.Count == 0 && !highlightsPossible)
            return null;
        var ctx = await _predicates.ResolveAsync();

        // New installs (they have a FirstSeenVersion) are offered Highlights once,
        // and never what's-new for releases that predate them.
        if (highlightsPossible)
        {
            var highlightsOffer = await BuildHighlightsOffer(tours, snapshot, current, ctx);
            if (highlightsOffer != null)
                return highlightsOffer;
        }

        if (eligible.Count == 0)
            return null;

        var plan = TourMergePlanner.Build(eligible, (tour, step) =>
            !snapshot.SeenStepIds.Contains(step.Id)
            && ctx.Satisfies(step.Requires, _siteContext.Slug, out _));
        if (plan.Steps.Count == 0)
        {
            _logger.LogDebug("No tour due: every step of {Count} eligible tour(s) was filtered out (seen or predicate)", eligible.Count);
            return null;
        }
        var tokens = await _tokens.ResolveAsync(plan.Steps.Select(s => s.Step.Url), _siteContext.Slug);
        var resolved = plan.Steps
            .Select(s => Resolve(s.Tour, s.Step, ctx, tokens))
            .OfType<ResolvedTourStep>()
            .ToList();
        if (resolved.Count == 0)
        {
            _logger.LogDebug("No tour due: every step's url needed a token this site cannot fill");
            return null;
        }

        _logger.LogInformation("Tour offer built: {Steps} step(s) from {Tours}", resolved.Count,
            string.Join(", ", resolved.Select(s => s.TourId).Distinct()));

        var contributing = resolved.Select(s => s.TourId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var newest = plan.Steps.Where(s => contributing.Contains(s.Tour.Id, StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Tour).OrderBy(t => t.ParsedVersion).Last();
        var single = contributing.Count == 1;
        return new TourOffer
        {
            Title = single ? newest.Title : "What's new since your last update",
            Summary = single ? newest.Summary : null,
            IsHighlights = false,
            Steps = resolved,
            TourIds = contributing,
            DroppedCount = plan.DroppedCount,
            Automatic = true,
        };
    }

    /// <summary>
    /// A manual launch (Settings launcher or a ?tour=id deep link): the tour's full
    /// eligible step set - predicates still apply, seen state and the cap do not.
    /// Null when the tour is unknown or every step is filtered out; the driver must
    /// no-op rather than show anything empty.
    /// </summary>
    public async Task<TourOffer?> BuildManualLaunchAsync(string tourId)
    {
        var tour = _definitions.GetTour(tourId);
        if (tour == null)
            return null;

        var ctx = await _predicates.ResolveAsync();
        var eligible = EligibleSteps(tour, ctx).ToList();
        var tokens = await _tokens.ResolveAsync(eligible.Select(s => s.Url), _siteContext.Slug);
        var steps = eligible.Select(s => Resolve(tour, s, ctx, tokens)).OfType<ResolvedTourStep>().ToList();
        if (steps.Count == 0)
            return null;

        return new TourOffer
        {
            Title = tour.Title,
            Summary = tour.Summary,
            IsHighlights = tour.IsHighlights,
            Steps = steps,
            TourIds = new List<string> { tour.Id },
            Automatic = false,
        };
    }

    /// <summary>Per-tour state lines for the Settings - Application launcher.</summary>
    public async Task<List<TourStatusInfo>> GetTourStatusesAsync()
    {
        var snapshot = await _state.GetSnapshotAsync();
        var ctx = await _predicates.ResolveAsync();

        var result = new List<TourStatusInfo>();
        foreach (var tour in _definitions.GetTours().OrderByDescending(t => t.ParsedVersion))
        {
            var eligible = EligibleSteps(tour, ctx).ToList();
            var seen = eligible.Count(s => snapshot.SeenStepIds.Contains(s.Id));
            var status = snapshot.DismissedTourIds.Contains(tour.Id) ? TourStatus.Skipped
                : eligible.Count > 0 && seen == eligible.Count ? TourStatus.Completed
                : snapshot.Offers.ContainsKey(tour.Id) || seen > 0 ? TourStatus.Deferred
                : TourStatus.NotSeen;
            result.Add(new TourStatusInfo
            {
                Tour = tour,
                Status = status,
                EligibleStepCount = eligible.Count,
                SeenStepCount = seen,
            });
        }
        return result;
    }

    public async Task RecordOfferShownAsync(TourOffer offer)
    {
        if (!offer.Automatic)
            return;
        var version = _definitions.CurrentEffectiveVersion().ToString();
        await _state.RecordOfferAsync(offer.TourIds, version);
    }

    public Task RecordStepSeenAsync(string stepId) => _state.RecordStepSeenAsync(stepId);

    /// <summary>Skip mid-tour: this tour is done and never offered again. Others still are.</summary>
    public Task RecordSkippedAsync(TourOffer offer) => _state.RecordToursDismissedAsync(offer.TourIds);

    public Task SetToursDisabledAsync(bool disabled) => _state.SetToursDisabledAsync(disabled);

    public async Task<bool> GetToursDisabledAsync() => (await _state.GetSnapshotAsync()).ToursDisabled;

    public Task ResetAsync() => _state.ResetAsync();

    private async Task<TourOffer?> BuildHighlightsOffer(
        IReadOnlyList<TourDefinition> tours,
        TourStateService.Snapshot snapshot,
        Version current,
        TourPredicateResolver.PredicateContext ctx)
    {
        var highlights = tours.Where(t => t.IsHighlights).ToList();
        if (highlights.Count == 0)
            return null;

        // "Never been offered a Highlights tour" - any prior offer or skip of any
        // Highlights revision consumes the automatic welcome.
        if (highlights.Any(t => snapshot.Offers.ContainsKey(t.Id) || snapshot.DismissedTourIds.Contains(t.Id)))
            return null;

        var tour = highlights.Where(t => t.ParsedVersion <= current).OrderBy(t => t.ParsedVersion).LastOrDefault();
        if (tour == null)
            return null;

        var unseen = EligibleSteps(tour, ctx)
            .Where(s => !snapshot.SeenStepIds.Contains(s.Id))
            .ToList();
        var tokens = await _tokens.ResolveAsync(unseen.Select(s => s.Url), _siteContext.Slug);
        var steps = unseen.Select(s => Resolve(tour, s, ctx, tokens)).OfType<ResolvedTourStep>().ToList();
        if (steps.Count == 0)
            return null;

        return new TourOffer
        {
            Title = tour.Title,
            Summary = tour.Summary,
            IsHighlights = true,
            Steps = steps,
            TourIds = new List<string> { tour.Id },
            Automatic = true,
        };
    }

    /// <summary>Level rules plus predicates. Highlights tours only ever render major steps.</summary>
    private IEnumerable<TourStep> EligibleSteps(TourDefinition tour, TourPredicateResolver.PredicateContext ctx)
    {
        foreach (var step in tour.Steps)
        {
            if (tour.IsHighlights && !step.IsMajor)
                continue;
            if (!ctx.Satisfies(step.Requires, _siteContext.Slug, out _))
                continue;
            yield return step;
        }
    }

    private ResolvedTourStep? Resolve(
        TourDefinition tour, TourStep step, TourPredicateResolver.PredicateContext ctx,
        IReadOnlyDictionary<string, string?> tokens)
    {
        var url = TourUrlTokenResolver.Fill(step.Url, tokens);
        if (url == null)
        {
            _logger.LogDebug("Tour step {StepId} dropped: its url needs a token this site cannot fill", step.Id);
            return null;
        }

        ctx.Satisfies(step.Requires, _siteContext.Slug, out var siteSlug);
        return new ResolvedTourStep
        {
            Step = step,
            TourId = tour.Id,
            NavigateUrl = ctx.MultiSiteEnabled ? SiteContextService.WithSiteParam(url, siteSlug) : url,
        };
    }

    private static bool IsStillOfferable(TourStateService.Snapshot snapshot, string tourId, Version current)
    {
        if (!snapshot.Offers.TryGetValue(tourId, out var versions))
            return true;
        // "Later" never re-prompts within a minor line: patches ship every few days, and
        // re-arming the modal on each would both nag and burn the carry budget before
        // the next feature release. A deferred tour comes back folded into the next
        // minor's merged tour, and carry-forward stops after two carries.
        return versions.Count < MaxAutomaticOffers
            && !versions.Any(v => SameMinorLine(TourVersions.Parse(v), current));
    }

    private static bool SameMinorLine(Version? offered, Version current) =>
        offered != null && offered.Major == current.Major && offered.Minor == current.Minor;
}
