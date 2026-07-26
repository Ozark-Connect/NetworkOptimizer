namespace NetworkOptimizer.Web.Services.Tours;

/// <summary>
/// Pure merged-tour construction: ordering, level filtering, dedup, and the step cap.
/// No services, no I/O - unit-testable in isolation.
/// </summary>
public static class TourMergePlanner
{
    /// <summary>
    /// A user several releases behind gets one merged tour, not one per release. Capped
    /// so a marathon never happens; minor steps are dropped first, then oldest, and the
    /// caller surfaces the dropped count in the offer modal.
    /// </summary>
    public const int StepCap = 8;

    public record PlannedStep(TourDefinition Tour, TourStep Step);

    public record MergePlan(List<PlannedStep> Steps, int DroppedCount);

    /// <summary>
    /// Builds the merged step list from every eligible tour.
    /// Tours must already be filtered for eligibility (kind, version window, dismissals,
    /// offer limits); <paramref name="stepIncluded"/> carries level, predicate and
    /// seen-state filtering so this stays pure.
    /// </summary>
    public static MergePlan Build(
        IEnumerable<TourDefinition> toursAscending,
        Func<TourDefinition, TourStep, bool> stepIncluded,
        int cap = StepCap)
    {
        // Order by release ascending, then declared order; dedup by step id keeping the
        // newest copy so a step revised in a later release wins.
        var byId = new Dictionary<string, PlannedStep>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var tour in toursAscending)
        {
            foreach (var step in tour.Steps)
            {
                if (!stepIncluded(tour, step))
                    continue;
                if (byId.ContainsKey(step.Id))
                    order.Remove(step.Id);
                byId[step.Id] = new PlannedStep(tour, step);
                order.Add(step.Id);
            }
        }

        var steps = order.Select(id => byId[id]).ToList();
        if (steps.Count <= cap)
            return new MergePlan(steps, 0);

        // Drop minor first (oldest first), then oldest of the rest.
        var toDrop = steps.Count - cap;
        var dropSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in steps.Where(s => s.Step.IsMinor).Concat(steps.Where(s => !s.Step.IsMinor)))
        {
            if (dropSet.Count >= toDrop)
                break;
            dropSet.Add(candidate.Step.Id);
        }

        return new MergePlan(steps.Where(s => !dropSet.Contains(s.Step.Id)).ToList(), dropSet.Count);
    }
}
