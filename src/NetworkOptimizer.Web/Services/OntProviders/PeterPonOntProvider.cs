using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for GPON SFP sticks running Peter-Pon (OpenWrt-based firmware, first seen on the
/// Huawei MA5671A). The stick's uhttpd serves OpenWrt's ubus JSON-RPC at <c>/ubus/</c>: a poll signs
/// in with <c>session.login</c>, then reads <c>luci.gpon status</c> (optics, PLOAM state,
/// temperatures) and <c>luci.gpon info</c> (GPON SN, firmware, OLT vendor) in one batch, and
/// <c>luci.gpon live_get</c> best effort for laser bias, supply voltage, the stick's uptime, and the
/// per-second GTC error rates, which it sums into BIP, FEC, and HEC counters.
///
/// The stick sits in a UniFi device's SFP slot, so it can also be attached to that monitored module
/// (ISfpSupplementalOntProvider): its PLOAM state, firmware, and OLT vendor merge into the module's
/// sfp series, and its optics only fill gaps the device's DDM read leaves.
/// </summary>
[VendorSpecific("Peter-Pon", "OpenWrt ubus JSON-RPC at /ubus/: luci.gpon status/info objects; live_get metric arrays inside a JSON string in docs")]
public sealed class PeterPonOntProvider : ISfpSupplementalOntProvider
{
    public string ProviderKey => "peter-pon";
    public string DisplayName => "Peter-Pon GPON Firmware (HTTP)";

    internal const string DefaultModel = "Peter-Pon GPON Stick";
    private const string DefaultUsername = "root";
    private const string NullSession = "00000000000000000000000000000000";
    private const int TimeoutSeconds = 10;

    /// <summary>JSON-RPC error uhttpd returns for a missing or expired ubus session.</summary>
    private const int AccessDeniedRpcError = -32002;

    /// <summary>ubus UBUS_STATUS_PERMISSION_DENIED, as the first element of a call's result.</summary>
    private const int UbusPermissionDenied = 6;

    private readonly ILogger<PeterPonOntProvider> _logger;

    /// <summary>
    /// ubus session per <see cref="OntPollContext.CacheKey"/>, with a hash of the credentials that
    /// opened it. rpcd expires a session after 300 s idle; an expired one is answered with Access
    /// denied and replaced by a new sign-in.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Token, int Credentials)> _sessions = new();

    /// <summary>
    /// The last live_get <c>seq</c> per <see cref="OntPollContext.CacheKey"/>. live_get returns every
    /// 1 s sample after <c>since</c>, so passing the previous reply's seq keeps a reply to the samples
    /// taken since the last poll instead of the whole ring.
    /// </summary>
    private readonly ConcurrentDictionary<string, long> _liveSeq = new();

    /// <summary>
    /// Running error counters per <see cref="OntPollContext.CacheKey"/>. The firmware serves only
    /// per-second rates, so these sum every sample once and restart at 0 with this process or the
    /// stick; consumers already read a counter that goes down as a reset.
    /// </summary>
    private readonly ConcurrentDictionary<string, ErrorTotals> _errorTotals = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public PeterPonOntProvider(ILogger<PeterPonOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return PollResult<OntStats>.Failed("No address is configured for this device.");

        try
        {
            using var client = CreateHttpClient(context);
            var url = BuildUrl(context);

            var session = await GetSessionAsync(client, url, context, cancellationToken);
            if (session.Failure != null)
                return PollResult<OntStats>.Failed(session.Failure);

            var batch = await CallStatusAndInfoAsync(client, url, session.Token!, cancellationToken);
            if (batch.AccessDenied)
            {
                // Expired between polls: sign in once more and retry.
                _sessions.TryRemove(context.CacheKey, out _);
                session = await GetSessionAsync(client, url, context, cancellationToken);
                if (session.Failure != null)
                    return PollResult<OntStats>.Failed(session.Failure);
                batch = await CallStatusAndInfoAsync(client, url, session.Token!, cancellationToken);
            }

            if (batch.AccessDenied)
                return PollResult<OntStats>.Failed(
                    $"{context.Host} accepted the sign-in but denied access to luci.gpon. Sign in as root.");

            if (batch.Status is not { } status)
                return PollResult<OntStats>.Failed(
                    $"{context.Host} answered, but not with Peter-Pon's luci.gpon status. Check that the stick runs Peter-Pon firmware.");

            var stats = MapToOntStats(status, batch.Info, context);

            // live_get adds bias, voltage, uptime, and the error counters. A firmware without it must not fail the poll.
            try
            {
                var since = context.Id > 0 && _liveSeq.TryGetValue(context.CacheKey, out var seq) ? seq : 0;
                var sample = await CallLiveAsync(client, url, session.Token!, since, cancellationToken);
                if (sample != null)
                {
                    ApplyLive(sample, stats);
                    // An unsaved configuration, and the Test button, have no stable key to keep totals under.
                    if (context.Id > 0)
                        ApplyErrorTotals(_errorTotals.GetOrAdd(context.CacheKey, _ => new ErrorTotals()), sample, stats);
                }

                // The stick restarts seq at boot, so a reply with no seq (or no read) starts over from 0.
                if (context.Id > 0 && sample?.Seq is { } next)
                    _liveSeq[context.CacheKey] = next;
                else
                    _liveSeq.TryRemove(context.CacheKey, out _);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _liveSeq.TryRemove(context.CacheKey, out _);
                _logger.LogDebug(ex, "Peter-Pon ONT {Name}: live_get not read", context.Name);
            }

            _logger.LogDebug("Peter-Pon ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Temp={Temp} C, PLOAM={State}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-", stats.TxPowerDbm?.ToString("F2") ?? "-",
                stats.TemperatureC?.ToString("F1") ?? "-", stats.PonLinkStatus);

            return PollResult<OntStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error polling Peter-Pon ONT {Name} at {Host}", context.Name, context.Host);
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
        // Id 0: the Test button probes the edited, unsaved form, which may point at another stick, so it
        // must not advance the saved configuration's session, since, or error totals.
        var result = await PollAsync(context with { Id = 0 }, cancellationToken);
        if (result.Stats is not { } stats)
            return (false, result.FailureReason ?? $"No stats could be read from {context.Host}.");

        var detail = stats.PonLinkStatus != PonLinkState.Unknown ? stats.PonLinkStatus.ToDisplayString() : "PLOAM state unknown";
        if (stats.RxPowerDbm is { } rx)
            detail += $", RX {rx:F2} dBm";
        return (true, $"Connected - {detail}");
    }

    // --- Transport ---

    private static HttpClient CreateHttpClient(OntPollContext context)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = DeviceHttp.Via(context.Dialer),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
    }

    private static string BuildUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var scheme = port == 443 ? "https" : "http";
        var portSuffix = port is 80 or 443 ? "" : $":{port}";
        // The trailing slash matches LuCI's own ubus path.
        return $"{scheme}://{context.Host}{portSuffix}/ubus/";
    }

    private sealed record SessionResult(string? Token, string? Failure);

    private async Task<SessionResult> GetSessionAsync(
        HttpClient client, string url, OntPollContext context, CancellationToken ct)
    {
        var username = string.IsNullOrWhiteSpace(context.Username) ? DefaultUsername : context.Username.Trim();
        var password = context.Password ?? "";
        var credentials = HashCode.Combine(username, password);

        // An unsaved configuration, and the Test button, have no stable key, so they never reuse a session.
        if (context.Id > 0 && _sessions.TryGetValue(context.CacheKey, out var cached) && cached.Credentials == credentials)
            return new(cached.Token, null);

        var request = Call(1, NullSession, "session", "login", new JsonObject
        {
            ["username"] = username,
            ["password"] = password,
        });
        using var response = await PostAsync(client, url, request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);

        var token = ParseLoginToken(body);
        if (token == null)
            return new(null, $"{context.Host} rejected the sign-in. Check the username and password.");

        if (context.Id > 0)
            _sessions[context.CacheKey] = (token, credentials);
        return new(token, null);
    }

    internal sealed record BatchResult(PeterPonStatus? Status, PeterPonInfo? Info, bool AccessDenied);

    private static async Task<BatchResult> CallStatusAndInfoAsync(
        HttpClient client, string url, string token, CancellationToken ct)
    {
        var request = new JsonArray
        {
            Call(10, token, "luci.gpon", "status", new JsonObject()),
            Call(11, token, "luci.gpon", "info", new JsonObject()),
        };
        using var response = await PostAsync(client, url, request, ct);
        response.EnsureSuccessStatusCode();
        return ParseStatusAndInfo(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<LiveSample?> CallLiveAsync(
        HttpClient client, string url, string token, long since, CancellationToken ct)
    {
        var request = Call(12, token, "luci.gpon", "live_get", new JsonObject
        {
            ["since"] = since,
            ["slow"] = false,
            ["slow_since"] = 0,
        });
        using var response = await PostAsync(client, url, request, ct);
        response.EnsureSuccessStatusCode();
        return ParseLive(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Posts with a Content-Length: a chunked JSON body is not something to count on from uhttpd.</summary>
    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, JsonNode request, CancellationToken ct) =>
        client.PostAsync(url, new StringContent(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct);

    private static JsonObject Call(int id, string session, string obj, string method, JsonObject args) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "call",
        ["params"] = new JsonArray(session, obj, method, args),
    };

    // --- Parsing (stateless, driven by tests) ---

    /// <summary>
    /// The <c>ubus_rpc_session</c> from a <c>session.login</c> response, or null when the sign-in
    /// was refused (a non-zero ubus status, or a JSON-RPC error).
    /// </summary>
    internal static string? ParseLoginToken(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetCallData(doc.RootElement, out var data, out _))
            return null;
        return data.TryGetProperty("ubus_rpc_session", out var t) && t.ValueKind == JsonValueKind.String
            && t.GetString() is { Length: > 0 } token
            ? token
            : null;
    }

    /// <summary>
    /// Reads the status (id 10) and info (id 11) replies from a batch response, matched by id since
    /// JSON-RPC does not promise the order. AccessDenied is set when either call was refused for its
    /// session.
    /// </summary>
    internal static BatchResult ParseStatusAndInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var replies = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        PeterPonStatus? status = null;
        PeterPonInfo? info = null;
        var denied = false;

        foreach (var reply in replies)
        {
            var id = reply.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var n) ? n : 0;
            if (!TryGetCallData(reply, out var data, out var accessDenied))
            {
                denied |= accessDenied;
                continue;
            }
            if (id == 10)
                status = data.Deserialize<PeterPonStatus>(JsonOptions);
            else if (id == 11)
                info = data.Deserialize<PeterPonInfo>(JsonOptions);
        }

        return new BatchResult(status, info, denied);
    }

    /// <summary>
    /// The newest live_get values this provider records, plus the reply's seq for the next since.
    /// Times holds each sample's time (ms since the stick booted), and Rates the error-rate arrays
    /// aligned with it, one count per 1 s sample.
    /// </summary>
    internal sealed record LiveSample(long? Seq, double? BiasMa, double? VoltageV, long? UptimeS)
    {
        public IReadOnlyList<long> Times { get; init; } = [];
        public IReadOnlyDictionary<string, IReadOnlyList<long?>> Rates { get; init; } =
            new Dictionary<string, IReadOnlyList<long?>>();
    }

    /// <summary>live_get's per-second GTC error rates, each summed into the counter of the same meaning.</summary>
    internal const string BipRate = "gtc.bip_ps";
    internal const string FecUncorrectedRate = "gtc.fec_uncorr_ps";
    internal const string FecCorrectedRate = "gtc.fec_corr_ps";
    internal const string HecCorrectedRate = "gtc.hec_corr_ps";
    internal const string HecUncorrectedRate = "gtc.hec_uncorr_ps";

    private static readonly string[] ErrorRates =
        [BipRate, FecUncorrectedRate, FecCorrectedRate, HecCorrectedRate, HecUncorrectedRate];

    /// <summary>Running sums of the error rates, and the time of the newest sample already counted.</summary>
    internal sealed class ErrorTotals
    {
        public long? LastSampleMs;
        public readonly Dictionary<string, long> Sums = new();
    }

    /// <summary>
    /// Reads a <c>live_get</c> response. The reply carries the samples as a JSON string in
    /// <c>result[1].docs</c> (the document whose <c>tool</c> is "live"); each metric under
    /// <c>live.m</c> is an array, and the last element is the newest sample.
    /// </summary>
    internal static LiveSample? ParseLive(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!TryGetCallData(doc.RootElement, out var data, out _))
            return null;
        long? seq = data.TryGetProperty("seq", out var s) && s.TryGetInt64(out var sv) ? sv : null;
        if (!data.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
            return seq == null ? null : new LiveSample(seq, null, null, null);

        foreach (var entry in docs.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { Length: > 0 } inner)
                continue;
            using var live = JsonDocument.Parse(inner);
            var root = live.RootElement;
            if (!root.TryGetProperty("tool", out var tool) || tool.GetString() != "live"
                || !root.TryGetProperty("live", out var l) || !l.TryGetProperty("m", out var m))
                continue;

            var bias = Newest(m, "optic.bias_ua");
            var vcc = Newest(m, "optic.vcc_mv");
            long? uptime = root.TryGetProperty("uptime_s", out var u) && u.TryGetInt64(out var uv) ? uv : null;

            var times = new List<long>();
            if (l.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var x in t.EnumerateArray())
                    times.Add(x.TryGetInt64(out var tv) ? tv : -1);

            var rates = new Dictionary<string, IReadOnlyList<long?>>();
            foreach (var name in ErrorRates)
            {
                if (!m.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;
                rates[name] = arr.EnumerateArray().Select(x => x.TryGetInt64(out var rv) ? rv : (long?)null).ToList();
            }

            return new LiveSample(
                seq,
                bias is { } b ? Math.Round(b / 1000.0, 2) : null,
                vcc is { } v && v > 0 ? Math.Round(v / 1000.0, 2) : null,
                uptime)
            {
                Times = times,
                Rates = rates,
            };
        }

        return seq == null ? null : new LiveSample(seq, null, null, null);
    }

    /// <summary>The last element of a <c>live.m</c> metric array, or null when absent or empty.</summary>
    private static double? Newest(JsonElement metrics, string name)
    {
        if (!metrics.TryGetProperty(name, out var samples) || samples.ValueKind != JsonValueKind.Array)
            return null;
        var n = samples.GetArrayLength();
        return n > 0 && samples[n - 1].ValueKind == JsonValueKind.Number ? samples[n - 1].GetDouble() : null;
    }

    /// <summary>
    /// Adds the reply's samples newer than the last one counted into <paramref name="totals"/>, then
    /// writes the totals onto the poll. Counting by sample time, not by since, keeps a sample from being
    /// counted twice when since falls back to 0 and the reply repeats the ring. A sample time older
    /// than the last one counted means the stick rebooted, so the totals start over.
    /// </summary>
    internal static void ApplyErrorTotals(ErrorTotals totals, LiveSample sample, OntStats stats)
    {
        lock (totals)
        {
            var n = sample.Times.Count;
            if (n > 0 && sample.Rates.Count > 0)
            {
                if (totals.LastSampleMs is { } last && sample.Times[^1] >= 0 && sample.Times[^1] < last)
                {
                    totals.Sums.Clear();
                    totals.LastSampleMs = null;
                }

                var seen = totals.LastSampleMs;
                foreach (var (name, values) in sample.Rates)
                {
                    totals.Sums.TryAdd(name, 0);
                    // Align from the newest end: the metric and time arrays describe the same samples.
                    var offset = values.Count - n;
                    for (var i = 0; i < n; i++)
                    {
                        var j = i + offset;
                        if (j < 0 || sample.Times[i] < 0 || (seen is { } s && sample.Times[i] <= s))
                            continue;
                        if (values[j] is { } count && count > 0)
                            totals.Sums[name] += count;
                    }
                }

                totals.LastSampleMs = Math.Max(seen ?? long.MinValue, sample.Times.Max());
            }

            long? Sum(string name) => totals.Sums.TryGetValue(name, out var s) ? s : null;
            stats.BipErrors = Sum(BipRate);
            stats.FecErrors = Sum(FecUncorrectedRate);
            if (stats.Pon != null)
            {
                stats.Pon.BipErrors = stats.BipErrors;
                stats.Pon.FecErrors = stats.FecErrors;
                stats.Pon.FecCorrectedWords = Sum(FecCorrectedRate);
                stats.Pon.HecCorrected = Sum(HecCorrectedRate);
                stats.Pon.HecUncorrected = Sum(HecUncorrectedRate);
            }
        }
    }

    /// <summary>Copies the live values onto the poll: bias, voltage, and the stick's uptime for reboot detection.</summary>
    internal static void ApplyLive(LiveSample sample, OntStats stats)
    {
        stats.BiasMa = sample.BiasMa ?? stats.BiasMa;
        stats.VoltageV = sample.VoltageV ?? stats.VoltageV;
        if (stats.Pon != null)
        {
            stats.Pon.VoltageV = stats.VoltageV;
            stats.Pon.SfpUptimeS = sample.UptimeS ?? stats.Pon.SfpUptimeS;
        }
    }

    /// <summary>
    /// A ubus call's data object: <c>result</c> is <c>[status, data]</c>, and status 0 is success.
    /// accessDenied reports a refused session (JSON-RPC -32002, or ubus status 6).
    /// </summary>
    private static bool TryGetCallData(JsonElement reply, out JsonElement data, out bool accessDenied)
    {
        data = default;
        accessDenied = false;

        if (reply.TryGetProperty("error", out var error))
        {
            accessDenied = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var c) && c == AccessDeniedRpcError;
            return false;
        }
        if (!reply.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            return false;

        var ubusStatus = result[0].TryGetInt32(out var s) ? s : -1;
        if (ubusStatus != 0)
        {
            accessDenied = ubusStatus == UbusPermissionDenied;
            return false;
        }
        if (result.GetArrayLength() < 2 || result[1].ValueKind != JsonValueKind.Object)
            return false;

        data = result[1].Clone();
        return true;
    }

    /// <summary>
    /// Maps status and info onto OntStats. TemperatureC is the laser temperature, the optic reading
    /// SFP DDM and the other sticks report; the SoC die temperature has no field.
    /// </summary>
    internal static OntStats MapToOntStats(PeterPonStatus status, PeterPonInfo? info, OntPollContext context)
    {
        var state = status.PloamState is >= 1 and <= 7 ? (PonLinkState)status.PloamState.Value : PonLinkState.Unknown;

        var stats = new OntStats
        {
            Timestamp = DateTime.UtcNow,
            DeviceHost = context.Host,
            DeviceName = context.Name,
            DeviceModel = DefaultModel,
            PonType = "GPON",
            RxPowerDbm = ParseDbm(status.RxPower),
            TxPowerDbm = ParseDbm(status.TxPower),
            TemperatureC = KelvinToCelsius(status.LaserTempK),
            PonLinkStatus = state,
            LinkState = state != PonLinkState.Unknown ? state.ToDisplayString() : null,
            OperationalStatus = state == PonLinkState.Unknown ? null : state == PonLinkState.Operation ? "Up" : "Down",
            VendorSn = Clean(info?.GponSn),
            OltVendor = Clean(info?.OltVendor),
            OltModel = Clean(info?.OltEquipment),
        };

        var known = state != PonLinkState.Unknown;
        stats.Pon = new PonSupplementalStats
        {
            RxPowerDbm = stats.RxPowerDbm,
            TxPowerDbm = stats.TxPowerDbm,
            TemperatureC = stats.TemperatureC,
            PloamStateRaw = known ? (int)state : null,
            PonLinkStatus = known ? state.ToInfluxValue() : null,
            OltVendorId = stats.OltVendor,
            FirmwareVersion = Clean(info?.Firmware),
            OltModel = stats.OltModel,
            OltVersion = Clean(info?.OltVersion),
        };

        return stats;
    }

    private static readonly Regex DbmPattern = new(@"(-?\d+(?:\.\d+)?)\s*dBm", RegexOptions.IgnoreCase);

    /// <summary>The dBm half of a reading like "12.14uW -19.16dBm". No light ("-inf") reads null.</summary>
    internal static double? ParseDbm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = DbmPattern.Match(text);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    internal static double? KelvinToCelsius(double? kelvin) =>
        kelvin is > 0 ? Math.Round(kelvin.Value - 273.15, 1) : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary><c>luci.gpon status</c> data object.</summary>
public sealed class PeterPonStatus
{
    /// <summary>ITU-T activation state, 1-7 = O1-O7.</summary>
    [JsonPropertyName("ploam_state")]
    public int? PloamState { get; set; }

    /// <summary>Receive power, both units in one string: "12.14uW -19.16dBm".</summary>
    [JsonPropertyName("rx_power")]
    public string? RxPower { get; set; }

    /// <summary>Transmit power, both units in one string: "1.43mW 1.57dBm".</summary>
    [JsonPropertyName("tx_power")]
    public string? TxPower { get; set; }

    /// <summary>SoC die temperature in kelvin.</summary>
    [JsonPropertyName("die_temp_k")]
    public double? DieTempK { get; set; }

    /// <summary>Laser temperature in kelvin.</summary>
    [JsonPropertyName("laser_temp_k")]
    public double? LaserTempK { get; set; }
}

/// <summary><c>luci.gpon info</c> data object.</summary>
public sealed class PeterPonInfo
{
    /// <summary>The stick's GPON serial number, e.g. "HWTC1234ABCD".</summary>
    [JsonPropertyName("gpon_sn")]
    public string? GponSn { get; set; }

    /// <summary>Firmware build, e.g. "OpenWrt SNAPSHOT r0-baf6e531".</summary>
    [JsonPropertyName("firmware")]
    public string? Firmware { get; set; }

    /// <summary>OLT vendor ID, e.g. "HWTC". Decoded for display only.</summary>
    [JsonPropertyName("olt_vendor")]
    public string? OltVendor { get; set; }

    /// <summary>OLT equipment ID from OMCI OLT-G, when the OLT sends one.</summary>
    [JsonPropertyName("olt_equipment")]
    public string? OltEquipment { get; set; }

    /// <summary>OLT version from OMCI OLT-G, e.g. "10".</summary>
    [JsonPropertyName("olt_version")]
    public string? OltVersion { get; set; }
}
