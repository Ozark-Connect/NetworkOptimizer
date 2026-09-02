using System.Collections.Concurrent;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One client an agent-covered access point currently holds, with what it knows about it.</summary>
/// <param name="ClientMac">The client key: MLD MAC for an MLO client, station MAC otherwise.</param>
/// <param name="ApMac">The access point holding it.</param>
/// <param name="Hostname">Hostname from the agent's identity poll, when it has run.</param>
/// <param name="Ip">IPv4 address from the agent's identity poll, when it has run.</param>
public sealed record ApAgentKnownClient(string ClientMac, string ApMac, string? Hostname, string? Ip);

/// <summary>Who joined and left one access point's answer since the previous one.</summary>
public sealed record MembershipDelta(IReadOnlyList<string> Joined, IReadOnlyList<string> Left)
{
    /// <summary>An answer that changed nothing.</summary>
    public static readonly MembershipDelta None = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Who each agent-covered access point said it holds, from the same polls that claim coverage.
///
/// This is what lets agent absence beat the Console's idle tolerance at the Console entry points:
/// an access point whose agent answered with clients, and did not name this one, is not holding it.
/// The answer is site-level on Present - a client ANY covered access point holds is present, so a
/// roam the agents have seen never reads as a departure while the Console still names the old AP.
///
/// A client can only be associated to one access point, but an access point holds a dead
/// association indefinitely when the client leaves without a disassoc (measured at 2.4 hours,
/// unauthorized, idle still climbing). Two answers therefore need reconciling, and it is done by
/// recency, not a threshold: the claim with the live association discards a rival old enough that
/// it cannot be the same association, and the discard STICKS until that access point sees a new
/// one - so when the winner later departs there is nothing left to resurrect the client. A
/// discarded or otherwise dead listing is kept as positive evidence of absence, never filtered:
/// "this AP lists the client and has not heard from it" is proof where "no AP lists it" is only
/// ambiguity.
/// </summary>
public sealed class ApAgentMembershipLedger
{
    /// <summary>
    /// How long an answer stands without being renewed. The same bound as the coverage claim,
    /// because both come from the same poll and must hand back to the Console path together.
    /// </summary>
    public static readonly TimeSpan AnswerTtl = ApAgentCoverageLedger.ClaimTtl;

    /// <summary>
    /// A claim at or under this idle is a live association: it clears its own discard and is the
    /// yardstick a rival claim is discarded against. Comfortably above the read jitter a real
    /// association shows between two poll passes.
    /// </summary>
    private const long FreshClaimIdleSeconds = 30;

    /// <summary>
    /// How much older than a fresh rival a claim must be before it is discarded. A real roam's
    /// double-claim skew is bounded by the agent's 6 s absent grace plus poll offsets, an order of
    /// magnitude under this, so no discard can form inside a roam window.
    /// </summary>
    private const long SupersedeMarginSeconds = 60;

    /// <summary>
    /// How recently the byte counters must have moved to keep vouching for a client past the idle
    /// tolerance. Movement between readings, never totals: an MLO link carries a few bytes at
    /// association and then freezes, so "has ever carried traffic" reads dead clients as alive.
    /// Reuses the idle constant deliberately - the two are one judgement of "recently active".
    /// </summary>
    private static readonly TimeSpan CounterMovementWindow = TimeSpan.FromSeconds(ClientPresence.MaxIdleSeconds);

    private sealed record ApAnswer(
        DateTime At,
        bool NamedAnyClient,
        HashSet<string> MemberMacs,
        HashSet<string> AbsentMacs,
        IReadOnlyList<ApAgentKnownClient> Members,
        HashSet<string> MemberKeys,
        IReadOnlyDictionary<string, long?> ClaimIdleByKey);

    private sealed record CounterTrack(long Total, DateTime ReadAt, DateTime? LastMovedAt);

    private readonly ConcurrentDictionary<string, ApAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _superseded = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CounterTrack> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one access point's answer. Returns whether the member set changed against the
    /// previous answer for this access point - false on the first answer, so a server restart or a
    /// newly enrolled agent does not read as churn.
    /// </summary>
    public bool Record(string apMac, IReadOnlyList<ApAgentClient> clients, DateTime at)
        => Record(apMac, clients, at, out _);

    /// <summary>
    /// As above, also reporting who joined and left. The delta is what makes a client appearing or
    /// disappearing on a surface traceable to the poll that saw it.
    /// </summary>
    public bool Record(string apMac, IReadOnlyList<ApAgentClient> clients, DateTime at, out MembershipDelta delta)
    {
        var ap = Normalize(apMac);
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var absentMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new List<ApAgentKnownClient>();
        var claimIdleByKey = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);

        // Same rule the telemetry gates apply: firmware that never reports the flag leaves every
        // client false, so it is trusted only where something in this answer set it.
        var authorizedIsReported = clients.Any(c => c.Authorized);

        foreach (var c in clients)
        {
            var key = Normalize(c.Key.Length > 0 ? c.Key : c.Mac);
            if (key.Length == 0) continue;

            var idle = c.Links.Count == 0 ? null : (long?)c.Links.Min(l => l.IdleSeconds);
            claimIdleByKey[key] = idle;

            ReconcileClaim(ap, key, idle, at);
            var countersAlive = NoteCounters(MarkKey(ap, key), c, at);

            // One class per listed client. Vouched means a live association: authenticated, not
            // discarded by a newer association elsewhere, and recently active - by idle, or by
            // counters still moving, which is what keeps a quiet-but-connected device on the map.
            // Everything else listed is positive evidence the client is NOT here.
            var vouched = !(authorizedIsReported && !c.Authorized)
                && !_superseded.ContainsKey(MarkKey(ap, key))
                && (ClientPresence.IsPresent(idle) || countersAlive);

            var target = vouched ? macs : absentMacs;
            target.Add(key);
            if (!string.IsNullOrEmpty(c.Mac)) target.Add(Normalize(c.Mac));
            if (!string.IsNullOrEmpty(c.MldMac)) target.Add(Normalize(c.MldMac));
            foreach (var link in c.Links)
            {
                if (!string.IsNullOrEmpty(link.Mac)) target.Add(Normalize(link.Mac));
            }

            if (!vouched) continue;

            keys.Add(key);
            members.Add(new ApAgentKnownClient(
                key, ap,
                string.IsNullOrWhiteSpace(c.Hostname) ? null : c.Hostname,
                string.IsNullOrWhiteSpace(c.Ip) ? null : c.Ip));
        }

        PruneUnlistedClaims(ap, claimIdleByKey);

        var changed = _answers.TryGetValue(ap, out var previous) && !previous.MemberKeys.SetEquals(keys);
        delta = previous == null
            ? MembershipDelta.None
            : new MembershipDelta(
                keys.Except(previous.MemberKeys, StringComparer.OrdinalIgnoreCase).ToList(),
                previous.MemberKeys.Except(keys, StringComparer.OrdinalIgnoreCase).ToList());
        _answers[ap] = new ApAnswer(at, clients.Count > 0, macs, absentMacs, members, keys, claimIdleByKey);
        return changed;
    }

    /// <summary>
    /// One-client-one-AP resolution, by recency rather than any threshold. A fresh claim clears
    /// its own discard and discards rivals too old to be the same association; the mirror runs so
    /// whichever side is recorded first still resolves the pair. A quiet device is the only claim
    /// on itself, so nothing here can ever touch it.
    /// </summary>
    private void ReconcileClaim(string ap, string key, long? idle, DateTime at)
    {
        if (idle is not { } i) return;
        var mark = MarkKey(ap, key);

        if (i <= FreshClaimIdleSeconds)
        {
            _superseded.TryRemove(mark, out _);
            foreach (var (otherAp, other) in _answers)
            {
                if (string.Equals(otherAp, ap, StringComparison.OrdinalIgnoreCase)) continue;
                if (at - other.At > AnswerTtl) continue;
                if (other.ClaimIdleByKey.TryGetValue(key, out var rival)
                    && rival is { } r && r - i >= SupersedeMarginSeconds)
                {
                    _superseded[MarkKey(otherAp, key)] = at;
                }
            }
            return;
        }

        foreach (var (otherAp, other) in _answers)
        {
            if (string.Equals(otherAp, ap, StringComparison.OrdinalIgnoreCase)) continue;
            if (at - other.At > AnswerTtl) continue;
            if (other.ClaimIdleByKey.TryGetValue(key, out var rival)
                && rival is { } r
                && r <= FreshClaimIdleSeconds
                && i - r >= SupersedeMarginSeconds)
            {
                _superseded[mark] = at;
                return;
            }
        }
    }

    /// <summary>
    /// Tracks the claim's byte counters and answers whether they moved recently. Only a CHANGE
    /// between two readings counts as movement; a first reading proves nothing either way.
    /// </summary>
    private bool NoteCounters(string mark, ApAgentClient client, DateTime at)
    {
        long total = 0;
        DateTime? bytesAt = null;
        foreach (var link in client.Links)
        {
            total += link.TxBytes + link.RxBytes;
            if (link.BytesAt is { } b && (bytesAt is not { } best || b > best)) bytesAt = b;
        }
        var readAt = bytesAt ?? at;

        if (!_counters.TryGetValue(mark, out var prev))
        {
            _counters[mark] = new CounterTrack(total, readAt, null);
            return false;
        }

        var moved = prev.LastMovedAt;
        if (total != prev.Total) moved = readAt;
        if (readAt != prev.ReadAt || total != prev.Total)
            _counters[mark] = new CounterTrack(total, readAt, moved);

        return moved is { } m && at - m <= CounterMovementWindow;
    }

    /// <summary>
    /// How many clients one access point's fresh answer vouches for; null without a fresh answer.
    /// Exact where the console's count lags its report interval.
    /// </summary>
    public int? MemberCount(string apMac, DateTime now)
        => _answers.TryGetValue(Normalize(apMac), out var answer) && now - answer.At <= AnswerTtl
            ? answer.MemberKeys.Count
            : null;

    /// <summary>
    /// Whether this access point's claim on the client has been discarded because a newer
    /// association exists elsewhere. The telemetry paths consult this so a dead entry's readings
    /// are not written or published as if the client were still on that access point.
    /// </summary>
    public bool IsClaimSuperseded(string apMac, string clientMac)
        => _superseded.ContainsKey(MarkKey(Normalize(apMac), Normalize(clientMac)));

    /// <summary>Forgets one access point's answer, mirroring a coverage release.</summary>
    public void Release(string apMac)
    {
        var ap = Normalize(apMac);
        _answers.TryRemove(ap, out _);
        DropApState(ap);
    }

    /// <summary>Forgets everything.</summary>
    public void ReleaseAll()
    {
        _answers.Clear();
        _superseded.Clear();
        _counters.Clear();
    }

    /// <summary>Drops answers for access points no longer on the site.</summary>
    public void RetainOnly(IReadOnlySet<string> apMacs)
    {
        foreach (var key in _answers.Keys)
        {
            if (apMacs.Contains(key)) continue;
            _answers.TryRemove(key, out _);
            DropApState(key);
        }
    }

    /// <summary>
    /// The agent verdict for one client. Present when any fresh answer holds it live - checked
    /// first, so a mid-roam client is never judged by the access point it just left. Absent when
    /// any fresh answer lists it dead - discarded, unauthenticated, or gone quiet past every
    /// tolerance - which needs no claimed AP at all, or when the claimed access point's fresh
    /// answer named at least one client and not this one. A client in no answer whatsoever stays
    /// Unknown: an empty or missing answer must never read as departure, or an agent restart or
    /// tunnel blip would mass-drop a site.
    /// </summary>
    /// <summary>
    /// How many access points answered within the TTL. Compared against the site's target count,
    /// this is what says the agents can see the whole site right now.
    ///
    /// An answer holding no clients counts: it is only recorded after a poll succeeded, so an
    /// empty access point is evidence that the client is not on it, exactly as a populated one is.
    /// Requiring a named client instead pinned a site with any idle access point below its target
    /// count forever, and the rule could never fire.
    /// </summary>
    public int FreshAnswers(DateTime now)
    {
        var n = 0;
        foreach (var answer in _answers.Values)
        {
            if (now - answer.At <= AnswerTtl) n++;
        }
        return n;
    }

    public AgentClientPresence PresenceFor(string? apMac, string? clientMac, DateTime now)
    {
        var mac = Normalize(clientMac);
        if (mac.Length == 0) return AgentClientPresence.Unknown;

        var listedDead = false;
        foreach (var answer in _answers.Values)
        {
            if (now - answer.At > AnswerTtl) continue;
            if (answer.MemberMacs.Contains(mac)) return AgentClientPresence.Present;
            if (answer.AbsentMacs.Contains(mac)) listedDead = true;
        }
        if (listedDead) return AgentClientPresence.Absent;

        if (_answers.TryGetValue(Normalize(apMac), out var claimed)
            && now - claimed.At <= AnswerTtl
            && claimed.NamedAnyClient)
        {
            return AgentClientPresence.Absent;
        }

        return AgentClientPresence.Unknown;
    }

    /// <summary>The client a fresh answer reports at this IPv4 address, or null when none does.</summary>
    public ApAgentKnownClient? FindByIp(string ip, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;

        foreach (var answer in _answers.Values)
        {
            if (now - answer.At > AnswerTtl) continue;
            foreach (var member in answer.Members)
            {
                if (string.Equals(member.Ip, ip, StringComparison.OrdinalIgnoreCase)) return member;
            }
        }
        return null;
    }

    /// <summary>Drops per-claim state for clients this access point no longer lists at all.</summary>
    private void PruneUnlistedClaims(string ap, Dictionary<string, long?> claims)
    {
        var prefix = ap + "|";
        foreach (var mark in _superseded.Keys)
        {
            if (mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !claims.ContainsKey(mark[prefix.Length..]))
            {
                _superseded.TryRemove(mark, out _);
            }
        }
        foreach (var mark in _counters.Keys)
        {
            if (mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !claims.ContainsKey(mark[prefix.Length..]))
            {
                _counters.TryRemove(mark, out _);
            }
        }
    }

    private void DropApState(string ap)
    {
        var prefix = ap + "|";
        foreach (var mark in _superseded.Keys)
        {
            if (mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _superseded.TryRemove(mark, out _);
        }
        foreach (var mark in _counters.Keys)
        {
            if (mark.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) _counters.TryRemove(mark, out _);
        }
    }

    private static string MarkKey(string ap, string key) => ap + "|" + key;

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}
