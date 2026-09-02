namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// One AP Agent client reduced to the values the <c>wifi_client</c> measurement stores. Produced
/// once per sample; several of these fold into one written point.
/// </summary>
/// <param name="ClientMac">The client's key: its MLD MAC when MLO, its station MAC otherwise.</param>
/// <param name="ApMac">The access point's MAC.</param>
/// <param name="Band">Band tag value, in the same spelling the console path writes.</param>
/// <param name="Channel">Active link's channel.</param>
/// <param name="ChannelWidth">Active link's width in MHz.</param>
/// <param name="SignalDbm">Active link's signal.</param>
/// <param name="NoiseDbm">Active link's noise floor.</param>
/// <param name="Rssi">Active link's signal-to-noise ratio.</param>
/// <param name="TxRateKbps">Active link's transmit rate.</param>
/// <param name="RxRateKbps">Active link's receive rate.</param>
/// <param name="Satisfaction">The AP's satisfaction score.</param>
/// <param name="TxBytes">Cumulative bytes to the client.</param>
/// <param name="RxBytes">Cumulative bytes from the client.</param>
/// <param name="IsMlo">Whether the client negotiated multi-link operation.</param>
/// <param name="TxRetries">Cumulative transmit retries.</param>
/// <param name="TxAttempts">Cumulative transmit attempts.</param>
/// <param name="TxDropped">Cumulative dropped frames.</param>
/// <param name="LatencyAvgMs">Mean transmit latency over the AP's own window.</param>
/// <param name="LatencyMaxMs">Worst transmit latency over the AP's own window.</param>
/// <param name="TcpStalls">Cumulative stalled TCP connections toward the client.</param>
/// <param name="TcpLatAvgMs">Mean TCP round-trip latency toward the client.</param>
/// <param name="Ccq">Client connection quality.</param>
/// <param name="Nss">Operating spatial streams.</param>
/// <param name="CollectedAt">The access point's own clock when it produced this reading. What the
/// point is stamped with, matching how loss, latency and SNMP are recorded from an agent: the
/// server's clock describes when it heard, not when it happened.</param>
/// <param name="JoinSignal">Signal at authentication; null when the association predates the agent.</param>
/// <param name="AssocSeconds">Seconds since the active link associated.</param>
/// <param name="BtmRequests">BSS transition requests this association answered.</param>
/// <param name="BtmAccepted">Of those, answers that accepted the transition.</param>
public sealed record ApAgentWifiSample(
    string ClientMac,
    string ApMac,
    string Band,
    int? Channel,
    int? ChannelWidth,
    double? SignalDbm,
    double? NoiseDbm,
    int? Rssi,
    long? TxRateKbps,
    long? RxRateKbps,
    int? Satisfaction,
    long? TxBytes,
    long? RxBytes,
    DateTime? BytesAt,
    bool IsMlo,
    long? TxRetries,
    long? TxAttempts,
    long? TxDropped,
    double? LatencyAvgMs,
    double? LatencyMaxMs,
    long? TcpStalls,
    double? TcpLatAvgMs,
    int? Ccq,
    bool NegotiatedIdle,
    long? IdleSeconds,
    int? Nss,
    DateTime? CollectedAt = null,
    int? JoinSignal = null,
    int? AssocSeconds = null,
    int? BtmRequests = null,
    int? BtmAccepted = null);

/// <summary>One client's samples folded into the single point written for a write window.</summary>
/// <param name="Sample">Field values, averaged or latest per the fold rules.</param>
/// <param name="SampleCount">How many agent samples folded into it.</param>
/// <param name="TxThroughputBps">Throughput to the client, from the byte delta since the last write.</param>
/// <param name="RxThroughputBps">Throughput from the client, from the byte delta since the last write.</param>
public sealed record ApAgentWifiFolded(
    ApAgentWifiSample Sample,
    int SampleCount,
    double? TxThroughputBps,
    double? RxThroughputBps);

/// <summary>
/// Turns one AP Agent client record into a sample. The agent has already resolved MLO to one
/// record keyed on the MLD MAC with active-link scalars, so this reads what it resolved rather than
/// walking the links again - re-deriving would triple the client count on a Wi-Fi 7 site.
/// </summary>
public static class ApAgentWifiFieldMapper
{
    /// <summary>
    /// Maps the agent's band token onto the tag value the measurement already uses. An unrecognized
    /// token yields an empty string, and the caller drops the client rather than tagging a guess.
    /// </summary>
    public static string MapBand(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        "2.4" or "2.4ghz" or "ng" => "2.4ghz",
        "5" or "5ghz" or "na" => "5ghz",
        "6" or "6ghz" or "6e" or "6g" => "6ghz",
        _ => string.Empty,
    };

    /// <summary>
    /// Reduces one client to a sample, or null when it cannot be tagged. The counters live on the
    /// link rather than the client, so they are read off the active link - the same link the
    /// agent's scalars already describe.
    /// </summary>
    public static ApAgentWifiSample? ToSample(ApAgentClient client, string apMac, DateTime? collectedAt = null)
    {
        var clientMac = NormalizeMac(client.Key.Length > 0 ? client.Key : client.Mac);
        if (clientMac.Length == 0) return null;

        var active = ActiveLink(client);
        var band = MapBand(string.IsNullOrEmpty(client.Band) ? active?.Band : client.Band);
        if (band.Length == 0) return null;

        var latency = active?.TxLatency;
        var tcp = active?.TxTcpStats;

        return new ApAgentWifiSample(
            ClientMac: clientMac,
            ApMac: NormalizeMac(apMac),
            Band: band,
            Channel: client.Channel > 0 ? client.Channel : null,
            ChannelWidth: client.Bandwidth > 0 ? client.Bandwidth : null,
            SignalDbm: client.Signal,
            NoiseDbm: client.Noise,
            Rssi: client.Snr,
            TxRateKbps: client.TxRateKbps > 0 ? client.TxRateKbps : null,
            RxRateKbps: client.RxRateKbps > 0 ? client.RxRateKbps : null,
            Satisfaction: client.Satisfaction,
            TxBytes: active?.TxBytes,
            RxBytes: active?.RxBytes,
            BytesAt: active?.BytesAt,
            CollectedAt: collectedAt,
            NegotiatedIdle: active?.NegotiatedIdle ?? false,
            // The LOWEST idle across the client's links, not the active link's. An MLO client
            // associates once per band under its own randomised MAC, and a link that carried a few
            // bytes at association looks alive forever while every link has actually gone quiet.
            IdleSeconds: client.Links.Count == 0 ? null : client.Links.Min(l => l.IdleSeconds),
            IsMlo: client.IsMlo,
            TxRetries: active?.TxRetries,
            TxAttempts: active?.TxAttempts,
            TxDropped: active?.TxDropped,
            // wifi_tx_latency_mov is microseconds on the AP; the stored fields are milliseconds.
            LatencyAvgMs: latency is { Avg: > 0 } ? latency.Avg / 1000.0 : null,
            LatencyMaxMs: latency is { Max: > 0 } ? latency.Max / 1000.0 : null,
            TcpStalls: tcp?.Stalls,
            TcpLatAvgMs: tcp is { LatAvg: > 0 } ? tcp.LatAvg : null,
            Ccq: active is { Ccq: > 0 } ? active.Ccq : null,
            // The operating stream count says what the link is doing; the capability bit is the
            // ceiling, and only stands in when the AP did not report the operating value.
            Nss: active is { Nss: > 0 } ? active.Nss
                : client.Capabilities is { Nss: > 0 } caps ? caps.Nss : null,
            JoinSignal: active?.JoinRssi,
            AssocSeconds: active is { AssocSeconds: > 0 } ? active.AssocSeconds : null,
            BtmRequests: active?.BtmRequests,
            BtmAccepted: active?.BtmAccepted);
    }

    /// <summary>The link carrying traffic, as the agent marked it. Falls back to the only link.</summary>
    private static ApAgentClientLink? ActiveLink(ApAgentClient client)
    {
        ApAgentClientLink? first = null;
        foreach (var link in client.Links)
        {
            if (link.Active) return link;
            first ??= link;
        }
        return first;
    }

    /// <summary>Lower-case colon form, matching every other MAC the measurement carries.</summary>
    public static string NormalizeMac(string? mac) => (mac ?? "").Trim().ToLowerInvariant();
}

/// <summary>
/// Folds the samples taken across one write window into one point per client.
///
/// The AP can sample far faster than the tier writes. Writing every sample would multiply the write
/// volume on a measurement whose per-client queries are already expensive, so the fast samples
/// average here and one point per client per window reaches InfluxDB.
/// </summary>
public sealed class ApAgentWifiAccumulator
{
    /// <summary>Caps what one access point can make this hold, whatever it reports.</summary>
    public const int MaxTrackedClients = 512;

    /// <summary>A client whose counters have not been seen for this long stops being tracked.</summary>
    private static readonly TimeSpan ByteCacheTtl = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, ClientFold> _folds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ByteSnapshot> _bytes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many clients are accumulating right now.</summary>
    public int PendingClients => _folds.Count;

    /// <summary>
    /// Folds one sample in. Samples beyond the cap are dropped rather than grown into, so a
    /// misreporting access point cannot make this unbounded.
    /// </summary>
    public void Add(ApAgentWifiSample sample, DateTime at)
    {
        if (!_folds.TryGetValue(sample.ClientMac, out var fold))
        {
            if (_folds.Count >= MaxTrackedClients) return;
            fold = new ClientFold();
            _folds[sample.ClientMac] = fold;
        }
        fold.Add(sample, at);
    }

    /// <summary>
    /// Closes the window: one folded result per client, and the accumulator empties. Throughput
    /// comes from the byte delta against the previous window, which is what lets a window holding a
    /// single sample still report a rate.
    /// </summary>
    public IReadOnlyList<ApAgentWifiFolded> Flush(DateTime now)
    {
        var results = new List<ApAgentWifiFolded>(_folds.Count);

        foreach (var (mac, fold) in _folds)
        {
            var folded = fold.Resolve();
            var (tx, rx) = ResolveThroughput(mac, folded, fold.LastSampleAt);
            results.Add(new ApAgentWifiFolded(folded, fold.Count, tx, rx));
        }

        _folds.Clear();
        Evict(now);
        return results;
    }

    private (double? Tx, double? Rx) ResolveThroughput(string mac, ApAgentWifiSample sample, DateTime at)
    {
        if (sample.TxBytes is not { } tx || sample.RxBytes is not { } rx)
            return (null, null);

        // Date the reading by when the AP read the counters, not when we folded them. An agent
        // that predates the counter tier sends nothing here and keeps the fold's own timing.
        at = sample.BytesAt ?? at;

        var resolved = _bytes.TryGetValue(mac, out var prev)
            ? ApAgentThroughput.FromCounters(tx, rx, at, prev.TxBytes, prev.RxBytes, prev.At)
            : (null, null);

        _bytes[mac] = new ByteSnapshot(at, tx, rx);
        return resolved;
    }

    private void Evict(DateTime now)
    {
        if (_bytes.Count == 0) return;
        var stale = _bytes.Where(kv => now - kv.Value.At > ByteCacheTtl).Select(kv => kv.Key).ToList();
        foreach (var key in stale) _bytes.Remove(key);
    }

    private readonly record struct ByteSnapshot(DateTime At, long TxBytes, long RxBytes);

    /// <summary>
    /// One client's running fold. Sampled values average, latency takes the worst of the maxima,
    /// and cumulative counters take the newest sample, because a counter is a running total rather
    /// than a measurement to average.
    /// </summary>
    private sealed class ClientFold
    {
        private ApAgentWifiSample? _latest;
        private readonly Mean _signal = new();
        private readonly Mean _noise = new();
        private readonly Mean _rssi = new();
        private readonly Mean _txRate = new();
        private readonly Mean _rxRate = new();
        private readonly Mean _latencyAvg = new();
        private readonly Mean _tcpLatAvg = new();
        private readonly Mean _ccq = new();
        private double? _latencyMax;

        public int Count { get; private set; }
        public DateTime LastSampleAt { get; private set; }

        public void Add(ApAgentWifiSample s, DateTime at)
        {
            Count++;
            _latest = s;
            LastSampleAt = at;

            _signal.Add(s.SignalDbm);
            _noise.Add(s.NoiseDbm);
            _rssi.Add(s.Rssi);
            _txRate.Add(s.TxRateKbps);
            _rxRate.Add(s.RxRateKbps);
            _latencyAvg.Add(s.LatencyAvgMs);
            _tcpLatAvg.Add(s.TcpLatAvgMs);
            _ccq.Add(s.Ccq);

            if (s.LatencyMaxMs is { } max && (_latencyMax is null || max > _latencyMax))
                _latencyMax = max;
        }

        public ApAgentWifiSample Resolve()
        {
            var latest = _latest!;
            return latest with
            {
                SignalDbm = _signal.Value,
                NoiseDbm = _noise.Value,
                Rssi = _rssi.Value is { } rssi ? (int)Math.Round(rssi) : null,
                TxRateKbps = _txRate.Value is { } txRate ? (long)Math.Round(txRate) : null,
                RxRateKbps = _rxRate.Value is { } rxRate ? (long)Math.Round(rxRate) : null,
                LatencyAvgMs = _latencyAvg.Value,
                LatencyMaxMs = _latencyMax,
                TcpLatAvgMs = _tcpLatAvg.Value,
                Ccq = _ccq.Value is { } ccq ? (int)Math.Round(ccq) : null,
            };
        }
    }

    /// <summary>A running mean that ignores absent samples, so one null does not zero the average.</summary>
    private sealed class Mean
    {
        private double _sum;
        private int _count;

        public void Add(double? value)
        {
            if (value is not { } v) return;
            _sum += v;
            _count++;
        }

        public double? Value => _count == 0 ? null : _sum / _count;
    }
}
