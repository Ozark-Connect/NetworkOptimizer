using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.OntProviders;

/// <summary>
/// ONT provider for the Zyxel PMG3000 GPON-SFP stick (a MaxLinear/T&amp;W-based unit),
/// used as an in-gateway ONT on GPON FTTH connections. The stick speaks plain HTTP
/// with HTTP Basic auth (default admin/1234) and exposes two data-bearing CGI GETs:
///
///   GET /cgi/get_sn        -> {cur_sn:"..(ASCII)",sn:"..(ASCII)",cur_pass:"..(ASCII)"}
///   GET /cgi/get_gpon_info -> {line_status:"5",loid_status:0,up_fec:"Disable",..,
///                              temp:"67.85",voltage:"3.30",current:"28.89",
///                              tx_power:"3.01",rx_power:"-17.17"}
///
/// These responses are JavaScript object literals, not strict JSON (unquoted keys,
/// whitespace after colons, mixed value types, a terminal "(ASCII)" suffix inside
/// strings), so they are decoded by a small provider-local lenient tokenizer rather
/// than System.Text.Json.
///
/// get_gpon_info is the required telemetry endpoint; get_sn is best-effort identity
/// enrichment only, and its body is never logged because it contains cur_pass.
/// </summary>
public sealed class ZyxelGponSfpOntProvider : IOntProvider
{
    public string ProviderKey => "zyxel-gpon-sfp";
    public string DisplayName => "Zyxel GPON-SFP PMG3000 (HTTP)";

    private const int TimeoutSeconds = 10;
    private const string SnPath = "/cgi/get_sn";
    private const string GponInfoPath = "/cgi/get_gpon_info";
    private const string DefaultUsername = "admin";
    private const string DefaultPassword = "1234";

    // Keys in get_gpon_info that mark the response as a real PON status payload (rather
    // than an HTML/login page returned with HTTP 200). Presence of at least one gates
    // whether the poll is treated as valid.
    private static readonly string[] RecognizedGponKeys =
        { "line_status", "rx_power", "tx_power", "temp", "voltage", "current" };

    private readonly ILogger<ZyxelGponSfpOntProvider> _logger;

    public ZyxelGponSfpOntProvider(ILogger<ZyxelGponSfpOntProvider> logger)
    {
        _logger = logger;
    }

    public async Task<PollResult<OntStats>> PollAsync(OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
        {
            _logger.LogWarning("Zyxel GPON-SFP ONT poll requested but Host is empty (config {Id})", context.Id);
            return PollResult<OntStats>.Failed("No address is configured for this device.");
        }

        try
        {
            using var client = CreateClient(context);
            var baseUrl = BuildBaseUrl(context);

            // Required: optical/link telemetry. A transport/HTTP failure here propagates
            // to the outer catch and yields null.
            var gponBody = await client.GetStringAsync($"{baseUrl}{GponInfoPath}", cancellationToken);

            // Best-effort: serial identity. Losing this endpoint must not discard the
            // optical telemetry we already have, so its failure is swallowed. The body is
            // never logged, since get_sn carries cur_pass.
            string? snBody = null;
            try
            {
                snBody = await client.GetStringAsync($"{baseUrl}{SnPath}", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // genuine caller-requested cancellation
            }
            catch (Exception ex)
            {
                // Includes an HttpClient.Timeout (surfaces as OperationCanceledException with our
                // token NOT cancelled): get_sn is best-effort, so a slow/hung serial endpoint must
                // not discard the optical telemetry we already have.
                _logger.LogDebug(ex,
                    "Zyxel GPON-SFP ONT {Name}: serial enrichment (get_sn) failed, continuing with optical telemetry",
                    context.Name);
            }

            var stats = new OntStats
            {
                Timestamp = DateTime.UtcNow,
                DeviceHost = context.ConfiguredHost ?? context.Host,
                DeviceName = context.Name,
            };

            if (!ApplyResponses(snBody, gponBody, stats))
            {
                _logger.LogWarning(
                    "Zyxel GPON-SFP ONT {Name}: get_gpon_info did not contain recognized PON status fields",
                    context.Name);
                return PollResult<OntStats>.Failed($"No stats could be read from {(context.ConfiguredHost ?? context.Host)}.");
            }

            _logger.LogDebug(
                "Zyxel GPON-SFP ONT {Name} polled: Rx={Rx} dBm, Tx={Tx} dBm, Link={Link}, SN={Sn}",
                context.Name, stats.RxPowerDbm?.ToString("F2") ?? "-",
                stats.TxPowerDbm?.ToString("F2") ?? "-", stats.LinkState ?? "-", stats.VendorSn ?? "-");

            return PollResult<OntStats>.Ok(stats);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine caller-requested cancellation
        }
        catch (Exception ex)
        {
            // A transport failure or an HttpClient.Timeout (OperationCanceledException with our
            // token not cancelled) yields null per the IOntProvider contract, not an exception.
            _logger.LogWarning(ex, "Error polling Zyxel GPON-SFP ONT {Name} at {Host}",
                context.Name, context.ConfiguredHost ?? context.Host);
            return PollResult<OntStats>.Failed(HttpFailureSummary.Describe(ex, (context.ConfiguredHost ?? context.Host)));
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(
        OntPollContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (false, "Host is empty");

        try
        {
            using var client = CreateClient(context);
            var baseUrl = BuildBaseUrl(context);

            // Exercise the telemetry endpoint monitoring actually needs, so a success cannot
            // be reported while get_gpon_info is broken.
            using var response = await client.GetAsync($"{baseUrl}{GponInfoPath}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized =>
                        (false, "Authentication failed - check username/password (default is admin/1234)"),
                    HttpStatusCode.Forbidden =>
                        (false, "Access denied (HTTP 403)"),
                    _ => (false, $"Device returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}"),
                };
            }

            var gponBody = await response.Content.ReadAsStringAsync(cancellationToken);

            var stats = new OntStats();
            if (!ApplyResponses(null, gponBody, stats))
                return (false, "Connected but response did not contain the expected GPON status fields");

            return (true,
                $"Connected (HTTP) - RX: {stats.RxPowerDbm?.ToString("F2") ?? "?"} dBm, Link: {stats.LinkState ?? "?"}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed - surfaces as a cancellation not tied to our token.
            return (false, "Connection timed out");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller-requested cancellation
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection failed - device unreachable ({ex.Message})");
        }
        catch (Exception ex)
        {
            return (false, HttpFailureSummary.Describe(ex, context.ConfiguredHost ?? context.Host));
        }
    }

    /// <summary>
    /// Maps the two CGI responses onto <paramref name="stats"/>. get_gpon_info is required:
    /// this returns false (and leaves <paramref name="stats"/> untouched) if it does not parse
    /// or contains no recognized PON status field, so the caller can drop the poll. get_sn is
    /// optional identity enrichment and its failure never affects the mapped optical telemetry.
    /// Never throws for bad device data.
    /// </summary>
    internal static bool ApplyResponses(string? snBody, string gponBody, OntStats stats)
    {
        if (!TryParseObjectLiteral(gponBody, out var gpon) || !RecognizedGponKeys.Any(gpon.ContainsKey))
            return false;

        // Identity seeds, applied only once get_gpon_info is confirmed to be a real payload.
        stats.VendorName = "Zyxel";
        stats.DeviceModel = "Zyxel PMG3000";
        stats.PonType = "GPON";

        // line_status is the raw ONU state ordinal (device returns "5").
        if (gpon.TryGetValue("line_status", out var lineStatus))
        {
            var state = MapLineStatus(lineStatus);
            stats.PonLinkStatus = state;
            stats.LinkState = state.ToDisplayString();
            stats.OperationalStatus = state switch
            {
                PonLinkState.Operation => "Up",
                PonLinkState.Unknown => null,
                _ => "Down",
            };
        }

        stats.RxPowerDbm = ParseDouble(GetValue(gpon, "rx_power")) ?? stats.RxPowerDbm;
        stats.TxPowerDbm = ParseDouble(GetValue(gpon, "tx_power")) ?? stats.TxPowerDbm;
        stats.TemperatureC = ParseDouble(GetValue(gpon, "temp")) ?? stats.TemperatureC;
        stats.VoltageV = ParseDouble(GetValue(gpon, "voltage")) ?? stats.VoltageV;
        stats.BiasMa = ParseDouble(GetValue(gpon, "current")) ?? stats.BiasMa;

        // up_fec/down_fec are FEC enablement flags, not cumulative error counts, so they are
        // deliberately not mapped to OntStats.FecErrors.

        if (snBody is not null && TryParseObjectLiteral(snBody, out var sn))
        {
            var serial = CleanSerial(GetValue(sn, "cur_sn") ?? GetValue(sn, "sn"));
            if (!string.IsNullOrEmpty(serial))
                stats.VendorSn = serial;
        }

        return true;
    }

    /// <summary>
    /// Hand-rolled linear tokenizer for the stick's JavaScript-object-literal responses.
    /// Handles optional leading whitespace/BOM, identifier or quoted keys, single/double
    /// quoted string values with escapes, bare scalar tokens (0, -17.17, Disable, null),
    /// an optional trailing comma, a required closing brace, and rejects unexplained
    /// trailing content. Duplicate keys resolve to the last value. Returns false (with an
    /// empty dictionary) for any malformed input; it never throws. A regex is deliberately
    /// avoided: it is fragile around quoted delimiters and escapes, and the repo forbids
    /// adding a permissive-JSON dependency.
    /// </summary>
    internal static bool TryParseObjectLiteral(string body, out IReadOnlyDictionary<string, string> fields)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        fields = result;

        if (TryParseInto(body, result))
            return true;

        result.Clear(); // no partial results leak out on malformed input
        return false;
    }

    private static bool TryParseInto(string body, Dictionary<string, string> result)
    {
        if (body is null)
            return false;

        var i = 0;
        var n = body.Length;

        if (n > 0 && body[0] == '﻿')
            i = 1;

        SkipWhitespace(body, ref i);
        if (i >= n || body[i] != '{')
            return false;
        i++;

        SkipWhitespace(body, ref i);
        if (i < n && body[i] == '}')
        {
            i++;
            SkipWhitespace(body, ref i);
            return i == n;
        }

        while (true)
        {
            SkipWhitespace(body, ref i);
            if (!TryReadKey(body, ref i, out var key))
                return false;

            SkipWhitespace(body, ref i);
            if (i >= n || body[i] != ':')
                return false;
            i++;

            SkipWhitespace(body, ref i);
            if (!TryReadValue(body, ref i, out var value))
                return false;

            result[key] = value; // last write wins on duplicate keys

            SkipWhitespace(body, ref i);
            if (i >= n)
                return false; // missing closing brace

            if (body[i] == ',')
            {
                i++;
                SkipWhitespace(body, ref i);
                if (i < n && body[i] == '}') // trailing comma before close
                {
                    i++;
                    SkipWhitespace(body, ref i);
                    return i == n;
                }
                continue;
            }

            if (body[i] == '}')
            {
                i++;
                SkipWhitespace(body, ref i);
                return i == n;
            }

            return false; // unexpected character between pairs
        }
    }

    private static bool TryReadKey(string body, ref int i, out string key)
    {
        key = "";
        if (i >= body.Length)
            return false;

        var c = body[i];
        if (c is '"' or '\'')
            return TryReadQuoted(body, ref i, out key);

        var start = i;
        while (i < body.Length)
        {
            var ch = body[i];
            if (char.IsWhiteSpace(ch) || ch is ':' or ',' or '{' or '}' or '"' or '\'')
                break;
            i++;
        }

        if (i == start)
            return false;

        key = body[start..i];
        return true;
    }

    private static bool TryReadValue(string body, ref int i, out string value)
    {
        value = "";
        if (i >= body.Length)
            return false;

        var c = body[i];
        if (c is '"' or '\'')
            return TryReadQuoted(body, ref i, out value);

        var start = i;
        while (i < body.Length)
        {
            var ch = body[i];
            if (char.IsWhiteSpace(ch) || ch is ',' or '}')
                break;
            i++;
        }

        if (i == start)
            return false; // empty/missing value

        value = body[start..i];
        return true;
    }

    private static bool TryReadQuoted(string body, ref int i, out string value)
    {
        value = "";
        var quote = body[i];
        i++; // opening quote

        var sb = new StringBuilder();
        while (i < body.Length)
        {
            var ch = body[i];
            if (ch == '\\')
            {
                i++;
                if (i >= body.Length)
                    return false; // dangling escape
                sb.Append(body[i] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    'b' => '\b',
                    'f' => '\f',
                    var other => other, // includes \" \' \\ \/ and any unknown escape
                });
                i++;
                continue;
            }

            if (ch == quote)
            {
                i++; // closing quote
                value = sb.ToString();
                return true;
            }

            sb.Append(ch);
            i++;
        }

        return false; // unterminated quote
    }

    private static void SkipWhitespace(string body, ref int i)
    {
        while (i < body.Length && char.IsWhiteSpace(body[i]))
            i++;
    }

    /// <summary>
    /// Maps the device's line_status to an ITU ONU state. The stick reports a single ordinal
    /// "1".."7"; matched exactly (not via substring), so an unexpected/malformed value such as
    /// "51" or "15" resolves to Unknown rather than being misread as a healthy O5 - reporting a
    /// bad status as Up could suppress a genuine down alert.
    /// </summary>
    internal static PonLinkState MapLineStatus(string? raw) => raw?.Trim() switch
    {
        "1" => PonLinkState.Initial,
        "2" => PonLinkState.Standby,
        "3" => PonLinkState.SerialNumber,
        "4" => PonLinkState.Ranging,
        "5" => PonLinkState.Operation,
        "6" => PonLinkState.Popup,
        "7" => PonLinkState.EmergencyStop,
        _ => PonLinkState.Unknown,
    };

    /// <summary>
    /// Trims the value, strips a terminal "(ASCII)" suffix (only when terminal), and returns
    /// null if nothing meaningful remains. Applied to serial fields only.
    /// </summary>
    private static string? CleanSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = raw.Trim();
        const string suffix = "(ASCII)";
        if (s.EndsWith(suffix, StringComparison.Ordinal))
            s = s[..^suffix.Length].Trim();

        return s.Length == 0 ? null : s;
    }

    private static string? GetValue(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var v) ? v : null;

    private static double? ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && double.IsFinite(val)
            ? val
            : null;

    /// <summary>
    /// Builds an HttpClient with preemptive HTTP Basic auth from the context credentials
    /// (defaulting to admin/1234 when blank) and a 10-second per-request timeout. No internal
    /// retry: the polling schedule is the retry mechanism and the stick is resource-constrained.
    /// </summary>
    internal static HttpClient CreateClient(OntPollContext context)
    {
        var user = string.IsNullOrWhiteSpace(context.Username) ? DefaultUsername : context.Username;
        var pass = string.IsNullOrWhiteSpace(context.Password) ? DefaultPassword : context.Password;
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));

        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        return client;
    }

    private static string BuildBaseUrl(OntPollContext context)
    {
        var port = context.Port > 0 ? context.Port : 80;
        var portSuffix = port == 80 ? "" : $":{port}";
        return $"http://{context.Host}{portSuffix}";
    }
}
