using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates cable modem DOCSIS metrics against thresholds and publishes alert
/// events on state transitions. Covers downstream SNR, downstream/upstream power
/// levels, uncorrectable FEC errors, and locked channel count drops. Thresholds
/// are based on DOCSIS 3.0/3.1 operating specifications. For modems that report
/// them, also raises the modem's own logged T3/T4/ranging events and reinits.
/// </summary>
public class CableModemAlertEvaluator
{
    private const double DsSnrLowDb = 33.0;
    private const double DsSnrClearDb = 36.0;

    private const double DsPowerLowDbmv = -7.0;
    private const double DsPowerHighDbmv = 7.0;
    private const double DsPowerClearLowDbmv = -5.0;
    private const double DsPowerClearHighDbmv = 5.0;

    private const double UsPowerHighDbmv = 51.0;
    private const double UsPowerClearDbmv = 48.0;

    private const long UncorrectablesDeltaThreshold = 500;

    private const int ChannelDropThreshold = 4;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<CableModemAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, CmAlertState> _states = new();
    private readonly string _siteSuffix;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/> - CM ids are per-site database
    /// sequences, so state must not be shared). Non-default sites get their
    /// slug appended to alert titles.
    /// </param>
    public CableModemAlertEvaluator(IAlertEventBus eventBus, ILogger<CableModemAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    public async ValueTask EvaluateAsync(
        int cmId, string cmName,
        double? dsSnrAvgDb, double? dsPowerAvgDbmv, double? usPowerAvgDbmv,
        int lockedDsChannels, int lockedUsChannels,
        long uncorrectablesDelta,
        CancellationToken ct = default)
    {
        var key = cmId.ToString();
        var state = _states.GetOrAdd(key, _ => new CmAlertState());

        if (dsSnrAvgDb.HasValue)
            await EvaluateDsSnr(state, cmId, cmName, dsSnrAvgDb.Value, ct);

        if (dsPowerAvgDbmv.HasValue)
            await EvaluateDsPower(state, cmId, cmName, dsPowerAvgDbmv.Value, ct);

        if (usPowerAvgDbmv.HasValue)
            await EvaluateUsPower(state, cmId, cmName, usPowerAvgDbmv.Value, ct);

        if (uncorrectablesDelta > UncorrectablesDeltaThreshold)
            await PublishUncorrectables(cmId, cmName, uncorrectablesDelta, ct);

        if (lockedDsChannels > 0)
            await EvaluateChannelLoss(state, cmId, cmName, lockedDsChannels, ct);
    }

    private async ValueTask EvaluateDsSnr(CmAlertState state, int cmId, string cmName, double snr, CancellationToken ct)
    {
        if (!state.DsSnrBreached && snr < DsSnrLowDb)
        {
            state.DsSnrBreached = true;
            _logger.LogDebug("Cable modem DS SNR low: {CmName} snr={Snr:0.#} dB", cmName, snr);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "cable_modem.ds_snr_low",
                Source = "cable_modem",
                Severity = AlertSeverity.Warning,
                Title = $"{cmName} downstream SNR low{_siteSuffix}",
                Message = $"Cable modem {cmName} downstream SNR averaged {snr:0.#} dB, below the {DsSnrLowDb} dB threshold. Below 30 dB is typically service-affecting.",
                MetricValue = snr,
                ThresholdValue = DsSnrLowDb,
                SourceUrl = MonitoringLinks.HardwareStats("cm", DateTime.UtcNow),
                Tags = ["cable_modem", "snr"],
                Context = new Dictionary<string, string>
                {
                    ["cm_id"] = cmId.ToString(),
                    ["metric"] = "ds_snr_avg_db"
                }
            }, ct);
        }
        else if (state.DsSnrBreached && snr >= DsSnrClearDb)
        {
            state.DsSnrBreached = false;
        }
    }

    private async ValueTask EvaluateDsPower(CmAlertState state, int cmId, string cmName, double power, CancellationToken ct)
    {
        var outOfRange = power < DsPowerLowDbmv || power > DsPowerHighDbmv;
        var inClearRange = power >= DsPowerClearLowDbmv && power <= DsPowerClearHighDbmv;

        if (!state.DsPowerBreached && outOfRange)
        {
            state.DsPowerBreached = true;
            var direction = power < DsPowerLowDbmv ? "low" : "high";
            _logger.LogDebug("Cable modem DS power out of range: {CmName} power={Power:0.#} dBmV ({Direction})", cmName, power, direction);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "cable_modem.ds_power_out_of_range",
                Source = "cable_modem",
                Severity = AlertSeverity.Warning,
                Title = $"{cmName} downstream power {direction}{_siteSuffix}",
                Message = $"Cable modem {cmName} downstream power averaged {power:0.#} dBmV, outside the {DsPowerLowDbmv} to {DsPowerHighDbmv} dBmV DOCSIS operating range.",
                MetricValue = power,
                ThresholdValue = power < DsPowerLowDbmv ? DsPowerLowDbmv : DsPowerHighDbmv,
                SourceUrl = MonitoringLinks.HardwareStats("cm", DateTime.UtcNow),
                Tags = ["cable_modem", "power"],
                Context = new Dictionary<string, string>
                {
                    ["cm_id"] = cmId.ToString(),
                    ["metric"] = "ds_power_avg_dbmv",
                    ["direction"] = direction
                }
            }, ct);
        }
        else if (state.DsPowerBreached && inClearRange)
        {
            state.DsPowerBreached = false;
        }
    }

    private async ValueTask EvaluateUsPower(CmAlertState state, int cmId, string cmName, double power, CancellationToken ct)
    {
        if (!state.UsPowerBreached && power > UsPowerHighDbmv)
        {
            state.UsPowerBreached = true;
            _logger.LogDebug("Cable modem US power high: {CmName} power={Power:0.#} dBmV", cmName, power);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "cable_modem.us_power_high",
                Source = "cable_modem",
                Severity = AlertSeverity.Warning,
                Title = $"{cmName} upstream power high{_siteSuffix}",
                Message = $"Cable modem {cmName} upstream power averaged {power:0.#} dBmV, above {UsPowerHighDbmv} dBmV. The modem is compensating for poor return path signal.",
                MetricValue = power,
                ThresholdValue = UsPowerHighDbmv,
                SourceUrl = MonitoringLinks.HardwareStats("cm", DateTime.UtcNow),
                Tags = ["cable_modem", "power"],
                Context = new Dictionary<string, string>
                {
                    ["cm_id"] = cmId.ToString(),
                    ["metric"] = "us_power_avg_dbmv"
                }
            }, ct);
        }
        else if (state.UsPowerBreached && power <= UsPowerClearDbmv)
        {
            state.UsPowerBreached = false;
        }
    }

    private async ValueTask PublishUncorrectables(int cmId, string cmName, long delta, CancellationToken ct)
    {
        _logger.LogDebug("Cable modem uncorrectable errors: {CmName} delta={Delta}", cmName, delta);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = "cable_modem.uncorrectable_errors",
            Source = "cable_modem",
            Severity = AlertSeverity.Warning,
            Title = $"{cmName} uncorrectable errors{_siteSuffix}",
            Message = $"Cable modem {cmName} had {delta:N0} uncorrectable FEC errors since the last poll, exceeding the {UncorrectablesDeltaThreshold} threshold.",
            MetricValue = delta,
            ThresholdValue = UncorrectablesDeltaThreshold,
            SourceUrl = MonitoringLinks.HardwareStats("cm", DateTime.UtcNow),
            Tags = ["cable_modem", "uncorrectables"],
            Context = new Dictionary<string, string>
            {
                ["cm_id"] = cmId.ToString(),
                ["metric"] = "uncorrectables_delta"
            }
        }, ct);
    }

    private async ValueTask EvaluateChannelLoss(CmAlertState state, int cmId, string cmName, int lockedDsChannels, CancellationToken ct)
    {
        if (lockedDsChannels > state.MaxLockedDsChannels)
        {
            state.MaxLockedDsChannels = lockedDsChannels;
            state.ChannelLossBreached = false;
            return;
        }

        var drop = state.MaxLockedDsChannels - lockedDsChannels;

        if (!state.ChannelLossBreached && drop >= ChannelDropThreshold)
        {
            state.ChannelLossBreached = true;
            _logger.LogDebug("Cable modem channel loss: {CmName} locked={Current} max={Max} drop={Drop}", cmName, lockedDsChannels, state.MaxLockedDsChannels, drop);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "cable_modem.channel_loss",
                Source = "cable_modem",
                Severity = AlertSeverity.Warning,
                Title = $"{cmName} downstream channels lost{_siteSuffix}",
                Message = $"Cable modem {cmName} dropped from {state.MaxLockedDsChannels} to {lockedDsChannels} locked downstream channels ({drop} lost).",
                MetricValue = lockedDsChannels,
                ThresholdValue = state.MaxLockedDsChannels,
                SourceUrl = MonitoringLinks.HardwareStats("cm", DateTime.UtcNow),
                Tags = ["cable_modem", "channels"],
                Context = new Dictionary<string, string>
                {
                    ["cm_id"] = cmId.ToString(),
                    ["metric"] = "locked_ds_channels",
                    ["max_channels"] = state.MaxLockedDsChannels.ToString(),
                    ["channels_lost"] = drop.ToString()
                }
            }, ct);
        }
        else if (state.ChannelLossBreached && drop < ChannelDropThreshold)
        {
            state.ChannelLossBreached = false;
        }
    }

    /// <summary>
    /// Publishes one recognised entry from the modem's own DOCSIS event log (T3/T4 timeout,
    /// ranging failure). The caller decides which entries are new; this only raises them.
    /// </summary>
    public async ValueTask PublishDocsisEventAsync(int cmId, string cmName, CmEvent entry, CancellationToken ct = default)
    {
        if (entry.Kind == null) return;

        var (title, what) = entry.Kind switch
        {
            CmEventKinds.T3Timeout => ("T3 timeout", "a T3 timeout (no ranging response from the CMTS)"),
            CmEventKinds.T4Timeout => ("T4 timeout", "a T4 timeout (no station maintenance from the CMTS; the modem reinitializes its MAC)"),
            CmEventKinds.RangingFailure => ("ranging failure", "a ranging failure"),
            _ => (entry.Kind, entry.Kind),
        };

        _logger.LogDebug("Cable modem DOCSIS event: {CmName} {Kind} at {Time}", cmName, entry.Kind, entry.Time);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = $"cable_modem.{entry.Kind}",
            Source = "cable_modem",
            Severity = AlertSeverity.Warning,
            Title = $"{cmName} {title}{_siteSuffix}",
            Message = $"Cable modem {cmName} logged {what}: {entry.Text}",
            SourceUrl = MonitoringLinks.HardwareStats("cm", entry.Time ?? DateTime.UtcNow),
            Tags = ["cable_modem", "docsis_event"],
            Context = new Dictionary<string, string>
            {
                ["cm_id"] = cmId.ToString(),
                ["level"] = entry.Level,
                ["event"] = entry.Text,
            }
        }, ct);
    }

    /// <summary>
    /// Raises <c>cable_modem.reinit</c> when the modem reinitialized since the last poll: its
    /// reported reinit reason changed, or its uptime went backwards. The first poll after startup
    /// only records the baseline. A repeat of the same reason without a restart is not visible.
    /// </summary>
    public async ValueTask EvaluateReinitAsync(int cmId, string cmName, string? reinitReason, long? uptimeSeconds, CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(cmId.ToString(), _ => new CmAlertState());
        var hadBaseline = state.ReinitBaselined;
        var reasonChanged = hadBaseline && !string.IsNullOrEmpty(reinitReason)
            && !string.Equals(reinitReason, state.LastReinitReason, StringComparison.Ordinal);
        var restarted = hadBaseline && uptimeSeconds.HasValue && state.LastUptimeSeconds.HasValue
            && uptimeSeconds.Value < state.LastUptimeSeconds.Value;

        state.ReinitBaselined = true;
        if (!string.IsNullOrEmpty(reinitReason)) state.LastReinitReason = reinitReason;
        if (uptimeSeconds.HasValue) state.LastUptimeSeconds = uptimeSeconds;

        if (!reasonChanged && !restarted) return;

        var reason = string.IsNullOrEmpty(reinitReason) ? "unknown" : reinitReason;
        _logger.LogDebug("Cable modem reinitialized: {CmName} reason={Reason} restarted={Restarted}", cmName, reason, restarted);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = "cable_modem.reinit",
            Source = "cable_modem",
            Severity = AlertSeverity.Warning,
            Title = $"{cmName} reinitialized{_siteSuffix}",
            Message = $"Cable modem {cmName} reinitialized (reason: {reason}).",
            SourceUrl = MonitoringLinks.HardwareStats("cm", DateTime.UtcNow),
            Tags = ["cable_modem", "reinit"],
            Context = new Dictionary<string, string>
            {
                ["cm_id"] = cmId.ToString(),
                ["reinit_reason"] = reason,
            }
        }, ct);
    }

    /// <summary>Drops a modem's alert state (its config was deleted).</summary>
    public void Forget(int cmId) => _states.TryRemove(cmId.ToString(), out _);

    private class CmAlertState
    {
        public bool DsSnrBreached;
        public bool DsPowerBreached;
        public bool UsPowerBreached;
        public bool ChannelLossBreached;
        public int MaxLockedDsChannels;
        public bool ReinitBaselined;
        public string? LastReinitReason;
        public long? LastUptimeSeconds;
    }
}
