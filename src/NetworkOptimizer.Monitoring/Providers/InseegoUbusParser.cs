using System.Globalization;
using System.Text.Json;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>
/// Outcome of one call inside an Inseego ubus JSON-RPC batch.
/// </summary>
public enum UbusCallStatus
{
    /// <summary>The call succeeded and carries a result object.</summary>
    Ok,
    /// <summary>The session token is unknown, expired, or lacks access. Log in again.</summary>
    AccessDenied,
    /// <summary>The call failed for another reason, or its response is missing.</summary>
    Failed,
}

/// <summary>
/// Request builder and pure-function translator for the ubus JSON-RPC interface on Inseego
/// FX-series 5G gateways (FX4100 and similar), at <c>POST /ubus</c>. Kept separate from
/// <c>InseegoFxProvider</c> so it can be unit-tested without an HTTP transport.
/// </summary>
/// <remarks>
/// Protocol notes (verified against an FX4100 browser trace):
/// <list type="bullet">
///   <item>Each call is <c>{"method":"call","params":[session, object, method, args]}</c>; the web UI batches several in one JSON array.</item>
///   <item>A result is <c>[code, {...}]</c>; code 0 is success. rpcd reports an invalid session as ubus code 6 or JSON-RPC error -32002.</item>
///   <item>Before login the session is 32 zeros. <c>webui.login.authenticate</c> returns <c>session_token</c>; no cookies are involved.</item>
///   <item>Signal fields the firmware does not report arrive as 0 (<c>rssi</c>, <c>sinr</c>, <c>tx_power</c>).</item>
///   <item>Some methods (<c>sysinterface.wan.get_telemetry_wan_info</c>) return invalid JSON, which breaks the whole batch. Never add them to a poll.</item>
/// </list>
/// </remarks>
[VendorSpecific("Inseego", "ubus JSON-RPC field names and result shapes from the FX4100 web UI")]
public static class InseegoUbusParser
{
    /// <summary>The session ID ubus expects on calls made before login.</summary>
    public const string AnonymousSession = "00000000000000000000000000000000";

    /// <summary>ubus <c>UBUS_STATUS_PERMISSION_DENIED</c>.</summary>
    private const int UbusPermissionDenied = 6;

    /// <summary>rpcd's JSON-RPC error for an unknown or expired session.</summary>
    private const int RpcAccessDenied = -32002;

    /// <summary>Largest 28-bit LTE E-UTRAN cell identity. An NR cell identity is 36 bits.</summary>
    private const long MaxLteCellIdentity = 0x0FFFFFFF;

    /// <summary>One ubus call in a poll batch.</summary>
    public sealed record UbusCall(string Object, string Method);

    /// <summary>
    /// The calls one poll makes, in request order. Request IDs are the 1-based index into
    /// this list. Only read-only status calls that return valid JSON belong here.
    /// </summary>
    public static readonly IReadOnlyList<UbusCall> PollCalls = new[]
    {
        new UbusCall("sysinterface.modem", "get_cellular_service_stats"),
        new UbusCall("sysinterface.modem", "get_cellular_service_status"),
        new UbusCall("sysinterface.modem", "get_device_model_name"),
        new UbusCall("sysinterface.modem", "get_hardware_info"),
        new UbusCall("sysinterface.modem", "get_system_version"),
    };

    /// <summary>
    /// Build the JSON-RPC batch body for <paramref name="calls"/>, each with empty arguments.
    /// </summary>
    public static string BuildBatchRequest(string session, IReadOnlyList<UbusCall> calls)
    {
        var batch = calls.Select((call, i) => new
        {
            jsonrpc = "2.0",
            id = i + 1,
            method = "call",
            @params = new object[] { session, call.Object, call.Method, new { } },
        });
        return JsonSerializer.Serialize(batch);
    }

    /// <summary>
    /// Build the single-call JSON-RPC body for <c>webui.login.authenticate</c>.
    /// </summary>
    public static string BuildLoginRequest(string password) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "call",
            @params = new object[] { AnonymousSession, "webui.login", "authenticate", new { password } },
        });

    /// <summary>
    /// Read the session token from an <c>authenticate</c> response. Returns null when the
    /// password was rejected or the response has no token.
    /// </summary>
    public static string? ParseLoginToken(JsonElement response)
    {
        var entry = response.ValueKind == JsonValueKind.Array && response.GetArrayLength() > 0
            ? response[0]
            : response;
        if (ReadResult(entry, out var result) != UbusCallStatus.Ok)
            return null;

        if (TryGetInt(result, "authenticated") != 1)
            return null;

        return result.TryGetProperty("session_token", out var token) && token.ValueKind == JsonValueKind.String
            ? token.GetString() is { Length: > 0 } s ? s : null
            : null;
    }

    /// <summary>
    /// Split a batch response into one result object per request ID. IDs whose call failed
    /// are absent. <paramref name="accessDenied"/> is true when any call was refused for
    /// its session, which means the token must be renewed.
    /// </summary>
    public static Dictionary<int, JsonElement> ParseBatchResponse(JsonElement response, out bool accessDenied)
    {
        accessDenied = false;
        var results = new Dictionary<int, JsonElement>();
        if (response.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var entry in response.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("id", out var idProp) ||
                !idProp.TryGetInt32(out var id))
                continue;

            switch (ReadResult(entry, out var result))
            {
                case UbusCallStatus.Ok:
                    results[id] = result.Clone();
                    break;
                case UbusCallStatus.AccessDenied:
                    accessDenied = true;
                    break;
            }
        }

        return results;
    }

    /// <summary>
    /// Translate the results of <see cref="PollCalls"/> (keyed by request ID) into stats.
    /// Returns null when the modem returned no signal data at all.
    /// </summary>
    public static CellularModemStats? Parse(IReadOnlyDictionary<int, JsonElement> results, ModemPollContext context)
    {
        var serviceStats = Get(results, "get_cellular_service_stats");
        if (serviceStats == null)
            return null;

        var svc = serviceStats.Value;
        var modelName = Get(results, "get_device_model_name");
        var hardware = Get(results, "get_hardware_info");
        var version = Get(results, "get_system_version");
        var serviceStatus = Get(results, "get_cellular_service_status");

        var stats = new CellularModemStats
        {
            Timestamp = DateTime.UtcNow,
            ModemHost = context.ConfiguredHost ?? context.Host,
            ModemName = context.Name,
            // get_device_model_name carries the marketed name; get_hardware_info can report a
            // sibling SKU (FX4120 on an FX4100).
            ModemModel = TryGetString(modelName, "model")
                         ?? TryGetString(hardware, "model")
                         ?? context.ModemType,
            SoftwareVersion = TryGetString(version, "mifios_version"),
            Carrier = TryGetString(svc, "oper_name") ?? "",
            IsRoaming = TryGetInt(svc, "roam") is > 0,
        };

        var operId = TryGetString(svc, "oper_id");
        if (operId is { Length: 5 or 6 } && operId.All(char.IsAsciiDigit))
        {
            stats.CarrierMcc = operId[..3];
            stats.CarrierMnc = operId[3..];
        }

        // 2 is in service on every trace so far; anything else is shown raw rather than guessed.
        var status = TryGetInt(serviceStatus, "status");
        stats.RegistrationState = status switch
        {
            2 => "registered",
            int s => $"state-{s}",
            null => "",
        };

        var signal = BuildSignalInfo(svc);
        var cellId = TryGetLong(svc, "cell_id");

        // The firmware reports one serving cell and a technology code with no published mapping.
        // The cell identity width is standard: 28 bits for LTE, 36 for NR, so a larger ID is an
        // NR cell. An NSA session therefore reads as LTE: the anchor is the cell it reports.
        var isNr = cellId > MaxLteCellIdentity;
        if (isNr)
            stats.Nr5g = signal;
        else
            stats.Lte = signal;

        var pci = TryGetInt(svc, "pci");
        if (signal != null || pci.HasValue || cellId.HasValue)
        {
            stats.ServingCell = new CellInfo
            {
                IsServing = true,
                PhysicalCellId = pci ?? 0,
                GlobalCellId = cellId is > 0 ? cellId.Value.ToString(CultureInfo.InvariantCulture) : null,
                Plmn = string.IsNullOrEmpty(stats.CarrierMcc) ? null : operId,
                Signal = signal,
            };
        }

        return stats;
    }

    /// <summary>
    /// Build signal info from a <c>get_cellular_service_stats</c> result. A zero is how the
    /// firmware reports a metric it does not measure, so zeros become null. SNR is the
    /// exception: 0 dB is a real reading once RSRP is present.
    /// </summary>
    private static SignalInfo? BuildSignalInfo(JsonElement svc)
    {
        var rsrp = NonZero(TryGetDouble(svc, "rsrp"));
        if (!rsrp.HasValue)
            return null;

        return new SignalInfo
        {
            Rsrp = rsrp,
            Rsrq = NonZero(TryGetDouble(svc, "rsrq")),
            Rssi = NonZero(TryGetDouble(svc, "rssi")),
            Snr = TryGetDouble(svc, "snr") ?? NonZero(TryGetDouble(svc, "sinr")),
        };
    }

    private static UbusCallStatus ReadResult(JsonElement entry, out JsonElement result)
    {
        result = default;
        if (entry.ValueKind != JsonValueKind.Object)
            return UbusCallStatus.Failed;

        if (entry.TryGetProperty("error", out var error))
        {
            return TryGetInt(error, "code") == RpcAccessDenied
                ? UbusCallStatus.AccessDenied
                : UbusCallStatus.Failed;
        }

        if (!entry.TryGetProperty("result", out var arr) ||
            arr.ValueKind != JsonValueKind.Array ||
            arr.GetArrayLength() == 0 ||
            !arr[0].TryGetInt32(out var code))
            return UbusCallStatus.Failed;

        if (code == UbusPermissionDenied)
            return UbusCallStatus.AccessDenied;
        if (code != 0 || arr.GetArrayLength() < 2 || arr[1].ValueKind != JsonValueKind.Object)
            return UbusCallStatus.Failed;

        result = arr[1];
        return UbusCallStatus.Ok;
    }

    private static JsonElement? Get(IReadOnlyDictionary<int, JsonElement> results, string method)
    {
        for (var i = 0; i < PollCalls.Count; i++)
        {
            if (PollCalls[i].Method == method)
                return results.TryGetValue(i + 1, out var r) ? r : null;
        }
        return null;
    }

    private static double? NonZero(double? value) => value is 0 ? null : value;

    private static string? TryGetString(JsonElement? source, string key)
    {
        if (source is not { ValueKind: JsonValueKind.Object } obj ||
            !obj.TryGetProperty(key, out var prop) ||
            prop.ValueKind != JsonValueKind.String)
            return null;
        var s = prop.GetString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int? TryGetInt(JsonElement? source, string key)
    {
        var l = TryGetLong(source, key);
        return l is >= int.MinValue and <= int.MaxValue ? (int)l.Value : null;
    }

    private static long? TryGetLong(JsonElement? source, string key)
    {
        if (source is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(key, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var n))
            return n;
        if (prop.ValueKind == JsonValueKind.String &&
            long.TryParse(prop.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }

    private static double? TryGetDouble(JsonElement? source, string key)
    {
        if (source is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(key, out var prop))
            return null;
        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var d))
            return d;
        if (prop.ValueKind == JsonValueKind.String &&
            double.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return s;
        return null;
    }
}
