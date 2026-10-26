using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One changed field. Secret fields never carry values, for any role.</summary>
public sealed record FieldChange(string Path, JsonNode? Old, JsonNode? New, bool Secret = false);

/// <summary>One object's difference between two states.</summary>
public sealed class ObjectDiff
{
    public required string Collection { get; init; }
    public required string ObjectId { get; init; }
    public required string Name { get; init; }
    public required ConfigChangeAction Action { get; init; }
    public List<FieldChange> Fields { get; init; } = [];
    public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
    public bool ManagementPath { get; set; }
    public bool WanPath { get; set; }

    /// <summary>The NO feature that owns the object, or null.</summary>
    public string? OwnedBy { get; set; }

    /// <summary>UniFi generated it (predefined or derived); never written.</summary>
    public bool Generated { get; set; }

    /// <summary>A churn collection (channel plans): recorded, never alerted.</summary>
    public bool Churn { get; set; }

    /// <summary>The object before (null on create).</summary>
    public JsonObject? Before { get; init; }

    /// <summary>The object after (null on delete).</summary>
    public JsonObject? After { get; init; }
}

/// <summary>The order of custom firewall policies changed within one zone pair.</summary>
public sealed record PolicyOrderDiff(string SourceZoneId, string DestinationZoneId, string ZonePairName,
    IReadOnlyList<string> Before, IReadOnlyList<string> After);

/// <summary>The difference between two config states.</summary>
public sealed class ConfigDiff
{
    public List<ObjectDiff> Objects { get; } = [];
    public List<PolicyOrderDiff> PolicyOrder { get; } = [];

    /// <summary>Collections skipped because one side could not read them.</summary>
    public List<string> SkippedCollections { get; } = [];

    /// <summary>
    /// Fields a Network app upgrade added to every object of a collection, by collection. Compared
    /// nowhere and reported once, not as a change on each object.
    /// </summary>
    public Dictionary<string, List<string>> SchemaAdditions { get; } = new(StringComparer.Ordinal);

    public bool IsEmpty => Objects.Count == 0 && PolicyOrder.Count == 0;
}

/// <summary>
/// Compares two config states per diff-engine.md: canonical JSON per object, read-only and churn
/// fields dropped, set arrays as sets, keyed arrays element by element, secrets by hash only.
/// </summary>
public static class ConfigDiffEngine
{
    /// <summary>Compares two states of the same site (IDs are meaningful on both sides).</summary>
    public static ConfigDiff Compare(ConfigState before, ConfigState after, bool sameSite = true)
    {
        var diff = new ConfigDiff();
        var beforeCtx = new ConfigIdentityContext(before.NamesById());
        var afterCtx = new ConfigIdentityContext(after.NamesById());
        var mgmtNetworks = ManagementNetworkIds(before).Concat(ManagementNetworkIds(after)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (matches, idMap) = ObjectMatcher.MatchAll(before, after, beforeCtx, afterCtx, sameSite, (d, o) => Relevant(d, o));
        var upgraded = before.NetworkAppVersion != null && after.NetworkAppVersion != null
            && !string.Equals(before.NetworkAppVersion, after.NetworkAppVersion, StringComparison.OrdinalIgnoreCase);

        foreach (var def in ConfigCollections.All)
        {
            var inBefore = before.Collections.ContainsKey(def.Key);
            var inAfter = after.Collections.ContainsKey(def.Key);
            if (!inBefore && !inAfter) continue;
            if (!inBefore || !inAfter)
            {
                // One side could not read it (or predates it): never report everything created or deleted.
                diff.SkippedCollections.Add(def.Key);
                continue;
            }

            var match = matches[def.Key];
            var added = upgraded ? AddedFields(match) : [];
            if (added.Count > 0) diff.SchemaAdditions[def.Key] = [.. added.Order(StringComparer.Ordinal)];
            foreach (var pair in match.Pairs)
            {
                var objDiff = CompareObjects(def, pair.Left, pair.Right, mgmtNetworks, idMap, added);
                if (objDiff != null) diff.Objects.Add(objDiff);
            }
        }

        diff.PolicyOrder.AddRange(ComparePolicyOrder(before, after, afterCtx));
        return diff;
    }

    /// <summary>Clients with no user config are equivalent to absent, so a new device joining is not a change.</summary>
    private static List<JsonObject> Relevant(ConfigCollectionDef def, IReadOnlyList<JsonObject> objects) =>
        def.Key == "user" ? objects.Where(HasClientConfig).ToList() : objects.ToList();

    /// <summary>True when a client record carries anything a user set.</summary>
    public static bool HasClientConfig(JsonObject o)
    {
        foreach (var field in ClientConfigMarkers)
        {
            var v = o[field];
            if (v == null) continue;
            if (v is JsonValue val)
            {
                if (val.TryGetValue<bool>(out var b)) { if (b) return true; continue; }
                if (val.TryGetValue<string>(out var s)) { if (!string.IsNullOrWhiteSpace(s)) return true; continue; }
                return true;
            }
            if (v is JsonArray arr && arr.Count > 0) return true;
        }
        return false;
    }

    private static readonly string[] ClientConfigMarkers =
    [
        "name", "note", "use_fixedip", "fixed_ap_enabled", "blocked", "local_dns_record_enabled",
        "virtual_network_override_enabled", "dev_id_override", "network_members_group_ids", "usergroup_id",
    ];

    /// <summary>Networks devices are managed on: each device's mgmt_network_id and current connection network, plus the default LAN.</summary>
    public static HashSet<string> ManagementNetworkIds(ConfigState state)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in state.Get("device"))
        {
            if (d["mgmt_network_id"]?.ToString() is { Length: > 0 } m) ids.Add(m);
            if (d[ConfigCollections.DeviceMetaField]?["connection_network_id"]?.ToString() is { Length: > 0 } c) ids.Add(c);
        }
        foreach (var n in state.Get("networkconf"))
            if (ConfigCollections.NetworkKey(n) == "vlan:1") ids.Add(ConfigState.IdOf(n));
        return ids;
    }

    /// <summary>
    /// Top-level fields present on every paired object after and on none before: columns a Network app
    /// upgrade added. Network 11's move to PostgreSQL writes every column MongoDB left out.
    /// </summary>
    private static HashSet<string> AddedFields(MatchResult match)
    {
        var pairs = match.Pairs.Where(p => p.Left != null && p.Right != null).ToList();
        if (pairs.Count == 0) return [];
        var added = pairs[0].Right!.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var p in pairs)
            added.RemoveWhere(k => p.Left!.ContainsKey(k) || !p.Right!.ContainsKey(k));
        return added;
    }

    /// <summary>
    /// True when the fields both objects carry are equal, references mapped through <paramref name="idMap"/>.
    /// Pairs same-named objects whose IDs changed; a field only one side has says nothing either way.
    /// </summary>
    public static bool SharedFieldsEqual(ConfigCollectionDef def, JsonObject left, JsonObject right, IReadOnlyDictionary<string, string> idMap)
    {
        var shared = left.Select(p => p.Key).Where(right.ContainsKey).ToHashSet(StringComparer.Ordinal);
        var l = new JsonObject(left.Where(p => shared.Contains(p.Key)).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        var r = new JsonObject(right.Where(p => shared.Contains(p.Key)).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
        var fields = new List<FieldChange>();
        CompareNode(def, "", l, r, fields, ConfigCollections.OwnedBy(def, right) != null, idMap);
        return fields.Count == 0;
    }

    /// <summary>
    /// Compares one matched (or unmatched) pair; null when equal. References on the before side are
    /// mapped through <paramref name="idMap"/>, so an object that kept its target under a new ID is unchanged.
    /// </summary>
    public static ObjectDiff? CompareObjects(ConfigCollectionDef def, JsonObject? before, JsonObject? after, IReadOnlySet<string>? mgmtNetworks = null,
        IReadOnlyDictionary<string, string>? idMap = null, IReadOnlySet<string>? skipFields = null)
    {
        var any = after ?? before!;
        var owned = ConfigCollections.OwnedBy(def, any);
        var generated = def.IsGenerated?.Invoke(any) == true;

        ObjectDiff Make(ConfigChangeAction action, List<FieldChange> fields) => new()
        {
            Collection = def.Key,
            ObjectId = ConfigState.IdOf(any),
            Name = ConfigState.DisplayName(def, any),
            Action = action,
            Fields = fields,
            Before = before,
            After = after,
            OwnedBy = owned,
            Generated = generated,
            Churn = def.Churn,
        };

        ObjectDiff result;
        if (before == null)
            result = Make(ConfigChangeAction.Created, []);
        else if (after == null)
            result = Make(ConfigChangeAction.Deleted, []);
        else
        {
            var fields = new List<FieldChange>();
            CompareNode(def, "", before, after, fields, owned != null, idMap, skipFields);
            if (fields.Count == 0) return null;
            result = Make(ConfigChangeAction.Updated, fields);
        }

        Classify(def, result, any, mgmtNetworks);
        return result;
    }

    private static void Classify(ConfigCollectionDef def, ObjectDiff diff, JsonObject obj, IReadOnlySet<string>? mgmtNetworks)
    {
        var paths = diff.Fields.Count > 0 ? diff.Fields.Select(f => f.Path).ToList() : [""];
        foreach (var path in paths)
        {
            if (def.TagsForField != null)
                foreach (var tag in def.TagsForField(obj, path))
                    diff.Tags.Add(tag);
            if (def.IsManagementField?.Invoke(obj, path) == true)
            {
                // A network edit only reaches the management path when devices are managed on it.
                diff.ManagementPath |= def.Key != "networkconf" || mgmtNetworks == null || mgmtNetworks.Contains(diff.ObjectId);
            }
        }
        // Deleting a network or profile devices are managed through is a management-path change.
        if (diff.Action == ConfigChangeAction.Deleted && def.Key is "networkconf" && mgmtNetworks?.Contains(diff.ObjectId) == true)
            diff.ManagementPath = true;
        diff.WanPath = def.IsWanPath?.Invoke(obj) == true;
    }

    /// <summary>Fields that change on their own anywhere: timestamps and last-seen values.</summary>
    public static bool IsChurnField(string name) =>
        name.EndsWith("_time", StringComparison.Ordinal) || name.StartsWith("last_", StringComparison.Ordinal)
        || name is "uptime" or "_uptime";

    private static void CompareNode(ConfigCollectionDef def, string path, JsonObject before, JsonObject after, List<FieldChange> fields, bool noOwned,
        IReadOnlyDictionary<string, string>? idMap = null, IReadOnlySet<string>? skipFields = null)
    {
        var keys = before.Select(p => p.Key).Union(after.Select(p => p.Key), StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var isTop = path.Length == 0;
            if (isTop && (key is "_id" or "id" or "site_id" or ObjectMatcher.LegacyIdField || def.IsReadOnly(after, key))) continue;
            if (isTop && skipFields?.Contains(key) == true) continue;
            if (IsChurnField(key)) continue;
            if (isTop && noOwned && ConfigCollections.NoOwnedContentFields.Contains(key)) continue;

            var childPath = isTop ? key : $"{path}.{key}";
            var b = Remap(before[key], idMap);
            var a = after[key];
            if (isTop && def.FieldDefaults.TryGetValue(key, out var fallback))
            {
                b ??= JsonValue.Create(fallback);
                a ??= JsonValue.Create(fallback);
            }

            if (ConfigCollections.IsSecretField(def, key))
            {
                if (HashOf(b) != HashOf(a)) fields.Add(new FieldChange(childPath, null, null, Secret: true));
                continue;
            }

            if (isTop && def.KeyedArrays.TryGetValue(key, out var keyField) && (b is JsonArray || a is JsonArray))
            {
                CompareKeyedArray(def, childPath, b as JsonArray, a as JsonArray, keyField, fields, noOwned);
                continue;
            }

            if (b is JsonObject bo && a is JsonObject ao)
            {
                CompareNode(def, childPath, bo, ao, fields, noOwned);
                continue;
            }

            if (def.SetArrays.Contains(key) && (b is JsonArray || a is JsonArray))
            {
                var bs = AsSet(b as JsonArray);
                var asSet = AsSet(a as JsonArray);
                if (!bs.SetEquals(asSet)) fields.Add(Shown(def, childPath, b, a));
                continue;
            }

            if (!CanonicalJson.DeepEquals(b, a) && !(IsEmpty(b) && IsEmpty(a)) && !SameNumber(b, a))
                fields.Add(Shown(def, childPath, b, a));
        }
    }

    /// <summary>A copy of <paramref name="node"/> with every ID in <paramref name="idMap"/> replaced by its match.</summary>
    private static JsonNode? Remap(JsonNode? node, IReadOnlyDictionary<string, string>? idMap)
    {
        if (idMap == null || idMap.Count == 0 || node == null) return node;
        return node switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Remap(p.Value, idMap)))),
            JsonArray arr => new JsonArray(arr.Select(e => Remap(e, idMap)).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) && idMap.TryGetValue(s, out var mapped) && mapped != s => JsonValue.Create(mapped),
            _ => node.DeepClone(),
        };
    }

    /// <summary>A number and a string holding the same number ("4109" and 4109): UniFi stores some fields either way.</summary>
    private static bool SameNumber(JsonNode? b, JsonNode? a) =>
        AsNumber(b) is { } x && AsNumber(a) is { } y && x == y;

    private static decimal? AsNumber(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<decimal>(out var d) ? d
          : v.TryGetValue<string>(out var s) && decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null
        : null;

    // A value holding nested secrets (PPSK entries) is compared whole but shown with those secrets
    // hidden; when only a secret differs, the change is a secret change with no values at all.
    private static FieldChange Shown(ConfigCollectionDef def, string path, JsonNode? before, JsonNode? after)
    {
        var b = HideSecrets(def, before);
        var a = HideSecrets(def, after);
        return CanonicalJson.DeepEquals(b, a) ? new FieldChange(path, null, null, Secret: true) : new FieldChange(path, b, a);
    }

    private const string Hidden = "(hidden)";

    private static JsonNode? HideSecrets(ConfigCollectionDef def, JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key,
            ConfigCollections.IsSecretField(def, p.Key) && p.Value != null ? (JsonNode?)JsonValue.Create(Hidden) : HideSecrets(def, p.Value)))),
        JsonArray a => new JsonArray(a.Select(e => HideSecrets(def, e)).ToArray()),
        _ => n?.DeepClone(),
    };

    private static void CompareKeyedArray(ConfigCollectionDef def, string path, JsonArray? before, JsonArray? after,
        string keyField, List<FieldChange> fields, bool noOwned)
    {
        Dictionary<string, JsonObject> Index(JsonArray? arr)
        {
            var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            if (arr == null) return map;
            foreach (var o in arr.OfType<JsonObject>())
                if (o[keyField]?.ToString() is { Length: > 0 } k) map.TryAdd(k, o);
            return map;
        }

        var b = Index(before);
        var a = Index(after);
        foreach (var key in b.Keys.Union(a.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var elementPath = $"{path}[{keyField}={key}]";
            b.TryGetValue(key, out var bo);
            a.TryGetValue(key, out var ao);
            if (bo == null || ao == null)
            {
                fields.Add(Shown(def, elementPath, bo, ao));
                continue;
            }
            CompareNode(def, elementPath, bo, ao, fields, noOwned);
        }
    }

    private static HashSet<string> AsSet(JsonArray? arr) =>
        arr == null ? [] : arr.Select(CanonicalJson.Serialize).ToHashSet(StringComparer.Ordinal);

    // An absent field reads as unset: UniFi treats a missing flag as false, and Network 11's PostgreSQL
    // store writes false, 0, and empty values into every column MongoDB left out.
    private static bool IsEmpty(JsonNode? n) =>
        n == null || (n is JsonArray arr && arr.Count == 0) || (n is JsonValue v &&
            ((v.TryGetValue<string>(out var s) && s.Length == 0) || (v.TryGetValue<bool>(out var flag) && !flag) || (v.TryGetValue<decimal>(out var num) && num == 0)));

    /// <summary>Hash of a secret's canonical value. Only ever compared, never stored or shown.</summary>
    public static string HashOf(JsonNode? node) =>
        node == null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson.Serialize(node))));

    /// <summary>Custom policy order per zone pair, compared for policies present on both sides.</summary>
    public static IEnumerable<PolicyOrderDiff> ComparePolicyOrder(ConfigState before, ConfigState after, ConfigIdentityContext ctx)
    {
        var b = PolicyOrder(before);
        var a = PolicyOrder(after);
        foreach (var (pair, afterIds) in a)
        {
            if (!b.TryGetValue(pair, out var beforeIds)) continue;
            var common = afterIds.Intersect(beforeIds, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var bOrder = beforeIds.Where(common.Contains).ToList();
            var aOrder = afterIds.Where(common.Contains).ToList();
            if (bOrder.SequenceEqual(aOrder, StringComparer.OrdinalIgnoreCase)) continue;
            var parts = pair.Split('>');
            yield return new PolicyOrderDiff(parts[0], parts[1], $"{ctx.NameOf(parts[0])} to {ctx.NameOf(parts[1])}", bOrder, aOrder);
        }
    }

    /// <summary>Custom (non-generated) policy IDs per "source>destination" zone pair, ordered by index.</summary>
    public static Dictionary<string, List<string>> PolicyOrder(ConfigState state)
    {
        var def = ConfigCollections.Get("firewall_policy")!;
        return state.Get("firewall_policy")
            .Where(p => def.IsGenerated?.Invoke(p) != true)
            .GroupBy(p => $"{p["source"]?["zone_id"]}>{p["destination"]?["zone_id"]}")
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue)
                      .Select(ConfigState.IdOf).ToList());
    }
}
