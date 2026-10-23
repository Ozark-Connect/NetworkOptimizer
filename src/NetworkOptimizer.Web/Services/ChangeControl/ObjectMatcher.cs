using System.Text.Json.Nodes;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A pairing of one object in each of two states.</summary>
public sealed record ObjectPair(JsonObject? Left, JsonObject? Right, string MatchedBy);

/// <summary>An object that could not be matched without guessing.</summary>
public sealed record MatchConflict(string Collection, string Name, string Reason);

/// <summary>Result of matching one collection across two states.</summary>
public sealed class MatchResult
{
    public List<ObjectPair> Pairs { get; } = [];
    public List<MatchConflict> Conflicts { get; } = [];
}

/// <summary>
/// Matches objects across two states (spec "Object identity"): UniFi ID while the object still
/// exists, then the enforced key, then the name when it is unambiguous on both sides. Anything
/// left is unmatched (a create on one side, a delete on the other); an ambiguous name is a conflict.
/// </summary>
public static class ObjectMatcher
{
    /// <summary>Matches one collection.</summary>
    /// <param name="def">The collection.</param>
    /// <param name="left">Objects on the left (desired, or before).</param>
    /// <param name="right">Objects on the right (live, or after).</param>
    /// <param name="leftCtx">Name lookup for the left state.</param>
    /// <param name="rightCtx">Name lookup for the right state.</param>
    /// <param name="matchById">False when the two states come from different consoles or sites, where IDs mean nothing.</param>
    public static MatchResult Match(
        ConfigCollectionDef def, IReadOnlyList<JsonObject> left, IReadOnlyList<JsonObject> right,
        ConfigIdentityContext leftCtx, ConfigIdentityContext rightCtx, bool matchById = true)
    {
        var result = new MatchResult();
        var leftOpen = left.ToList();
        var rightOpen = right.ToList();

        void Pair(JsonObject l, JsonObject r, string by)
        {
            result.Pairs.Add(new ObjectPair(l, r, by));
            leftOpen.Remove(l);
            rightOpen.Remove(r);
        }

        if (def.Shape == CollectionShape.Single)
        {
            if (left.Count > 0 && right.Count > 0) Pair(left[0], right[0], "single");
        }

        // 1. UniFi ID.
        if (matchById)
        {
            var rightById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rightOpen) rightById.TryAdd(ConfigState.IdOf(r), r);
            foreach (var l in leftOpen.ToList())
                if (rightById.TryGetValue(ConfigState.IdOf(l), out var r) && rightOpen.Contains(r))
                    Pair(l, r, "id");
        }

        // 2. Enforced key.
        if (def.EnforcedKey != null)
        {
            var rightByKey = GroupBy(rightOpen, o => def.EnforcedKey(o, rightCtx));
            var leftByKey = GroupBy(leftOpen, o => def.EnforcedKey(o, leftCtx));
            foreach (var (key, ls) in leftByKey)
            {
                if (!rightByKey.TryGetValue(key, out var rs)) continue;
                if (ls.Count == 1 && rs.Count == 1)
                    Pair(ls[0], rs[0], "key");
                else if (def.KeyIsUnique)
                    result.Conflicts.Add(new MatchConflict(def.Key, key, "The key repeats, which UniFi should not allow"));
            }
        }

        // 3. Name, only when unambiguous on both sides.
        var rightByName = GroupBy(rightOpen, o => ConfigState.DisplayName(def, o));
        var leftByName = GroupBy(leftOpen, o => ConfigState.DisplayName(def, o));
        foreach (var (name, ls) in leftByName)
        {
            if (!rightByName.TryGetValue(name, out var rs)) continue;
            if (ls.Count == 1 && rs.Count == 1)
                Pair(ls[0], rs[0], "name");
            else
                result.Conflicts.Add(new MatchConflict(def.Key, name,
                    $"{ls.Count} on one side and {rs.Count} on the other share the name"));
        }

        foreach (var l in leftOpen) result.Pairs.Add(new ObjectPair(l, null, "none"));
        foreach (var r in rightOpen) result.Pairs.Add(new ObjectPair(null, r, "none"));
        return result;
    }

    private static Dictionary<string, List<JsonObject>> GroupBy(IEnumerable<JsonObject> objects, Func<JsonObject, string?> key)
    {
        var map = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            var k = key(o);
            if (string.IsNullOrEmpty(k)) continue;
            if (!map.TryGetValue(k, out var list)) map[k] = list = [];
            list.Add(o);
        }
        return map;
    }
}
