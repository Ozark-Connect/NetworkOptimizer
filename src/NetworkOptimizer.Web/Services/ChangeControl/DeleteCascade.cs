using System.Text.Json.Nodes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// What deleting an object does to the objects that point at it. A reference held in a list (a WLAN's
/// AP groups, a port profile's tagged networks) is removed in the same plan, provided the list keeps
/// another entry: an emptied match list can mean "any". A single reference (a device's WLAN group, a
/// WLAN's network) has no safe default, so the person points it elsewhere and Review Plan waits.
/// </summary>
public static class DeleteCascade
{
    /// <summary>One place an ID appears: the field path, and whether it sits in a list of how many entries.</summary>
    public sealed record Hit(string Field, bool InList, int ListCount);

    /// <summary>Every place <paramref name="id"/> appears in an object (its own ID aside).</summary>
    public static List<Hit> Find(JsonNode? node, string id)
    {
        var hits = new List<Hit>();
        Walk(node, id, "", hits);
        return hits;
    }

    private static void Walk(JsonNode? node, string id, string path, List<Hit> hits)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o)
                    if (key is not "_id" and not "id") Walk(value, id, path.Length == 0 ? key : $"{path}.{key}", hits);
                break;
            case JsonArray a:
                foreach (var e in a)
                {
                    if (IsId(e, id)) hits.Add(new Hit(path, true, a.Count));
                    else Walk(e, id, path + "[]", hits);
                }
                break;
            default:
                if (IsId(node, id)) hits.Add(new Hit(path, false, 0));
                break;
        }
    }

    private static bool IsId(JsonNode? node, string id) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>The delete can take the references with it: every one is in a list that keeps another entry.</summary>
    public static bool CanCascade(IReadOnlyList<Hit> hits) => hits.Count > 0 && hits.All(h => h.InList && h.ListCount > 1);

    /// <summary>Removes <paramref name="id"/> from every list in the object. Call only when <see cref="CanCascade"/> said so.</summary>
    public static void RemoveFromLists(JsonNode? node, string id)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                    if (key is not "_id" and not "id") RemoveFromLists(value, id);
                break;
            case JsonArray a:
                for (var i = a.Count - 1; i >= 0; i--)
                {
                    if (IsId(a[i], id)) a.RemoveAt(i);
                    else RemoveFromLists(a[i], id);
                }
                break;
        }
    }

    /// <summary>
    /// Applies the cascade to a desired state: for each object the edit deletes from a deletable
    /// collection, removes it from the writable objects that hold it only in lists. Returns the
    /// collections it changed, which the plan must then include.
    /// </summary>
    public static HashSet<string> Apply(ConfigState baseState, ConfigState desired, IReadOnlyCollection<string> touched)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new List<string>();
        foreach (var key in touched)
        {
            var def = ConfigCollections.Get(key);
            if (def?.Write?.DeletePath == null) continue;
            var kept = desired.Get(key).Select(ConfigState.IdOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            deleted.AddRange(baseState.Get(key).Where(o => !kept.Contains(ConfigState.IdOf(o)) && !IsProtected(o)).Select(ConfigState.IdOf));
        }
        if (deleted.Count == 0) return changed;

        foreach (var (key, list) in desired.Collections)
        {
            var def = ConfigCollections.Get(key);
            if (def?.Write?.UpdatePath == null) continue;
            foreach (var o in list)
                foreach (var id in deleted)
                    if (CanCascade(Find(o, id)))
                    {
                        RemoveFromLists(o, id);
                        changed.Add(key);
                    }
        }
        return changed;
    }

    /// <summary>UniFi's built-in objects (the Default WLAN group, the default network) refuse deletion.</summary>
    public static bool IsProtected(JsonObject o) =>
        o["attr_no_delete"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
