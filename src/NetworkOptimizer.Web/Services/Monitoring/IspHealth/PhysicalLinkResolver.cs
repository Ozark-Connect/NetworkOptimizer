using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Resolves the ONE physical-link source that belongs to the WAN being scored, by matching the
/// configured access technology to a monitored device (ONT/SFP, cable modem, or cellular modem),
/// then assembles its window-aggregated metrics (time-series) enriched with the live poll snapshot
/// (PON link state, active OFDMA channel, 5G capability) into a <see cref="PhysicalLinkInput"/>.
///
/// Matching is conservative: a single in-medium candidate is used automatically; multiple
/// candidates require the user's persisted pick (<see cref="MonitoringSettings.PhysicalLinkSourceKey"/>)
/// and otherwise surface as an ambiguity the panel resolves with a dropdown. Registered as a
/// singleton (all monitor services and the InfluxDB client are singletons).
/// </summary>
public class PhysicalLinkResolver
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly MonitoringInfluxClient _influx;
    private readonly CableModemMonitorService _cmMonitor;
    private readonly OntMonitorService _ontMonitor;
    private readonly CellularModemService _cellularMonitor;
    private readonly ILogger<PhysicalLinkResolver> _logger;

    public PhysicalLinkResolver(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        MonitoringInfluxClient influx,
        CableModemMonitorService cmMonitor,
        OntMonitorService ontMonitor,
        CellularModemService cellularMonitor,
        ILogger<PhysicalLinkResolver> logger)
    {
        _dbFactory = dbFactory;
        _influx = influx;
        _cmMonitor = cmMonitor;
        _ontMonitor = ontMonitor;
        _cellularMonitor = cellularMonitor;
        _logger = logger;
    }

    /// <summary>One matched/selectable physical source.</summary>
    private sealed record Cand(string Key, string Label, PhysicalMedium Medium, int? ConfigId, string? Mac, string? Port);

    public async Task<PhysicalLinkResolution> ResolveAsync(
        AccessTechnology tech, DateTime windowStart, DateTime windowEnd, TimeSpan aggregate, CancellationToken ct)
    {
        MonitoringSettings? settings;
        List<Cand> candidates;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            candidates = await EnumerateCandidatesAsync(db, tech, ct);
        }

        var selectedKey = settings?.PhysicalLinkSourceKey;
        var candidateModels = candidates.Select(c => new PhysicalLinkCandidate(c.Key, c.Label)).ToList();

        if (candidates.Count == 0)
            return new PhysicalLinkResolution(null, candidateModels, selectedKey, false);

        Cand chosen;
        if (candidates.Count == 1)
        {
            chosen = candidates[0];
        }
        else
        {
            var match = candidates.FirstOrDefault(c => c.Key == selectedKey);
            if (match is null)
            {
                _logger.LogDebug("ISP Health physical link: {Count} candidates, none selected - ambiguous", candidates.Count);
                return new PhysicalLinkResolution(null, candidateModels, null, true);
            }
            chosen = match;
        }

        var input = await AssembleAsync(chosen, tech == AccessTechnology.XgsPon, windowStart, windowEnd, aggregate, ct);
        return new PhysicalLinkResolution(input, candidateModels, chosen.Key, false);
    }

    // ---------------------------------------------------------------------------
    // Candidate enumeration (access technology -> medium family)
    // ---------------------------------------------------------------------------

    private async Task<List<Cand>> EnumerateCandidatesAsync(NetworkOptimizerDbContext db, AccessTechnology tech, CancellationToken ct)
    {
        switch (tech)
        {
            case AccessTechnology.Gpon:
            case AccessTechnology.XgsPon:
                return await PonCandidatesAsync(db, ct);

            case AccessTechnology.DirectEthernet:
                return await SfpCandidatesAsync(db, SfpCategory.ActiveEthernet, PhysicalMedium.ActiveEthernet, ct);

            case AccessTechnology.Docsis:
                return await CableModemCandidatesAsync(db, ct);

            case AccessTechnology.Cellular:
                return await CellularCandidatesAsync(db, ct);

            case AccessTechnology.PppoE:
                // Carve-out: PPPoE often rides PON. If exactly one PON source is monitored,
                // it is safe to assume it is the user's WAN; otherwise stay out of it.
                var pon = await PonCandidatesAsync(db, ct);
                return pon.Count == 1 ? pon : new List<Cand>();

            default:
                return new List<Cand>();
        }
    }

    private async Task<List<Cand>> PonCandidatesAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        var sfps = await SfpCandidatesAsync(db, SfpCategory.Pon, PhysicalMedium.Pon, ct);
        var onts = (await db.OntConfigurations.AsNoTracking().Where(o => o.Enabled).ToListAsync(ct))
            .Where(o => IsFresh(o.LastPolled, o.PollingIntervalSeconds))
            .Select(o => new Cand($"ont:{o.Id}", o.Name, PhysicalMedium.Pon, o.Id, null, null));
        return sfps.Concat(onts).ToList();
    }

    private async Task<List<Cand>> SfpCandidatesAsync(NetworkOptimizerDbContext db, SfpCategory category, PhysicalMedium medium, CancellationToken ct)
    {
        return (await db.MonitoredSfps.AsNoTracking().Where(s => s.Category == category).ToListAsync(ct))
            .Select(s => new Cand(
                $"sfp:{s.DeviceMac}/{s.PortName}",
                string.IsNullOrWhiteSpace(s.FriendlyName) ? s.PortName : s.FriendlyName!,
                medium, null, s.DeviceMac, s.PortName))
            .ToList();
    }

    private async Task<List<Cand>> CableModemCandidatesAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        return (await db.CmConfigurations.AsNoTracking().Where(c => c.Enabled).ToListAsync(ct))
            .Where(c => IsFresh(c.LastPolled, c.PollingIntervalSeconds))
            .Select(c => new Cand($"cm:{c.Id}", c.Name, PhysicalMedium.Docsis, c.Id, null, null))
            .ToList();
    }

    private async Task<List<Cand>> CellularCandidatesAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        return (await db.ModemConfigurations.AsNoTracking().Where(m => m.Enabled).ToListAsync(ct))
            .Where(m => IsFresh(m.LastPolled, m.PollingIntervalSeconds))
            .Select(m => new Cand($"modem:{m.Id}", m.Name, PhysicalMedium.Cellular, m.Id, null, null))
            .ToList();
    }

    /// <summary>A configured device counts as a candidate only if it polled recently (3x its interval, min 15 min).</summary>
    private static bool IsFresh(DateTime? lastPolled, int intervalSeconds) =>
        lastPolled.HasValue && DateTime.UtcNow - lastPolled.Value <= TimeSpan.FromSeconds(Math.Max(900, intervalSeconds * 3));

    // ---------------------------------------------------------------------------
    // Assembly (time-series window aggregate + live snapshot enrichment)
    // ---------------------------------------------------------------------------

    private async Task<PhysicalLinkInput?> AssembleAsync(
        Cand c, bool isXgsPon, DateTime windowStart, DateTime windowEnd, TimeSpan aggregate, CancellationToken ct)
    {
        return c.Medium switch
        {
            PhysicalMedium.Pon when c.ConfigId is int ontId => await AssembleOntAsync(c, ontId, isXgsPon, windowStart, windowEnd, aggregate, ct),
            PhysicalMedium.Pon => await AssembleSfpAsync(c, PhysicalMedium.Pon, isXgsPon, windowStart, windowEnd, aggregate, ct),
            PhysicalMedium.ActiveEthernet => await AssembleSfpAsync(c, PhysicalMedium.ActiveEthernet, false, windowStart, windowEnd, aggregate, ct),
            PhysicalMedium.Docsis when c.ConfigId is int cmId => await AssembleCableModemAsync(c, cmId, windowStart, windowEnd, aggregate, ct),
            PhysicalMedium.Cellular when c.ConfigId is int modemId => await AssembleCellularAsync(c, modemId, windowStart, windowEnd, aggregate, ct),
            _ => null
        };
    }

    private async Task<PhysicalLinkInput?> AssembleSfpAsync(
        Cand c, PhysicalMedium medium, bool isXgsPon, DateTime windowStart, DateTime windowEnd, TimeSpan aggregate, CancellationToken ct)
    {
        // SFP DDM only: TimeSpan.Zero => RAW (un-aggregated) so glitchy stick reads stay isolated,
        // with temperature passed through so OpticalSampleStats can reject the artifacts (RX + temp
        // glitch together). Mean aggregation would smear a glitch into a bucket that defeats the filter.
        var dict = await _influx.QuerySfpByModulesAsync(new[] { (c.Mac!, c.Port!) }, windowStart, windowEnd, TimeSpan.Zero, ct);
        var pts = dict.Values.FirstOrDefault() ?? new();
        var stats = OpticalSampleStats.Compute(pts.Select(p => (p.Time, p.RxPowerDbm, p.TemperatureC)).ToList());
        _logger.LogDebug("ISP Health physical: SFP {Key} - {N} samples, {Rej} DDM artifacts rejected, rxMed={Med} worst={Worst}",
            c.Key, pts.Count, stats.RejectedArtifacts, stats.MedianDbm, stats.WorstDbm);

        return new PhysicalLinkInput
        {
            Medium = medium,
            SourceName = c.Label,
            RxPowerMedianDbm = stats.MedianDbm,
            RxPowerWorstDbm = stats.WorstDbm,
            RxPowerBaselineDbm = stats.BaselineDbm,
            TxPowerDbm = pts.OrderBy(p => p.Time).LastOrDefault(p => p.TxPowerDbm.HasValue)?.TxPowerDbm,
            IsXgsPon = isXgsPon,
            WindowDays = (windowEnd - windowStart).TotalDays
        };
    }

    private async Task<PhysicalLinkInput?> AssembleOntAsync(
        Cand c, int ontId, bool isXgsPon, DateTime windowStart, DateTime windowEnd, TimeSpan aggregate, CancellationToken ct)
    {
        // External ONT: vendor firmware, not a DDM stick - no read-artifact problem, so temperature is
        // NOT passed for rejection (null temps => OpticalSampleStats keeps every sample). RAW points
        // (TimeSpan.Zero) so the FEC/BIP per-poll deltas are real polls, comparable to the alert threshold.
        var dict = await _influx.QueryOntAsync(windowStart, windowEnd, ontId.ToString(), TimeSpan.Zero, ct);
        var pts = (dict.Values.FirstOrDefault() ?? new()).OrderBy(p => p.Time).ToList();
        var stats = OpticalSampleStats.Compute(pts.Select(p => (p.Time, p.RxPowerDbm, (double?)null)).ToList());
        var live = _ontMonitor.GetCachedStats(ontId);
        var fecTotal = TotalIncrements(pts.Select(p => p.FecErrors).ToList());
        var bipTotal = TotalIncrements(pts.Select(p => p.BipErrors).ToList());
        var operational = ResolveOperationalFromHistory(pts);
        _logger.LogDebug("ISP Health physical: ONT {Key} - {N} samples, rxMed={Med} worst={Worst}, op={Op}, fecTotal={Fec} bipTotal={Bip}",
            c.Key, pts.Count, stats.MedianDbm, stats.WorstDbm, operational, fecTotal, bipTotal);

        return new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Pon,
            SourceName = c.Label,
            RxPowerMedianDbm = stats.MedianDbm,
            RxPowerWorstDbm = stats.WorstDbm,
            RxPowerBaselineDbm = stats.BaselineDbm,
            TxPowerDbm = live?.TxPowerDbm ?? pts.LastOrDefault(p => p.TxPowerDbm.HasValue)?.TxPowerDbm,
            PonOperational = operational,
            PonType = live?.PonType,
            IsXgsPon = isXgsPon,
            FecErrorsTotal = fecTotal,
            BipErrorsTotal = bipTotal,
            WindowDays = (windowEnd - windowStart).TotalDays
        };
    }

    /// <summary>Total positive increments of a cumulative error counter over the window, reset-guarded
    /// (negative steps from a counter reset count as zero). Null when there aren't two readings.</summary>
    private static long? TotalIncrements(IReadOnlyList<long?> counters)
    {
        long total = 0;
        var any = false;
        long? prev = null;
        foreach (var v in counters)
        {
            if (v is not long cur) continue;
            if (prev is long p)
            {
                var delta = cur - p;
                if (delta > 0) total += delta;
                any = true;
            }
            prev = cur;
        }
        return any ? total : (long?)null;
    }

    /// <summary>
    /// Grades the PON O5 (Operation) state from the persisted series rather than a single live poll:
    /// true if every reported state was Operation, false if the link broke out of O5 at least once
    /// during the window. A poll that omits PON Link Status (a DDM stick that never reports it, or the
    /// gateway's stats page momentarily dropping the row) parses to Unknown and is ignored - missing
    /// status is absence of data, not a link-down. Returns null when the source reported no O-state at
    /// all, so the factor never false-alarms on ONTs that don't expose it. Matches OntAlertEvaluator,
    /// which likewise treats Unknown as "not down".
    /// </summary>
    internal static bool? ResolveOperationalFromHistory(IReadOnlyList<MonitoringInfluxClient.OntPoint> pts)
    {
        var known = pts
            .Select(p => PonLinkStateExtensions.ParsePonLinkState(p.PonLinkStatus))
            .Where(s => s != PonLinkState.Unknown)
            .ToList();
        if (known.Count == 0) return null;
        return known.All(s => s == PonLinkState.Operation);
    }

    private async Task<PhysicalLinkInput?> AssembleCableModemAsync(
        Cand c, int cmId, DateTime windowStart, DateTime windowEnd, TimeSpan aggregate, CancellationToken ct)
    {
        var dict = await _influx.QueryCableModemAsync(windowStart, windowEnd, cmId.ToString(), aggregate, ct);
        var pts = (dict.Values.FirstOrDefault() ?? new()).OrderBy(p => p.Time).ToList();
        var live = _cmMonitor.GetCachedStats(cmId);

        var lockedDs = pts.LastOrDefault(p => p.LockedDsChannels.HasValue)?.LockedDsChannels;
        var peakDs = pts.Where(p => p.LockedDsChannels.HasValue).Select(p => p.LockedDsChannels!.Value)
            .DefaultIfEmpty().Max();

        return new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Docsis,
            SourceName = c.Label,
            DsSnrDb = Median(pts.Where(p => p.DsSnrAvgDb.HasValue).Select(p => p.DsSnrAvgDb!.Value).ToList()),
            DsPowerDbmv = Median(pts.Where(p => p.DsPowerAvgDbmv.HasValue).Select(p => p.DsPowerAvgDbmv!.Value).ToList()),
            UsPowerDbmv = Median(pts.Where(p => p.UsPowerAvgDbmv.HasValue).Select(p => p.UsPowerAvgDbmv!.Value).ToList()),
            CorrectablesDelta = pts.Where(p => p.CorrDelta.HasValue).Sum(p => p.CorrDelta!.Value),
            UncorrectablesDelta = pts.Where(p => p.UncorrDelta.HasValue).Sum(p => p.UncorrDelta!.Value),
            LockedDsChannels = lockedDs,
            PeakDsChannels = peakDs > 0 ? peakDs : null,
            OfdmaActive = live != null
                ? live.UpstreamChannels.Any(ch => (ch.ChannelType ?? "").Contains("OFDMA", StringComparison.OrdinalIgnoreCase))
                : null,
            ModemModel = string.IsNullOrWhiteSpace(live?.DeviceModel) ? null : live!.DeviceModel,
            WindowDays = (windowEnd - windowStart).TotalDays
        };
    }

    private async Task<PhysicalLinkInput?> AssembleCellularAsync(
        Cand c, int modemId, DateTime windowStart, DateTime windowEnd, TimeSpan aggregate, CancellationToken ct)
    {
        // Cellular is keyed by modem_id + network_mode, so a mode change splits into several series.
        var dict = await _influx.QueryCellularAsync(windowStart, windowEnd, modemId.ToString(), aggregate, ct);
        var pts = dict.Values.SelectMany(v => v).OrderBy(p => p.Time).ToList();
        var live = _cellularMonitor.GetCachedStats(modemId);

        var qualities = pts.Where(p => p.SignalQuality.HasValue).Select(p => (double)p.SignalQuality!.Value).ToList();
        var quality = Median(qualities);
        if (quality is null && live != null) quality = live.SignalQuality;

        var had5g = pts.Any(p => IsFiveG(p.NetworkMode)) || live?.Nr5g != null;
        var latestMode = pts.LastOrDefault(p => !string.IsNullOrEmpty(p.NetworkMode))?.NetworkMode
                         ?? (live != null ? live.NetworkModeLabel : null);
        var downgraded = had5g && IsLte(latestMode);

        return new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Cellular,
            SourceName = c.Label,
            SignalQuality = quality is double q ? (int)Math.Round(q) : null,
            NetworkMode = latestMode,
            NetworkModeDowngraded = downgraded,
            Is5gCapable = had5g,
            WindowDays = (windowEnd - windowStart).TotalDays
        };
    }

    private static bool IsFiveG(string? mode) =>
        !string.IsNullOrEmpty(mode) && (mode.Contains("5G", StringComparison.OrdinalIgnoreCase) || mode.Contains("Nr5g", StringComparison.OrdinalIgnoreCase));

    private static bool IsLte(string? mode) =>
        !string.IsNullOrEmpty(mode) && mode.Contains("LTE", StringComparison.OrdinalIgnoreCase) && !IsFiveG(mode);

    // ---------------------------------------------------------------------------
    // Aggregation helpers
    // ---------------------------------------------------------------------------

    private static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

/// <summary>Outcome of physical-link resolution for one report.</summary>
public sealed record PhysicalLinkResolution(
    PhysicalLinkInput? Input,
    List<PhysicalLinkCandidate> Candidates,
    string? SelectedKey,
    bool Ambiguous);
