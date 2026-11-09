using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// A site's whole UniFi Network config at one moment: every collection's objects, canonicalized.
/// Built from a live read or loaded from a snapshot's blobs; the diff engine and the plan engine
/// both work on this.
/// </summary>
public sealed class ConfigState
{
    /// <summary>Synthetic ID for single-object collections.</summary>
    public const string SingleId = "_single";

    /// <summary>Objects by collection key.</summary>
    public Dictionary<string, List<JsonObject>> Collections { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collections that could not be read, with the reason.</summary>
    public Dictionary<string, string> ReadErrors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Network app version the state was read from.</summary>
    public string? NetworkAppVersion { get; set; }

    /// <summary>When it was read.</summary>
    public DateTime ReadAt { get; set; }

    /// <summary>A collection's objects; empty when absent.</summary>
    public IReadOnlyList<JsonObject> Get(string collection) =>
        Collections.TryGetValue(collection, out var list) ? list : [];

    /// <summary>UniFi ID of an object (<c>_id</c>, else <c>id</c>, else the synthetic single ID).</summary>
    public static string IdOf(JsonObject o)
    {
        foreach (var field in IdFields)
            if (o[field] is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0)
                return id;
        return SingleId;
    }

    private static readonly string[] IdFields = ["_id", "id"];

    /// <summary>Every object's display name by UniFi ID, across collections, for reference rendering.</summary>
    public Dictionary<string, string> NamesById()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in Collections)
        {
            var def = ConfigCollections.Get(key);
            foreach (var o in list)
            {
                var id = IdOf(o);
                if (id == SingleId) continue;
                map[id] = DisplayName(def, o);
            }
        }
        return map;
    }

    /// <summary>Every object by UniFi ID with its collection key, for reference resolution.</summary>
    public Dictionary<string, (string Collection, JsonObject Object)> ObjectsById()
    {
        var map = new Dictionary<string, (string, JsonObject)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, list) in Collections)
            foreach (var o in list)
            {
                var id = IdOf(o);
                if (id != SingleId) map[id] = (key, o);
            }
        return map;
    }

    /// <summary>The object's display name per its collection's name field, falling back to the ID.</summary>
    public static string DisplayName(ConfigCollectionDef? def, JsonObject o)
    {
        if (def?.Key == "device" || def?.Key == "user")
        {
            var n = o["name"]?.ToString();
            return string.IsNullOrWhiteSpace(n) ? o["mac"]?.ToString() ?? IdOf(o) : n;
        }
        var field = def?.NameField ?? "name";
        var name = o[field]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = o["name"]?.ToString();
        return string.IsNullOrWhiteSpace(name) ? IdOf(o) : name;
    }

    /// <summary>A deep copy.</summary>
    public ConfigState Clone()
    {
        var copy = new ConfigState { NetworkAppVersion = NetworkAppVersion, ReadAt = ReadAt };
        foreach (var (key, list) in Collections)
            copy.Collections[key] = list.Select(o => (JsonObject)o.DeepClone()).ToList();
        foreach (var (key, err) in ReadErrors) copy.ReadErrors[key] = err;
        return copy;
    }
}

/// <summary>Canonical JSON: object keys sorted ordinally at every depth, compact, UTF-8.</summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>A copy of the node with every object's keys sorted.</summary>
    public static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject obj => SortObject(obj),
        JsonArray arr => new JsonArray(arr.Select(Normalize).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };

    private static JsonObject SortObject(JsonObject obj)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
            sorted[key] = Normalize(value);
        return sorted;
    }

    /// <summary>Canonical text of a node.</summary>
    public static string Serialize(JsonNode? node) => Normalize(node)?.ToJsonString(Compact) ?? "null";

    /// <summary>Canonical text of a collection: objects sorted by UniFi ID, each canonical.</summary>
    public static string SerializeCollection(IEnumerable<JsonObject> objects)
    {
        var sorted = objects
            .Select(o => (Id: ConfigState.IdOf(o), Node: (JsonObject)Normalize(o)!))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => (JsonNode?)x.Node)
            .ToArray();
        return new JsonArray(sorted).ToJsonString(Compact);
    }

    /// <summary>Parses a canonical collection back into objects.</summary>
    public static List<JsonObject> DeserializeCollection(string json) =>
        (JsonNode.Parse(json) as JsonArray)?.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList() ?? [];

    /// <summary>Deep equality on canonical form.</summary>
    public static bool DeepEquals(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(Normalize(a), Normalize(b));

    /// <summary>UTF-8 byte count.</summary>
    public static int ByteCount(string s) => Encoding.UTF8.GetByteCount(s);
}
