using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// One UniFi AUDIT log event. It names who, which object, and (for one-object changes) which field;
/// it never carries old or new values (api-findings.md).
/// </summary>
public sealed record AuditLogEvent
{
    public string Id { get; init; } = "";
    public long Timestamp { get; init; }
    public string Key { get; init; } = "";
    public string? AdminId { get; init; }
    public string? AdminName { get; init; }
    public string? Ip { get; init; }
    public string? Platform { get; init; }
    public string? ObjectId { get; init; }
    public string? ObjectName { get; init; }
    public string? Section { get; init; }
    public string? SettingCollection { get; init; }
    public string? SettingField { get; init; }
    public int Count { get; init; } = 1;

    public DateTime TimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).UtcDateTime;

    /// <summary>A config change event (as opposed to sign-ins and generated/removed entity notices).</summary>
    public bool IsChange => Key.StartsWith("MADE_CHANGES", StringComparison.Ordinal)
        || Key is "REMOVED_ENTITY" or "PAUSED_ENTITY" or "RESUMED_ENTITY";
}

/// <summary>Reads AUDIT events from <c>v2 system-log/all</c>.</summary>
[VendorSpecific("UniFi", "system-log/all with categories AUDIT; parameters ADMIN, IP, OBJECT, SECTION, SETTING_NAME, COUNT")]
public static class AuditLogReader
{
    private const int PageSize = 100;
    private const int MaxPages = 50;

    /// <summary>Events after <paramref name="fromMs"/> (inclusive) up to now, oldest first.</summary>
    public static async Task<(List<AuditLogEvent> Events, bool Ok)> ReadAsync(
        IUniFiConfigApi api, long fromMs, long toMs, CancellationToken cancellationToken)
    {
        var events = new List<AuditLogEvent>();
        for (var page = 0; page < MaxPages; page++)
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["searchText"] = "",
                ["severities"] = new[] { "LOW", "MEDIUM", "HIGH", "VERY_HIGH" },
                ["categories"] = new[] { "AUDIT" },
                ["timestampFrom"] = fromMs,
                ["timestampTo"] = toMs,
                ["pageNumber"] = page,
                ["pageSize"] = PageSize,
                ["adminIds"] = Array.Empty<string>(),
                ["clientDeviceMacs"] = Array.Empty<string>(),
            });
            var response = await api.SendAsync(UniFiApiScope.SiteV2, HttpMethod.Post, "system-log/all", body, cancellationToken);
            if (!response.IsSuccess) return (events, false);
            var batch = Parse(response.Body);
            events.AddRange(batch);
            if (batch.Count < PageSize) break;
        }
        return (events.OrderBy(e => e.Timestamp).ToList(), true);
    }

    /// <summary>Parses a system-log response (<c>{data:[...]}</c> or a bare array).</summary>
    public static List<AuditLogEvent> Parse(string body)
    {
        var result = new List<AuditLogEvent>();
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return result; }
        var items = root is JsonObject o ? o["data"] as JsonArray : root as JsonArray;
        if (items == null) return result;

        foreach (var item in items.OfType<JsonObject>())
        {
            var p = item["parameters"] as JsonObject ?? [];
            var ts = item["timestamp"] is JsonValue tv && tv.TryGetValue<long>(out var t) ? t : 0;
            var setting = p["SETTING_NAME"] as JsonObject;
            result.Add(new AuditLogEvent
            {
                Id = Str(item["id"]) ?? Str(item["_id"]) ?? $"{ts}:{Str(item["key"])}:{Str(F(p["OBJECT"], "id"))}",
                Timestamp = ts,
                Key = Str(item["key"]) ?? "",
                AdminId = Str(F(p["ADMIN"], "id")),
                AdminName = Str(F(p["ADMIN"], "name")) ?? Str(p["ADMIN"]),
                Ip = Str(F(p["IP"], "id")) ?? Str(F(p["IP"], "name")) ?? Str(p["IP"]),
                Platform = Str(F(p["PLATFORM"], "id")) ?? Str(F(p["PLATFORM"], "name")) ?? Str(p["PLATFORM"]),
                ObjectId = Str(F(p["OBJECT"], "id")),
                ObjectName = Str(F(p["OBJECT"], "name")),
                Section = Str(F(p["SECTION"], "id")) ?? Str(F(p["SECTION"], "name")) ?? Str(p["SECTION"]),
                SettingCollection = setting == null ? null : Str(setting["collection"]),
                SettingField = setting == null ? null : Str(setting["id"]),
                Count = int.TryParse(Str(F(p["COUNT"], "id")) ?? Str(F(p["COUNT"], "name")) ?? Str(p["COUNT"]), out var c) ? c : 1,
            });
        }
        return result;
    }

    private static JsonNode? F(JsonNode? n, string field) => n is JsonObject o ? o[field] : null;

    private static string? Str(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToString(),
        _ => null,
    };
}
