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

        // 1. UniFi ID, then the legacy_id Network 11 keeps of a 10.x ObjectId it replaced with a UUID.
        if (matchById)
        {
            var rightById = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rightOpen) rightById.TryAdd(ConfigState.IdOf(r), r);
            foreach (var l in leftOpen.ToList())
                if (rightById.TryGetValue(ConfigState.IdOf(l), out var r) && rightOpen.Contains(r))
                    Pair(l, r, "id");

            var rightByLegacy = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rightOpen)
                if (r[LegacyIdField]?.ToString() is { Length: > 0 } legacy) rightByLegacy.TryAdd(legacy, r);
            foreach (var l in leftOpen.ToList())
                if (rightByLegacy.TryGetValue(ConfigState.IdOf(l), out var r) && rightOpen.Contains(r))
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

    /// <summary>Network 11's copy of the 10.x ObjectId an object had before the UUID migration.</summary>
    public const string LegacyIdField = "legacy_id";

    /// <summary>
    /// Matches every collection both states hold, then pairs objects that share a name or key by their
    /// content, with references mapped through the pairs already made. Same-named objects (Network
    /// Optimizer's per-zone policies) are only told apart that way once their IDs change, as in the
    /// Network 11 UUID migration. Returns the matches and the left-to-right ID map.
    /// </summary>
    public static (Dictionary<string, MatchResult> Matches, Dictionary<string, string> IdMap) MatchAll(
        ConfigState left, ConfigState right, ConfigIdentityContext leftCtx, ConfigIdentityContext rightCtx, bool matchById,
        Func<ConfigCollectionDef, IReadOnlyList<JsonObject>, IReadOnlyList<JsonObject>> relevant)
    {
        var matches = new Dictionary<string, MatchResult>(StringComparer.OrdinalIgnoreCase);
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in ConfigCollections.All)
        {
            if (!left.Collections.ContainsKey(def.Key) || !right.Collections.ContainsKey(def.Key)) continue;
            var match = Match(def, relevant(def, left.Get(def.Key)), relevant(def, right.Get(def.Key)), leftCtx, rightCtx, matchById);
            matches[def.Key] = match;
            AddToMap(match, idMap);
        }

        foreach (var def in ConfigCollections.All)
            if (matches.TryGetValue(def.Key, out var match) && match.Conflicts.Count > 0)
            {
                PairByContent(def, match, idMap);
                AddToMap(match, idMap);
            }
        return (matches, idMap);
    }

    private static void AddToMap(MatchResult match, Dictionary<string, string> idMap)
    {
        foreach (var p in match.Pairs.Where(p => p.Left != null && p.Right != null))
            idMap[ConfigState.IdOf(p.Left!)] = ConfigState.IdOf(p.Right!);
    }

    /// <summary>Pairs the unmatched objects behind each conflict whose shared fields are equal; a conflict fully paired is cleared.</summary>
    private static void PairByContent(ConfigCollectionDef def, MatchResult match, IReadOnlyDictionary<string, string> idMap)
    {
        foreach (var conflict in match.Conflicts.ToList())
        {
            var lefts = match.Pairs.Where(p => p.Right == null && ConfigState.DisplayName(def, p.Left!) == conflict.Name).ToList();
            var rights = match.Pairs.Where(p => p.Left == null && ConfigState.DisplayName(def, p.Right!) == conflict.Name).ToList();
            if (lefts.Count == 0 || rights.Count == 0) continue;
            foreach (var l in lefts)
            {
                var r = rights.FirstOrDefault(r => ConfigDiffEngine.SharedFieldsEqual(def, l.Left!, r.Right!, idMap));
                if (r == null) continue;
                rights.Remove(r);
                match.Pairs.Remove(l);
                match.Pairs.Remove(r);
                match.Pairs.Add(new ObjectPair(l.Left, r.Right, "content"));
            }
            if (!match.Pairs.Any(p => (p.Right == null && ConfigState.DisplayName(def, p.Left!) == conflict.Name)
                    || (p.Left == null && ConfigState.DisplayName(def, p.Right!) == conflict.Name)))
                match.Conflicts.Remove(conflict);
        }
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
