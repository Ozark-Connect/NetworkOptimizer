using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Evaluates cellular modem readings and publishes alert events on state
/// transitions. Tracks three conditions per modem:
/// - Signal quality dropping below the poor threshold (composite score)
/// - Network mode downgrade (e.g. 5G SA → 5G NSA → LTE)
/// - Unexpected roaming state
/// All checks are state-transition based with hysteresis to avoid flapping.
/// </summary>
public class CellularAlertEvaluator
{
    private const int SignalPoorThreshold = 25;
    private const int SignalClearThreshold = 35;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<CellularAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<int, CellularAlertState> _states = new();
    private readonly string _siteSuffix;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/> - modem ids are per-site database
    /// sequences, so state must not be shared). Non-default sites get their
    /// slug appended to alert titles.
    /// </param>
    public CellularAlertEvaluator(IAlertEventBus eventBus, ILogger<CellularAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    public async ValueTask EvaluateAsync(
        int modemId, string modemName,
        int signalQuality, CellularNetworkMode networkMode, bool isRoaming,
        CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(modemId, _ => new CellularAlertState());

        await EvaluateSignalQuality(state, modemId, modemName, signalQuality, ct);
        await EvaluateNetworkMode(state, modemId, modemName, networkMode, ct);
        await EvaluateRoaming(state, modemId, modemName, isRoaming, ct);
    }

    private async ValueTask EvaluateSignalQuality(
        CellularAlertState state, int modemId, string modemName,
        int signalQuality, CancellationToken ct)
    {
        if (!state.SignalBreached && signalQuality < SignalPoorThreshold && signalQuality > 0)
        {
            state.SignalBreached = true;
            _logger.LogDebug("Cellular signal poor: modem {ModemId} quality={Quality}", modemId, signalQuality);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "cellular.signal_poor",
                Source = "cellular",
                Severity = AlertSeverity.Warning,
                Title = $"{modemName} signal quality poor{_siteSuffix}",
                Message = $"Cellular modem {modemName} signal quality dropped to {signalQuality}/100, below the {SignalPoorThreshold} threshold.",
                DeviceName = modemName,
                MetricValue = signalQuality,
                ThresholdValue = SignalPoorThreshold,
                SourceUrl = "/monitoring?tab=cellular",
                Tags = ["cellular", "signal"],
                Context = new Dictionary<string, string>
                {
                    ["modem_id"] = modemId.ToString(),
                    ["metric"] = "signal_quality"
                }
            }, ct);
        }
        else if (state.SignalBreached && signalQuality >= SignalClearThreshold)
        {
            state.SignalBreached = false;
        }
    }

    private async ValueTask EvaluateNetworkMode(
        CellularAlertState state, int modemId, string modemName,
        CellularNetworkMode networkMode, CancellationToken ct)
    {
        var currentRank = ModeRank(networkMode);
        var previousRank = state.LastKnownModeRank;

        state.LastKnownModeRank = currentRank;

        if (!previousRank.HasValue || networkMode == CellularNetworkMode.Unknown)
            return;

        if (currentRank < previousRank.Value && previousRank.Value > 0)
        {
            if (!state.NetworkDowngraded)
            {
                state.NetworkDowngraded = true;
                var previousMode = RankToLabel(previousRank.Value);
                var currentMode = ModeLabel(networkMode);

                _logger.LogDebug("Cellular network downgrade: modem {ModemId} {From} -> {To}",
                    modemId, previousMode, currentMode);

                await _eventBus.PublishAsync(new AlertEvent
                {
                    EventType = "cellular.network_downgrade",
                    Source = "cellular",
                    Severity = AlertSeverity.Warning,
                    Title = $"{modemName} network downgraded{_siteSuffix}",
                    Message = $"Cellular modem {modemName} dropped from {previousMode} to {currentMode}.",
                    DeviceName = modemName,
                    SourceUrl = "/monitoring?tab=cellular",
                    Tags = ["cellular", "network_mode"],
                    Context = new Dictionary<string, string>
                    {
                        ["modem_id"] = modemId.ToString(),
                        ["previous_mode"] = previousMode,
                        ["current_mode"] = currentMode
                    }
                }, ct);
            }
        }
        else if (currentRank >= previousRank.Value)
        {
            state.NetworkDowngraded = false;
        }
    }

    private async ValueTask EvaluateRoaming(
        CellularAlertState state, int modemId, string modemName,
        bool isRoaming, CancellationToken ct)
    {
        var wasRoaming = state.IsRoaming;
        state.IsRoaming = isRoaming;

        if (isRoaming && !wasRoaming && state.HasObservedRoaming)
        {
            _logger.LogDebug("Cellular roaming detected: modem {ModemId}", modemId);

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = "cellular.roaming",
                Source = "cellular",
                Severity = AlertSeverity.Warning,
                Title = $"{modemName} is roaming{_siteSuffix}",
                Message = $"Cellular modem {modemName} entered roaming state. This may indicate loss of primary carrier coverage or incur additional charges.",
                DeviceName = modemName,
                SourceUrl = "/monitoring?tab=cellular",
                Tags = ["cellular", "roaming"],
                Context = new Dictionary<string, string>
                {
                    ["modem_id"] = modemId.ToString()
                }
            }, ct);
        }

        state.HasObservedRoaming = true;
    }

    private static int ModeRank(CellularNetworkMode mode) => mode switch
    {
        CellularNetworkMode.Unknown => 0,
        CellularNetworkMode.Lte => 1,
        CellularNetworkMode.Nr5gNsa => 2,
        CellularNetworkMode.Nr5gSa => 3,
        _ => 0
    };

    private static string ModeLabel(CellularNetworkMode mode) => mode switch
    {
        CellularNetworkMode.Lte => "LTE",
        CellularNetworkMode.Nr5gNsa => "5G NSA",
        CellularNetworkMode.Nr5gSa => "5G SA",
        _ => "Unknown"
    };

    private static string RankToLabel(int rank) => rank switch
    {
        1 => "LTE",
        2 => "5G NSA",
        3 => "5G SA",
        _ => "Unknown"
    };

    private class CellularAlertState
    {
        public bool SignalBreached;
        public int? LastKnownModeRank;
        public bool NetworkDowngraded;
        public bool IsRoaming;
        public bool HasObservedRoaming;
    }
}
