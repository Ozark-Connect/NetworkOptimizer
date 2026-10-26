using System.Text.Json.Nodes;
using NetworkOptimizer.Core;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>What a plan covers and how it matches.</summary>
public sealed class PlanOptions
{
    /// <summary>Desired and live come from the same site: UniFi IDs match.</summary>
    public bool SameSite { get; init; } = true;

    /// <summary>Collections to plan; null plans every writable collection.</summary>
    public IReadOnlySet<string>? Collections { get; init; }

    /// <summary>Delete live objects the desired state does not have (in the collections planned).</summary>
    public bool DeleteMissing { get; init; } = true;

    /// <summary>Restrict to these (collection, UniFi ID in the desired state) pairs (Roll Back of one change).</summary>
    public IReadOnlySet<(string Collection, string Id)>? OnlyObjects { get; init; }

    /// <summary>Old device MAC to new device MAC (Replace Device).</summary>
    public IReadOnlyDictionary<string, string> DeviceMap { get; init; } = new Dictionary<string, string>();

    /// <summary>The site is reached through an On-Site Agent.</summary>
    public bool AgentSite { get; init; }

    /// <summary>Console host from the site's connection URL, for the inform-path checks.</summary>
    public string? ConsoleHost { get; init; }

    /// <summary>
    /// Addresses Network Optimizer reaches the site from: the agent host on an agent site, the
    /// server itself otherwise. The lockout pre-flight checks policies and networks against them.
    /// </summary>
    public IReadOnlyList<string> ManagementAddresses { get; init; } = [];

    /// <summary>The target has no gateway adopted yet: gateway-bound collections wait.</summary>
    public bool TargetHasGateway { get; init; } = true;
}

/// <summary>
/// Builds a plan: desired state against live state becomes per-object create, update, and delete
/// operations in dependency order (spec "One engine for rollback, restore, and edit"). Nothing here
/// talks to the console; <see cref="PlanExecutor"/> applies the result.
/// </summary>
[VendorSpecific("UniFi", "write bodies per collection allowlist, reference IDs, firewall policy order slots")]
public static class PlanBuilder
{
    /// <summary>Placeholder that means "keep the current secret" (Download YAML redaction).</summary>
    public const string KeepSecret = "<keep current>";

    /// <summary>Prefix of a reference to an object this plan creates; resolved to its new ID at apply.</summary>
    public const string RefPrefix = "@ref:";

    public static PlanDocument Build(ConfigState desired, ConfigState live, PlanOptions options)
    {
        var doc = new PlanDocument();
        var desiredCtx = new ConfigIdentityContext(desired.NamesById());
        var liveCtx = new ConfigIdentityContext(live.NamesById());
        var desiredById = desired.ObjectsById();
        var liveById = live.ObjectsById();

        // Pass 1: match every collection both states have, so references resolve across collections
        // (zones in policies, networks in profiles), including objects this plan never writes.
        var idMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = new Dictionary<string, MatchResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in ConfigCollections.All)
        {
            if (!desired.Collections.ContainsKey(def.Key) || !live.Collections.ContainsKey(def.Key)) continue;
            var left = Relevant(def, desired.Get(def.Key), options);
            var right = Relevant(def, live.Get(def.Key), options);
            var match = ObjectMatcher.Match(def, left, right, desiredCtx, liveCtx, options.SameSite);
            matches[def.Key] = match;
            foreach (var p in match.Pairs.Where(p => p.Left != null && p.Right != null))
                idMap[ConfigState.IdOf(p.Left!)] = ConfigState.IdOf(p.Right!);
        }

        var mgmtNetworks = ConfigDiffEngine.ManagementNetworkIds(live);

        // Pass 2: operations.
        var creates = new Dictionary<string, PlanOperation>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(ConfigCollectionDef Def, JsonObject? Desired, JsonObject? Live)>();
        foreach (var def in ConfigCollections.All)
        {
            if (options.Collections != null && !options.Collections.Contains(def.Key)) continue;
            if (!desired.Collections.ContainsKey(def.Key)) continue;
            if (!live.Collections.ContainsKey(def.Key))
            {
                doc.Notes.Add($"{def.DisplayName}: not readable on the UniFi Console, not written.");
                continue;
            }
            if (def.Write == null)
            {
                if (HasDifferences(def, matches[def.Key], options))
                    doc.Notes.Add($"{def.DisplayName}: Network Optimizer does not write this collection; differences are listed only.");
                continue;
            }

            var match = matches[def.Key];
            foreach (var conflict in match.Conflicts)
                doc.Errors.Add($"{def.Noun} {conflict.Name}: {conflict.Reason}. Use the enforced key or the ID in the file.");

            foreach (var pair in match.Pairs)
            {
                if (options.OnlyObjects != null)
                {
                    var id = pair.Left != null ? ConfigState.IdOf(pair.Left) : ConfigState.IdOf(pair.Right!);
                    if (!options.OnlyObjects.Contains((def.Key, id))) continue;
                }
                pending.Add((def, pair.Left, pair.Right));
                if (pair.Left != null && pair.Right == null && def.Write.CreatePath != null)
                {
                    var key = OpKey(def, PlanAction.Create, ConfigState.DisplayName(def, pair.Left), ConfigState.IdOf(pair.Left));
                    creates[ConfigState.IdOf(pair.Left)] = new PlanOperation { Key = key };
                }
            }
        }

        foreach (var (def, d, l) in pending)
        {
            var op = BuildOperation(def, d, l, desiredById, liveById, idMap, creates, desiredCtx, liveCtx, options, doc, mgmtNetworks);
            if (op != null) doc.Operations.Add(op);
        }

        AddPolicyOrder(doc, desired, live, idMap, creates, liveCtx, options);
        CheckReferrers(doc, live, desired, idMap);

        var planned = PlannedState(live, doc);
        foreach (var finding in LockoutChecker.Check(live, planned, doc, options))
        {
            doc.Lockouts.Add(finding);
            foreach (var key in finding.OperationKeys)
                if (doc.Operations.FirstOrDefault(o => o.Key == key) is { } op)
                {
                    op.Tier = PlanTiers.Lockout;
                    op.ManagementPath = true;
                }
        }

        Order(doc);
        doc.WatchDevices = doc.Operations.Any(o => o.ManagementPath);
        doc.WatchInternet = doc.Operations.Any(o => o.WanPath);
        doc.Collections = doc.Operations.Select(o => o.Collection).Distinct().ToList();
        doc.DeviceMap = options.DeviceMap.ToDictionary(k => k.Key, k => k.Value);
        return doc;
    }

    private static List<JsonObject> Relevant(ConfigCollectionDef def, IReadOnlyList<JsonObject> objects, PlanOptions options)
    {
        var list = def.Key == "user" ? objects.Where(ConfigDiffEngine.HasClientConfig).ToList() : objects.ToList();
        if (def.Key == "device" && options.DeviceMap.Count > 0)
        {
            // Replace Device: the desired device carries the old MAC; rename it so it matches the new one.
            list = list.Select(o =>
            {
                var mac = o["mac"]?.ToString()?.ToLowerInvariant();
                if (mac == null || !options.DeviceMap.TryGetValue(mac, out var newMac)) return o;
                var copy = (JsonObject)o.DeepClone();
                copy["mac"] = newMac;
                copy.Remove("_id");
                return copy;
            }).ToList();
        }
        return list;
    }

    private static bool HasDifferences(ConfigCollectionDef def, MatchResult match, PlanOptions options) =>
        match.Pairs.Any(p => p.Left == null || p.Right == null || ConfigDiffEngine.CompareObjects(def, p.Right, p.Left) != null);

    private static string OpKey(ConfigCollectionDef def, PlanAction action, string name, string id) =>
        $"{def.Key}:{action.ToString().ToLowerInvariant()}:{name}:{id}";

    private static PlanOperation? BuildOperation(
        ConfigCollectionDef def, JsonObject? desired, JsonObject? live,
        Dictionary<string, (string Collection, JsonObject Object)> desiredById,
        Dictionary<string, (string Collection, JsonObject Object)> liveById,
        Dictionary<string, string> idMap, Dictionary<string, PlanOperation> creates,
        ConfigIdentityContext desiredCtx, ConfigIdentityContext liveCtx, PlanOptions options, PlanDocument doc, IReadOnlySet<string> mgmtNetworks)
    {
        var name = ConfigState.DisplayName(def, desired ?? live!);
        var write = def.Write!;

        if (desired != null && (def.IsGenerated?.Invoke(desired) == true || (live != null && def.IsGenerated?.Invoke(live) == true)))
            return null;
        var owned = ConfigCollections.OwnedBy(def, desired ?? live!);
        if (owned != null)
        {
            if (live == null)
                doc.Notes.Add($"{def.Noun} {name}: managed by {owned}, which re-provisions it.");
            return null;
        }

        // Delete.
        if (desired == null)
        {
            if (!options.DeleteMissing) return null;
            // A WLAN's own AP group (Specific APs on that WLAN) is removed by UniFi with the WLAN; deleting it
            // ourselves races that removal, and fails while the WLAN still points at it.
            if (def.Key == "apgroups" && live!["for_wlanconf"] is JsonValue fw && fw.TryGetValue<bool>(out var forWlan) && forWlan)
            {
                doc.Notes.Add($"{def.Noun} {name}: belongs to a WLAN; UniFi removes it with that WLAN.");
                return null;
            }
            if (write.DeletePath == null && write.BatchDeletePath == null)
            {
                doc.Notes.Add($"{def.Noun} {name}: is not in the source, and UniFi has no delete for it; left in place.");
                return null;
            }
            return new PlanOperation
            {
                Key = OpKey(def, PlanAction.Delete, name, ConfigState.IdOf(live!)),
                Collection = def.Key,
                Action = PlanAction.Delete,
                ObjectId = ConfigState.IdOf(live!),
                ObjectName = name,
                Order = def.Order,
                ChangeText = DescribeObject(def, live!, liveCtx),
                Tier = PlanTiers.Acknowledge,
                Reasons = ["Delete"],
                NeedsGateway = write.NeedsGateway,
                WanPath = def.IsWanPath?.Invoke(live!) == true,
            };
        }

        var remapped = (JsonObject)Remap(desired, desiredById, idMap, creates, options, doc, $"{def.Noun} {name}")!;
        RestoreKeptSecrets(def, live, remapped, doc, name);
        // A field YAML leaves out (a locked zone's networks) keeps its live value; absent is not "empty".
        if (live != null && def.HiddenInYaml != null)
            foreach (var (field, value) in live)
                if (!remapped.ContainsKey(field) && def.HiddenInYaml(live, field))
                    remapped[field] = value?.DeepClone();

        // Create.
        if (live == null)
        {
            if (write.CreatePath == null)
            {
                if (def.Key == "device")
                {
                    doc.Notes.Add($"Device {name} ({desired["mac"]}): not adopted on this console. Its config is written when it adopts.");
                    return null;
                }
                doc.Notes.Add($"{def.Noun} {name}: UniFi has no create for it; not written.");
                return null;
            }
            var body = CreateBody(def, remapped, doc, name);
            var op = creates[ConfigState.IdOf(desired)];
            op.Collection = def.Key;
            op.Action = PlanAction.Create;
            op.ObjectName = name;
            op.Body = body;
            op.WriteFields = body.Select(p => p.Key).Where(k => k is not "_id" and not "id").ToList();
            op.Order = def.Order;
            op.ChangeText = DescribeObject(def, desired, desiredCtx);
            op.NeedsGateway = write.NeedsGateway;
            op.WanPath = def.IsWanPath?.Invoke(desired) == true;
            var created = ConfigDiffEngine.CompareObjects(def, null, desired);
            if (created != null) ApplyTags(op, created);
            return op;
        }

        // Update.
        var diff = ConfigDiffEngine.CompareObjects(def, live, remapped, mgmtNetworks);
        if (diff == null) return null;
        diff = DropKeptSecrets(diff);
        if (diff.Fields.Count == 0) return null;

        var update = new PlanOperation
        {
            Key = OpKey(def, PlanAction.Update, name, ConfigState.IdOf(live)),
            Collection = def.Key,
            Action = PlanAction.Update,
            ObjectId = ConfigState.IdOf(live),
            ObjectName = name,
            Order = def.Order,
            NeedsGateway = write.NeedsGateway,
            DevicePhase = def.Key == "device",
        };
        var updateBody = UpdateBody(def, live, remapped, diff, update.WriteFields);
        update.Body = updateBody;
        update.Fields = diff.Fields.Select(f => new StoredField
        {
            Path = f.Path,
            Label = ChangeDescriber.LabelFor(f.Path, remapped, live),
            Old = f.Secret ? null : ChangeDescriber.Value(f.Old, liveCtx),
            New = f.Secret ? null : ChangeDescriber.Value(f.New, liveCtx),
            Secret = f.Secret,
        }).ToList();
        if (def.Key == "wlanconf" && ChangeDescriber.Rows(diff).FirstOrDefault(r => r.Label == "Security protocol") is { } sec)
        {
            update.Fields.RemoveAll(f => ChangeDescriber.LabelFor(f.Path) == "Security");
            update.Fields.Insert(0, new StoredField { Path = "_security", Label = "Security protocol", Old = sec.Old, New = sec.New });
        }
        update.ChangeText = ChangeDescriber.ChangeText(diff, liveCtx, liveCtx);
        ApplyTags(update, diff);
        update.ManagementPath = diff.ManagementPath;
        update.WanPath = diff.WanPath;
        if (def.Key == "wlanconf" && diff.Fields.Any(f => f.Path == "x_passphrase"))
            AddReason(update, $"A passphrase change disconnects every client on {name}");
        if (update.WanPath) AddReason(update, "WAN path change");
        return update;
    }

    private static void ApplyTags(PlanOperation op, ObjectDiff diff)
    {
        op.Tags = diff.Tags.OrderBy(t => t).ToList();
        foreach (var tag in diff.Tags.Where(ServiceTags.ClassD.Contains))
            AddReason(op, $"Service tag: {tag}");
        op.ManagementPath |= diff.ManagementPath;
    }

    private static void AddReason(PlanOperation op, string reason)
    {
        if (!op.Reasons.Contains(reason)) op.Reasons.Add(reason);
        op.Tier = Math.Max(op.Tier, PlanTiers.Acknowledge);
    }

    /// <summary>Removes secret field changes the desired side asked to keep (the redaction placeholder).</summary>
    private static ObjectDiff DropKeptSecrets(ObjectDiff diff)
    {
        var after = diff.After;
        var kept = diff.Fields.Where(f => f.Secret && ValueAt(after, f.Path) is JsonValue v && v.TryGetValue<string>(out var s) && s == KeepSecret).ToList();
        if (kept.Count == 0) return diff;
        var fields = diff.Fields.Except(kept).ToList();
        var copy = new ObjectDiff
        {
            Collection = diff.Collection, ObjectId = diff.ObjectId, Name = diff.Name, Action = diff.Action,
            Fields = fields, Before = diff.Before, After = diff.After, OwnedBy = diff.OwnedBy, Generated = diff.Generated,
            ManagementPath = diff.ManagementPath, WanPath = diff.WanPath,
        };
        foreach (var t in diff.Tags) copy.Tags.Add(t);
        return copy;
    }

    /// <summary>
    /// Puts the live value back wherever a nested secret (a PPSK entry's password) is the redaction
    /// placeholder; top-level secrets are left to <see cref="DropKeptSecrets"/> and CreateBody.
    /// List entries pair with live entries by their non-secret fields, in order among equals. A
    /// placeholder with nothing to keep is a plan error, so it is never written as a value.
    /// </summary>
    private static void RestoreKeptSecrets(ConfigCollectionDef def, JsonObject? live, JsonObject desired, PlanDocument doc, string name)
    {
        foreach (var (key, value) in desired.ToList())
            if (!ConfigCollections.IsSecretField(def, key)) RestoreNested(def, live?[key], value);
        if (desired.Any(p => !ConfigCollections.IsSecretField(def, p.Key) && HasKept(def, p.Value)))
            doc.Errors.Add($"{def.Noun} {name}: a list entry keeps its secret as {KeepSecret} but matches no existing entry. Enter the value in the file.");
    }

    private static void RestoreNested(ConfigCollectionDef def, JsonNode? live, JsonNode? desired)
    {
        switch (desired)
        {
            case JsonObject d:
                foreach (var (k, v) in d.ToList())
                {
                    if (!ConfigCollections.IsSecretField(def, k)) RestoreNested(def, (live as JsonObject)?[k], v);
                    else if (IsKept(v) && (live as JsonObject)?[k] is { } kept && !IsKept(kept)) d[k] = kept.DeepClone();
                }
                break;
            case JsonArray d:
                var pool = (live as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
                foreach (var entry in d.OfType<JsonObject>())
                {
                    var shape = CanonicalJson.Serialize(WithoutSecrets(def, entry));
                    var match = pool.FirstOrDefault(p => CanonicalJson.Serialize(WithoutSecrets(def, p)) == shape);
                    if (match == null) continue;
                    pool.Remove(match);
                    RestoreNested(def, match, entry);
                }
                break;
        }
    }

    private static bool IsKept(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s == KeepSecret;

    private static bool HasKept(ConfigCollectionDef def, JsonNode? n) => n switch
    {
        JsonObject o => o.Any(p => ConfigCollections.IsSecretField(def, p.Key) ? IsKept(p.Value) : HasKept(def, p.Value)),
        JsonArray a => a.Any(e => HasKept(def, e)),
        _ => false,
    };

    private static JsonNode? WithoutSecrets(ConfigCollectionDef def, JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.Where(p => !ConfigCollections.IsSecretField(def, p.Key))
            .Select(p => KeyValuePair.Create(p.Key, WithoutSecrets(def, p.Value)))),
        JsonArray a => new JsonArray(a.Select(e => WithoutSecrets(def, e)).ToArray()),
        _ => n?.DeepClone(),
    };

    private static JsonNode? ValueAt(JsonObject? o, string path)
    {
        JsonNode? node = o;
        foreach (var part in path.Split('.'))
        {
            if (node is not JsonObject obj) return null;
            node = obj[part];
        }
        return node;
    }

    /// <summary>
    /// Replaces source-site IDs with live IDs (or <c>@ref:</c> tokens for objects this plan creates)
    /// and old device MACs with new ones. An ID that maps nowhere is an error: the plan would write a
    /// dangling reference.
    /// </summary>
    private static JsonNode? Remap(JsonNode? node, Dictionary<string, (string Collection, JsonObject Object)> desiredById,
        Dictionary<string, string> idMap, Dictionary<string, PlanOperation> creates, PlanOptions options, PlanDocument doc, string owner,
        string? fieldName = null)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (k, v) in obj)
                    copy[k] = k is "_id" or "id" or "site_id" ? v?.DeepClone() : Remap(v, desiredById, idMap, creates, options, doc, owner, k);
                return copy;
            case JsonArray arr:
                return new JsonArray(arr.Select(e => Remap(e, desiredById, idMap, creates, options, doc, owner, fieldName)).ToArray());
            case JsonValue val when val.TryGetValue<string>(out var s):
                if (options.DeviceMap.TryGetValue(s.ToLowerInvariant(), out var newMac)) return newMac;
                // An edit's temporary ID that never resolved must not reach the console as a value.
                if (s.StartsWith(YamlConverter.TempIdPrefix, StringComparison.Ordinal))
                {
                    var unresolved = $"{owner}: {fieldName ?? "a field"} refers to an object from the edit that could not be resolved. Name an object that exists, or create it in a separate plan first.";
                    if (!doc.Errors.Contains(unresolved)) doc.Errors.Add(unresolved);
                    return val.DeepClone();
                }
                if (!desiredById.TryGetValue(s, out var target)) return val.DeepClone();
                if (idMap.TryGetValue(s, out var liveId)) return liveId;
                if (creates.TryGetValue(s, out var create)) return RefPrefix + create.Key;
                var targetDef = ConfigCollections.Get(target.Collection);
                var targetName = ConfigState.DisplayName(targetDef, target.Object);
                var err = $"{owner} references {targetDef?.Noun ?? target.Collection} {targetName}, which is not on the console and is not created by this plan.";
                if (!doc.Errors.Contains(err)) doc.Errors.Add(err);
                return val.DeepClone();
            default:
                return node?.DeepClone();
        }
    }

    private static JsonObject CreateBody(ConfigCollectionDef def, JsonObject desired, PlanDocument doc, string name)
    {
        var body = new JsonObject();
        foreach (var (k, v) in desired)
        {
            if (k is "_id" or "id" or "site_id" || k == ConfigCollections.DeviceMetaField) continue;
            if (def.ReadOnlyFields.Contains(k) && !(def.Write!.Allowlist?.Contains(k) ?? false)) continue;
            if (def.Write!.Allowlist != null && !def.Write.Allowlist.Contains(k)) continue;
            if (ConfigCollections.IsSecretField(def, k) && v is JsonValue sv && sv.TryGetValue<string>(out var s) && s == KeepSecret)
            {
                doc.Errors.Add($"{def.Noun} {name}: {ChangeDescriber.LabelFor(k)} is redacted in the source. Enter a value for it in the file.");
                continue;
            }
            body[k] = v?.DeepClone();
        }
        return body;
    }

    private static JsonObject UpdateBody(ConfigCollectionDef def, JsonObject live, JsonObject desired, ObjectDiff diff, List<string> writeFields)
    {
        var changedTop = diff.Fields.Select(f => TopField(f.Path)).Distinct(StringComparer.Ordinal).ToList();
        writeFields.AddRange(changedTop);
        var write = def.Write!;

        if (write.PartialUpdate)
        {
            var partial = new JsonObject();
            foreach (var field in changedTop)
                partial[field] = desired[field]?.DeepClone();
            return partial;
        }

        // Full object: the live object with the changed fields from the desired side. v2 endpoints
        // then keep only their allowlist, since the strict ones reject anything else.
        var merged = (JsonObject)live.DeepClone();
        foreach (var field in changedTop)
        {
            if (desired.ContainsKey(field)) merged[field] = desired[field]?.DeepClone();
            else merged.Remove(field);
        }
        merged.Remove("site_id");
        merged.Remove(ConfigCollections.DeviceMetaField);
        if (write.Allowlist != null)
            foreach (var key in merged.Select(p => p.Key).ToList())
                if (!write.Allowlist.Contains(key)) merged.Remove(key);
        return merged;
    }

    /// <summary>The top-level field of a diff path ("port_overrides[port_idx=4].name" is port_overrides).</summary>
    public static string TopField(string path)
    {
        var end = path.IndexOfAny(['.', '[']);
        return end < 0 ? path : path[..end];
    }

    private static string DescribeObject(ConfigCollectionDef def, JsonObject o, ConfigIdentityContext ctx) => def.Key switch
    {
        "static_dns" => $"{o["record_type"]} {o["value"]}",
        "firewall_policy" => $"{ChangeDescriber.Value(o["action"])}, {ctx.NameOf(o["source"]?["zone_id"]?.ToString())} to {ctx.NameOf(o["destination"]?["zone_id"]?.ToString())}",
        "networkconf" => o["vlan"] != null ? $"VLAN {o["vlan"]}, {o["ip_subnet"]}" : o["ip_subnet"]?.ToString() ?? "",
        "user" => string.Join(", ", new[] { o["fixed_ip"]?.ToString() is { Length: > 0 } ip ? "Fixed IP " + ip : null, o["name"]?.ToString() is { Length: > 0 } ? "name" : null }.Where(s => s != null)),
        _ => "",
    };

    private static void AddPolicyOrder(PlanDocument doc, ConfigState desired, ConfigState live, Dictionary<string, string> idMap,
        Dictionary<string, PlanOperation> creates, ConfigIdentityContext liveCtx, PlanOptions options)
    {
        if (options.Collections != null && !options.Collections.Contains("firewall_policy")) return;
        if (!desired.Collections.ContainsKey("firewall_policy") || !live.Collections.ContainsKey("firewall_policy")) return;
        var def = ConfigCollections.Get("firewall_policy")!;

        string Map(string id) => idMap.TryGetValue(id, out var l) ? l : creates.TryGetValue(id, out var c) ? RefPrefix + c.Key : id;

        var liveOrder = ConfigDiffEngine.PolicyOrder(live);
        var deleted = doc.Operations.Where(o => o.Action == PlanAction.Delete && o.Collection == "firewall_policy").Select(o => o.ObjectId).ToHashSet();
        foreach (var group in desired.Get("firewall_policy").Where(p => def.IsGenerated?.Invoke(p) != true)
                     .GroupBy(p => (Src: p["source"]?["zone_id"]?.ToString() ?? "", Dst: p["destination"]?["zone_id"]?.ToString() ?? "")))
        {
            var src = Map(group.Key.Src);
            var dst = Map(group.Key.Dst);
            var desiredIds = group.OrderBy(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue)
                .Select(p => Map(ConfigState.IdOf(p))).ToList();
            var current = liveOrder.GetValueOrDefault($"{src}>{dst}", []).Where(id => !deleted.Contains(id)).ToList();
            var desiredExisting = desiredIds.Where(id => !id.StartsWith(RefPrefix, StringComparison.Ordinal)).ToList();
            var needs = desiredIds.Any(id => id.StartsWith(RefPrefix, StringComparison.Ordinal))
                ? desiredIds.Count > 1
                : !current.Where(desiredExisting.Contains).SequenceEqual(desiredExisting);
            if (!needs) continue;

            // Custom policies are ordered per zone pair, split into the slots before and after UniFi's
            // predefined ones; a policy keeps the slot it has in the desired state.
            var predefinedMin = live.Get("firewall_policy")
                .Where(p => def.IsGenerated?.Invoke(p) == true && p["source"]?["zone_id"]?.ToString() == src && p["destination"]?["zone_id"]?.ToString() == dst)
                .Select(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue)
                .DefaultIfEmpty(long.MaxValue).Min();
            var before = new JsonArray();
            var after = new JsonArray();
            foreach (var p in group.OrderBy(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue))
            {
                var idx = p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue;
                (idx < predefinedMin ? before : after).Add(Map(ConfigState.IdOf(p)));
            }

            var pair = $"{liveCtx.NameOf(src)} to {liveCtx.NameOf(dst)}";
            doc.Operations.Add(new PlanOperation
            {
                Key = $"firewall_policy:reorder:{src}>{dst}",
                Collection = "firewall_policy",
                Action = PlanAction.Reorder,
                ObjectName = $"Policy order, {pair}",
                Order = 7,
                SourceZoneId = src,
                DestinationZoneId = dst,
                Body = new JsonObject
                {
                    ["source_zone_id"] = src, ["destination_zone_id"] = dst,
                    ["before_predefined_ids"] = before, ["after_predefined_ids"] = after,
                },
                ChangeText = "Order changed",
                NeedsGateway = true,
            });
        }
    }

    /// <summary>
    /// Deleting an object that is still referenced is refused unless the plan also changes every
    /// referrer (spec "Safety tiers"). Referrers are always listed.
    /// </summary>
    private static void CheckReferrers(PlanDocument doc, ConfigState live, ConfigState desired, Dictionary<string, string> idMap)
    {
        var deletes = doc.Operations.Where(o => o.Action == PlanAction.Delete && o.ObjectId != null).ToList();
        if (deletes.Count == 0) return;
        var deletedIds = deletes.Select(d => d.ObjectId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatesById = doc.Operations.Where(o => o.Action == PlanAction.Update && o.ObjectId != null)
            .GroupBy(o => o.ObjectId!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var del in deletes)
        {
            var still = new List<string>();
            foreach (var (collection, list) in live.Collections)
            {
                var def = ConfigCollections.Get(collection);
                foreach (var o in list)
                {
                    var oid = ConfigState.IdOf(o);
                    if (oid.Equals(del.ObjectId, StringComparison.OrdinalIgnoreCase) || deletedIds.Contains(oid)) continue;
                    if (!References(o, del.ObjectId!)) continue;
                    // The referrer is fixed when the plan's update no longer carries the reference.
                    if (updatesById.TryGetValue(oid, out var upd) && upd.Body is JsonObject body && !References(MergeForCheck(o, body), del.ObjectId!))
                        continue;
                    if (def?.IsGenerated?.Invoke(o) == true) continue;
                    still.Add($"{def?.Noun ?? collection} {ConfigState.DisplayName(def, o)}");
                }
            }
            del.Referrers = still;
            if (still.Count > 0)
                doc.Errors.Add($"{ConfigCollections.Get(del.Collection)?.Noun} {del.ObjectName} cannot be deleted while it is still referenced by: {string.Join(", ", still)}.");
        }
    }

    private static JsonObject MergeForCheck(JsonObject live, JsonObject body)
    {
        var merged = (JsonObject)live.DeepClone();
        foreach (var (k, v) in body) merged[k] = v?.DeepClone();
        return merged;
    }

    private static bool References(JsonNode? node, string id) => node switch
    {
        JsonObject o => o.Any(p => p.Key is not "_id" and not "id" && References(p.Value, id)),
        JsonArray a => a.Any(e => References(e, id)),
        JsonValue v when v.TryGetValue<string>(out var s) => string.Equals(s, id, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>The live state with the plan's writes applied (for the lockout pre-flight).</summary>
    public static ConfigState PlannedState(ConfigState live, PlanDocument doc)
    {
        var planned = live.Clone();
        foreach (var op in doc.Operations)
        {
            if (!planned.Collections.TryGetValue(op.Collection, out var list)) continue;
            switch (op.Action)
            {
                case PlanAction.Delete:
                    list.RemoveAll(o => ConfigState.IdOf(o).Equals(op.ObjectId, StringComparison.OrdinalIgnoreCase));
                    break;
                case PlanAction.Create when op.Body is JsonObject body:
                    var created = (JsonObject)body.DeepClone();
                    created["_id"] = RefPrefix + op.Key;
                    list.Add(created);
                    break;
                case PlanAction.Update when op.Body is JsonObject body:
                    var target = list.FirstOrDefault(o => ConfigState.IdOf(o).Equals(op.ObjectId, StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                        foreach (var (k, v) in body) target[k] = v?.DeepClone();
                    break;
            }
        }
        return planned;
    }

    /// <summary>
    /// Apply order (spec, diff-engine.md): creates and updates by collection order, policy order after
    /// policies, devices after site objects, deletes in reverse after their referrers change, and
    /// anything that can cut a management path last.
    /// </summary>
    /// <summary>Re-sorts a plan after a caller changed operation order (Re-address moves the console's network last).</summary>
    internal static void Reorder(PlanDocument doc) => Order(doc);

    private static void Order(PlanDocument doc)
    {
        // Deletes go after everything, management-path updates included: those updates are what move
        // ports and devices off a profile or network the plan deletes (CheckReferrers relies on it).
        int Rank(PlanOperation o) => o.Action == PlanAction.Delete ? 3 : o.ManagementPath ? 2 : 1;
        doc.Operations = doc.Operations
            .OrderBy(Rank)
            .ThenBy(o => o.Action == PlanAction.Delete ? -o.Order : o.Order)
            .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        doc.WriteOrder.Clear();
        var step = new List<PlanOperation>();
        string? stepGroup = null;
        foreach (var op in doc.Operations)
        {
            var group = Rank(op) == 3 ? "delete" : Rank(op) == 2 ? "last" : GroupFor(op);
            if (stepGroup != null && group != stepGroup)
            {
                doc.WriteOrder.Add(Step(step, stepGroup));
                step = [];
            }
            stepGroup = group;
            step.Add(op);
        }
        if (step.Count > 0) doc.WriteOrder.Add(Step(step, stepGroup!));
        doc.WriteOrder.Add(new WriteStep { Title = "Verify", Detail = "New snapshot, compared with the plan" });
    }

    private static string GroupFor(PlanOperation op) => op.Order switch
    {
        <= 2 => "base",
        <= 5 => op.Collection == "wlanconf" ? "wlan" : "base",
        <= 7 => "firewall",
        9 => "clients",
        10 => "devices",
        _ => "routes",
    };

    private static WriteStep Step(List<PlanOperation> ops, string group)
    {
        var names = string.Join(", ", ops.Take(4).Select(o => o.ObjectName)) + (ops.Count > 4 ? $", and {ops.Count - 4} more" : "");
        var gateway = ops.Any(o => o.NeedsGateway) ? "One gateway provision" : null;
        var detail = group switch
        {
            "last" => "Management path: changes that can cut a device off",
            "delete" => "Deletes, after their referrers change",
            "firewall" => gateway ?? "Firewall",
            "devices" => "Devices re-provision",
            "base" => ops.Any(o => o.NeedsGateway) ? "One gateway provision" : "No dependencies",
            _ => gateway ?? "",
        };
        return new WriteStep { Title = names, Detail = detail, OperationKeys = ops.Select(o => o.Key).ToList() };
    }
}
