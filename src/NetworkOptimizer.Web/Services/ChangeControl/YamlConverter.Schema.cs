using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.ChangeControl;

public static partial class YamlConverter
{
    private const int MaxEnumValues = 12;

    // Name tokens of free-text fields: their distinct values are not a choice list. Whole tokens
    // only, so ip_version stays an enum; address values are caught by AddressValue instead.
    private static readonly HashSet<string> FreeTextTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "desc", "comment", "note", "host", "hostname", "domain", "url", "filename",
        "username", "password", "passphrase", "psk", "secret", "members", "alias", "aliases", "ssid", "cert", "token",
    };

    private static readonly Regex AddressValue = new(
        @"^(\d{1,3}(\.\d{1,3}){3}(/\d+)?|[0-9a-f]{2}(:[0-9a-f]{2}){5}|[0-9a-f:]*:[0-9a-f:]+(/\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Completion data for the Edit tab, from the state being edited: each collection's fields in
    /// the order the YAML writes them, the choices seen for enum-like fields, and every reference
    /// target as (name, YAML value). Secret fields never contribute values.
    /// </summary>
    public static JsonObject EditSchema(ConfigState state, IEnumerable<ConfigCollectionDef> defs)
    {
        var index = new ReferenceIndex(state);
        var collections = new JsonObject();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var def in defs)
        {
            var shape = new ShapeBuilder();
            foreach (var o in state.Get(def.Key))
                shape.Add(def, o, top: true);
            var json = shape.ToJson(def, targets);
            // The editor offers its remove button only where a delete can be written.
            if (def.Write?.CanDelete != true) json["readOnly"] = true;
            // ...and never on an object no plan deletes, by the ID the editor writes on its first line.
            var keep = state.Get(def.Key).Where(o => DeleteCascade.CannotDelete(def, o) || ConfigCollections.OwnedBy(def, o) != null)
                .Select(o => (JsonNode?)ConfigState.IdOf(o)).ToArray();
            if (keep.Length > 0) json["undeletable"] = new JsonArray(keep);
            // ...and read-only, by ID, with who manages it, on UniFi's own and Network Optimizer's objects.
            var managed = new JsonObject();
            foreach (var o in state.Get(def.Key))
                if (ConfigCollections.ManagedBy(def, o) is { } who) managed[ConfigState.IdOf(o)] = who;
            if (managed.Count > 0) json["managed"] = managed;
            collections[YamlName(def)] = json;
        }

        var refs = new JsonObject();
        foreach (var target in targets)
        {
            var lan = target.EndsWith(LanRefSuffix, StringComparison.Ordinal);
            var collection = lan ? target[..^LanRefSuffix.Length] : target;
            refs[target] = new JsonArray(index.Candidates(collection, lan ? IsLanNetwork : null)
                .Select(c =>
                {
                    var item = new JsonObject { ["label"] = c.Label, ["value"] = c.Value };
                    if (c.Detail != null) item["detail"] = c.Detail;
                    return (JsonNode?)item;
                }).ToArray());
        }
        return new JsonObject { ["collections"] = collections, ["refs"] = refs };
    }

    // Fields that carry a LAN network (a WLAN's network, a port profile's native, voice, and tagged
    // networks). WANs and VPN networks are not valid there, so the editor does not offer them.
    private static readonly HashSet<string> LanNetworkFields = new(StringComparer.Ordinal)
    {
        "networkconf_id", "native_networkconf_id", "voice_networkconf_id", "tagged_networkconf_ids",
        "excluded_networkconf_ids", "multicast_router_networkconf_ids", "mgmt_network_id",
    };

    private const string LanRefSuffix = ":lan";

    private static string RefKey(string field, string target) =>
        target == "networkconf" && LanNetworkFields.Contains(field) ? target + LanRefSuffix : target;

    private static bool IsLanNetwork(JsonObject o) =>
        o["purpose"]?.ToString() is "corporate" or "guest" or "vlan-only";

    private sealed class ShapeBuilder
    {
        private readonly List<string> _order = [];
        private readonly Dictionary<string, FieldInfo> _fields = new(StringComparer.Ordinal);

        private sealed class FieldInfo
        {
            /// <summary>The UniFi field name before reference stripping (networkconf_id for network).</summary>
            public string Original = "";
            public string? Target;
            public bool Secret;
            public bool List;
            public bool HasNonBool;
            public int Bools;
            public HashSet<string> Strings { get; } = new(StringComparer.Ordinal);
            public bool TooMany;
            public ShapeBuilder? Shape;
        }

        public void Add(ConfigCollectionDef def, JsonObject o, bool top)
        {
            foreach (var (_, key, value) in Ordered(def, o, top))
            {
                if (top && (key is "_id" or "id" or "site_id" || key == ConfigCollections.DeviceMetaField || def.IsReadOnly(o, key) && key is not "key")) continue;
                if (ConfigDiffEngine.IsChurnField(key)) continue;
                var outKey = key;
                string? target = null;
                if (StrippedName(key) is { } stripped && !o.ContainsKey(stripped))
                {
                    outKey = stripped;
                    target = TargetCollection(key);
                }
                if (!_fields.TryGetValue(outKey, out var f))
                {
                    _fields[outKey] = f = new FieldInfo { Original = key };
                    _order.Add(outKey);
                }
                f.Target ??= target;
                if (ConfigCollections.IsSecretField(def, key)) { f.Secret = true; continue; }
                Observe(def, f, value);
            }
        }

        private static void Observe(ConfigCollectionDef def, FieldInfo f, JsonNode? value)
        {
            switch (value)
            {
                case JsonArray arr:
                    f.List = true;
                    foreach (var e in arr)
                    {
                        if (e is JsonObject eo) (f.Shape ??= new ShapeBuilder()).Add(def, eo, top: false);
                        else Note(f, e as JsonValue);
                    }
                    break;
                case JsonObject obj:
                    (f.Shape ??= new ShapeBuilder()).Add(def, obj, top: false);
                    break;
                case JsonValue v:
                    Note(f, v);
                    break;
            }
        }

        private static void Note(FieldInfo f, JsonValue? v)
        {
            if (v == null) return;
            if (v.TryGetValue<bool>(out _)) { f.Bools++; return; }
            f.HasNonBool = true;
            if (f.TooMany || !v.TryGetValue<string>(out var s)) return;
            f.Strings.Add(s);
            if (f.Strings.Count > MaxEnumValues) f.TooMany = true;
        }

        public JsonObject ToJson(ConfigCollectionDef def, HashSet<string> targets, bool top = true)
        {
            // Fields arrive in first-seen order across objects; suggest them in the YAML's field order.
            var rank = (top ? def.FieldGroups.SelectMany(g => g.Fields) : [def.NameField, .. def.NestedFieldOrder])
                .Select((field, i) => (field, i)).GroupBy(p => p.field).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
            var fields = new JsonArray();
            foreach (var name in _order.OrderBy(n => rank.GetValueOrDefault(_fields[n].Original, int.MaxValue)))
            {
                var f = _fields[name];
                var field = new JsonObject { ["name"] = name };
                if (f.Target != null && ConfigCollections.Get(f.Target) is { } targetDef)
                {
                    var refKey = RefKey(f.Original, f.Target);
                    field["ref"] = refKey;
                    field["detail"] = f.List ? $"{targetDef.Noun} list" : targetDef.Noun;
                    targets.Add(refKey);
                }
                else if (f.Shape != null)
                {
                    field["shape"] = f.Shape.ToJson(def, targets, top: false);
                    field["detail"] = f.List ? "list" : "object";
                }
                else if (f.Bools > 0 && !f.HasNonBool)
                {
                    field["values"] = new JsonArray("true", "false");
                    field["detail"] = "true / false";
                }
                else if (!f.Secret && IsEnum(name, f))
                {
                    field["values"] = new JsonArray(f.Strings.Order(StringComparer.OrdinalIgnoreCase).Select(s => (JsonNode?)s).ToArray());
                    if (f.List) field["detail"] = "list";
                }
                else if (f.Secret)
                {
                    field["detail"] = "secret";
                }
                if (f.Secret) field["secret"] = true;
                fields.Add(field);
            }
            return new JsonObject { ["fields"] = fields };
        }

        private static bool IsEnum(string name, FieldInfo f) =>
            !f.TooMany && f.Strings.Count > 0 && !name.Split('_').Any(FreeTextTokens.Contains)
            && !f.Strings.Any(s => AddressValue.IsMatch(s) || s.Length > 40);
    }
}
