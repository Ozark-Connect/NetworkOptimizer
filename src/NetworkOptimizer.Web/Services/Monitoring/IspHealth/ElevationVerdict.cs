namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Whether a line's elevation under load is OVER - the operator fixed it - or still happening.
/// <para>
/// Asked this way round because of what the noise floor does downstream. Most loaded samples sit
/// near zero even while a line misbehaves, so the floor keeps only the elevated minority and the
/// figure reported is the median OF THE BAD ONES. Comparing medians cannot see a fix there: the
/// median over everything is ~0 before and after. Whether elevation is still HAPPENING can be
/// seen, and that is the question an operator is really asking.
/// </para>
/// <para>
/// Pure and separate from the scorer so the rule can be tested directly. Every branch here was
/// found by being wrong about a real WAN first.
/// </para>
/// </summary>
internal static class ElevationVerdict
{
    /// <param name="CleanRun">The newest episodes, all below the floor, ending at the first elevated one.</param>
    /// <param name="ElevatedCount">Episodes anywhere in the window that were elevated.</param>
    /// <param name="ProblemHourReTested">Whether the clean run covers the hour elevation appeared at.</param>
    /// <param name="ElevationIsOver">The verdict: everything above agreeing that it stopped.</param>
    internal sealed record Verdict(
        IReadOnlyList<(DateTime Time, double Value)> CleanRun,
        int ElevatedCount,
        bool ProblemHourReTested,
        bool ElevationIsOver);

    /// <param name="episodesNewestFirst">One value per load episode, newest first.</param>
    /// <param name="noiseFloor">Added delay below which an episode counts as clean.</param>
    /// <param name="staleEpisodes">Clean episodes in a row required to call the elevation over.</param>
    /// <param name="needsSameHour">Whether a cyclical problem must be re-tested at its own hour.</param>
    /// <param name="episodeSpan">How long one episode's window covers, for hour attribution.</param>
    /// <param name="hourDependenceFloor">Below this an older episode counts as clean when deciding
    /// whether the history shows hour-dependence at all.</param>
    internal static Verdict For(
        IReadOnlyList<(DateTime Time, double Value)> episodesNewestFirst,
        double noiseFloor,
        int staleEpisodes,
        bool needsSameHour,
        TimeSpan episodeSpan,
        double hourDependenceFloor)
    {
        var cleanRun = episodesNewestFirst.TakeWhile(e => e.Value < noiseFloor).ToList();
        var elevated = episodesNewestFirst.Where(e => e.Value >= noiseFloor).ToList();
        if (elevated.Count == 0)
        {
            // Nothing was ever elevated, so there is nothing to declare over. A line that has
            // always been clean takes the path it always took.
            return new Verdict(cleanRun, 0, false, false);
        }

        var older = episodesNewestFirst.Skip(cleanRun.Count).ToList();
        var hourReTested = !needsSameHour
            || !ShowsHourDependence(older, hourDependenceFloor)
            || CoversProblemHour(cleanRun, elevated, episodeSpan);

        var over = cleanRun.Count >= staleEpisodes && hourReTested;
        return new Verdict(cleanRun, elevated.Count, hourReTested, over);
    }

    /// <summary>
    /// Whether the history before the clean run varied by hour at all. If EVERY earlier episode was
    /// elevated, the line misbehaved whenever it was loaded - the hour was never the variable, so a
    /// clean run at any hour disproves it. Requiring the same hour there would hold a fix hostage
    /// to whenever the line is next busy, which on a WAN whose only regular load is a scheduled
    /// speed test is the following day.
    /// </summary>
    private static bool ShowsHourDependence(
        IReadOnlyList<(DateTime Time, double Value)> older, double floor)
        => older.Any(e => e.Value < floor);

    /// <summary>
    /// Whether the clean run covers the hour of day elevation appeared at - the hour with the most
    /// elevated episodes. A nightly problem otherwise clears itself: a line that bufferbloats every
    /// evening is clean all night, so a run computed at 3 AM finds clean episodes on top of
    /// elevated ones and calls it fixed. "It has been fine since" means nothing if the since never
    /// covered the hour it went wrong.
    /// </summary>
    private static bool CoversProblemHour(
        IReadOnlyList<(DateTime Time, double Value)> cleanRun,
        IReadOnlyList<(DateTime Time, double Value)> elevated,
        TimeSpan episodeSpan)
    {
        IEnumerable<int> HoursOf((DateTime Time, double Value) episode) =>
            UsageWeighting.LocalHoursSpanned(episode.Time, episode.Time + episodeSpan, TimeZoneInfo.Local);

        var problemHour = elevated
            .SelectMany(HoursOf)
            .GroupBy(h => h)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .First().Key;

        return cleanRun.SelectMany(HoursOf).Contains(problemHour);
    }
}
