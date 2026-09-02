using System.Collections.Concurrent;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One radio move the agent reported, and what has been learned about it since.</summary>
public sealed class ApAgentChannelMove
{
    /// <summary>Observing AP MAC, lowercase.</summary>
    public required string ApMac { get; init; }

    /// <summary>Band code: "ng", "na", or "6e".</summary>
    public required string Band { get; init; }
    public required int FromChannel { get; init; }
    public int FromWidth { get; init; }
    public int? FromCenter { get; init; }
    public required int ToChannel { get; init; }
    public int ToWidth { get; init; }
    public int? ToCenter { get; init; }

    /// <summary>When the agent saw the move (UTC).</summary>
    public required DateTime At { get; init; }

    /// <summary>When the one-hour verdict can be reached.</summary>
    public DateTime VerdictDueAt => At + ApAgentChannelMoveTracker.VerdictAfter;

    /// <summary>The landing check has run (it runs once).</summary>
    public bool LandingChecked { get; set; }

    /// <summary>The one-hour verdict, once reached.</summary>
    public MoveOutcome? Outcome { get; set; }

    /// <summary>Interference percent over the hour before the move.</summary>
    public double? InterferenceBefore { get; set; }

    /// <summary>Interference percent over the first full hour after the move.</summary>
    public double? InterferenceAfter { get; set; }

    /// <summary>When the verdict was reached (UTC).</summary>
    public DateTime? VerdictAt { get; set; }
}

/// <summary>
/// The post-move loop's memory, pure and per site: which radios moved, whether the block they
/// landed in was the one the guess predicted, and how the destination measured after an hour
/// against the origin's last hour. Lost on restart, which only loses verdicts for moves in the
/// last day; the change log itself is persisted by the collector.
/// </summary>
public sealed class ApAgentChannelMoveTracker
{
    /// <summary>How long after a move its verdict is reached.</summary>
    public static readonly TimeSpan VerdictAfter = TimeSpan.FromHours(1);

    /// <summary>A move with no destination hour by then is given up on.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(3);

    /// <summary>Moves older than this are forgotten; the card shows a move for a day.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(26);

    /// <summary>Interference within this many points either way is Same.</summary>
    public const double SameBandPct = 5;

    private readonly ConcurrentDictionary<(string Mac, string Band), ApAgentChannelMove> _moves = new();

    /// <summary>Records a move, replacing any earlier one for the radio.</summary>
    public void Record(ApAgentChannelMove move) =>
        _moves[(move.ApMac.ToLowerInvariant(), move.Band)] = move;

    /// <summary>The latest move for a radio, or null.</summary>
    public ApAgentChannelMove? For(string apMac, string band) =>
        _moves.TryGetValue((apMac.ToLowerInvariant(), band), out var m) ? m : null;

    /// <summary>Every move still remembered.</summary>
    public IReadOnlyList<ApAgentChannelMove> All() => _moves.Values.ToList();

    /// <summary>
    /// The landing check, once per move: the block the guess predicted for the destination
    /// against the block the radio measured itself in. Null until the radio reports a center on
    /// the destination channel, and null forever after the first answer.
    /// </summary>
    public ((int Low, int High) Predicted, (int Low, int High) Landed)? CheckLanding(
        string apMac, string band, int channel, int width, int centerChannel)
    {
        var move = For(apMac, band);
        if (move == null || move.LandingChecked || channel != move.ToChannel) return null;
        move.LandingChecked = true;

        var radioBand = RadioBandExtensions.FromUniFiCode(band);
        var predicted = ChannelSpanHelper.GetChannelSpan(radioBand, channel, width);
        var landed = ChannelSpanHelper.GetChannelSpan(radioBand, channel, width, centerChannel);
        return (predicted, landed);
    }

    /// <summary>
    /// The one-hour verdict, once per move: the destination's first full hour against the
    /// origin's last hour, from the agent's own airtime hours. False until due, until both
    /// hours exist, or after a verdict. A move with no destination hour by
    /// <see cref="GiveUpAfter"/> is dropped without a verdict.
    /// </summary>
    public bool TryEvaluate(ApAgentChannelMove move, IReadOnlyList<ApAgentAirtimeHour> hours, DateTime nowUtc)
    {
        if (move.Outcome != null || nowUtc < move.VerdictDueAt) return false;

        var mine = hours.Where(h => h.ApMac.Equals(move.ApMac, StringComparison.OrdinalIgnoreCase) && h.Band == move.Band).ToList();
        var origin = mine.Where(h => h.Channel == move.FromChannel && h.HourUtc < move.At)
            .OrderByDescending(h => h.HourUtc).FirstOrDefault();
        var destination = mine.Where(h => h.Channel == move.ToChannel && h.HourUtc >= move.At)
            .OrderBy(h => h.HourUtc).FirstOrDefault();

        if (origin == null || destination == null)
        {
            if (nowUtc - move.At > GiveUpAfter)
                _moves.TryRemove((move.ApMac.ToLowerInvariant(), move.Band), out _);
            return false;
        }

        var delta = destination.AvgInterference - origin.AvgInterference;
        move.InterferenceBefore = origin.AvgInterference;
        move.InterferenceAfter = destination.AvgInterference;
        move.Outcome = delta <= -SameBandPct ? MoveOutcome.Improved
            : delta >= SameBandPct ? MoveOutcome.Worse
            : MoveOutcome.Same;
        move.VerdictAt = nowUtc;
        return true;
    }

    /// <summary>Forgets moves past retention.</summary>
    public void Prune(DateTime nowUtc)
    {
        foreach (var (key, move) in _moves.ToList())
            if (nowUtc - move.At > Retention)
                _moves.TryRemove(key, out _);
    }
}
