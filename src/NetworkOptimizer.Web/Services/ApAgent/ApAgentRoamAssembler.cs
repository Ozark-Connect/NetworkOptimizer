namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One event, tagged with the access point that reported it.</summary>
/// <param name="ObserverApMac">The access point whose ring the event came out of.</param>
/// <param name="Event">The event itself.</param>
/// <param name="AfterGap">True when that access point's replay window had been overwritten.</param>
public sealed record ApRoamObservedEvent(string ObserverApMac, ApAgentEvent Event, bool AfterGap);

/// <summary>
/// One roam being built. It stays open for a short window because the same roam reaches us from
/// several access points, and it carries <see cref="RecordId"/> once persisted so a later
/// observation updates that row instead of writing a second one.
/// </summary>
public sealed class ApRoamCandidate
{
    /// <summary>The client, on its MLD MAC when the link MAC resolved to one.</summary>
    public string ClientMac { get; internal set; } = "";

    /// <summary>The link MAC the access point named.</summary>
    public string? LinkMac { get; internal set; }

    /// <summary>The access point the client left.</summary>
    public string? FromApMac { get; internal set; }

    /// <summary>The BSSID the client left.</summary>
    public string? FromBssid { get; internal set; }

    /// <summary>Band of the BSSID the client left.</summary>
    public string? FromBand { get; internal set; }

    /// <summary>Channel of the BSSID the client left.</summary>
    public int? FromChannel { get; internal set; }

    /// <summary>The access point the client joined, when it is one of ours.</summary>
    public string? ToApMac { get; internal set; }

    /// <summary>The BSSID the client joined.</summary>
    public string? ToBssid { get; internal set; }

    /// <summary>Band of the joined BSSID.</summary>
    public string? Band { get; internal set; }

    /// <summary>Channel of the joined BSSID.</summary>
    public int? Channel { get; internal set; }

    /// <summary>The access point's clock when the roam landed.</summary>
    public DateTime RoamedAt { get; internal set; }

    /// <summary>Seconds the client held the association it left.</summary>
    public double? DwellSeconds { get; internal set; }

    /// <summary>True when events were lost before this roam, so the previous access point may be wrong.</summary>
    public bool AfterEventGap { get; internal set; }

    /// <summary>How the roam was seen. First-hand association beats cross-AP gossip.</summary>
    public string Source { get; internal set; } = "";

    /// <summary>Every access point that reported it, in the order they were heard from.</summary>
    public List<string> Observers { get; } = new();

    /// <summary>The persisted row's id, set by the collector after the first save.</summary>
    public int? RecordId { get; set; }

    /// <summary>Whether the candidate changed since it was last written.</summary>
    public bool Dirty { get; internal set; } = true;
}

/// <summary>
/// Folds AP Agent membership events into one row per roam.
///
/// A single roam is reported up to three times: the gaining access point sees the association, the
/// losing one sees the disassociation, and any peer may be told about it over UBNT_ROAM gossip.
/// Observations naming the same client landing on the same BSSID within <see cref="DedupWindow"/>
/// are therefore one roam, and the later ones fill in fields the first one lacked rather than
/// writing a second row.
///
/// Pure state in memory, no I/O: the collector feeds it events and persists what comes back.
/// </summary>
public sealed class ApAgentRoamAssembler
{
    /// <summary>
    /// How far apart two reports of the same landing may be and still be one roam. A roam completes
    /// in well under a second; the width here is for the poll and gossip delay behind it.
    /// </summary>
    public static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(10);

    /// <summary>Beyond this, a client turning up elsewhere is a fresh visit rather than a roam.</summary>
    public static readonly TimeSpan RoamMaxGap = TimeSpan.FromMinutes(30);

    /// <summary>After an observed disassociation, a join this much later is a rejoin, not a roam.</summary>
    public static readonly TimeSpan RejoinGap = TimeSpan.FromSeconds(60);

    /// <summary>How long a candidate stays open for late observations before it is forgotten.</summary>
    private static readonly TimeSpan CandidateRetention = TimeSpan.FromMinutes(5);

    /// <summary>Ceiling on tracked clients, so a busy site cannot grow this without bound.</summary>
    private const int MaxTrackedClients = 8192;

    private readonly Dictionary<string, Dictionary<string, ApAgentVap>> _vapsByAp = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _apByBssid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ApAgentVap> _vapByBssid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _clientKeyByLinkMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Association> _lastAssoc = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ApRoamCandidate> _open = new();

    /// <summary>Records one access point's VAP table, which is what resolves an event to a BSSID.</summary>
    public void SetVaps(string apMac, IReadOnlyList<ApAgentVap> vaps)
    {
        var mac = Normalize(apMac);
        if (mac.Length == 0) return;

        var byName = new Dictionary<string, ApAgentVap>(StringComparer.OrdinalIgnoreCase);
        foreach (var vap in vaps)
        {
            if (!string.IsNullOrEmpty(vap.Name)) byName[vap.Name] = vap;
            var bssid = Normalize(vap.Bssid);
            if (bssid.Length == 0) continue;
            _apByBssid[bssid] = mac;
            _vapByBssid[bssid] = vap;
        }
        _vapsByAp[mac] = byName;
    }

    /// <summary>
    /// Records that a link MAC belongs to a client. An MLO client associates under a different MAC
    /// per link, so without this every link of one Wi-Fi 7 client reads as a separate client.
    /// </summary>
    public void NoteClientKey(string? linkMac, string? clientKey)
    {
        var link = Normalize(linkMac);
        var key = Normalize(clientKey);
        if (link.Length == 0 || key.Length == 0 || link == key) return;
        if (_clientKeyByLinkMac.Count > MaxTrackedClients) _clientKeyByLinkMac.Clear();
        _clientKeyByLinkMac[link] = key;
    }

    /// <summary>
    /// Folds a batch of events into roams. The returned candidates are the ones this batch touched:
    /// one with no <see cref="ApRoamCandidate.RecordId"/> is new, and one that already has an id
    /// gained an observation and must be updated in place.
    /// </summary>
    public IReadOnlyList<ApRoamCandidate> Process(IEnumerable<ApRoamObservedEvent> events)
    {
        var touched = new List<ApRoamCandidate>();

        foreach (var observed in events.OrderBy(e => e.Event.At).ThenBy(e => e.Event.Seq))
        {
            var candidate = Observe(observed);
            if (candidate != null && !touched.Contains(candidate)) touched.Add(candidate);
        }

        Prune();
        return touched;
    }

    private ApRoamCandidate? Observe(ApRoamObservedEvent observed)
    {
        var e = observed.Event;
        var observer = Normalize(observed.ObserverApMac);
        var linkMac = Normalize(e.Mac);
        if (linkMac.Length == 0) return null;

        var key = _clientKeyByLinkMac.TryGetValue(linkMac, out var mapped) ? mapped : linkMac;

        switch (e.Type)
        {
            case ApAgentEventTypes.Disassoc:
                if (_lastAssoc.TryGetValue(key, out var held) && MacEquals(held.ApMac, observer))
                    held.LeftAt = e.At;
                return null;

            case ApAgentEventTypes.Assoc:
            {
                var vap = ResolveVap(observer, e.Vap);
                var destination = new Destination(observer, Normalize(vap?.Bssid), vap?.Band, vap?.Channel);
                return Land(key, linkMac, destination, e.At, observer, RoamSources.Assoc, observed.AfterGap);
            }

            case ApAgentEventTypes.RoamBroadcast:
            case ApAgentEventTypes.RoamToPeer:
            {
                var bssid = Normalize(e.PeerBssid);
                if (bssid.Length == 0) return null;
                var peerAp = _apByBssid.TryGetValue(bssid, out var owner) ? owner : null;
                var vap = _vapByBssid.TryGetValue(bssid, out var v) ? v : null;
                var destination = new Destination(peerAp, bssid, vap?.Band, vap?.Channel);
                var source = e.Type == ApAgentEventTypes.RoamToPeer ? RoamSources.RoamToPeer : RoamSources.RoamBroadcast;
                return Land(key, linkMac, destination, e.At, observer, source, observed.AfterGap);
            }

            default:
                return null;
        }
    }

    private ApRoamCandidate? Land(
        string key, string linkMac, Destination destination, DateTime at, string observer, string source, bool afterGap)
    {
        var prior = _lastAssoc.GetValueOrDefault(key);

        if (prior != null && SameDestination(prior, destination))
        {
            // The client is where we already believe it is, so this is a second report of a landing
            // we already hold rather than a new one.
            var open = FindOpen(key, destination, at);
            if (open != null) Merge(open, destination, at, observer, source, afterGap);
            return open;
        }

        var isRoam = prior != null
            && at - prior.At <= RoamMaxGap
            && (prior.LeftAt == null || at - prior.LeftAt.Value <= RejoinGap);
        var dwell = prior != null ? (at - prior.At).TotalSeconds : (double?)null;

        Remember(key, destination, at);

        if (!isRoam || prior == null) return null;

        var existing = FindOpen(key, destination, at);
        if (existing != null)
        {
            Merge(existing, destination, at, observer, source, afterGap);
            return existing;
        }

        var candidate = new ApRoamCandidate
        {
            ClientMac = key,
            LinkMac = linkMac == key ? null : linkMac,
            FromApMac = prior.ApMac,
            FromBssid = NullIfEmpty(prior.Bssid),
            FromBand = prior.Band,
            FromChannel = prior.Channel,
            ToApMac = destination.ApMac,
            ToBssid = NullIfEmpty(destination.Bssid),
            Band = destination.Band,
            Channel = destination.Channel,
            RoamedAt = at,
            DwellSeconds = dwell,
            AfterEventGap = afterGap,
            Source = source,
        };
        candidate.Observers.Add(observer);
        _open.Add(candidate);
        return candidate;
    }

    private ApRoamCandidate? FindOpen(string key, Destination destination, DateTime at)
    {
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var c = _open[i];
            if (!string.Equals(c.ClientMac, key, StringComparison.OrdinalIgnoreCase)) continue;
            if ((at - c.RoamedAt).Duration() > DedupWindow) continue;
            if (DestinationMatches(c, destination)) return c;
        }
        return null;
    }

    /// <summary>
    /// Whether an observation describes the landing a candidate already holds. A BSSID match is
    /// decisive; an access point match stands in when one side never learned the BSSID.
    /// </summary>
    private static bool DestinationMatches(ApRoamCandidate candidate, Destination destination)
    {
        if (!string.IsNullOrEmpty(candidate.ToBssid) && destination.Bssid.Length > 0)
            return MacEquals(candidate.ToBssid, destination.Bssid);
        if (!string.IsNullOrEmpty(candidate.ToApMac) && !string.IsNullOrEmpty(destination.ApMac))
            return MacEquals(candidate.ToApMac, destination.ApMac);
        return string.IsNullOrEmpty(candidate.ToBssid) || destination.Bssid.Length == 0;
    }

    private static void Merge(
        ApRoamCandidate candidate, Destination destination, DateTime at, string observer, string source, bool afterGap)
    {
        if (!candidate.Observers.Contains(observer, StringComparer.OrdinalIgnoreCase))
            candidate.Observers.Add(observer);

        candidate.ToApMac ??= destination.ApMac;
        candidate.ToBssid ??= NullIfEmpty(destination.Bssid);
        candidate.Band ??= destination.Band;
        candidate.Channel ??= destination.Channel;
        candidate.AfterEventGap |= afterGap;

        // First-hand association outranks gossip, and the earliest report sits closest to the roam.
        if (source == RoamSources.Assoc) candidate.Source = RoamSources.Assoc;
        if (at < candidate.RoamedAt) candidate.RoamedAt = at;
        candidate.Dirty = true;
    }

    private void Remember(string key, Destination destination, DateTime at)
    {
        if (_lastAssoc.Count > MaxTrackedClients) EvictOldest();
        _lastAssoc[key] = new Association
        {
            ApMac = destination.ApMac,
            Bssid = destination.Bssid,
            Band = destination.Band,
            Channel = destination.Channel,
            At = at,
        };
    }

    private void EvictOldest()
    {
        var cutoff = _lastAssoc.Values.Select(a => a.At).OrderBy(t => t).Skip(_lastAssoc.Count / 2).FirstOrDefault();
        foreach (var stale in _lastAssoc.Where(kv => kv.Value.At < cutoff).Select(kv => kv.Key).ToList())
            _lastAssoc.Remove(stale);
    }

    private void Prune()
    {
        if (_open.Count == 0) return;
        var newest = _open.Max(c => c.RoamedAt);
        _open.RemoveAll(c => newest - c.RoamedAt > CandidateRetention);
    }

    private ApAgentVap? ResolveVap(string apMac, string? vapName)
    {
        if (string.IsNullOrEmpty(vapName)) return null;
        return _vapsByAp.TryGetValue(apMac, out var byName) && byName.TryGetValue(vapName, out var vap) ? vap : null;
    }

    private static bool SameDestination(Association prior, Destination destination)
    {
        if (prior.Bssid.Length > 0 && destination.Bssid.Length > 0)
            return MacEquals(prior.Bssid, destination.Bssid);
        return MacEquals(prior.ApMac, destination.ApMac);
    }

    private static bool MacEquals(string? a, string? b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? mac) => (mac ?? "").Trim().ToLowerInvariant();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private sealed record Destination(string? ApMac, string Bssid, string? Band, int? Channel);

    private sealed class Association
    {
        public string? ApMac { get; init; }
        public string Bssid { get; init; } = "";
        public string? Band { get; init; }
        public int? Channel { get; init; }
        public DateTime At { get; init; }
        public DateTime? LeftAt { get; set; }
    }
}

/// <summary>How a roam reached this server, which is also how much of the record to trust.</summary>
public static class RoamSources
{
    /// <summary>The gaining access point saw the association itself.</summary>
    public const string Assoc = "assoc";

    /// <summary>An access point announced that a client is on a named BSSID.</summary>
    public const string RoamBroadcast = "roam_broadcast";

    /// <summary>A peer told this access point a client moved, giving a whole-ESS view from one agent.</summary>
    public const string RoamToPeer = "roam_to_peer";
}
