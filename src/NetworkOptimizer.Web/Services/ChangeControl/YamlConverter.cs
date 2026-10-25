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
    public static string Export(ConfigState state, IEnumerable<string>? collections, bool includeSecrets)
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
                WriteObject(sb, def, o, index, includeSecrets, indent: 2, listItem: true, withId: ambiguous.Contains(o), top: true);
        }
        return sb.ToString();
    }

    private static string Identity(ConfigCollectionDef def, JsonObject o) =>
        def.EnforcedKey?.Invoke(o, new ConfigIdentityContext(new Dictionary<string, string>())) ?? ConfigState.DisplayName(def, o);

    private static void WriteObject(StringBuilder sb, ConfigCollectionDef def, JsonObject o, ReferenceIndex index, bool includeSecrets,
        int indent, bool listItem, bool withId, bool top)
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
        foreach (var (key, value) in Ordered(def, o))
        {
            if (top && (key is "_id" or "id" or "site_id" || key == ConfigCollections.DeviceMetaField || def.ReadOnlyFields.Contains(key) && key is not "key")) continue;
            if (ConfigDiffEngine.IsChurnField(key)) continue;
            var outKey = key;
            JsonNode? outValue = value;
            if (ConfigCollections.IsSecretField(def, key) && !includeSecrets)
                outValue = PlanBuilder.KeepSecret;
            else if (StrippedName(key) is { } stripped && !o.ContainsKey(stripped) && index.RenderReference(value) is { } rendered)
            {
                outKey = stripped;
                outValue = rendered;
            }
            Prefix();
            WriteValue(sb, def, outKey, outValue, index, includeSecrets, indent);
        }
        if (first) { Prefix(); sb.Append("{}\n"); }
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Ordered(ConfigCollectionDef def, JsonObject o)
    {
        // The name field first, then the rest in stored (sorted) order, as the mockup reads.
        var nameField = def.NameField;
        if (o.TryGetPropertyValue(nameField, out var n)) yield return new(nameField, n);
        foreach (var p in o) if (p.Key != nameField) yield return p;
    }

    private static void WriteValue(StringBuilder sb, ConfigCollectionDef def, string key, JsonNode? value, ReferenceIndex index, bool includeSecrets, int indent)
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
                foreach (var e in arr)
                {
                    if (e is JsonObject eo) WriteObject(sb, def, eo, index, includeSecrets, indent + 4, listItem: true, withId: false, top: false);
                    else sb.Append(' ', indent + 2).Append("- ").Append(e == null ? "null" : Flow(e)).Append('\n');
                }
                break;
            case JsonObject obj when obj.Count == 0:
                sb.Append(" {}\n");
                break;
            case JsonObject obj when obj.Count <= 2 && obj.All(p => p.Value is JsonValue):
                sb.Append(' ').Append(Flow(obj)).Append('\n');
                break;
            case JsonObject obj:
                sb.Append('\n');
                WriteObject(sb, def, obj, index, includeSecrets, indent + 2, listItem: false, withId: false, top: false);
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
        if (s == PlanBuilder.KeepSecret) return s;
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
        // Objects in the file without an ID get a temporary one so they can be referenced.
        var tempId = 0;
        foreach (var (_, _, obj) in raw)
            if (!obj.ContainsKey("_id") && obj["id"] is JsonValue idv && idv.TryGetValue<string>(out var given)) { obj["_id"] = given; obj.Remove("id"); }
            else if (!obj.ContainsKey("_id")) obj["_id"] = $"new-{++tempId}";

        var index = new ReferenceIndex(desired);
        var fieldMap = ReferenceFields(baseState);
        foreach (var (def, map, obj) in raw)
        {
            var resolved = ResolveObject(def, obj, map, index, fieldMap, result, fileName);
            result.Collections[def.Key].Add(resolved);
        }
        foreach (var (_, _, obj) in raw.Where(r => ConfigState.IdOf(r.Obj).StartsWith("new-", StringComparison.Ordinal)))
            obj.Remove("_id");
        foreach (var list in result.Collections.Values)
            foreach (var obj in list.Where(o => (o["_id"]?.ToString() ?? "").StartsWith("new-", StringComparison.Ordinal)))
                obj.Remove("_id");

        Lint(result, baseState, raw, fileName);
        return result;
    }

    private static int Line(YamlNode node) => (int)node.Start.Line;

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

        // An object removed from the file is deleted; warn when something still refers to it.
        foreach (var (key, list) in result.Collections)
        {
            var def = ConfigCollections.Get(key);
            var kept = list.Select(ConfigState.IdOf).ToHashSet();
            foreach (var removed in baseState.Get(key).Where(o => !kept.Contains(ConfigState.IdOf(o))))
            {
                if (def?.Key == "user" && !ConfigDiffEngine.HasClientConfig(removed)) continue;
                var id = ConfigState.IdOf(removed);
                foreach (var (refCollection, objects) in result.Collections.Concat(baseState.Collections.Where(c => !result.Collections.ContainsKey(c.Key))))
                    foreach (var o in objects)
                        if (o.ToJsonString().Contains($"\"{id}\"", StringComparison.Ordinal) && ConfigState.IdOf(o) != id)
                        {
                            var refDef = ConfigCollections.Get(refCollection);
                            result.Problems.Add(new YamlProblem("Warning", fileName, 1,
                                $"{refDef?.Noun} {ConfigState.DisplayName(refDef, o)} references {def?.Noun.ToLowerInvariant()} {ConfigState.DisplayName(def, removed)}, which this file deletes. Review Plan will refuse the delete unless the {refDef?.Noun.ToLowerInvariant()} changes too."));
                        }
            }
        }
    }

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
                .Select(e => (ConfigState.DisplayName(ConfigCollections.Get(collection), e.Value.Object), RenderOne(e.Key) is JsonValue v ? Scalar(v) : Flow(RenderOne(e.Key))))
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
