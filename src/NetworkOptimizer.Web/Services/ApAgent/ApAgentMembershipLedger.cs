using System.Collections.Concurrent;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One client an agent-covered access point currently holds, with what it knows about it.</summary>
/// <param name="ClientMac">The client key: MLD MAC for an MLO client, station MAC otherwise.</param>
/// <param name="ApMac">The access point holding it.</param>
/// <param name="Hostname">Hostname from the agent's identity poll, when it has run.</param>
/// <param name="Ip">IPv4 address from the agent's identity poll, when it has run.</param>
public sealed record ApAgentKnownClient(string ClientMac, string ApMac, string? Hostname, string? Ip);

/// <summary>
/// Who each agent-covered access point said it holds, from the same polls that claim coverage.
///
/// This is what lets agent absence beat the Console's idle tolerance at the Console entry points:
/// an access point whose agent answered with clients, and did not name this one, is not holding it.
/// The answer is site-level on Present - a client ANY covered access point holds is present, so a
/// roam the agents have seen never reads as a departure while the Console still names the old AP.
/// </summary>
public sealed class ApAgentMembershipLedger
{
    /// <summary>
    /// How long an answer stands without being renewed. The same bound as the coverage claim,
    /// because both come from the same poll and must hand back to the Console path together.
    /// </summary>
    public static readonly TimeSpan AnswerTtl = ApAgentCoverageLedger.ClaimTtl;

    private sealed record ApAnswer(
        DateTime At,
        bool NamedAnyClient,
        HashSet<string> MemberMacs,
        IReadOnlyList<ApAgentKnownClient> Members,
        HashSet<string> MemberKeys);

    private readonly ConcurrentDictionary<string, ApAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records one access point's answer. Returns whether the member set changed against the
    /// previous answer for this access point - false on the first answer, so a server restart or a
    /// newly enrolled agent does not read as churn.
    /// </summary>
    public bool Record(string apMac, IReadOnlyList<ApAgentClient> clients, DateTime at)
    {
        var ap = Normalize(apMac);
        var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var members = new List<ApAgentKnownClient>();

        // Same rule the telemetry gates apply: firmware that never reports the flag leaves every
        // client false, so it is trusted only where something in this answer set it.
        var authorizedIsReported = clients.Any(c => c.Authorized);

        foreach (var c in clients)
        {
            if (authorizedIsReported && !c.Authorized) continue;

            // The agent's own table can hold a dead association indefinitely (a measured MLO one
            // sat 80 minutes idle, still listed). The idle rule keeps such a client out of the
            // member set, so it reads Absent here rather than being vouched for.
            var idle = c.Links.Count == 0 ? null : (long?)c.Links.Min(l => l.IdleSeconds);
            if (!ClientPresence.IsPresent(idle)) continue;

            var key = Normalize(c.Key.Length > 0 ? c.Key : c.Mac);
            if (key.Length == 0) continue;

            keys.Add(key);
            macs.Add(key);
            if (!string.IsNullOrEmpty(c.Mac)) macs.Add(Normalize(c.Mac));
            if (!string.IsNullOrEmpty(c.MldMac)) macs.Add(Normalize(c.MldMac));
            foreach (var link in c.Links)
            {
                if (!string.IsNullOrEmpty(link.Mac)) macs.Add(Normalize(link.Mac));
            }

            members.Add(new ApAgentKnownClient(
                key, ap,
                string.IsNullOrWhiteSpace(c.Hostname) ? null : c.Hostname,
                string.IsNullOrWhiteSpace(c.Ip) ? null : c.Ip));
        }

        var changed = _answers.TryGetValue(ap, out var previous) && !previous.MemberKeys.SetEquals(keys);
        _answers[ap] = new ApAnswer(at, clients.Count > 0, macs, members, keys);
        return changed;
    }

    /// <summary>Forgets one access point's answer, mirroring a coverage release.</summary>
    public void Release(string apMac) => _answers.TryRemove(Normalize(apMac), out _);

    /// <summary>Forgets everything.</summary>
    public void ReleaseAll() => _answers.Clear();

    /// <summary>Drops answers for access points no longer on the site.</summary>
    public void RetainOnly(IReadOnlySet<string> apMacs)
    {
        foreach (var key in _answers.Keys)
        {
            if (!apMacs.Contains(key)) _answers.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// The agent verdict for one client. Present when any fresh answer holds it; Absent only when
    /// the claimed access point's fresh answer named at least one client and not this one - an
    /// empty answer yields Unknown, so an agent restart or tunnel blip cannot mass-drop a site.
    /// </summary>
    public AgentClientPresence PresenceFor(string? apMac, string? clientMac, DateTime now)
    {
        var mac = Normalize(clientMac);
        if (mac.Length == 0) return AgentClientPresence.Unknown;

        foreach (var answer in _answers.Values)
        {
            if (now - answer.At <= AnswerTtl && answer.MemberMacs.Contains(mac))
                return AgentClientPresence.Present;
        }

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

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}
