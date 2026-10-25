using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A problem found in an edited YAML file.</summary>
public sealed record YamlProblem(string Level, string File, int Line, string Message);

/// <summary>The result of parsing an edited YAML file.</summary>
public sealed class YamlParseResult
{
    /// <summary>Objects by collection key, with references resolved back to UniFi IDs.</summary>
    public Dictionary<string, List<JsonObject>> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<YamlProblem> Problems { get; } = [];
    public bool HasErrors => Problems.Any(p => p.Level == "Error");
}

/// <summary>How an export writes secret fields.</summary>
public enum SecretMode
{
    /// <summary><c>&lt;keep current&gt;</c>: a downloaded file, applied later against whatever the Console has.</summary>
    Redact,

    /// <summary>
    /// <c>&lt;secret:id/field&gt;</c>: the Edit tab. The token names the value's place in the state the
    /// edit started from, so it resolves exactly (an entry moved in a list keeps its own password)
    /// and a Site Admin can reveal it.
    /// </summary>
    Mask,

    /// <summary>The values themselves: Download with Secrets.</summary>
    Clear,
}

/// <summary>
/// Config as YAML (spec "Format", "Object identity and references"). Keys are UniFi's own field names;
/// a reference field is written with the ID suffix stripped and holds a name
/// (<c>networkconf_id</c> becomes <c>network</c>), the target's enforced key where names collide
/// (<c>{vlan: 30}</c>, <c>{mac: ...}</c>), or <c>{id: ...}</c>. Secrets are written as
/// <c>&lt;keep current&gt;</c> unless secrets are included.
/// </summary>
public static partial class YamlConverter
{
    /// <summary>The YAML key for a collection ("wlans", "firewall_policies").</summary>
    public static string YamlName(ConfigCollectionDef def) => def.Key switch
    {
        "setting" => "settings",
        "static_dns" => "local_dns",
        "routing" => "static_routes",
        "user" => "clients",
        _ => Regex.Replace(def.DisplayName.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_'),
    };

    /// <summary>The file name the Edit tab shows for a collection ("wlans.yaml").</summary>
    public static string FileName(ConfigCollectionDef def) => YamlName(def) + ".yaml";

    private static ConfigCollectionDef? FromYamlName(string name) =>
        ConfigCollections.All.FirstOrDefault(d => YamlName(d) == name || d.Key == name);

    /// <summary>The stripped key of a reference field, or null when the field is not a reference by name.</summary>
    public static string? StrippedName(string field)
    {
        foreach (var suffix in new[] { "conf_ids", "conf_id", "_ids", "_id" })
            if (field.EndsWith(suffix, StringComparison.Ordinal) && field.Length > suffix.Length && field != "_id")
            {
                var stem = field[..^suffix.Length].TrimEnd('_');
                return suffix.EndsWith("s", StringComparison.Ordinal) ? stem + "s" : stem;
            }
        return null;
    }

    // ---------- Export ----------

    /// <summary>Writes the given collections (all when null) as one YAML document.</summary>
    public static string Export(ConfigState state, IEnumerable<string>? collections, bool includeSecrets) =>
        Export(state, collections, includeSecrets ? SecretMode.Clear : SecretMode.Redact);

    /// <summary>Writes the given collections (all when null), with secrets as <paramref name="secrets"/> says.</summary>
    public static string Export(ConfigState state, IEnumerable<string>? collections, SecretMode secrets)
    {
        var index = new ReferenceIndex(state);
        var sb = new StringBuilder();
        var keys = collections?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var def in ConfigCollections.All)
        {
            if (keys != null && !keys.Contains(def.Key)) continue;
            if (!state.Collections.ContainsKey(def.Key)) continue;
            var objects = state.Get(def.Key).Where(o => def.Key != "user" || ConfigDiffEngine.HasClientConfig(o)).ToList();
            sb.Append(YamlName(def)).Append(':');
            if (objects.Count == 0) { sb.Append(" []\n"); continue; }
            sb.Append('\n');
            var ambiguous = objects.GroupBy(o => Identity(def, o)).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            foreach (var o in objects)
                WriteObject(sb, def, o, index, secrets, indent: 2, listItem: true, withId: ambiguous.Contains(o), top: true, path: ConfigState.IdOf(o));
        }
        return sb.ToString();
    }

    /// <summary>The masked form of the secret at <paramref name="path"/> (object ID, then keys and list indexes).</summary>
    public static string SecretToken(string path) => SecretTokenPrefix + path + ">";

    private const string SecretTokenPrefix = "<secret:";

    /// <summary>The path inside a <see cref="SecretToken"/>, or null when the text is not one.</summary>
    public static string? SecretTokenPath(string? text) =>
        text != null && text.StartsWith(SecretTokenPrefix, StringComparison.Ordinal) && text.EndsWith('>') && text.Length > SecretTokenPrefix.Length + 1
            ? text[SecretTokenPrefix.Length..^1]
            : null;

    /// <summary>
    /// The value a secret token points at in <paramref name="state"/>, with its collection and field,
    /// or null when the path leads nowhere or ends on a field that is not a secret.
    /// </summary>
    public static (JsonNode Value, ConfigCollectionDef Def, JsonObject Object, string Field)? FindSecret(ConfigState state, string path)
    {
        var parts = path.Split('/');
        if (parts.Length < 2 || !state.ObjectsById().TryGetValue(parts[0], out var entry)) return null;
        var def = ConfigCollections.Get(entry.Collection);
        if (def == null) return null;
        JsonNode? node = entry.Object;
        string? field = null;
        foreach (var part in parts.Skip(1))
        {
            if (node is JsonObject o) { node = o[part]; field = part; }
            else if (node is JsonArray a && int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count) node = a[i];
            else return null;
        }
        // Only a secret field can be reached this way, so a token never reads out an ordinary value.
        return node != null && field != null && ConfigCollections.IsSecretField(def, field) && parts[^1] == field
            ? (node, def, entry.Object, field)
            : null;
    }

    /// <summary>A value as one YAML scalar (quoted when it needs to be), for inserting into an edited file.</summary>
    public static string ScalarText(JsonNode? value) => value switch
    {
        null => "null",
        JsonValue v => Scalar(v),
        _ => Flow(value),
    };

    private static string Identity(ConfigCollectionDef def, JsonObject o) =>
        def.EnforcedKey?.Invoke(o, new ConfigIdentityContext(new Dictionary<string, string>())) ?? ConfigState.DisplayName(def, o);

    private static void WriteObject(StringBuilder sb, ConfigCollectionDef def, JsonObject o, ReferenceIndex index, SecretMode secrets,
        int indent, bool listItem, bool withId, bool top, string path)
    {
        var first = true;
        void Prefix()
        {
            if (listItem && first) sb.Append(' ', indent - 2).Append("- ");
            else sb.Append(' ', indent);
            first = false;
        }

        if (withId)
        {
            Prefix();
            sb.Append("id: ").Append(Scalar(ConfigState.IdOf(o))).Append('\n');
        }
        string? pending = null;
        var anyField = false;
        foreach (var (heading, key, value) in Ordered(def, o, top))
        {
            // A group's heading survives its skipped fields and prints before its first written one.
            pending = heading ?? pending;
            if (top && (key is "_id" or "id" or "site_id" || key == ConfigCollections.DeviceMetaField || def.ReadOnlyFields.Contains(key) && key is not "key")) continue;
            if (top && def.HiddenInYaml?.Invoke(o, key) == true) continue;
            if (ConfigDiffEngine.IsChurnField(key)) continue;
            // Never above the object's first field (the id: line of an ambiguous object does not count).
            if (pending != null && anyField) sb.Append(' ', indent).Append("# ").Append(pending).Append('\n');
            pending = null;
            anyField = true;
            var outKey = key;
            JsonNode? outValue = value;
            var childPath = $"{path}/{key}";
            if (ConfigCollections.IsSecretField(def, key) && secrets != SecretMode.Clear)
                outValue = secrets == SecretMode.Mask && value != null ? JsonValue.Create(SecretToken(childPath)) : PlanBuilder.KeepSecret;
            else if (StrippedName(key) is { } stripped && !o.ContainsKey(stripped) && index.RenderReference(value) is { } rendered)
            {
                outKey = stripped;
                outValue = rendered;
            }
            Prefix();
            WriteValue(sb, def, outKey, outValue, index, secrets, indent, childPath);
        }
        if (first) { Prefix(); sb.Append("{}\n"); }
    }

    /// <summary>
    /// An object's fields in YAML order, each with the heading of the group it starts (null inside a
    /// group). Top level follows <see cref="ConfigCollectionDef.FieldGroups"/>; nested objects follow
    /// <see cref="ConfigCollectionDef.NestedFieldOrder"/>; anything unlisted comes last, alphabetically.
    /// </summary>
    private static IEnumerable<(string? Heading, string Key, JsonNode? Value)> Ordered(ConfigCollectionDef def, JsonObject o, bool top)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        if (top && def.FieldGroups.Count > 0)
        {
            foreach (var group in def.FieldGroups)
            {
                var heading = group.Heading;
                foreach (var field in group.Fields)
                    if (o.TryGetPropertyValue(field, out var v) && placed.Add(field))
                    {
                        yield return (heading, field, v);
                        heading = null;
                    }
            }
            var other = "Other";
            foreach (var p in o.Where(p => !placed.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                yield return (other, p.Key, p.Value);
                other = null;
            }
            yield break;
        }

        IEnumerable<string> lead = !top
            ? def.NestedFieldOrder.Count > 0 ? def.NestedFieldOrder : [def.NameField]
            : def.SectionFieldOrder != null && o[def.NameField]?.ToString() is { } section && def.SectionFieldOrder.TryGetValue(section, out var fields)
                ? [def.NameField, .. fields]
                : [def.NameField];
        foreach (var field in lead)
            if (o.TryGetPropertyValue(field, out var v) && placed.Add(field)) yield return (null, field, v);
        foreach (var p in o.Where(p => !placed.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            yield return (null, p.Key, p.Value);
    }

    private static void WriteValue(StringBuilder sb, ConfigCollectionDef def, string key, JsonNode? value, ReferenceIndex index, SecretMode secrets, int indent, string path)
    {
        sb.Append(KeyText(key)).Append(':');
        switch (value)
        {
            case null:
                sb.Append(" null\n");
                break;
            case JsonValue v:
                sb.Append(' ').Append(Scalar(v)).Append('\n');
                break;
            case JsonArray arr when arr.Count == 0:
                sb.Append(" []\n");
                break;
            case JsonArray arr when arr.All(e => e is JsonValue or null):
                sb.Append(" [").Append(string.Join(", ", arr.Select(e => e == null ? "null" : Scalar((JsonValue)e)))).Append("]\n");
                break;
            case JsonArray arr:
                sb.Append('\n');
                for (var i = 0; i < arr.Count; i++)
                {
                    var e = arr[i];
                    if (e is JsonObject eo) WriteObject(sb, def, eo, index, secrets, indent + 4, listItem: true, withId: false, top: false, path: $"{path}/{i}");
                    else sb.Append(' ', indent + 2).Append("- ").Append(e == null ? "null" : Flow(e)).Append('\n');
                }
                break;
            case JsonObject obj when obj.Count == 0:
                sb.Append(" {}\n");
                break;
            // Flow style skips WriteObject's per-key handling, so an object with a secret or a reference
            // (zone_id written as zone: Internal) keeps block style.
            case JsonObject obj when obj.Count <= 2 && obj.All(p => p.Value is JsonValue && !ConfigCollections.IsSecretField(def, p.Key) && StrippedName(p.Key) == null):
                sb.Append(' ').Append(Flow(obj)).Append('\n');
                break;
            case JsonObject obj:
                sb.Append('\n');
                WriteObject(sb, def, obj, index, secrets, indent + 2, listItem: false, withId: false, top: false, path: path);
                break;
        }
    }

    private static string KeyText(string key) => Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_.-]*$") ? key : Quote(key);

    private static string Flow(JsonNode node) => node switch
    {
        JsonObject o => "{" + string.Join(", ", o.Select(p => $"{KeyText(p.Key)}: {(p.Value == null ? "null" : Flow(p.Value))}")) + "}",
        JsonArray a => "[" + string.Join(", ", a.Select(e => e == null ? "null" : Flow(e))) + "]",
        JsonValue v => Scalar(v),
        _ => "null",
    };

    private static string Scalar(JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<string>(out var s)) return Scalar(s);
        return v.ToJsonString();
    }

    private static string Scalar(string s)
    {
        if (s == PlanBuilder.KeepSecret || SecretTokenPath(s) is { } p && !p.Contains('"') && !p.Contains(' ')) return s;
        var plain = s.Length > 0 && Regex.IsMatch(s, @"^[A-Za-z0-9_./@][A-Za-z0-9 _./@:+()-]*$") && !s.EndsWith(' ') && !s.Contains(": ")
                    && !Regex.IsMatch(s, @"^(true|false|null|yes|no|on|off|~|[-+]?[0-9][0-9_]*(\.[0-9]*)?([eE][-+]?[0-9]+)?|0x[0-9a-fA-F]+)$", RegexOptions.IgnoreCase);
        return plain ? s : Quote(s);
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

    // ---------- Parse ----------

    /// <summary>
    /// Parses an edited file against the state it was made from. References resolve against the
    /// desired state (the base with this file's objects in place), so a file can refer to an object it
    /// creates.
    /// </summary>
    public static YamlParseResult Parse(string text, ConfigState baseState, string fileName)
    {
        var result = new YamlParseResult();
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            result.Problems.Add(new YamlProblem("Error", fileName, (int)ex.Start.Line, ex.InnerException?.Message ?? ex.Message));
            return result;
        }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            result.Problems.Add(new YamlProblem("Error", fileName, 1, "The file must start with a collection name, such as wlans:"));
            return result;
        }

        // First pass: plain conversion, so references can see every object the file declares.
        var raw = new List<(ConfigCollectionDef Def, YamlMappingNode Node, JsonObject Obj)>();
        foreach (var (keyNode, valueNode) in root.Children)
        {
            var name = ((YamlScalarNode)keyNode).Value ?? "";
            var def = FromYamlName(name);
            if (def == null)
            {
                result.Problems.Add(new YamlProblem("Error", fileName, Line(keyNode), $"Unknown collection {name}."));
                continue;
            }
            result.Collections[def.Key] = [];
            if (valueNode is not YamlSequenceNode seq)
            {
                if (valueNode is not YamlSequenceNode && !(valueNode is YamlScalarNode s && string.IsNullOrEmpty(s.Value)))
                    result.Problems.Add(new YamlProblem("Error", fileName, Line(valueNode), $"{name} must be a list."));
                continue;
            }
            foreach (var item in seq.Children)
            {
                if (item is not YamlMappingNode map)
                {
                    result.Problems.Add(new YamlProblem("Error", fileName, Line(item), "Each item must be a set of fields."));
                    continue;
                }
                raw.Add((def, map, (JsonObject)ToJson(map)!));
            }
        }

        var desired = baseState.Clone();
        foreach (var key in result.Collections.Keys)
            desired.Collections[key] = raw.Where(r => r.Def.Key == key).Select(r => r.Obj).ToList();
        foreach (var (_, _, obj) in raw)
            if (!obj.ContainsKey("_id") && obj["id"] is JsonValue idv && idv.TryGetValue<string>(out var given)) { obj["_id"] = given; obj.Remove("id"); }
        // The file carries no IDs: an object that matches one in the state it was made from (enforced key,
        // then name, as Review Plan matches) takes that object's ID, so references to it in this file
        // resolve to the real object. Without this, an untouched WLAN's AP group became a temporary ID.
        var baseCtx = new ConfigIdentityContext(baseState.NamesById());
        var fileCtx = new ConfigIdentityContext(desired.NamesById());
        foreach (var key in result.Collections.Keys)
        {
            var def = ConfigCollections.Get(key)!;
            var unnamed = raw.Where(r => r.Def.Key == key && !r.Obj.ContainsKey("_id")).Select(r => r.Obj).ToList();
            var taken = raw.Where(r => r.Def.Key == key && r.Obj.ContainsKey("_id")).Select(r => ConfigState.IdOf(r.Obj)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var match = ObjectMatcher.Match(def, unnamed, baseState.Get(key).Where(o => !taken.Contains(ConfigState.IdOf(o))).ToList(), fileCtx, baseCtx);
            foreach (var pair in match.Pairs.Where(p => p.Left != null && p.Right != null))
                pair.Left!["_id"] = ConfigState.IdOf(pair.Right!);
        }
        // Only new objects are left: a temporary ID, so the file can refer to what it creates.
        var tempId = 0;
        foreach (var (_, _, obj) in raw)
            if (!obj.ContainsKey("_id")) obj["_id"] = $"{TempIdPrefix}{++tempId}";

        var index = new ReferenceIndex(desired);
        var fieldMap = ReferenceFields(baseState);
        foreach (var (def, map, obj) in raw)
        {
            var resolved = ResolveObject(def, obj, map, index, fieldMap, result, fileName);
            ResolveSecretTokens(def, resolved, baseState, Line(map), result, fileName);
            result.Collections[def.Key].Add(resolved);
        }
        foreach (var (_, _, obj) in raw.Where(r => ConfigState.IdOf(r.Obj).StartsWith(TempIdPrefix, StringComparison.Ordinal)))
            obj.Remove("_id");
        foreach (var list in result.Collections.Values)
            foreach (var obj in list.Where(o => (o["_id"]?.ToString() ?? "").StartsWith(TempIdPrefix, StringComparison.Ordinal)))
                obj.Remove("_id");

        Lint(result, baseState, raw, fileName);
        return result;
    }

    /// <summary>Prefix of a temporary ID for an object an edit creates. No UniFi value starts with it, so a plan can refuse one.</summary>
    public const string TempIdPrefix = "@new:";

    private static int Line(YamlNode node) => (int)node.Start.Line;

    /// <summary>
    /// Replaces each <c>&lt;secret:...&gt;</c> with the value it names in the state the edit started from.
    /// A token is accepted only as a secret field's value, so it cannot copy a secret into a field
    /// that is shown in clear text.
    /// </summary>
    private static void ResolveSecretTokens(ConfigCollectionDef def, JsonNode? node, ConfigState baseState, int line,
        YamlParseResult result, string fileName)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s) && SecretTokenPath(s) is { } path)
                    {
                        var found = FindSecret(baseState, path);
                        if (!ConfigCollections.IsSecretField(def, key))
                            result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: a masked secret can only be the value of a secret field."));
                        else if (found == null)
                            result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: the masked secret is not in the config this edit started from. Enter a value."));
                        // Same field name only, so a VPN key can never become a Wi-Fi passphrase someone can read.
                        else if (found.Value.Field != key)
                            result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: a masked {found.Value.Field} cannot be used here. Enter a value."));
                        else
                            o[key] = found.Value.Value.DeepClone();
                    }
                    else ResolveSecretTokens(def, value, baseState, line, result, fileName);
                }
                break;
            case JsonArray a:
                foreach (var e in a)
                {
                    if (e is JsonValue v && v.TryGetValue<string>(out var s) && SecretTokenPath(s) != null)
                        result.Problems.Add(new YamlProblem("Error", fileName, line, "A masked secret can only be the value of a secret field."));
                    else ResolveSecretTokens(def, e, baseState, line, result, fileName);
                }
                break;
        }
    }

    /// <summary>The collection a reference field points into, when the name says; null searches every collection.</summary>
    internal static string? TargetCollection(string field) => field switch
    {
        _ when field.Contains("network", StringComparison.Ordinal) => "networkconf",
        _ when field.Contains("zone", StringComparison.Ordinal) => "firewall_zone",
        _ when field.Contains("portconf", StringComparison.Ordinal) => "portconf",
        _ when field.Contains("usergroup", StringComparison.Ordinal) => "usergroup",
        _ when field.Contains("ap_group", StringComparison.Ordinal) => "apgroups",
        _ when field.Contains("wlangroup", StringComparison.Ordinal) => "wlangroup",
        _ when field.Contains("radiusprofile", StringComparison.Ordinal) => "radiusprofile",
        _ when field.Contains("group_id", StringComparison.Ordinal) => "firewallgroup",
        _ => null,
    };

    /// <summary>Reference fields by stripped name, for collections whose base objects never used them.</summary>
    private static readonly Dictionary<string, string> CommonReferenceFields = new(StringComparer.Ordinal)
    {
        ["network"] = "networkconf_id", ["native_network"] = "native_networkconf_id", ["voice_network"] = "voice_networkconf_id",
        ["excluded_networks"] = "excluded_networkconf_ids", ["tagged_networks"] = "tagged_networkconf_ids",
        ["multicast_router_networks"] = "multicast_router_networkconf_ids", ["mgmt_network"] = "mgmt_network_id",
        ["ap_groups"] = "ap_group_ids", ["zone"] = "zone_id", ["usergroup"] = "usergroup_id", ["network_ids"] = "network_ids",
        ["radiusprofile"] = "radiusprofile_id", ["port_group"] = "port_group_id", ["ip_group"] = "ip_group_id",
    };

    /// <summary>Reference field names seen in the base state, by stripped name, per collection.</summary>
    private static Dictionary<string, Dictionary<string, string>> ReferenceFields(ConfigState state)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        void Walk(string collection, JsonNode? node)
        {
            if (node is JsonObject o)
                foreach (var (k, v) in o)
                {
                    if (StrippedName(k) is { } s)
                    {
                        if (!map.TryGetValue(collection, out var m)) map[collection] = m = new(StringComparer.Ordinal);
                        m.TryAdd(s, k);
                    }
                    Walk(collection, v);
                }
            else if (node is JsonArray a)
                foreach (var e in a) Walk(collection, e);
        }
        foreach (var (key, list) in state.Collections)
            foreach (var o in list) Walk(key, o);
        return map;
    }

    private static JsonObject ResolveObject(ConfigCollectionDef def, JsonObject obj, YamlMappingNode map, ReferenceIndex index,
        Dictionary<string, Dictionary<string, string>> fieldMap, YamlParseResult result, string fileName)
    {
        var known = new Dictionary<string, string>(CommonReferenceFields, StringComparer.Ordinal);
        foreach (var (k, v) in fieldMap.GetValueOrDefault(def.Key) ?? []) known[k] = v;
        JsonObject Resolve(JsonObject o, YamlMappingNode? m)
        {
            var output = new JsonObject();
            foreach (var (key, value) in o)
            {
                var valueNode = m?.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == key).Value;
                var line = valueNode == null ? Line(map) : Line(valueNode);
                if (key is "_id" or "id" || StrippedName(key) != null || !known.TryGetValue(key, out var original))
                {
                    output[key] = value switch
                    {
                        JsonObject child => Resolve(child, valueNode as YamlMappingNode),
                        JsonArray arr => new JsonArray(arr.Select((e, i) => e is JsonObject eo
                            ? (JsonNode)Resolve(eo, (valueNode as YamlSequenceNode)?.Children.ElementAtOrDefault(i) as YamlMappingNode)
                            : e?.DeepClone()).ToArray()),
                        _ => value?.DeepClone(),
                    };
                    continue;
                }
                // A stripped reference key: turn names back into IDs.
                output[original] = value is JsonArray list
                    ? new JsonArray(list.Select(e => (JsonNode?)index.ResolveReference(e, key, TargetCollection(original), line, result, fileName)).ToArray())
                    : index.ResolveReference(value, key, TargetCollection(original), line, result, fileName);
            }
            return output;
        }
        return Resolve(obj, map);
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(c => KeyValuePair.Create(((YamlScalarNode)c.Key).Value ?? "", ToJson(c.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(ToJson).ToArray()),
        YamlScalarNode sc => ScalarToJson(sc),
        _ => null,
    };

    private static JsonNode? ScalarToJson(YamlScalarNode sc)
    {
        var v = sc.Value ?? "";
        if (sc.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded)
            return JsonValue.Create(v);
        if (v is "" or "~" or "null") return null;
        if (v is "true" or "True") return JsonValue.Create(true);
        if (v is "false" or "False") return JsonValue.Create(false);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (Regex.IsMatch(v, @"^[-+]?[0-9]*\.[0-9]+([eE][-+]?[0-9]+)?$") && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    /// <summary>Warnings and notes that do not stop Review Plan, from the mockup's Problems card.</summary>
    private static void Lint(YamlParseResult result, ConfigState baseState, List<(ConfigCollectionDef Def, YamlMappingNode Node, JsonObject Obj)> raw, string fileName)
    {
        foreach (var (def, node, obj) in raw.Where(r => r.Def.Key == "wlanconf"))
        {
            if (obj["wpa3_transition"] is JsonValue t && t.TryGetValue<bool>(out var tr) && tr)
            {
                var keyNode = node.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == "wpa3_transition").Key;
                result.Problems.Add(new YamlProblem("Info", fileName, keyNode == null ? Line(node) : Line(keyNode),
                    $"WPA3 transition on {ConfigState.DisplayName(def, obj)}: some IoT devices cannot join. Review Plan asks you to acknowledge it."));
            }
        }

        // An object removed from the file is deleted. Say what that does: nothing in a collection
        // Network Optimizer does not delete from, nothing to UniFi's built-in objects, list references
        // go with it (DeleteCascade), and single references must be pointed elsewhere first.
        var desired = baseState.Clone();
        foreach (var (key, list) in result.Collections) desired.Collections[key] = list;
        foreach (var (key, list) in result.Collections)
        {
            var def = ConfigCollections.Get(key);
            if (def == null) continue;
            var removedObjects = DeleteCascade.Removed(def, baseState, desired)
                .Where(o => !(def.Key == "user" && !ConfigDiffEngine.HasClientConfig(o))).ToList();
            if (removedObjects.Count == 0) continue;
            if (def.Write?.DeletePath == null)
            {
                result.Problems.Add(new YamlProblem("Warning", fileName, 1,
                    $"{def.DisplayName} are read-only in Network Optimizer: {Names(def, removedObjects)} will not be deleted."));
                continue;
            }
            foreach (var removed in removedObjects)
            {
                var name = $"{def.Noun} {ConfigState.DisplayName(def, removed)}";
                if (DeleteCascade.IsProtected(removed))
                {
                    result.Problems.Add(new YamlProblem("Error", fileName, 1, $"{name} is built into UniFi and cannot be deleted."));
                    continue;
                }
                var id = ConfigState.IdOf(removed);
                var cascaded = new List<string>();
                var blocking = new List<string>();
                foreach (var (refCollection, objects) in result.Collections.Concat(baseState.Collections.Where(c => !result.Collections.ContainsKey(c.Key))))
                {
                    var refDef = ConfigCollections.Get(refCollection);
                    foreach (var o in objects)
                    {
                        if (ConfigState.IdOf(o) == id || refDef?.IsGenerated?.Invoke(o) == true) continue;
                        var hits = DeleteCascade.Find(o, id);
                        if (hits.Count == 0) continue;
                        var who = $"{refDef?.Noun ?? refCollection} {ConfigState.DisplayName(refDef, o)}";
                        if (refDef?.Write?.UpdatePath != null && DeleteCascade.CanCascade(hits)) cascaded.Add(who);
                        else blocking.Add($"{who} ({string.Join(", ", hits.Select(h => h.Field).Distinct())})");
                    }
                }
                if (cascaded.Count > 0)
                    result.Problems.Add(new YamlProblem("Info", fileName, 1,
                        $"Deleting {name} also removes it from {List(cascaded)}. Review Plan shows those changes."));
                if (blocking.Count > 0)
                    result.Problems.Add(new YamlProblem("Warning", fileName, 1,
                        $"{name} is still used by {List(blocking)}. Point {(blocking.Count == 1 ? "it" : "them")} at another {def.Noun.ToLowerInvariant()} first; Review Plan refuses the delete until then."));
            }
        }
    }

    private static string Names(ConfigCollectionDef? def, IReadOnlyList<JsonObject> objects) =>
        List(objects.Select(o => ConfigState.DisplayName(def, o)).ToList());

    /// <summary>"A, B, and C", or the first five and how many more.</summary>
    private static string List(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        <= 5 => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}",
        _ => $"{string.Join(", ", items.Take(5))}, and {items.Count - 5} more",
    };

    /// <summary>Renders and resolves references between objects.</summary>
    private sealed class ReferenceIndex
    {
        private readonly Dictionary<string, (string Collection, JsonObject Object)> _byId;
        private readonly Dictionary<string, List<(string Collection, JsonObject Object)>> _byName = new(StringComparer.OrdinalIgnoreCase);

        public ReferenceIndex(ConfigState state)
        {
            _byId = state.ObjectsById();
            foreach (var (id, entry) in _byId)
            {
                var name = ConfigState.DisplayName(ConfigCollections.Get(entry.Collection), entry.Object);
                if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = [];
                list.Add(entry);
            }
        }

        /// <summary>A reference value as a name, enforced key, or {id}; null when it is not a reference.</summary>
        public JsonNode? RenderReference(JsonNode? value)
        {
            if (value is JsonArray arr)
            {
                if (arr.Count == 0 || !arr.All(e => e is JsonValue v && v.TryGetValue<string>(out var s) && _byId.ContainsKey(s))) return null;
                return new JsonArray(arr.Select(e => RenderOne(e!.ToString())).ToArray());
            }
            return value is JsonValue jv && jv.TryGetValue<string>(out var id) && _byId.ContainsKey(id) ? RenderOne(id) : null;
        }

        /// <summary>Every object in a collection as (display name, the YAML a reference to it is written as).</summary>
        public IEnumerable<(string Label, string Value)> Candidates(string collection) =>
            _byId.Where(e => e.Value.Collection == collection)
                .Select(e => (ConfigState.DisplayName(ConfigCollections.Get(collection), e.Value.Object), RenderOne(e.Key) switch
                {
                    JsonValue v => Scalar(v),
                    var node => Flow(node),
                }))
                .OrderBy(c => c.Item1, StringComparer.OrdinalIgnoreCase);

        private JsonNode RenderOne(string id)
        {
            var (collection, obj) = _byId[id];
            var name = ConfigState.DisplayName(ConfigCollections.Get(collection), obj);
            // Same name in the same collection: the enforced key, else the ID.
            var sameCollection = _byName.GetValueOrDefault(name)?.Count(e => e.Collection == collection) ?? 0;
            if (sameCollection <= 1) return JsonValue.Create(name)!;
            if (collection == "networkconf" && obj["vlan"] is JsonNode vlan) return new JsonObject { ["vlan"] = vlan.DeepClone() };
            if (collection is "device" or "user" && obj["mac"] is JsonNode mac) return new JsonObject { ["mac"] = mac.DeepClone() };
            return new JsonObject { ["id"] = id };
        }

        public JsonNode? ResolveReference(JsonNode? value, string key, string? target, int line, YamlParseResult result, string fileName)
        {
            switch (value)
            {
                case null:
                    return null;
                case JsonObject o when o["id"] is JsonValue idv:
                    var id = idv.ToString();
                    if (!_byId.ContainsKey(id)) result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: no object has the ID {id}."));
                    return id;
                case JsonObject o when o["vlan"] is JsonNode vlan:
                    var nets = _byId.Values.Where(e => e.Collection == "networkconf" && e.Object["vlan"]?.ToString() == vlan.ToString()).ToList();
                    if (nets.Count == 1) return ConfigState.IdOf(nets[0].Object);
                    result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: no network has VLAN {vlan}."));
                    return null;
                case JsonObject o when o["mac"] is JsonNode mac:
                    var byMac = _byId.Values.Where(e => string.Equals(e.Object["mac"]?.ToString(), mac.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();
                    if (byMac.Count > 0) return ConfigState.IdOf(byMac[0].Object);
                    result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: no device or client has MAC {mac}."));
                    return null;
                case JsonValue v when v.TryGetValue<string>(out var name):
                    if (_byId.ContainsKey(name)) return name;
                    var matches = (_byName.GetValueOrDefault(name) ?? []).Where(m => target == null || m.Collection == target).ToList();
                    if (matches.Count == 1) return ConfigState.IdOf(matches[0].Object);
                    if (matches.Count == 0)
                    {
                        result.Problems.Add(new YamlProblem("Error", fileName, line, $"{key}: nothing is named {name}."));
                        return value.DeepClone();
                    }
                    var suggestion = matches.All(m => m.Collection == "networkconf")
                        ? $" Use {{vlan: {matches[0].Object["vlan"]}}}, the network's VLAN ID, which UniFi keeps unique."
                        : " Use {id: ...} to say which one.";
                    var noun = ConfigCollections.Get(matches[0].Collection)?.Noun ?? "Object";
                    result.Problems.Add(new YamlProblem("Error", fileName, line,
                        $"{noun} {name} is ambiguous: {matches.Count} share the name" + (matches.All(m => m.Collection == "networkconf")
                            ? $" (VLAN {string.Join(", VLAN ", matches.Select(m => m.Object["vlan"]?.ToString() ?? "none"))})." : ".") + suggestion));
                    return value.DeepClone();
                default:
                    return value.DeepClone();
            }
        }
    }
}
