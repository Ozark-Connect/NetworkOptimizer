using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>Raw access to one site's UniFi Console, so the engine can be tested against a fake.</summary>
public interface IUniFiConfigApi
{
    /// <summary>The console is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Sends one request (see <see cref="UniFiApiClient.SendRawAsync"/>).</summary>
    Task<UniFiRawResponse> SendAsync(UniFiApiScope scope, HttpMethod method, string path, string? json = null, CancellationToken cancellationToken = default);

    /// <summary>Downloads raw bytes.</summary>
    Task<(int Status, byte[] Bytes)> GetBytesAsync(UniFiApiScope scope, string path, CancellationToken cancellationToken = default);
}

/// <summary>Reads every collection in the catalog into a <see cref="ConfigState"/>.</summary>
[VendorSpecific("UniFi", "v1 data envelope, v2 arrays and dictionaries; stat/device and rest/user config extraction")]
public sealed class ConfigSnapshotReader
{
    private readonly ILogger _logger;

    public ConfigSnapshotReader(ILogger logger) => _logger = logger;

    /// <summary>Reads the whole config. A collection that fails is recorded in <see cref="ConfigState.ReadErrors"/>.</summary>
    public async Task<ConfigState> ReadAsync(IUniFiConfigApi api, CancellationToken cancellationToken = default)
    {
        var state = new ConfigState { ReadAt = DateTime.UtcNow };
        state.NetworkAppVersion = await ReadNetworkAppVersionAsync(api, cancellationToken);

        foreach (var def in ConfigCollections.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (objects, error) = await ReadCollectionAsync(api, def, cancellationToken);
            if (error != null)
            {
                state.ReadErrors[def.Key] = error;
                continue;
            }
            state.Collections[def.Key] = objects!;
        }
        return state;
    }

    /// <summary>The Network app version from stat/sysinfo.</summary>
    public static async Task<string?> ReadNetworkAppVersionAsync(IUniFiConfigApi api, CancellationToken cancellationToken)
    {
        var response = await api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/sysinfo", null, cancellationToken);
        if (!response.IsSuccess) return null;
        var data = ExtractList(response.Body);
        return data?.FirstOrDefault()?["version"]?.ToString();
    }

    /// <summary>Reads one collection. Returns the error text instead of throwing.</summary>
    public async Task<(List<JsonObject>? Objects, string? Error)> ReadCollectionAsync(
        IUniFiConfigApi api, ConfigCollectionDef def, CancellationToken cancellationToken)
    {
        UniFiRawResponse response;
        try
        {
            response = await api.SendAsync(def.Scope, HttpMethod.Get, def.ReadPath, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Change Control read of {Collection} threw", def.Key);
            return (null, ex.Message);
        }

        // A collection the app does not have (404/400) is reported, not fatal: older Network apps
        // lack some v2 routes, and the empty list keeps the diff from reading it as everything deleted.
        if (response.Status is 404 or 400 or 405)
            return (null, $"Not available on this Network app ({response.Status})");
        if (!response.IsSuccess)
            return (null, $"HTTP {response.Status}{(response.ErrorCode is { } code ? " " + code : "")}");

        List<JsonObject>? objects;
        try
        {
            objects = def.Shape == CollectionShape.Single
                ? ExtractSingle(response.Body)
                : ExtractList(response.Body);
        }
        catch (JsonException ex)
        {
            return (null, "Unreadable response: " + ex.Message);
        }
        if (objects == null)
            return (null, "Unexpected response shape");

        return (objects.Select(o => Shape(def, o)).ToList(), null);
    }

    /// <summary>The list in a v1 envelope (<c>data</c>) or a bare v2 array.</summary>
    internal static List<JsonObject>? ExtractList(string body)
    {
        var node = JsonNode.Parse(body);
        if (node is JsonObject obj)
        {
            if (obj["meta"] is JsonObject meta && meta["rc"]?.ToString() is { } rc && rc != "ok")
                return null;
            node = obj["data"];
        }
        return node is JsonArray arr ? arr.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() : null;
    }

    private static List<JsonObject>? ExtractSingle(string body)
    {
        var node = JsonNode.Parse(body);
        if (node is JsonObject obj && obj["data"] is JsonArray data)
            node = data.FirstOrDefault();
        if (node is not JsonObject single) return null;
        var copy = (JsonObject)single.DeepClone();
        copy["_id"] = ConfigState.SingleId;
        return [copy];
    }

    /// <summary>Applies the collection's keep list and builds the device capability context.</summary>
    internal static JsonObject Shape(ConfigCollectionDef def, JsonObject raw)
    {
        // NO-owned objects keep their shell only: the members are generated, change constantly, and
        // the owning feature re-provisions them on restore.
        if (ConfigCollections.OwnedBy(def, raw) != null)
            foreach (var field in ConfigCollections.NoOwnedContentFields)
                raw.Remove(field);

        if (def.KeepFields == null) return raw;

        var kept = new JsonObject();
        foreach (var (key, value) in raw)
        {
            if (!def.KeepFields.Contains(key) || key == ConfigCollections.DeviceMetaField) continue;
            if (key.StartsWith("x_", StringComparison.Ordinal)) continue; // device auth keys are per device, never restored
            kept[key] = value?.DeepClone();
        }

        if (def.Key == "device")
        {
            if (kept["radio_table"] is JsonArray radios)
                kept["radio_table"] = new JsonArray(radios.OfType<JsonObject>().Select(r => (JsonNode?)Pick(r, ConfigCollections.RadioConfigFields)).ToArray());
            kept[ConfigCollections.DeviceMetaField] = BuildDeviceMeta(raw);
        }
        return kept;
    }

    private static JsonObject Pick(JsonObject source, IReadOnlySet<string> fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in source)
            if (fields.Contains(k)) o[k] = v?.DeepClone();
        return o;
    }

    private static readonly HashSet<string> MetaTop = new(StringComparer.Ordinal)
    {
        "model", "shortname", "type", "version", "state", "supported_afc_regions", "total_max_power", "total_used_power",
        "switch_caps", "gw_caps", "ip", "connection_network_id", "connection_network_name", "is_access_point",
    };

    private static readonly HashSet<string> MetaPort = new(StringComparer.Ordinal)
    {
        "port_idx", "name", "media", "speed", "speed_caps", "poe_caps", "poe_enable", "poe_power", "port_poe", "up",
        "is_uplink", "sfp_found", "aggregated_by", "ifname", "network_name",
    };

    private static readonly HashSet<string> MetaRadio = new(StringComparer.Ordinal)
    {
        "radio", "name", "has_dfs", "has_ht160", "has_eht240", "has_eht320", "has_fccdfs", "is_11ac", "is_11ax", "is_11be",
        "min_txpower", "max_txpower", "nss", "ap_pwr_type", "afc_done", "builtin_antenna", "builtin_ant_gain",
    };

    private static readonly HashSet<string> MetaUplink = new(StringComparer.Ordinal)
    {
        "uplink_mac", "uplink_remote_port", "port_idx", "type", "media", "speed", "uplink_device_name",
    };

    private static readonly HashSet<string> MetaLldp = new(StringComparer.Ordinal)
    {
        "local_port_idx", "chassis_id", "port_id", "is_wired", "power_allocated",
    };

    /// <summary>
    /// The capability and topology context Replace Device needs about a device that may be offline
    /// later: ports with media, speed and PoE, radio capabilities, AFC regions, power budget, uplink.
    /// </summary>
    internal static JsonObject BuildDeviceMeta(JsonObject raw)
    {
        var meta = Pick(raw, MetaTop);
        if (raw["port_table"] is JsonArray ports)
            meta["ports"] = new JsonArray(ports.OfType<JsonObject>().Select(p => (JsonNode?)Pick(p, MetaPort)).ToArray());
        if (raw["radio_table"] is JsonArray radios)
            meta["radios"] = new JsonArray(radios.OfType<JsonObject>().Select(r => (JsonNode?)Pick(r, MetaRadio)).ToArray());
        if (raw["uplink"] is JsonObject uplink)
            meta["uplink"] = Pick(uplink, MetaUplink);
        if (raw["lldp_table"] is JsonArray lldp)
            meta["lldp"] = new JsonArray(lldp.OfType<JsonObject>().Select(l => (JsonNode?)Pick(l, MetaLldp)).ToArray());
        return meta;
    }
}
