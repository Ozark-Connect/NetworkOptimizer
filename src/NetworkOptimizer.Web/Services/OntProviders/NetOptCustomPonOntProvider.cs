using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// Provider for the "Network Optimizer Custom" PON stats JSON contract
/// (docs/features/netopt-custom-pon-contract.md): a plain HTTP endpoint, typically
/// served from the gateway or the ONT itself, returning ITU-T PON-layer stats
/// (PLOAM state, GTC status/counters, GEM/allocation counters, bridge-port
/// discards, host-link counters) as JSON. Anyone can implement the contract for
/// their ONT hardware; the first implementation gathers Lantiq `onu` CLI output
/// from a GPON SFP stick.
///
/// Usable standalone like any ONT provider (PON state and error counters flow to
/// the ont measurement), but designed to be attached to a monitored SFP module
/// (ISfpSupplementalOntProvider), where its metrics merge into that module's sfp
/// measurement on the gateway SFP poll cycle.
/// </summary>
public class NetOptCustomPonOntProvider : ISfpSupplementalOntProvider
{
    private readonly ILogger<NetOptCustomPonOntProvider> _logger;

    /// <summary>
    /// Reference endpoints are one-shot listeners (a netcat accept loop) that serve a
    /// single connection at a time and re-gather stats per request, so requests to the
    /// same endpoint must never overlap. Gated per (host, port) so distinct endpoints -
    /// different devices, different sites - still poll in parallel (this is a shared
    /// singleton across all sites).
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new();

    private const int DefaultPort = 10012;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public NetOptCustomPonOntProvider(ILogger<NetOptCustomPonOntProvider> logger)
    {
        _logger = logger;
    }

    public string ProviderKey => "netopt-custom";
    public string DisplayName => "Network Optimizer Custom (HTTP JSON)";

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        var payload = await FetchPayloadAsync(context, cancellationToken);
        if (payload == null)
            return PollResult<OntStats>.Failed(
                $"No stats could be read from {context.ConfiguredHost ?? context.Host}.");
        var stats = MapToOntStats(payload);
        stats.DeviceHost = context.ConfiguredHost ?? context.Host;
        stats.DeviceName = context.Name;
        return PollResult<OntStats>.Ok(stats);
    }

    public async Task<PonSupplementalStats?> PollSupplementalAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        var payload = await FetchPayloadAsync(context, cancellationToken);
        return payload == null ? null : MapToSupplemental(payload);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await FetchPayloadAsync(context, cancellationToken, throwOnError: true);
            if (payload == null)
                return (false, "Endpoint returned no parseable stats");

            var state = ToPonLinkState(payload.Ploam?.CurrState);
            var detail = state != PonLinkState.Unknown
                ? $"PLOAM {state.ToDisplayString()}"
                : "no PLOAM section";
            if (payload.GtcStatus?.OnuId != null)
                detail += $", ONU ID {payload.GtcStatus.OnuId}";
            if (payload.SfpUptimeS != null)
            {
                var up = TimeSpan.FromSeconds(payload.SfpUptimeS.Value);
                detail += $", up {(int)up.TotalDays}d {up.Hours}h {up.Minutes}m";
            }
            return (true, $"Connected - {detail}");
        }
        catch (HttpRequestException ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.ConfiguredHost ?? context.Host));
        }
        catch (TaskCanceledException)
        {
            return (false, "Connection timed out");
        }
        catch (Exception ex)
        {
            return (false, $"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetch and parse the endpoint. Returns null on failure (logged at Debug)
    /// unless <paramref name="throwOnError"/> (the Test button wants the message).
    /// </summary>
    private async Task<NetOptCustomPonPayload?> FetchPayloadAsync(
        OntPollContext context, CancellationToken cancellationToken, bool throwOnError = false)
    {
        var port = context.Port > 0 ? context.Port : DefaultPort;
        var url = $"http://{context.Host}:{port}/";

        var gate = _requestGates.GetOrAdd($"{context.Host}:{port}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Generous timeout: reference implementations gather stats on demand
            // (e.g. an SSH round-trip into the SFP stick) before responding.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await client.GetStringAsync(url, cancellationToken);

            var payload = ParsePayload(json);
            if (payload == null)
            {
                _logger.LogDebug("PON stats endpoint {Host} returned unparseable payload", context.ConfiguredHost ?? context.Host);
                if (throwOnError) throw new InvalidOperationException("Response was not valid PON stats JSON");
                return null;
            }

            if (!string.IsNullOrEmpty(payload.Error))
            {
                _logger.LogDebug("PON stats endpoint {Host} reported error: {Error} - {Message}",
                    context.ConfiguredHost ?? context.Host, payload.Error, payload.Message);
                if (throwOnError)
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(payload.Message) ? payload.Error : $"{payload.Error}: {payload.Message}");
                return null;
            }

            return payload;
        }
        catch (Exception ex) when (!throwOnError)
        {
            _logger.LogDebug(ex, "PON stats poll failed for {Host}", context.ConfiguredHost ?? context.Host);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    internal static NetOptCustomPonPayload? ParsePayload(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NetOptCustomPonPayload>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Map the contract payload onto the shared PON supplemental DTO. Standard
    /// concepts keep their standard encodings: PLOAM states use the same strings
    /// as the ont measurement's pon_link_status; fec_errors is uncorrectable
    /// codewords; bip_errors is the raw BIP counter. DDM optics readings are a
    /// fallback only - the gateway's own SFP DDM poll takes precedence when it can
    /// read the module (see CollectSfpForDevice).
    /// </summary>
    internal static PonSupplementalStats MapToSupplemental(NetOptCustomPonPayload p) => new()
    {
        RxPowerDbm = p.Optics?.RxPowerDbm,
        TxPowerDbm = p.Optics?.TxPowerDbm,
        TemperatureC = p.Optics?.TemperatureC,
        VoltageV = p.Optics?.VoltageV,
        PloamStateRaw = p.Ploam?.CurrState,
        PonLinkStatus = EncodePloamState(p.Ploam?.CurrState),
        PonLinkStatusPrev = EncodePloamState(p.Ploam?.PreviousState),
        PloamElapsedMs = p.Ploam?.ElapsedMsec,
        GtcDsState = p.GtcStatus?.DsState,
        OnuId = p.GtcStatus?.OnuId,
        DsFecEnabled = p.GtcStatus?.DsFecEnable,
        UsFecEnabled = p.GtcStatus?.UsFecEnable,
        OnuResponseTime = p.GtcStatus?.OnuResponseTime,
        BipErrors = p.GtcCounters?.Bip,
        FecErrors = p.GtcCounters?.FecWordsUncorr,
        FecCorrectedWords = p.GtcCounters?.FecWordsCorr,
        HecCorrected = p.GtcCounters?.HecErrorCorr,
        HecUncorrected = p.GtcCounters?.HecErrorUncorr,
        BwmapCorrected = p.GtcCounters?.BwmapErrorCorr,
        BwmapUncorrected = p.GtcCounters?.BwmapErrorUncorr,
        GemTxFrames = p.GtcCounters?.TxGemFramesTotal,
        GemTxIdleFrames = p.GtcCounters?.TxGemIdleFramesTotal,
        GemRxFrames = p.GtcCounters?.RxGemFramesTotal,
        GemRxDropped = p.GtcCounters?.RxGemFramesDropped,
        AllocTotal = p.GtcCounters?.AllocationsTotal,
        AllocLost = p.GtcCounters?.AllocationsLost,
        GpePonIngressDiscard = p.GpePon?.IbpDiscard,
        GpePonEgressDiscard = p.GpePon?.EbpDiscard,
        GpePonLearningDiscard = p.GpePon?.LearningDiscard,
        GpeLanIngressDiscard = p.GpeLan?.IbpDiscard,
        GpeLanEgressDiscard = p.GpeLan?.EbpDiscard,
        GpeLanLearningDiscard = p.GpeLan?.LearningDiscard,
        LanLinkStatus = p.Lan?.LinkStatus,
        LanTxFrames = p.LanCounters?.TxFrames,
        LanRxFrames = p.LanCounters?.RxFrames,
        LanTxDropEvents = p.LanCounters?.TxDropEvents,
        LanRxFcsErrors = p.LanCounters?.RxFcsErr,
        LanBufferOverflow = p.LanCounters?.BufferOverflow,
        SfpUptimeS = p.SfpUptimeS,
    };

    /// <summary>
    /// Standalone mapping: the standard OntStats fields this contract can serve.
    /// DDM optics readings are populated only when the endpoint supplies the
    /// optional <c>optics</c> section; in the SFP-module (attached) scenario the
    /// gateway's own DDM poll owns those and takes precedence.
    /// </summary>
    internal static OntStats MapToOntStats(NetOptCustomPonPayload p) => new()
    {
        Timestamp = DateTime.UtcNow,
        RxPowerDbm = p.Optics?.RxPowerDbm,
        TxPowerDbm = p.Optics?.TxPowerDbm,
        TemperatureC = p.Optics?.TemperatureC,
        VoltageV = p.Optics?.VoltageV,
        PonLinkStatus = ToPonLinkState(p.Ploam?.CurrState),
        FecErrors = p.GtcCounters?.FecWordsUncorr,
        BipErrors = p.GtcCounters?.Bip,
    };

    /// <summary>Raw PLOAM state number (1-7 = O1-O7) to the shared enum.</summary>
    internal static PonLinkState ToPonLinkState(long? state) =>
        state is >= 1 and <= 7 ? (PonLinkState)(int)state.Value : PonLinkState.Unknown;

    private static string? EncodePloamState(long? state) =>
        state == null ? null : ToPonLinkState(state).ToInfluxValue();
}

/// <summary>
/// The "Network Optimizer Custom" PON stats JSON contract, v1. Every section is
/// optional - implementations serve what their hardware exposes, and absent
/// sections are simply not recorded. Counters are cumulative since ONT boot.
/// See docs/features/netopt-custom-pon-contract.md for field semantics.
/// </summary>
public class NetOptCustomPonPayload
{
    /// <summary>Machine-readable failure code (e.g. "sfp_unreachable"). Non-null means the poll failed.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Human-readable failure detail accompanying <see cref="Error"/>.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("optics")]
    public OpticsSection? Optics { get; set; }

    [JsonPropertyName("lan")]
    public LanSection? Lan { get; set; }

    [JsonPropertyName("lan_counters")]
    public LanCountersSection? LanCounters { get; set; }

    [JsonPropertyName("ploam")]
    public PloamSection? Ploam { get; set; }

    [JsonPropertyName("gtc_status")]
    public GtcStatusSection? GtcStatus { get; set; }

    [JsonPropertyName("gtc_counters")]
    public GtcCountersSection? GtcCounters { get; set; }

    [JsonPropertyName("gpe_pon")]
    public GpeBridgePortSection? GpePon { get; set; }

    [JsonPropertyName("gpe_lan")]
    public GpeBridgePortSection? GpeLan { get; set; }

    /// <summary>Seconds since the ONT module booted.</summary>
    [JsonPropertyName("sfp_uptime_s")]
    public long? SfpUptimeS { get; set; }

    /// <summary>
    /// DDM optics readings, in the same units as SFP DDM (dBm / degrees C / volts).
    /// Optional - serve these when the module exposes DDM but the gateway cannot
    /// read it off the SFP slot (common with GPON sticks). When the config is
    /// attached to a monitored SFP module and the gateway's own DDM poll returns a
    /// value, that value wins; these are used only to fill the gaps.
    /// </summary>
    public class OpticsSection
    {
        /// <summary>Receive optical power in dBm.</summary>
        [JsonPropertyName("rx_power_dbm")]
        public double? RxPowerDbm { get; set; }

        /// <summary>Transmit optical power in dBm.</summary>
        [JsonPropertyName("tx_power_dbm")]
        public double? TxPowerDbm { get; set; }

        /// <summary>Transceiver temperature in degrees Celsius.</summary>
        [JsonPropertyName("temperature_c")]
        public double? TemperatureC { get; set; }

        /// <summary>Supply voltage in volts.</summary>
        [JsonPropertyName("voltage_v")]
        public double? VoltageV { get; set; }
    }

    public class LanSection
    {
        [JsonPropertyName("mode")]
        public long? Mode { get; set; }

        [JsonPropertyName("link_status")]
        public long? LinkStatus { get; set; }

        [JsonPropertyName("phy_duplex")]
        public long? PhyDuplex { get; set; }
    }

    public class LanCountersSection
    {
        [JsonPropertyName("tx_frames")]
        public long? TxFrames { get; set; }

        [JsonPropertyName("rx_frames")]
        public long? RxFrames { get; set; }

        [JsonPropertyName("tx_drop_events")]
        public long? TxDropEvents { get; set; }

        [JsonPropertyName("rx_fcs_err")]
        public long? RxFcsErr { get; set; }

        [JsonPropertyName("buffer_overflow")]
        public long? BufferOverflow { get; set; }
    }

    public class PloamSection
    {
        /// <summary>Current ITU-T activation state, numeric: 1-7 = O1-O7.</summary>
        [JsonPropertyName("curr_state")]
        public long? CurrState { get; set; }

        [JsonPropertyName("previous_state")]
        public long? PreviousState { get; set; }

        [JsonPropertyName("elapsed_msec")]
        public long? ElapsedMsec { get; set; }
    }

    public class GtcStatusSection
    {
        [JsonPropertyName("ds_state")]
        public long? DsState { get; set; }

        [JsonPropertyName("onu_id")]
        public long? OnuId { get; set; }

        [JsonPropertyName("ds_fec_enable")]
        public long? DsFecEnable { get; set; }

        [JsonPropertyName("us_fec_enable")]
        public long? UsFecEnable { get; set; }

        [JsonPropertyName("onu_response_time")]
        public long? OnuResponseTime { get; set; }
    }

    public class GtcCountersSection
    {
        [JsonPropertyName("bip")]
        public long? Bip { get; set; }

        [JsonPropertyName("hec_error_corr")]
        public long? HecErrorCorr { get; set; }

        [JsonPropertyName("hec_error_uncorr")]
        public long? HecErrorUncorr { get; set; }

        [JsonPropertyName("bwmap_error_corr")]
        public long? BwmapErrorCorr { get; set; }

        [JsonPropertyName("bwmap_error_uncorr")]
        public long? BwmapErrorUncorr { get; set; }

        [JsonPropertyName("fec_error_corr")]
        public long? FecErrorCorr { get; set; }

        [JsonPropertyName("fec_words_corr")]
        public long? FecWordsCorr { get; set; }

        [JsonPropertyName("fec_words_uncorr")]
        public long? FecWordsUncorr { get; set; }

        [JsonPropertyName("fec_words_total")]
        public long? FecWordsTotal { get; set; }

        [JsonPropertyName("fec_seconds")]
        public long? FecSeconds { get; set; }

        [JsonPropertyName("tx_gem_frames_total")]
        public long? TxGemFramesTotal { get; set; }

        [JsonPropertyName("tx_gem_bytes_total")]
        public long? TxGemBytesTotal { get; set; }

        [JsonPropertyName("tx_gem_idle_frames_total")]
        public long? TxGemIdleFramesTotal { get; set; }

        [JsonPropertyName("rx_gem_frames_total")]
        public long? RxGemFramesTotal { get; set; }

        [JsonPropertyName("rx_gem_bytes_total")]
        public long? RxGemBytesTotal { get; set; }

        [JsonPropertyName("rx_gem_frames_dropped")]
        public long? RxGemFramesDropped { get; set; }

        [JsonPropertyName("omci_drop")]
        public long? OmciDrop { get; set; }

        [JsonPropertyName("drop")]
        public long? Drop { get; set; }

        [JsonPropertyName("rx_oversized_frames")]
        public long? RxOversizedFrames { get; set; }

        [JsonPropertyName("allocations_total")]
        public long? AllocationsTotal { get; set; }

        [JsonPropertyName("allocations_lost")]
        public long? AllocationsLost { get; set; }
    }

    public class GpeBridgePortSection
    {
        [JsonPropertyName("ibp_good")]
        public long? IbpGood { get; set; }

        [JsonPropertyName("ibp_discard")]
        public long? IbpDiscard { get; set; }

        [JsonPropertyName("ebp_good")]
        public long? EbpGood { get; set; }

        [JsonPropertyName("ebp_discard")]
        public long? EbpDiscard { get; set; }

        [JsonPropertyName("learning_discard")]
        public long? LearningDiscard { get; set; }
    }
}
