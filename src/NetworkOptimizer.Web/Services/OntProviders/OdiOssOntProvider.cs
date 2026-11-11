using System.Collections.Concurrent;
using System.Globalization;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for GPON sticks running odi-oss (https://github.com/AndreZiviani/odi-oss), the
/// open firmware for Realtek RTL9601/9602 sticks (ODI DFP-34X-2C2 and its HSGQ / Luleey
/// siblings). odi-oss ships a Prometheus exporter, metricsd, on TCP 9100; this provider reads
/// its text exposition, so no credentials, no web UI scraping and no telnet are involved.
///
/// The PON counters come from the odi-oss GPON driver's own 64-bit totals in /proc/odi_gpon:
/// the hardware counters clear on read, the driver is their one reader, and metricsd only
/// relays its totals. Polling therefore takes nothing away from the stick's own web UI.
/// Metric semantics: https://github.com/AndreZiviani/odi-sfp-exporter/blob/main/docs/METRICS.md
///
/// The stick sits in a gateway SFP slot, so it can be attached to that monitored module
/// (ISfpSupplementalOntProvider): its PON state and counters merge into the module's sfp series,
/// and its optics fill only the gaps the gateway's own DDM read leaves.
/// </summary>
public sealed class OdiOssOntProvider : ISfpSupplementalOntProvider
{
    public string ProviderKey => "odi-oss";
    public string DisplayName => "odi-oss GPON Stick (Prometheus)";

    internal const int DefaultPort = 9100;

    /// <summary>
    /// One scrape runs a diag batch and short omcicli calls on the stick, each bounded by the
    /// exporter at 2-3 s; a slow stick answers in a few seconds, not instantly.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A full scrape is about 40 KB; anything far larger is not this exporter.</summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly ILogger<OdiOssOntProvider> _logger;

    /// <summary>
    /// metricsd answers one connection at a time, and each scrape forks diag on a 2-core
    /// MIPS stick with ~27 MB of RAM. Requests to one stick never overlap: an attached
    /// config and a standalone one on the same site would otherwise double the work.
    /// Keyed by site and endpoint for the same reason as the netopt-custom provider.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _requestGates = new();

    public OdiOssOntProvider(ILogger<OdiOssOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return PollResult<OntStats>.Failed("No address is configured for this device.");

        try
        {
            var body = await FetchAsync(context, cancellationToken);
            var metrics = PrometheusText.Parse(body);
            if (!IsOdiExporter(metrics))
                return PollResult<OntStats>.Failed($"{context.Host} did not answer as an odi-oss exporter.");

            var stats = MapToOntStats(metrics);
            stats.DeviceHost = context.Host;
            stats.DeviceName = context.Name;
            return PollResult<OntStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "odi-oss poll failed for {Name} at {Host}", context.Name, context.Host);
            return PollResult<OntStats>.Failed(HttpFailureSummary.Describe(ex, context.Host));
        }
    }

    public async Task<PonSupplementalStats?> PollSupplementalAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        var result = await PollAsync(context, cancellationToken);
        return result.Stats?.Pon;
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            var body = await FetchAsync(context, cancellationToken);
            var metrics = PrometheusText.Parse(body);
            if (!IsOdiExporter(metrics))
                return (false, "Connected, but the response is not odi-oss exporter metrics");

            return (true, $"Connected - {Describe(metrics)}");
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.Host));
        }
    }

    private async Task<string> FetchAsync(OntPollContext context, CancellationToken cancellationToken)
    {
        var port = context.Port > 0 ? context.Port : DefaultPort;
        var gate = _requestGates.GetOrAdd($"{context.SiteSlug}/{context.Host}:{port}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler { ConnectCallback = DeviceHttp.Via(context.Dialer) })
            {
                Timeout = RequestTimeout,
                MaxResponseContentBufferSize = MaxResponseBytes,
            };
            // metricsd answers any path; /metrics is the conventional one.
            return await client.GetStringAsync($"http://{FormatHost(context.Host)}:{port}/metrics", cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static string FormatHost(string host)
    {
        var h = host.Trim();
        return h.Contains(':') && !h.StartsWith('[') ? $"[{h}]" : h;
    }

    /// <summary>gpon_exporter_up is the exporter's own "I ran" series; nothing else serves it.</summary>
    internal static bool IsOdiExporter(PrometheusText metrics) => metrics.Has("gpon_exporter_up");

    internal static string Describe(PrometheusText metrics)
    {
        var parts = new List<string>();
        var image = metrics.Label("gpon_image_info", "image");
        if (image != null) parts.Add($"odi-oss {image}");

        var state = ToPonLinkState(metrics.Long("gpon_onu_state"));
        parts.Add(state != PonLinkState.Unknown ? state.ToDisplayString() : "no ONU state");

        var onuId = metrics.Long("gpon_onu_id");
        if (onuId != null) parts.Add($"ONU ID {onuId}");

        var uptime = metrics.Double("gpon_uptime_seconds");
        if (uptime is >= 0)
        {
            var up = TimeSpan.FromSeconds(Math.Floor(uptime.Value));
            parts.Add($"up {(int)up.TotalDays}d {up.Hours}h {up.Minutes}m");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Standalone mapping. The PON detail rides along so both modes record the same series.</summary>
    internal static OntStats MapToOntStats(PrometheusText m)
    {
        var state = ToPonLinkState(m.Long("gpon_onu_state"));
        var pon = MapToSupplemental(m);

        return new OntStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceModel = "odi-oss",
            PonType = "GPON",
            RxPowerDbm = pon.RxPowerDbm,
            TxPowerDbm = pon.TxPowerDbm,
            TemperatureC = pon.TemperatureC,
            VoltageV = pon.VoltageV,
            BiasMa = Finite(m.Double("gpon_bias_current_ma")),
            PonLinkStatus = state,
            LinkState = state != PonLinkState.Unknown ? state.ToDisplayString() : null,
            OperationalStatus = state switch
            {
                PonLinkState.Operation => "Up",
                PonLinkState.Unknown => null,
                _ => "Down",
            },
            FecErrors = pon.FecErrors,
            BipErrors = pon.BipErrors,
            Pon = pon,
        };
    }

    /// <summary>
    /// The exporter series that carry the same meaning as the shared PON fields. What odi-oss
    /// does not expose with matching semantics stays null rather than approximated: previous
    /// state and time in the current state (the exporter keeps per-state totals, not the
    /// current visit), uncorrectable HEC, lost allocations, upstream idle GEM frames, the bridge-port
    /// discards, and the host link (port 0's MIB counters are reset every 15 minutes by the
    /// OMCI PM interval, and the link bit does not describe a 2500BASE-X host link).
    /// </summary>
    internal static PonSupplementalStats MapToSupplemental(PrometheusText m)
    {
        var state = m.Long("gpon_onu_state");
        var known = ToPonLinkState(state) != PonLinkState.Unknown;
        var uptime = m.Double("gpon_uptime_seconds");
        // A transition is printed once it has happened, so "never left O5" is the family being
        // there without a from="O5" line, not a missing value.
        var hasTransitions = m.Has("gpon_onu_state_transitions_total");
        var raised = m.Has("gpon_transceiver_alarm")
            ? m.LabelsWhere("gpon_transceiver_alarm", "1", s => $"{s("kind")}:{s("condition")}")
            : null;

        return new PonSupplementalStats
        {
            // The optics are absent when the module's I2C read failed (gpon_transceiver_up 0).
            RxPowerDbm = Finite(m.Double("gpon_rx_power_dbm")),
            TxPowerDbm = Finite(m.Double("gpon_tx_power_dbm")),
            TemperatureC = Finite(m.Double("gpon_temperature_celsius")),
            VoltageV = Finite(m.Double("gpon_voltage_volts")),

            PloamStateRaw = known ? state : null,
            PonLinkStatus = known ? ToPonLinkState(state).ToInfluxValue() : null,
            OnuId = m.Long("gpon_onu_id"),
            // The bit read in the counter sample is the one the frame and idle counts were taken under,
            // so it is the one the load calculation needs; older exporters only have the status bit.
            DsFecEnabled = m.Long("gpon_counter_sample_fec_active", ("direction", "ds"))
                ?? m.Long("gpon_fec_active", ("direction", "ds")),
            UsFecEnabled = m.Long("gpon_fec_active", ("direction", "us")),

            // BIP-8 error blocks: what the REI reports to the OLT.
            BipErrors = m.Long("gpon_ds_bip_error_blocks_total"),
            FecErrors = m.Long("gpon_ds_fec_codewords_total", ("result", "uncorrectable")),
            FecCorrectedWords = m.Long("gpon_ds_fec_codewords_total", ("result", "corrected")),
            HecCorrected = m.Long("gpon_ds_gem_hec_corrected_total"),
            GemTxFrames = m.Long("gpon_us_gem_frames_total"),
            GemRxFrames = m.Long("gpon_ds_gem_frames_total", ("kind", "non_idle")),
            // Idle fill and the frame counter it is read against: the downstream load of the whole PON.
            GemRxIdleFrames = m.Long("gpon_ds_gem_frames_total", ("kind", "idle")),
            DsSuperframe = m.Long("gpon_ds_superframe"),
            DsSuperframeSampled = m.Long(SampledSuperframeMetric),
            // This ONT's own downstream, to set beside the PON's: every downstream GEM port it has.
            GemRxBytes = OwnDownstreamBytes(m),
            OltLaunchPowerDbm = LaunchPower(m.Double("gpon_olt_launch_power_dbm")),
            OdnClass = m.Long("gpon_pon_id_odn_class") is long cls and >= 0 and <= 4 ? cls : null,
            ReachExtender = m.Long("gpon_pon_id_reach_extender") is long re and (0 or 1) ? re : null,
            AllocTotal = m.Long("gpon_ds_bwmap_allocations_total"),
            GemTxBytes = m.Long("gpon_us_gem_bytes_total"),

            BiasMa = Finite(m.Double("gpon_bias_current_ma")),
            BiasWarnHighMa = Finite(m.Double("gpon_transceiver_limit", ("measure", "bias_current"), ("level", "warning"), ("side", "high"))),
            TransceiverAlarmCount = raised?.Count,
            TransceiverAlarms = raised is { Count: > 0 } ? string.Join(",", raised) : null,
            FecCorrectedBits = m.Long("gpon_ds_fec_corrected_bits_total"),
            OmciRxFrames = m.Long("gpon_ds_omci_frames_total"),
            MibDataSync = m.Long("gpon_provision_mib_data_sync"),
            OmciUp = m.Long("gpon_omci_up") is long omciUp and (0 or 1) ? omciUp : null,
            OmciServices = m.Long("gpon_omci_services"),
            PloamRequestKey = PloamReceived(m, "0x0d"),
            PloamKeySwitch = PloamReceived(m, "0x13"),
            PloamRangingTime = PloamReceived(m, "0x04"),
            TrafficPirBytesPerSecond = m.MaxLong("gpon_provision_traffic_descriptor_pir_bytes_per_second"),
            TrafficCirBytesPerSecond = m.MaxLong("gpon_provision_traffic_descriptor_cir_bytes_per_second"),
            HostLinkUp = m.Long("gpon_host_link_up") is long hostUp and (0 or 1) ? hostUp : null,
            EqdBits = m.Long("gpon_ranging_eqd_bits") is long eqd and >= 0 ? eqd : null,
            PonIdAgeSeconds = PonIdAge(uptime, m.Double("gpon_pon_id_received_uptime_seconds")),
            CpuBusySeconds = m.SumDoubleExcept("gpon_cpu_seconds_total", "mode", "idle"),
            CpuTotalSeconds = m.SumDoubleExcept("gpon_cpu_seconds_total", "mode", ""),
            UsBursts = m.Long("gpon_us_bursts_total"),
            UsDbru = m.Long("gpon_us_dbru_total"),

            // The driver's own clock on the activation states and the alarms: what happened
            // between two polls, which the state at poll time cannot say.
            O5Seconds = Finite(m.Double("gpon_onu_state_seconds_total", ("state", "O5"))),
            NotO5Seconds = m.SumDoubleExcept("gpon_onu_state_seconds_total", "state", "O5"),
            O5Exits = m.SumLong("gpon_onu_state_transitions_total", ("from", "O5")) ?? (hasTransitions ? 0 : null),
            LosEvents = m.Long("gpon_alarm_events_total", ("alarm", "los")),
            LosSeconds = Finite(m.Double("gpon_alarm_seconds_total", ("alarm", "los"))),
            LofEvents = m.Long("gpon_alarm_events_total", ("alarm", "lof")),
            LofSeconds = Finite(m.Double("gpon_alarm_seconds_total", ("alarm", "lof"))),
            OltDeactivations = OltDeactivations(m),
            BootCount = m.Long("gpon_boot_count"),
            LastResetReason = Clean(m.Label("gpon_last_reset_reason", "reason")),

            SfpUptimeS = uptime is >= 0 and < long.MaxValue ? (long)Math.Floor(uptime.Value) : null,
            FirmwareVersion = Clean(m.Label("gpon_image_info", "image")),
        };
    }

    /// <summary>
    /// The superframe count from the same driver sample as the counter totals
    /// (ctr_ds_superframe in /proc/odi_gpon/counters, odi-oss v1.16.0-beta.4 and later). The
    /// exporter serves it under this name from the release after v1.9.0; before that the line is
    /// skipped and gpon_ds_superframe stands in, which is read live and can be one sample
    /// (a second) ahead of the totals.
    /// </summary>
    internal const string SampledSuperframeMetric = "gpon_counter_sample_ds_superframe";

    /// <summary>
    /// The driver stamps a PON-ID message with the uptime at which it arrived, so its age is the
    /// uptime less that stamp. The stamp is kept in 32 bits of milliseconds and wraps after 49.7
    /// days, after which it reads as later than now or far in the past: no age then, rather than
    /// a wrong one.
    /// </summary>
    internal static double? PonIdAge(double? uptime, double? receivedAt)
    {
        if (Finite(uptime) is not double up || Finite(receivedAt) is not double at || at < 0) return null;
        if (up >= PonIdStampWrapSeconds) return null;
        var age = up - at;
        // A scrape reads the stamp and the uptime a moment apart.
        return age >= -2 ? Math.Round(Math.Max(0, age), 1) : null;
    }

    private const double PonIdStampWrapSeconds = 4294967.296;

    /// <summary>
    /// The exporter drops the sample when the OLT sends no PON-ID or reports no power, so absent
    /// is the "not supported" case. The PON-ID field (TOL) spans -30 dBm upward in 0.1 dB steps;
    /// anything outside what a PON transmitter can launch is not a reading.
    /// </summary>
    private static double? LaunchPower(double? dbm) =>
        Finite(dbm) is double d && d >= -30 && d <= 30 ? d : null;

    /// <summary>
    /// Deactivate_ONU-ID (message id 5) and Disable_Serial_Number (6) received, G.984.3 9.2.3: the
    /// two ways an OLT takes an ONU off the PON. The exporter prints a type once it was seen, so
    /// with the family present a missing type is zero.
    /// </summary>
    private static long? OltDeactivations(PrometheusText m) =>
        PloamReceived(m, "0x05") is long deactivate && PloamReceived(m, "0x06") is long disable ? deactivate + disable : null;

    /// <summary>
    /// Downstream PLOAM messages of one type (G.984.3 9.2.3). The exporter prints a type once it
    /// was seen, so with the family present a missing type is zero; without the family it is unknown.
    /// </summary>
    private static long? PloamReceived(PrometheusText m, string type) =>
        m.Has("gpon_ploam_messages_by_type_total")
            ? m.Long("gpon_ploam_messages_by_type_total", ("direction", "ds"), ("type", type)) ?? 0
            : null;

    /// <summary>
    /// This ONT's user traffic downstream: every downstream GEM port but 4095, which is the OMCI
    /// channel and not traffic.
    /// </summary>
    private static long? OwnDownstreamBytes(PrometheusText m) =>
        m.SumLong("gpon_gem_bytes_total", ("direction", "ds")) is long all
            ? all - (m.Long("gpon_gem_bytes_total", ("direction", "ds"), ("gem_port", "4095")) ?? 0)
            : null;

    /// <summary>gpon_onu_state is the N in O(N), the same raw numbering as the netopt-custom contract.</summary>
    private static PonLinkState ToPonLinkState(long? state) => NetOptCustomPonOntProvider.ToPonLinkState(state);

    private static double? Finite(double? v) => v is { } d && double.IsFinite(d) ? d : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A minimal reader for the Prometheus text exposition format (version 0.0.4), enough for
/// one exporter's flat series: comments and blank lines are skipped, label values are
/// unescaped, an optional timestamp is ignored, and malformed lines are dropped one by one
/// rather than failing the scrape. Values keep their text so integer counters above 2^53
/// are read exactly.
/// </summary>
public sealed class PrometheusText
{
    private readonly Dictionary<string, List<Sample>> _series = new(StringComparer.Ordinal);

    private sealed record Sample(IReadOnlyDictionary<string, string> Labels, string Value);

    public static PrometheusText Parse(string? text)
    {
        var result = new PrometheusText();
        if (string.IsNullOrEmpty(text))
            return result;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (TryParseLine(line, out var name, out var labels, out var value))
            {
                if (!result._series.TryGetValue(name, out var list))
                    result._series[name] = list = new List<Sample>();
                list.Add(new Sample(labels, value));
            }
        }
        return result;
    }

    public bool Has(string name) => _series.ContainsKey(name);

    /// <summary>The first sample of <paramref name="name"/> whose labels include all of <paramref name="match"/>.</summary>
    private Sample? Find(string name, (string Key, string Value)[] match)
    {
        if (!_series.TryGetValue(name, out var list))
            return null;
        foreach (var s in list)
        {
            if (Matches(s, match)) return s;
        }
        return null;
    }

    private static bool Matches(Sample s, (string Key, string Value)[] match)
    {
        foreach (var (k, v) in match)
        {
            if (!s.Labels.TryGetValue(k, out var actual) || actual != v) return false;
        }
        return true;
    }

    public double? Double(string name, params (string Key, string Value)[] match)
    {
        var s = Find(name, match);
        if (s == null) return null;
        return s.Value switch
        {
            "+Inf" or "Inf" => double.PositiveInfinity,
            "-Inf" => double.NegativeInfinity,
            "NaN" => double.NaN,
            _ => double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        };
    }

    /// <summary>
    /// The sample as a whole number: exact for integer text, truncated for a finite decimal,
    /// null when it does not fit a signed 64-bit value (never a wrapped or clamped number).
    /// </summary>
    public long? Long(string name, params (string Key, string Value)[] match)
    {
        var s = Find(name, match);
        if (s == null) return null;
        if (long.TryParse(s.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d) && d >= long.MinValue && d < long.MaxValue)
            return (long)Math.Truncate(d);
        return null;
    }

    /// <summary>
    /// The sum over every sample of <paramref name="name"/> whose labels include all of
    /// <paramref name="match"/>. Null when there is none, or when one does not read as a whole
    /// number or the sum leaves 64 bits: a partial sum would look like a counter that went back.
    /// </summary>
    public long? SumLong(string name, params (string Key, string Value)[] match)
    {
        if (!_series.TryGetValue(name, out var list))
            return null;
        long sum = 0;
        var any = false;
        foreach (var s in list)
        {
            if (!Matches(s, match)) continue;
            if (!long.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                return null;
            try { sum = checked(sum + v); }
            catch (OverflowException) { return null; }
            any = true;
        }
        return any ? sum : null;
    }

    /// <summary>
    /// The sum of <paramref name="name"/> over every sample whose <paramref name="label"/> is
    /// not <paramref name="except"/>. Null when there is none or one is not a finite number.
    /// </summary>
    public double? SumDoubleExcept(string name, string label, string except)
    {
        if (!_series.TryGetValue(name, out var list))
            return null;
        double sum = 0;
        var any = false;
        foreach (var s in list)
        {
            if (!s.Labels.TryGetValue(label, out var value) || value == except) continue;
            if (!double.TryParse(s.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v))
                return null;
            sum += v;
            any = true;
        }
        return any ? sum : null;
    }

    /// <summary>The largest whole-number sample of <paramref name="name"/>; null when there is none.</summary>
    public long? MaxLong(string name)
    {
        if (!_series.TryGetValue(name, out var list))
            return null;
        long? max = null;
        foreach (var s in list)
        {
            if (long.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && (max is null || v > max))
                max = v;
        }
        return max;
    }

    /// <summary>
    /// For every sample of <paramref name="name"/> whose value is <paramref name="value"/>, what
    /// <paramref name="describe"/> makes of its labels (a missing label reads as empty).
    /// </summary>
    public List<string> LabelsWhere(string name, string value, Func<Func<string, string>, string> describe)
    {
        var result = new List<string>();
        if (!_series.TryGetValue(name, out var list))
            return result;
        foreach (var s in list)
        {
            if (s.Value == value)
                result.Add(describe(key => s.Labels.TryGetValue(key, out var v) ? v : ""));
        }
        return result;
    }

    /// <summary>A label's value on the first sample of an info-style series.</summary>
    public string? Label(string name, string label, params (string Key, string Value)[] match)
    {
        var s = Find(name, match);
        return s != null && s.Labels.TryGetValue(label, out var v) ? v : null;
    }

    private static bool TryParseLine(string line, out string name, out IReadOnlyDictionary<string, string> labels, out string value)
    {
        name = "";
        value = "";
        labels = EmptyLabels;

        var i = 0;
        while (i < line.Length && IsNameChar(line[i], i == 0)) i++;
        if (i == 0) return false;
        name = line[..i];

        if (i < line.Length && line[i] == '{')
        {
            if (!TryParseLabels(line, ref i, out var parsed)) return false;
            labels = parsed;
        }

        // At least one space or tab between the series and its value.
        var start = i;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i == start || i >= line.Length) return false;

        var end = i;
        while (end < line.Length && line[end] != ' ' && line[end] != '\t') end++;
        value = line[i..end];
        return value.Length > 0;
    }

    private static bool TryParseLabels(string line, ref int i, out Dictionary<string, string> labels)
    {
        labels = new Dictionary<string, string>(StringComparer.Ordinal);
        i++; // '{'
        while (true)
        {
            while (i < line.Length && (line[i] == ' ' || line[i] == ',')) i++;
            if (i >= line.Length) return false;
            if (line[i] == '}') { i++; return true; }

            var keyStart = i;
            while (i < line.Length && IsNameChar(line[i], i == keyStart)) i++;
            if (i == keyStart || i >= line.Length) return false;
            var key = line[keyStart..i];

            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length || line[i] != '=') return false;
            i++;
            while (i < line.Length && line[i] == ' ') i++;
            if (i >= line.Length || line[i] != '"') return false;
            i++;

            var sb = new System.Text.StringBuilder();
            var closed = false;
            while (i < line.Length)
            {
                var c = line[i++];
                if (c == '"') { closed = true; break; }
                if (c == '\\' && i < line.Length)
                {
                    var e = line[i++];
                    sb.Append(e switch { 'n' => '\n', _ => e });
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (!closed) return false;
            labels[key] = sb.ToString();
        }
    }

    private static bool IsNameChar(char c, bool first) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or ':' || (!first && c is >= '0' and <= '9');

    private static readonly IReadOnlyDictionary<string, string> EmptyLabels = new Dictionary<string, string>();
}
