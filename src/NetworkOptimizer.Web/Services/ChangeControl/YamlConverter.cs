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

    /// <summary>
    /// Splits a whole-site file at its top-level collection keys, in file order. Export writes list
    /// items at column 0 ("- name: Default"), so a line starting with "-" belongs to the open collection.
    /// </summary>
    public static List<(ConfigCollectionDef Def, string Text)> SplitByCollection(string text, List<string> unknown)
    {
        var segments = new List<(ConfigCollectionDef Def, StringBuilder Text)>();
        StringBuilder? current = null;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] is not ('#' or '-') && line.Contains(':'))
            {
                var key = line[..line.IndexOf(':')].Trim();
                if (FromYamlName(key) is not { } def) { current = null; unknown.Add(key); continue; }
                segments.Add((def, current = new StringBuilder()));
            }
            current?.Append(line).Append('\n');
        }
        return segments.Select(s => (s.Def, s.Text.ToString())).ToList();
    }

    // Where the suffix rule reads wrong: portconf_id would be "port", which next to port_idx reads as the port
    // number, and is the UI's Port Profile. Older files wrote "port"; a port override still accepts it.
    private static readonly Dictionary<string, string> NamedReferences = new(StringComparer.Ordinal) { ["portconf_id"] = "port_profile" };

    /// <summary>The stripped key of a reference field, or null when the field is not a reference by name.</summary>
    public static string? StrippedName(string field)
    {
        if (NamedReferences.TryGetValue(field, out var named)) return named;
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

    /// <summary>
    /// Writes the given collections (all when null), with secrets as <paramref name="secrets"/> says.
    /// <paramref name="ids"/> writes every object's ID, for the editor: a rename then stays an update.
    /// Files meant for another site leave them out; there only ambiguous names carry one.
    /// </summary>
    public static string Export(ConfigState state, IEnumerable<string>? collections, SecretMode secrets, bool ids = false)
    {
        var index = new ReferenceIndex(state);
        var sb = new StringBuilder();
        var keys = collections?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var def in ConfigCollections.All)
        {
            if (keys != null && !keys.Contains(def.Key)) continue;
            if (!state.Collections.ContainsKey(def.Key)) continue;
            var objects = state.Get(def.Key).Where(o => def.Key != "user" || ConfigDiffEngine.HasClientConfig(o)).ToList();
            // Grouped by zone pair, in UniFi's zone order (the matrix's), each group under a comment
            // naming it, and in index order within it: the file omits the read-only index, so a parsed
            // policy's order is its place in the file (PlanBuilder.AddPolicyOrder). Stored order differs,
            // and every zone pair then read as reordered.
            Func<JsonObject, string?>? pairOf = null;
            if (def.Key == "firewall_policy")
            {
                var zones = state.Get("firewall_zone").Select((z, i) => (Id: ConfigState.IdOf(z), Name: z["name"]?.ToString() ?? ConfigState.IdOf(z), i))
                    .GroupBy(z => z.Id).ToDictionary(g => g.Key, g => g.First());
                (int, string) Zone(JsonObject o, string side) => o[side]?["zone_id"]?.ToString() is { } id && zones.TryGetValue(id, out var z)
                    ? (z.i, z.Name) : (int.MaxValue, o[side]?["zone_id"]?.ToString() ?? "?");
                objects = objects.OrderBy(o => Zone(o, "source").Item1).ThenBy(o => Zone(o, "destination").Item1)
                    .ThenBy(o => o["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue).ToList();
                pairOf = o => $"{Zone(o, "source").Item2} → {Zone(o, "destination").Item2}";
            }
            sb.Append(YamlName(def)).Append(':');
            if (objects.Count == 0) { sb.Append(" []\n"); continue; }
            sb.Append('\n');
            var ambiguous = objects.GroupBy(o => Identity(def, o)).Where(g => g.Count() > 1).SelectMany(g => g).ToHashSet();
            string? pair = null;
            foreach (var o in objects)
            {
                if (pairOf?.Invoke(o) is { } p && p != pair) sb.Append("# ").Append(pair = p).Append('\n');
                WriteObject(sb, def, o, index, secrets, indent: 2, listItem: true, withId: ids || ambiguous.Contains(o), top: true, path: ConfigState.IdOf(o));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="to"/> with each collection in <paramref name="from"/>'s object order, by ID; objects new in
    /// <paramref name="to"/> follow, in their own order. For a line diff of two states: UniFi moves an updated
    /// object in its stored list, and diffed by position, one object's change read as two objects swapped.
    /// Firewall policies keep their own order, which Export writes by index: there the order is the change.
    /// </summary>
    public static ConfigState InOrderOf(ConfigState to, ConfigState from)
    {
        var aligned = to.Clone();
        foreach (var key in aligned.Collections.Keys.ToList())
        {
            if (key == "firewall_policy" || !from.Collections.TryGetValue(key, out var before)) continue;
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < before.Count; i++) rank.TryAdd(ConfigState.IdOf(before[i]), i);
            aligned.Collections[key] = aligned.Collections[key].Select((o, i) => (o, i))
                .OrderBy(x => rank.TryGetValue(ConfigState.IdOf(x.o), out var r) ? r : int.MaxValue).ThenBy(x => x.i)
                .Select(x => x.o).ToList();
        }
        return aligned;
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
            if (top && (key is "_id" or "id" or "site_id" || key == ConfigCollections.DeviceMetaField || def.IsReadOnly(o, key) && key is not "key")) continue;
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
            else if (StrippedName(key) is { } stripped && !o.ContainsKey(stripped) && index.RenderReference(value, TargetCollection(key)) is { } rendered)
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
            // Keys in Ordered's order, not UniFi's: UniFi reorders them between versions, which reads as a change.
            case JsonObject obj when obj.Count <= 2 && obj.All(p => p.Value is JsonValue && !ConfigCollections.IsSecretField(def, p.Key) && StrippedName(p.Key) == null):
                sb.Append(" {").Append(string.Join(", ", Ordered(def, obj, top: false).Select(f => $"{KeyText(f.Key)}: {Scalar((JsonValue)f.Value!)}"))).Append("}\n");
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
    /// creates. <paramref name="declared"/> adds objects other files of the same edit create.
    /// A new object keeps a temporary ID (<see cref="TempIdPrefix"/>, stable for the same text), which
    /// Review Plan turns into a reference to its create.
    /// </summary>
    /// <param name="edited">The whole edit's state (every file applied): a delete is checked against what the other files leave referring to it, not the base.</param>
    public static YamlParseResult Parse(string text, ConfigState baseState, string fileName, ConfigState? declared = null, ConfigState? edited = null)
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
        // An id: line counts only for an object of that collection in the base, once. A copied block or
        // an ID from another site falls back to matching by name below.
        var givenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (def, _, obj) in raw)
        {
            if (obj["id"] is JsonValue idv && idv.TryGetValue<string>(out var given)
                && baseState.Get(def.Key).Any(b => string.Equals(ConfigState.IdOf(b), given, StringComparison.OrdinalIgnoreCase))
                && givenIds.Add(given))
                obj["_id"] = given;
            obj.Remove("id");
        }
        // An object without an ID that matches one in the base (enforced key, then name, as Review Plan
        // matches) takes that object's ID, so references to it in this file resolve to the real object.
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
        // Only new objects are left: a temporary ID, unique to this file, so the edit can refer to what it creates.
        var tempId = 0;
        foreach (var (_, _, obj) in raw)
            if (!obj.ContainsKey("_id")) obj["_id"] = $"{TempIdPrefix}{fileName}#{++tempId}";

        var indexState = desired;
        if (declared != null)
        {
            indexState = desired.Clone();
            var present = desired.ObjectsById();
            foreach (var (key, list) in declared.Collections)
            {
                var extra = list.Where(o => !present.ContainsKey(ConfigState.IdOf(o))).ToList();
                if (extra.Count > 0) indexState.Collections[key] = [.. indexState.Get(key), .. extra];
            }
        }
        var index = new ReferenceIndex(indexState);
        var fieldMap = ReferenceFields(baseState);
        foreach (var (def, map, obj) in raw)
        {
            var resolved = ResolveObject(def, obj, map, index, fieldMap, result, fileName);
            ResolveSecretTokens(def, resolved, baseState, Line(map), result, fileName);
            // A new object UniFi generates is never created by Review Plan (an older file's own predefined set), so
            // the create checks do not apply to it; Lint counts it in the generated-objects Info line.
            var created = ConfigState.IdOf(resolved).StartsWith(TempIdPrefix, StringComparison.Ordinal) && def.IsGenerated?.Invoke(resolved) != true;
            if (created)
                foreach (var (missing, refused) in NewObjects.Missing(def, resolved))
                {
                    // Named as the file writes it: networkconf_id is "network", source.zone_id is "source.zone".
                    var parts = missing.Split('.').Select(p => StrippedName(p) ?? p).ToArray();
                    YamlNode at = map;
                    YamlMappingNode? level = map;
                    foreach (var part in parts)
                    {
                        var entry = level?.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == part);
                        if (entry?.Key == null) break;
                        at = entry.Value.Key;
                        level = entry.Value.Value as YamlMappingNode;
                    }
                    var field = string.Join(".", parts);
                    result.Problems.Add(refused
                        ? new YamlProblem("Error", fileName, Line(at), $"A new {def.Noun} needs {field}.")
                        : new YamlProblem("Warning", fileName, Line(at), $"A new {def.Noun} without {field} is created, but does nothing until it has one."));
                }
            if (created)
                foreach (var (path, message) in NewObjects.Refused(def, resolved))
                {
                    var keyNode = map.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == path).Key;
                    result.Problems.Add(new YamlProblem("Error", fileName, Line(keyNode ?? map), message));
                }
            result.Collections[def.Key].Add(resolved);
        }

        Lint(result, baseState, raw, fileName, editedState: edited);
        return result;
    }

    /// <summary>Prefix of a temporary ID for an object an edit creates. No UniFi value starts with it, so a plan can refuse one.</summary>
    public const string TempIdPrefix = "@new:";

    /// <summary>
    /// Gives each object with a temporary ID a permanent one, and rewrites references to it. For a
    /// state that is stored (a template): temporary IDs are only unique within one edit.
    /// </summary>
    public static void AssignPermanentIds(ConfigState state)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var list in state.Collections.Values)
            foreach (var obj in list)
                if (ConfigState.IdOf(obj) is { } id && id.StartsWith(TempIdPrefix, StringComparison.Ordinal))
                    map[id] = Guid.NewGuid().ToString("N")[..24];
        if (map.Count == 0) return;
        foreach (var (key, list) in state.Collections)
            state.Collections[key] = list.Select(o => (JsonObject)Rewrite(o)!).ToList();

        JsonNode? Rewrite(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key, Rewrite(p.Value)))),
            JsonArray a => new JsonArray(a.Select(Rewrite).ToArray()),
            JsonValue v when v.TryGetValue<string>(out var s) && map.TryGetValue(s, out var real) => JsonValue.Create(real),
            _ => node?.DeepClone(),
        };
    }

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
    /// <summary>The edited object with each <c>&lt;keep current&gt;</c> secret replaced by the stored value at the same place.</summary>
    private static JsonObject KeepingSecrets(ConfigCollectionDef def, JsonObject before, JsonObject edited)
    {
        var copy = (JsonObject)edited.DeepClone();
        Fill(copy, before);
        return copy;

        void Fill(JsonNode? e, JsonNode? b)
        {
            switch (e, b)
            {
                case (JsonObject eo, JsonObject bo):
                    foreach (var key in eo.Select(p => p.Key).ToList())
                    {
                        if (ConfigCollections.IsSecretField(def, key) && eo[key] is JsonValue v && v.TryGetValue<string>(out var s)
                            && s == PlanBuilder.KeepSecret && bo.ContainsKey(key))
                            eo[key] = bo[key]?.DeepClone();
                        else
                            Fill(eo[key], bo[key]);
                    }
                    break;
                case (JsonArray ea, JsonArray ba):
                    for (var i = 0; i < Math.Min(ea.Count, ba.Count); i++) Fill(ea[i], ba[i]);
                    break;
            }
        }
    }

    internal static string? TargetCollection(string field) => field switch
    {
        // Before "network": client group references (network_members_group_ids) are not networks.
        _ when field.Contains("network_members_group", StringComparison.Ordinal) => "network_members_group",
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
        ["port_profile"] = "portconf_id",
    };

    /// <summary>Reference field names seen in the base state, by stripped name, per collection.</summary>
    private static Dictionary<string, Dictionary<string, string>> ReferenceFields(ConfigState state)
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        // Learned only from values that are object IDs: a field that merely ends in _id (an LLDP neighbor's port_id,
        // "local Port 1") claimed the name first, and a port profile (portconf_id, written "port") was read back into it.
        var ids = state.ObjectsById().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsReference(JsonNode? v) => v switch
        {
            JsonValue s when s.TryGetValue<string>(out var text) => ids.Contains(text),
            JsonArray a => a.Any(e => e is JsonValue s && s.TryGetValue<string>(out var text) && ids.Contains(text)),
            _ => false,
        };
        void Walk(string collection, JsonNode? node)
        {
            if (node is JsonObject o)
                foreach (var (k, v) in o)
                {
                    if (StrippedName(k) is { } s && IsReference(v))
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
                // Files written before port_profile named a port override's profile "port". Only there: a firewall
                // policy's port is a real field.
                var lookup = key == "port" && o.ContainsKey("port_idx") && !o.ContainsKey("port_profile") ? "port_profile" : key;
                if (key is "_id" or "id" || StrippedName(key) != null || !known.TryGetValue(lookup, out var original))
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
                if (LanNetworkFields.Contains(original))
                    foreach (var id in output[original] is JsonArray ids ? ids.Select(i => i?.ToString()) : [output[original]?.ToString()])
                        if (id != null && index.Network(id) is { } network && network["purpose"] != null && !IsLanNetwork(network))
                            result.Problems.Add(new YamlProblem("Warning", fileName, line,
                                $"{key}: {network["name"]} is not a LAN network ({network["purpose"]}). Only corporate, guest, and VLAN-only networks work here; the UniFi Console rejects it."));
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
    private static void Lint(YamlParseResult result, ConfigState baseState, List<(ConfigCollectionDef Def, YamlMappingNode Node, JsonObject Obj)> raw, string fileName, ConfigState? editedState)
    {
        var baseById = baseState.ObjectsById();
        foreach (var (def, node, obj) in raw.Where(r => r.Def.Key == "wlanconf"))
        {
            // Only where the edit turns it on, not on every WLAN already using it.
            static bool On(JsonObject? o) => o?["wpa3_transition"] is JsonValue t && t.TryGetValue<bool>(out var tr) && tr;
            if (On(obj) && !(baseById.TryGetValue(ConfigState.IdOf(obj), out var before) && On(before.Object)))
            {
                var keyNode = node.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == "wpa3_transition").Key;
                result.Problems.Add(new YamlProblem("Info", fileName, keyNode == null ? Line(node) : Line(keyNode),
                    $"WPA3 transition on {ConfigState.DisplayName(def, obj)}: some IoT devices cannot join."));
            }
        }

        // The plan refuses the same subnet (PlanBuilder.SubnetProblem); here it gets its own line, and
        // ValidateYamlAsync drops the plan's copy filed at line 1.
        foreach (var (def, node, obj) in raw.Where(r => r.Def.Key == "networkconf"))
        {
            var was = baseById.TryGetValue(ConfigState.IdOf(obj), out var before) ? before.Object["ip_subnet"]?.ToString() : null;
            if (obj["ip_subnet"]?.ToString() == was || PlanBuilder.SubnetProblem(obj) is not { } problem) continue;
            var keyNode = node.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == "ip_subnet").Key;
            result.Problems.Add(new YamlProblem("Error", fileName, keyNode == null ? Line(node) : Line(keyNode),
                $"{def.Noun} {ConfigState.DisplayName(def, obj)}: {problem}"));
        }
        foreach (var (def, node, obj) in raw.Where(r => r.Def.Key == "setting" && r.Obj["key"]?.ToString() == "doh"))
        {
            var keyNode = node.Children.FirstOrDefault(c => ((YamlScalarNode)c.Key).Value == "custom_servers").Key;
            foreach (var problem in PlanBuilder.DohServerNameProblems(obj))
                result.Problems.Add(new YamlProblem("Error", fileName, keyNode == null ? Line(node) : Line(keyNode),
                    $"{def.Noun} {ConfigState.DisplayName(def, obj)}: {problem}"));
        }

        // UniFi's own and Network Optimizer's objects are read-only here; the editor locks them, and an
        // upload or a pasted file is held to the same rule.
        // Compared as resolved (zone names to IDs); raw only gives each object's line.
        var resolved = result.Collections.SelectMany(c => c.Value.Select(o => (Key: c.Key, Id: ConfigState.IdOf(o), Obj: o)))
            .GroupBy(x => (x.Key, x.Id)).ToDictionary(g => g.Key, g => g.First().Obj);
        // By collection and ID: a mirror collection (v2 RADIUS profiles) shares its IDs with v1.
        var baseObjects = baseState.Collections.SelectMany(c => c.Value.Select(o => (Key: c.Key, Id: ConfigState.IdOf(o), Obj: o)))
            .GroupBy(x => (x.Key, x.Id)).ToDictionary(g => g.Key, g => g.First().Obj);
        // Objects UniFi generates (def.IsGenerated) that the file changes, adds, or leaves out. Review Plan never writes
        // them (PlanBuilder.BuildOperation), so an older file whose zones differ is not refused for carrying its own set;
        // they get one Info line. Only IsGenerated: a predefined object in another collection is still refused below.
        var generated = new Dictionary<ConfigCollectionDef, int>();
        void Generated(ConfigCollectionDef def) => generated[def] = generated.GetValueOrDefault(def) + 1;
        foreach (var (def, node, obj) in raw)
        {
            var id = ConfigState.IdOf(obj);
            if (!baseObjects.TryGetValue((def.Key, id), out var before))
            {
                if (def.IsGenerated?.Invoke(obj) == true) Generated(def);
                continue;
            }
            if (ConfigCollections.ManagedBy(def, before) is not { } who
                || !resolved.TryGetValue((def.Key, id), out var edited)
                // Shared fields only: the export leaves out fields UniFi derives, so an untouched object differs in those.
                // A downloaded file's <keep current> stands for the stored secret, as Review Plan reads it.
                || ConfigDiffEngine.SharedFieldsEqual(def, before, KeepingSecrets(def, before, edited), new Dictionary<string, string>())) continue;
            if (def.IsGenerated?.Invoke(before) == true && ConfigCollections.OwnedBy(def, before) == null)
            {
                Generated(def);
                continue;
            }
            var name = $"{def.Noun} {ConfigState.DisplayName(def, before)}";
            result.Problems.Add(new YamlProblem("Error", fileName, Line(node), who == "UniFi"
                ? $"{name} is managed by UniFi and cannot be edited here."
                : $"{name} belongs to {who} and cannot be edited here."));
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
            if (def.Write?.CanDelete != true)
            {
                result.Problems.Add(new YamlProblem("Warning", fileName, 1,
                    $"{def.DisplayName} are read-only in Network Optimizer: {Names(def, removedObjects)} will not be deleted."));
                continue;
            }
            // The owning feature re-provisions these, and Review Plan writes nothing for them (PlanBuilder): one line per
            // owner, since a feature keeps one same-named object per zone pair. One in a zone the edit deletes goes with
            // the zone (PlanBuilder.OwnedPoliciesGoWithTheirZone).
            // Against the whole edit: baseState already has the files parsed before this one, the zones file among them.
            var keptZones = (editedState ?? desired).Get("firewall_zone").Select(ConfigState.IdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Gone(JsonNode? zone) => zone?.ToString() is { Length: > 0 } z && !keptZones.Contains(z);
            bool InDeletedZone(JsonObject o) => def.Key == "firewall_policy" && (Gone(o["source"]?["zone_id"]) || Gone(o["destination"]?["zone_id"]));
            foreach (var owned in removedObjects.Where(o => ConfigCollections.OwnedBy(def, o) != null)
                         .GroupBy(o => (Owner: ConfigCollections.OwnedBy(def, o)!, WithZone: InDeletedZone(o))))
            {
                var names = owned.GroupBy(o => ConfigState.DisplayName(def, o))
                    .Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()})").ToList();
                var one = owned.Count() == 1;
                var (owner, withZone) = owned.Key;
                var subject = one ? $"{def.Noun} {names[0]} belongs" : $"{def.DisplayName} {List(names)} belong";
                result.Problems.Add(new YamlProblem("Warning", fileName, 1, withZone
                    ? $"{subject} to {owner} and {(one ? "is" : "are")} deleted with {(one ? "its" : "their")} zone; {owner} rewrites its policies for the zones left."
                    : $"{subject} to {owner} and {(one ? "is" : "are")} not deleted here."));
            }
            foreach (var removed in removedObjects)
            {
                var name = $"{def.Noun} {ConfigState.DisplayName(def, removed)}";
                if (ConfigCollections.OwnedBy(def, removed) != null) continue;
                if (def.IsGenerated?.Invoke(removed) == true)
                {
                    Generated(def);
                    continue;
                }
                if (DeleteCascade.CannotDelete(def, removed))
                {
                    result.Problems.Add(new YamlProblem("Error", fileName, 1, $"{name} is built into UniFi and cannot be deleted."));
                    continue;
                }
                var id = ConfigState.IdOf(removed);
                var cascaded = new List<string>();
                var blocking = new List<string>();
                foreach (var (refCollection, objects) in result.Collections.Concat((editedState ?? baseState).Collections.Where(c => !result.Collections.ContainsKey(c.Key))))
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
        if (generated.Count > 0)
            result.Problems.Add(new YamlProblem("Info", fileName, 1,
                $"Objects UniFi generates differ from the console ({string.Join(", ", generated.Select(g => $"{g.Key.DisplayName}: {g.Value}"))}). Review Plan leaves them to UniFi."));
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
            // Every object is named under its own collection, not through ObjectsById: a mirror collection shares
            // its IDs (v2 RADIUS profiles mirror rest/radiusprofile), and keyed by ID alone whichever collection came
            // last hid the other, so a reference targeting it named nothing. An ID belongs to the catalog's first.
            _byId = new(StringComparer.OrdinalIgnoreCase);
            var order = ConfigCollections.All.Select((d, i) => (d.Key, i)).ToDictionary(x => x.Key, x => x.i);
            foreach (var (key, list) in state.Collections.OrderBy(c => order.GetValueOrDefault(c.Key, int.MaxValue)))
            {
                var def = ConfigCollections.Get(key);
                foreach (var o in list)
                {
                    var id = ConfigState.IdOf(o);
                    if (id == ConfigState.SingleId) continue;
                    _byId.TryAdd(id, (key, o));
                    var name = ConfigState.DisplayName(def, o);
                    if (!_byName.TryGetValue(name, out var named)) _byName[name] = named = [];
                    named.Add((key, o));
                }
            }
        }

        /// <summary>A reference value as a name, enforced key, or {id}; null when it is not a reference.</summary>
        public JsonNode? RenderReference(JsonNode? value, string? target)
        {
            if (value is JsonArray arr)
            {
                if (arr.Count == 0 || !arr.All(e => e is JsonValue v && v.TryGetValue<string>(out var s) && _byId.ContainsKey(s))) return null;
                return new JsonArray(arr.Select(e => RenderOne(e!.ToString(), target)).ToArray());
            }
            return value is JsonValue jv && jv.TryGetValue<string>(out var id) && _byId.ContainsKey(id) ? RenderOne(id, target) : null;
        }

        // The objects a name resolves to for a field, as ResolveReference reads it: one per ID, so a mirror
        // collection (v2 RADIUS profiles) sharing an ID and name is not counted twice.
        private List<(string Collection, JsonObject Object)> Named(string name, string? target) =>
            (_byName.GetValueOrDefault(name) ?? []).Where(m => target == null || m.Collection == target)
                .DistinctBy(m => ConfigState.IdOf(m.Object), StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>Every object in a collection as (display name, the YAML a reference to it is written as).</summary>
        public IEnumerable<(string Label, string Value, string? Detail)> Candidates(string collection, Func<JsonObject, bool>? keep = null) =>
            _byId.Where(e => e.Value.Collection == collection && (keep == null || keep(e.Value.Object)))
                .Select(e => (ConfigState.DisplayName(ConfigCollections.Get(collection), e.Value.Object), RenderOne(e.Key, collection) switch
                {
                    JsonValue v => Scalar(v),
                    var node => Flow(node),
                }, CandidateDetail(collection, e.Value.Object)))
                .OrderBy(c => c.Item1, StringComparer.OrdinalIgnoreCase);

        /// <summary>The network with this ID, or null when the ID is not a network.</summary>
        public JsonObject? Network(string id) =>
            _byId.TryGetValue(id, out var e) && e.Collection == "networkconf" ? e.Object : null;

        // What tells two candidates apart at a glance: a network's VLAN and subnet.
        private static string? CandidateDetail(string collection, JsonObject o)
        {
            if (collection != "networkconf") return null;
            var parts = new List<string>();
            if (o["vlan"] is JsonValue vlan && vlan.ToString() is { Length: > 0 } v) parts.Add($"VLAN {v}");
            if (o["ip_subnet"] is JsonValue subnet && subnet.ToString() is { Length: > 0 } s) parts.Add(s);
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        private JsonNode RenderOne(string id, string? target)
        {
            var (collection, obj) = _byId[id];
            var name = ConfigState.DisplayName(ConfigCollections.Get(collection), obj);
            // The name only when it resolves back to this object; a field with no target collection resolves
            // across all of them (a derived traffic route's origin_id shares its name with the route). Else
            // the enforced key, else the ID.
            if (Named(name, target) is [var only] && string.Equals(ConfigState.IdOf(only.Object), id, StringComparison.OrdinalIgnoreCase))
                return JsonValue.Create(name)!;
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
                    var matches = Named(name, target);
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
