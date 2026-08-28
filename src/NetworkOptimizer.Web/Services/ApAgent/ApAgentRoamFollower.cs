namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>Where the follow believes one client is.</summary>
public enum ApAgentFollowState
{
    /// <summary>No access point is being followed yet.</summary>
    Idle,

    /// <summary>The client is on a known access point and that access point is being polled.</summary>
    Attached,

    /// <summary>The client left, and neighbouring access points are being probed for it.</summary>
    Searching,

    /// <summary>The search window closed without finding it, or the agent path is unusable.</summary>
    Lost,
}

/// <summary>
/// Follows one client across a roam, so Client Performance tracks it to its new access point
/// instead of freezing on the old one.
///
/// Pure state and time in, probe list out: every request is issued by the caller. The one rule that
/// is not obvious is that an unreachable agent is NOT a roam. Searching the site because one
/// access point stopped answering would fan out over the fleet for a fault that belongs on the
/// fallback path, so it drops to <see cref="ApAgentFollowState.Lost"/> instead.
/// </summary>
public sealed class ApAgentRoamFollower
{
    /// <summary>
    /// How long the fleet is probed after a client leaves. A roam completes in well under a second;
    /// this is generous enough for a slow reassociation and short enough that a client that walked
    /// out of the building stops costing requests.
    /// </summary>
    public static readonly TimeSpan SearchWindow = TimeSpan.FromSeconds(12);

    /// <summary>Access points probed per tick, so the fan-out is a trickle rather than a burst.</summary>
    public const int MaxProbesPerTick = 3;

    /// <summary>Ceiling on how many access points one search will ever consider.</summary>
    public const int MaxCandidates = 24;

    /// <summary>
    /// How long a closed search refuses to be re-seeded onto the access point that lost the
    /// client. Without it the console's stale view restarts the same fruitless search every tick
    /// for as long as the page is open. A console reporting a DIFFERENT access point is adopted
    /// straight away, which is the normal way a client comes back.
    /// </summary>
    public static readonly TimeSpan ReseedCooldown = SearchWindow;

    private readonly List<string> _candidates = new();
    private int _cursor;
    private string? _leftAp;
    private string? _peerHint;
    private string? _refusedAp;
    private DateTime _searchStartedAt;
    private DateTime _lostAt;

    /// <summary>Where the follow believes the client is.</summary>
    public ApAgentFollowState State { get; private set; } = ApAgentFollowState.Idle;

    /// <summary>The access point being polled, or null while searching or lost.</summary>
    public string? CurrentAp { get; private set; }

    /// <summary>The access point the client left, kept so the search does not probe it again.</summary>
    public string? PreviousAp => _leftAp;

    /// <summary>Whether a roam is being followed right now.</summary>
    public bool IsSearching => State == ApAgentFollowState.Searching;

    /// <summary>
    /// Whether the follow should adopt the access point the console reports. False while it is
    /// already following, and false for the access point a just-closed search already ruled out.
    /// </summary>
    public bool ShouldSeed(string? apMac, DateTime now)
    {
        if (State is ApAgentFollowState.Attached or ApAgentFollowState.Searching) return false;
        if (string.IsNullOrWhiteSpace(apMac)) return false;

        return _refusedAp == null
            || !string.Equals(Normalize(apMac), _refusedAp, StringComparison.Ordinal)
            || now - _lostAt >= ReseedCooldown;
    }

    /// <summary>
    /// Records that the client is on this access point. Called both to seed the follow from the
    /// console's own view and to complete a search.
    /// </summary>
    public void Seen(string apMac)
    {
        var mac = Normalize(apMac);
        if (mac.Length == 0) return;

        if (State == ApAgentFollowState.Searching && !string.Equals(mac, _leftAp, StringComparison.Ordinal))
            _leftAp = null;

        CurrentAp = mac;
        State = ApAgentFollowState.Attached;
        _candidates.Clear();
        _cursor = 0;
        _peerHint = null;
        _refusedAp = null;
    }

    /// <summary>
    /// Records that the access point answered and does not have the client: it roamed. The peer
    /// BSSID, when the access point announced one, only orders the candidates.
    /// </summary>
    public void Left(DateTime at, string? peerBssid = null)
    {
        if (State == ApAgentFollowState.Searching)
        {
            // A hint that arrives after the order was built only helps if the order is rebuilt.
            if (string.IsNullOrEmpty(_peerHint) && Normalize(peerBssid).Length > 0)
            {
                _peerHint = Normalize(peerBssid);
                _candidates.Clear();
            }
            return;
        }

        _leftAp = CurrentAp;
        _peerHint = Normalize(peerBssid);
        CurrentAp = null;
        State = ApAgentFollowState.Searching;
        _searchStartedAt = at;
        _candidates.Clear();
        _cursor = 0;
    }

    /// <summary>
    /// Records that the agent could not be reached. The client's whereabouts are unknown rather
    /// than changed, so the caller falls back rather than searching.
    /// </summary>
    public void Stalled()
    {
        CurrentAp = null;
        State = ApAgentFollowState.Lost;
        _candidates.Clear();
        _cursor = 0;
        _leftAp = null;
        _peerHint = null;
    }

    /// <summary>Clears everything, for a page that switched to a different client.</summary>
    public void Reset()
    {
        Stalled();
        State = ApAgentFollowState.Idle;
    }

    /// <summary>
    /// The access points to probe on this tick, empty unless a search is running. The window is
    /// checked here, so a client that never reappears stops the fan-out on its own.
    /// </summary>
    public IReadOnlyList<string> NextProbes(IReadOnlyList<string> siteApMacs, DateTime at)
    {
        if (State != ApAgentFollowState.Searching) return Array.Empty<string>();

        if (at - _searchStartedAt > SearchWindow)
        {
            State = ApAgentFollowState.Lost;
            _refusedAp = _leftAp;
            _lostAt = at;
            _candidates.Clear();
            return Array.Empty<string>();
        }

        if (_candidates.Count == 0) BuildCandidates(siteApMacs);
        if (_candidates.Count == 0) return Array.Empty<string>();

        var take = Math.Min(MaxProbesPerTick, _candidates.Count);
        var probes = new List<string>(take);
        for (var i = 0; i < take; i++)
        {
            probes.Add(_candidates[_cursor]);
            _cursor = (_cursor + 1) % _candidates.Count;
        }
        return probes;
    }

    /// <summary>
    /// Orders the fleet for this search: the announced peer first, then everything else in the
    /// order the site listed it, minus the access point the client just left.
    /// </summary>
    private void BuildCandidates(IReadOnlyList<string> siteApMacs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rest = new List<string>();

        foreach (var raw in siteApMacs)
        {
            var mac = Normalize(raw);
            if (mac.Length == 0) continue;
            if (string.Equals(mac, _leftAp, StringComparison.Ordinal)) continue;
            if (!seen.Add(mac)) continue;

            if (MatchesPeerHint(mac)) _candidates.Add(mac);
            else rest.Add(mac);
        }

        _candidates.AddRange(rest);
        if (_candidates.Count > MaxCandidates) _candidates.RemoveRange(MaxCandidates, _candidates.Count - MaxCandidates);
        _cursor = 0;
    }

    /// <summary>
    /// Whether an access point's MAC plausibly owns the announced BSSID. A UniFi BSSID is derived
    /// from the device MAC and differs from it in the last octet, so five octets is the match and
    /// the sixth is deliberately ignored. Ordering only: a wrong guess costs one probe.
    /// </summary>
    private bool MatchesPeerHint(string apMac)
    {
        if (string.IsNullOrEmpty(_peerHint)) return false;
        const int prefix = 14; // "aa:bb:cc:dd:ee" - five octets and their separators.
        return _peerHint.Length >= prefix && apMac.Length >= prefix
            && string.CompareOrdinal(_peerHint, 0, apMac, 0, prefix) == 0;
    }

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}
