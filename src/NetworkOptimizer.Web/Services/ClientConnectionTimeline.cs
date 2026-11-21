using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// A client's connection events turned into the stretches it spent on each AP, for Client
/// Performance's Connection History: a bar per stretch, and a gap where the client was offline.
/// </summary>
public static class ClientConnectionTimeline
{
    /// <summary>One stretch on one AP (or switch / gateway for a wired client).</summary>
    /// <param name="Ap">The AP the client was on.</param>
    /// <param name="Band">UniFi's radio code (ng, na, 6e), or null for wired or unknown.</param>
    /// <param name="Start">When the stretch began, or the range start when it was already open.</param>
    /// <param name="End">When it ended, or the range end when it is still open.</param>
    /// <param name="Signal">The signal as the stretch began (dBm), when known.</param>
    /// <param name="EndSignal">The signal as it ended in a roam (dBm), when known.</param>
    /// <param name="EndedBy">How it ended: Roamed, Disconnected, or null when still open.</param>
    public sealed record Segment(
        string Ap, string? Band, DateTime Start, DateTime End,
        int? Signal, int? EndSignal, ClientConnectionEventType? EndedBy);

    /// <summary>The stretches, and what the range adds up to.</summary>
    public sealed record Result(
        IReadOnlyList<Segment> Segments, int Disconnects, int Roams, TimeSpan? LongestOffline);

    /// <summary>
    /// Builds the stretches from <paramref name="events"/> (any order) within
    /// [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>], all UTC. A stretch already open
    /// when the range starts begins there; one still open when it ends runs to the end, unless
    /// <paramref name="connectedNow"/> is false (its disconnect fell outside what the console kept).
    /// </summary>
    public static Result Build(
        IEnumerable<ClientConnectionEvent> events, DateTime rangeStart, DateTime rangeEnd, bool connectedNow)
    {
        var segments = new List<Segment>();
        var offline = new List<TimeSpan>();
        int disconnects = 0, roams = 0;

        string? ap = null, band = null;
        DateTime start = default;
        int? signal = null;
        DateTime? offlineSince = null;
        var first = true;

        void Close(DateTime at, int? endSignal, ClientConnectionEventType endedBy)
        {
            if (ap != null && at > start)
                segments.Add(new Segment(ap, band, start, at, signal, endSignal, endedBy));
            ap = null;
        }

        foreach (var e in events.OrderBy(e => e.Timestamp))
        {
            var at = e.Timestamp.UtcDateTime;
            if (at < rangeStart || at > rangeEnd) continue;
            switch (e.Type)
            {
                case ClientConnectionEventType.Connected:
                    if (offlineSince is { } since) offline.Add(at - since);
                    offlineSince = null;
                    Close(at, null, ClientConnectionEventType.Disconnected);
                    (ap, band, start, signal) = (e.ApName ?? "Unknown", e.RadioBand, at, e.Signal);
                    break;

                case ClientConnectionEventType.Roamed:
                    roams++;
                    // The first event a roam: the client was already on the AP it roamed from.
                    if (first) (ap, band, start, signal) = (e.FromApName ?? "Unknown", e.PreviousRadioBand, rangeStart, null);
                    Close(at, e.PreviousSignal, ClientConnectionEventType.Roamed);
                    (ap, band, start, signal) = (e.ToApName ?? e.ApName ?? "Unknown", e.RadioBand, at, e.Signal);
                    break;

                case ClientConnectionEventType.Disconnected:
                    disconnects++;
                    if (first) (ap, band, start, signal) = (e.ApName ?? "Unknown", e.RadioBand, rangeStart, null);
                    Close(at, e.Signal, ClientConnectionEventType.Disconnected);
                    offlineSince = at;
                    break;

                default:
                    continue;
            }
            first = false;
        }

        if (ap != null && connectedNow && rangeEnd > start)
            segments.Add(new Segment(ap, band, start, rangeEnd, signal, null, null));
        // Offline to the end of the range only when it really still is.
        if (offlineSince is { } stillOffline && !connectedNow) offline.Add(rangeEnd - stillOffline);

        return new Result(segments, disconnects, roams, offline.Count > 0 ? offline.Max() : null);
    }
}
