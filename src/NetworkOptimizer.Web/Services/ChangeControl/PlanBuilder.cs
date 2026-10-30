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

    /// <summary>
    /// A template: lists in <see cref="ConfigCollectionDef.MergeOnTemplate"/> add to the target's
    /// entries instead of replacing them (a template's PPSKs join the site's own).
    /// </summary>
    public bool MergeLists { get; init; }
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
        // Onto another site, or from another Network version, the target's own values fill what the source
        // lacks, so work on a copy.
        var otherVersion = desired.NetworkAppVersion != null && live.NetworkAppVersion != null
            && !string.Equals(desired.NetworkAppVersion, live.NetworkAppVersion, StringComparison.OrdinalIgnoreCase);
        if (!options.SameSite || otherVersion) desired = desired.Clone();
        var desiredCtx = new ConfigIdentityContext(desired.NamesById());
        var liveCtx = new ConfigIdentityContext(live.NamesById());
        var desiredById = desired.ObjectsById();
        var liveById = live.ObjectsById();

        // Pass 1: match every collection both states have, so references resolve across collections
        // (zones in policies, networks in profiles), including objects this plan never writes.
        var (matches, idMap) = ObjectMatcher.MatchAll(desired, live, desiredCtx, liveCtx, options.SameSite, (d, o) => Relevant(d, o, options));
        if (!options.SameSite) NoteNameMatchesOnAnotherVlan(matches, doc);

        // A snapshot from another Network version predates the columns the upgrade added.
        if (!options.SameSite || otherVersion)
        {
            foreach (var def in ConfigCollections.All.Where(d => matches.ContainsKey(d.Key)))
                foreach (var p in matches[def.Key].Pairs.Where(p => p.Left != null && p.Right != null))
                {
                    // A field the source never carried (an older Network version, a setting never saved there)
                    // is unspecified, not unset: the target keeps its own.
                    foreach (var (k, v) in p.Right!)
                        if (!p.Left!.ContainsKey(k)) p.Left[k] = v?.DeepClone();
                    if (!options.SameSite && def.KeepTargetValues?.Invoke(p.Left!, p.Right!) is { } what)
                        doc.KeptValues.Add(what);
                    if (options.MergeLists) MergeListsInto(def, p.Left!, p.Right!);
                }
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
            {
                // A name only objects no plan writes share (UniFi's predefined policies, one per zone pair;
                // Network Optimizer's own Threat Intelligence policies) changes nothing, so it blocks nothing.
                // They stay in matching: their IDs resolve references in other collections.
                if (NeverWritten(def, desired, conflict.Name) && NeverWritten(def, live, conflict.Name)) continue;
                doc.Errors.Add($"{def.Noun} {conflict.Name}: {conflict.Reason}. Use the enforced key or the ID in the file.");
            }

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
            var op = BuildOperation(def, d, l, desiredById, liveById, idMap, creates, desiredCtx, liveCtx, options, doc, mgmtNetworks, otherVersion);
            if (op == null) continue;
            doc.Operations.Add(op);
            // Right after its create, so PlannedState (before Order) applies the two in sequence.
            if (op.Action == PlanAction.Create && def.Key == "wlanconf" && Wpa3AfterCreate(op, def, op.ObjectName) is { } then)
                doc.Operations.Add(then);
        }

        AddPolicyOrder(doc, desired, live, idMap, creates, liveCtx, options);
        CheckReferrers(doc, live, desired, idMap);
        ZoneMovesAfterTheirTarget(doc, live);

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

    /// <summary>
    /// A network paired by name onto another VLAN ID (names come first across sites): what it carries
    /// lands on that VLAN here, and a written network moves to the source's VLAN ID. Said once per network.
    /// </summary>
    private static void NoteNameMatchesOnAnotherVlan(Dictionary<string, MatchResult> matches, PlanDocument doc)
    {
        if (!matches.TryGetValue("networkconf", out var networks)) return;
        static string Vlan(JsonObject o) =>
            o["vlan_enabled"] is JsonValue e && e.TryGetValue<bool>(out var on) && on && o["vlan"]?.ToString() is { Length: > 0 } v ? $"VLAN {v}" : "untagged";
        foreach (var pair in networks.Pairs.Where(p => p.MatchedBy == "name" && p.Left != null && p.Right != null))
        {
            var (source, here) = (Vlan(pair.Left!), Vlan(pair.Right!));
            if (source == here) continue;
            var note = $"Network {ConfigState.DisplayName(ConfigCollections.Get("networkconf"), pair.Right!)}: matched by name; {source} in the source, {here} here.";
            if (!doc.Notes.Contains(note)) doc.Notes.Add(note);
        }
    }

    /// <summary>
    /// The target's entries the desired list does not have (by the merge key), then the desired list's own:
    /// an entry with the same key is the desired one. A template therefore never removes an entry.
    /// </summary>
    internal static void MergeListsInto(ConfigCollectionDef def, JsonObject desired, JsonObject live)
    {
        foreach (var (field, key) in def.MergeOnTemplate)
        {
            if (desired[field] is not JsonArray want || live[field] is not JsonArray have) continue;
            var keys = want.OfType<JsonObject>().Select(e => e[key]?.ToString()).Where(k => k != null).ToHashSet(StringComparer.Ordinal);
            desired[field] = new JsonArray(have.OfType<JsonObject>().Where(e => e[key]?.ToString() is not { } k || !keys.Contains(k))
                .Select(e => (JsonNode?)e.DeepClone()).Concat(want.Select(e => e?.DeepClone())).ToArray());
        }
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

    /// <summary>
    /// Objects named <paramref name="name"/> exist and none is written by a plan: generated, or owned by a
    /// Network Optimizer feature. A key conflict names no object, so it is never excused here.
    /// </summary>
    private static bool NeverWritten(ConfigCollectionDef def, ConfigState state, string name)
    {
        var named = state.Get(def.Key).Where(o => ConfigState.DisplayName(def, o) == name).ToList();
        return named.Count > 0 && named.All(o => def.IsGenerated?.Invoke(o) == true || ConfigCollections.OwnedBy(def, o) != null);
    }

    private static bool HasDifferences(ConfigCollectionDef def, MatchResult match, PlanOptions options) =>
        match.Pairs.Where(p => def.IsGenerated?.Invoke(p.Left ?? p.Right!) != true)
            .Any(p => p.Left == null || p.Right == null || ConfigDiffEngine.CompareObjects(def, p.Right, p.Left) != null);

    private static string OpKey(ConfigCollectionDef def, PlanAction action, string name, string id) =>
        $"{def.Key}:{action.ToString().ToLowerInvariant()}:{name}:{id}";

    private static PlanOperation? BuildOperation(
        ConfigCollectionDef def, JsonObject? desired, JsonObject? live,
        Dictionary<string, (string Collection, JsonObject Object)> desiredById,
        Dictionary<string, (string Collection, JsonObject Object)> liveById,
        Dictionary<string, string> idMap, Dictionary<string, PlanOperation> creates,
        ConfigIdentityContext desiredCtx, ConfigIdentityContext liveCtx, PlanOptions options, PlanDocument doc, IReadOnlySet<string> mgmtNetworks,
        bool otherVersion = false)
    {
        var name = ConfigState.DisplayName(def, desired ?? live!);
        var write = def.Write!;

        // UniFi derives these (predefined, Return, Isolated Networks policies): never created, updated, or deleted.
        if ((desired != null && def.IsGenerated?.Invoke(desired) == true) || (live != null && def.IsGenerated?.Invoke(live) == true))
            return null;
        var owned = ConfigCollections.OwnedBy(def, desired ?? live!);
        if (owned != null)
        {
            // Once per name: another site's feature can own several same-named objects, one per zone pair.
            if (live == null && $"{def.Noun} {name}: managed by {owned}, which re-provisions it." is var note && !doc.Notes.Contains(note))
                doc.Notes.Add(note);
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
            if (DeleteCascade.CannotDelete(def, live!))
            {
                doc.Notes.Add($"{def.Noun} {name}: built into UniFi; never deleted.");
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
        if (def.Key == "wlanconf" && PmfProblem(remapped) is { } pmf && $"{def.Noun} {name}: {pmf}" is var pmfError && !doc.Errors.Contains(pmfError))
            doc.Errors.Add(pmfError);
        // Only a subnet the plan writes: a live one UniFi already holds is not ours to refuse.
        if (def.Key == "networkconf" && remapped["ip_subnet"]?.ToString() != live?["ip_subnet"]?.ToString()
            && SubnetProblem(remapped) is { } subnetProblem && $"{def.Noun} {name}: {subnetProblem}" is var subnetError && !doc.Errors.Contains(subnetError))
            doc.Errors.Add(subnetError);
        RestoreKeptSecrets(def, live, remapped, doc, name);
        if (live != null)
            foreach (var field in remapped.Select(p => p.Key).ToList())
            {
                // Fields never compared or written (external_id, legacy_id) have nothing to keep or report.
                if (field is "_id" or "id" or "site_id" or "external_id" or ObjectMatcher.LegacyIdField || def.IsReadOnly(remapped, field)) continue;
                var liveValue = live[field];
                // Identical before mapping: the live object still points at an old ID UniFi renumbered (Network 11's
                // Default WLAN group), and writing the mapped ID would change what nothing asked to change.
                var keep = CanonicalJson.DeepEquals(desired[field], liveValue);
                // A secret from another Network version: 11's stored form of a 10.6 secret is unproven, and a
                // wrong one breaks the WAN or VPN using it. The live secret stays, and one 11 no longer returns
                // (x_uplink_password) stays unwritten.
                if (!keep && otherVersion && ConfigCollections.IsSecretField(def, field))
                {
                    keep = true;
                    doc.KeptSecrets.Add($"{def.Noun} {name}: {FieldLabel(field)}");
                }
                // A snapshot from another Network version can reference objects it never captured (client groups
                // before 11): those IDs mean nothing here, and writing them would drop the live references.
                if (!keep && otherVersion && UnresolvedIds(desired[field], idMap, liveById, creates).Any())
                {
                    keep = true;
                    if (!doc.KeptReferences.TryGetValue(def.DisplayName, out var labels)) doc.KeptReferences[def.DisplayName] = labels = [];
                    if (!labels.Contains(FieldLabel(field))) labels.Add(FieldLabel(field));
                }
                if (!keep) continue;
                if (liveValue == null) remapped.Remove(field);
                else remapped[field] = liveValue.DeepClone();
            }
        // Onto another site, an update must not write an ID that means nothing here: a template saved
        // without its references (before they were kept) carries the source site's network IDs in its PPSKs.
        if (live != null && !options.SameSite)
            foreach (var field in remapped.Select(p => p.Key).Where(k => k is not "_id" and not "id" and not "site_id" and not "external_id"
                         and not ObjectMatcher.LegacyIdField && !def.IsReadOnly(remapped, k) && !ConfigCollections.IsSecretField(def, k)).ToList())
            {
                if (CanonicalJson.DeepEquals(remapped[field], live[field]) || !UnresolvedIds(remapped[field], idMap, liveById, creates).Any()) continue;
                var unresolved = $"{def.Noun} {name}: its {FieldLabel(field).ToLowerInvariant()} names an object that is not on this console. Recreate the template from its site so it carries the objects it references.";
                if (!doc.Errors.Contains(unresolved)) doc.Errors.Add(unresolved);
            }
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
            // An ID that matches nothing here (an object deleted since, or one the source never captured) is refused
            // by UniFi (api.err.IdInvalid). Dropped from a list it costs one member; as a single reference the
            // object cannot be built, so it is left out with a note instead of failing on the console.
            foreach (var field in remapped.Select(p => p.Key).Where(k => k is not "_id" and not "id" and not "site_id" and not "external_id" and not ObjectMatcher.LegacyIdField
                         && !def.IsReadOnly(remapped, k) && !ConfigCollections.IsSecretField(def, k)).ToList())
            {
                if (!UnresolvedIds(remapped[field], idMap, liveById, creates).Any()) continue;
                var (pruned, single) = PruneUnresolved(remapped[field], idMap, liveById, creates);
                if (single)
                {
                    doc.Notes.Add($"{def.Noun} {name}: not created. Its {FieldLabel(field).ToLowerInvariant()} is an object that is not on this console.");
                    return null;
                }
                remapped[field] = pruned;
                var note = $"{def.DisplayName}: references to objects not on this console were left out ({FieldLabel(field)}).";
                if (!doc.Notes.Contains(note)) doc.Notes.Add(note);
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
            // What the create writes, as an update shows its changes: Review Plan and the Config Delta list
            // fields, and a create had none, so a new WLAN's PPSKs never showed. Compared with an empty
            // object, every field it sets is a row (CompareObjects against null reports only the create).
            if (ConfigDiffEngine.CompareObjects(def, new JsonObject { ["_id"] = ConfigState.IdOf(desired) }, desired) is { } written)
            {
                // Values as written: the mapped body, named as on this site, a reference to another create by its name.
                var names = live == null ? liveById.ToDictionary(e => e.Key, e => ConfigState.DisplayName(ConfigCollections.Get(e.Value.Collection), e.Value.Object)) : [];
                foreach (var other in creates.Values.Where(c => c.Key.Length > 0))
                    names[RefPrefix + other.Key] = other.Key.Split(':') is { Length: > 2 } parts ? parts[2] : other.Key;
                var here = new ConfigIdentityContext(names);
                op.Fields = ChangeDescriber.ForDisplay(written.Fields.SelectMany(f => CreateRows(def, f, remapped, here)).ToList());
            }
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
        if (def.Key == "wlanconf" && ChangeDescriber.Rows(diff).FirstOrDefault(r => r.Label == ChangeDescriber.SecurityProtocol) is { } sec)
        {
            update.Fields.RemoveAll(f => ChangeDescriber.LabelFor(f.Path) == ChangeDescriber.SecurityLabel);
            update.Fields.Insert(0, new StoredField { Path = "_security", Label = ChangeDescriber.SecurityProtocol, Old = sec.Old, New = sec.New });
        }
        // Display rows only; the write uses Body.
        update.Fields = ChangeDescriber.ForDisplay(update.Fields);
        update.ChangeText = ChangeDescriber.ChangeText(diff, liveCtx, liveCtx);
        ApplyTags(update, diff);
        update.ManagementPath = diff.ManagementPath;
        update.WanPath = diff.WanPath;
        if (def.Key == "wlanconf" && diff.Fields.Any(f => f.Path == "x_passphrase"))
            AddReason(update, $"A passphrase change disconnects every client on {name}");
        if (update.WanPath) AddReason(update, "WAN path change");
        return update;
    }

    /// <summary>
    /// A WPA mode and PMF setting UniFi refuses together (api.err.InvalidSecurityPmfModeCombination,
    /// scratch site, Network 11.0.97, 2026-10-29): WPA3 only needs PMF required; WPA2/WPA3 transition
    /// needs it optional or required; WPA2 takes any. UniFi's own UI changes PMF with the mode; the file
    /// says what to set instead of changing it.
    /// </summary>
    internal static string? PmfProblem(JsonObject wlan)
    {
        if (wlan["pmf_mode"] is not JsonValue p || !p.TryGetValue<string>(out var pmf)) return null;
        static bool On(JsonObject o, string field) => o[field] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        if (!On(wlan, "wpa3_support")) return null;
        if (!On(wlan, "wpa3_transition") && pmf != "required")
            return $"WPA3 only needs pmf_mode: required; UniFi refuses {pmf}.";
        if (On(wlan, "wpa3_transition") && pmf == "disabled")
            return "WPA2/WPA3 transition needs pmf_mode: optional or required; UniFi refuses disabled.";
        return null;
    }

    /// <summary>
    /// A network subnet UniFi refuses (api.err.IncorrectIPSubnetSpec, Network 11, 2026-10-29):
    /// ip_subnet is the gateway's own address with its prefix ("10.69.30.1/24"), never the network's
    /// or the broadcast address. Null when it is fine or there is none (a VLAN-only network).
    /// </summary>
    internal static string? SubnetProblem(JsonObject network)
    {
        if (network["ip_subnet"] is not JsonValue v || !v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return null;
        if (Readdressing.ParseSubnet(text) is not { } subnet || Readdressing.ParseIp(text.Trim().Split('/')[0]) is not { } ip)
            return $"Subnet {text} is not the gateway's address with its prefix, such as 192.168.30.1/24.";
        var broadcast = subnet.Network | ~subnet.Mask;
        if (ip == subnet.Network || ip == broadcast)
            return $"Subnet {text} is the network's {(ip == subnet.Network ? "own" : "broadcast")} address; UniFi needs the gateway's, such as {Readdressing.Format(subnet.Network + 1)}/{subnet.Prefix}.";
        return null;
    }

    // Fields a WLAN create cannot set (scratch site, Network 11.0.97, 2026-10-29): UniFi stores WPA3 off
    // and PMF disabled whatever the create sends, so 6 GHz, which needs WPA3, is refused
    // (api.err.Wpa3MandatoryFor6GHzBand). An update afterwards sets them.
    private static readonly string[] Wpa3Fields = ["wpa3_support", "wpa3_transition", "wpa3_enhanced_192", "wpa3_fast_roaming", "pmf_mode", "wlan_bands"];

    /// <summary>
    /// For a WLAN create that wants WPA3 or 6 GHz: creates it on WPA2 without 6 GHz, and returns the
    /// update that then sets what the create could not. Null when the create needs no second step.
    /// </summary>
    private static PlanOperation? Wpa3AfterCreate(PlanOperation create, ConfigCollectionDef def, string name)
    {
        var body = create.Body as JsonObject;
        var wantsWpa3 = body?["wpa3_support"] is JsonValue w && w.TryGetValue<bool>(out var on) && on;
        var bands = (body?["wlan_bands"] as JsonArray)?.Select(b => b?.ToString()).ToList();
        if (body == null || (!wantsWpa3 && bands?.Contains("6g") != true)) return null;

        var later = new JsonObject();
        foreach (var field in Wpa3Fields)
            if (body[field] is { } value) later[field] = value.DeepClone();
        body["wpa3_support"] = false;
        body["wpa3_transition"] = false;
        if (body.ContainsKey("wpa3_enhanced_192")) body["wpa3_enhanced_192"] = false;
        if (body.ContainsKey("wpa3_fast_roaming")) body["wpa3_fast_roaming"] = false;
        body["pmf_mode"] = "disabled";
        if (bands != null)
        {
            var withoutSix = bands.Where(b => b != "6g").ToList();
            body["wlan_bands"] = new JsonArray((withoutSix.Count > 0 ? withoutSix : ["5g"]).Select(b => (JsonNode?)b).ToArray());
        }
        create.WriteFields = body.Select(p => p.Key).Where(k => k is not "_id" and not "id").ToList();

        return new PlanOperation
        {
            Key = OpKey(def, PlanAction.Update, name, RefPrefix + create.Key),
            Collection = def.Key,
            Action = PlanAction.Update,
            ObjectId = RefPrefix + create.Key,
            ObjectName = name,
            Order = def.Order,
            Body = later,
            WriteFields = later.Select(p => p.Key).ToList(),
            Fields = later.Select(p => new StoredField
            {
                Path = p.Key, Label = ChangeDescriber.LabelFor(p.Key),
                Old = ChangeDescriber.Value(body[p.Key], null), New = ChangeDescriber.Value(p.Value, null),
            }).ToList(),
            ChangeText = "WPA3 and bands, set right after the create: UniFi creates every WLAN on WPA2",
            NeedsGateway = create.NeedsGateway,
        };
    }

    /// <summary>
    /// A create's field as Review Plan rows: one row, or for a list of objects one row per entry naming
    /// its fields ("Private Preshared Keys 1: Network Gaming"). Secrets are never shown, nested ones included.
    /// </summary>
    private static IEnumerable<StoredField> CreateRows(ConfigCollectionDef def, FieldChange f, JsonObject desired, ConfigIdentityContext ctx)
    {
        var label = ChangeDescriber.LabelFor(f.Path, desired);
        if (!f.Secret && desired[f.Path] is JsonArray list && list.Count > 0 && list.All(e => e is JsonObject))
        {
            for (var i = 0; i < list.Count; i++)
            {
                var parts = ((JsonObject)list[i]!).Where(p => !ConfigCollections.IsSecretField(def, p.Key) && p.Value is not null)
                    .Select(p => $"{ChangeDescriber.LabelFor(p.Key)} {ChangeDescriber.Value(p.Value, ctx)}");
                yield return new StoredField { Path = $"{f.Path}[{i}]", Label = $"{label} {i + 1}", Old = "none", New = string.Join(", ", parts) };
            }
            yield break;
        }
        yield return new StoredField
        {
            Path = f.Path, Label = label, Old = "none",
            New = f.Secret ? null : ChangeDescriber.Value(f.New, ctx), Secret = f.Secret,
        };
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
                if (idMap.TryGetValue(s, out var liveId) && desiredById.ContainsKey(s)) return liveId;
                if (creates.TryGetValue(s, out var create)) return RefPrefix + create.Key;
                // An edit's temporary ID that is not one of this plan's creates must not reach the console as a value.
                if (s.StartsWith(YamlConverter.TempIdPrefix, StringComparison.Ordinal))
                {
                    var unresolved = $"{owner}: {fieldName ?? "a field"} refers to an object from the edit that is not created by this plan.";
                    if (!doc.Errors.Contains(unresolved)) doc.Errors.Add(unresolved);
                    return val.DeepClone();
                }
                if (!desiredById.TryGetValue(s, out var target)) return val.DeepClone();
                var targetDef = ConfigCollections.Get(target.Collection);
                var targetName = ConfigState.DisplayName(targetDef, target.Object);
                var err = $"{owner} references {targetDef?.Noun ?? target.Collection} {targetName}, which is not on the console and is not created by this plan.";
                if (!doc.Errors.Contains(err)) doc.Errors.Add(err);
                return val.DeepClone();
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>ID-shaped strings in a value that map to nothing: no match, no live object, no create.</summary>
    private static IEnumerable<string> UnresolvedIds(JsonNode? node, Dictionary<string, string> idMap,
        Dictionary<string, (string Collection, JsonObject Object)> liveById, Dictionary<string, PlanOperation> creates) => node switch
    {
        JsonObject o => o.SelectMany(p => p.Key is "_id" or "id" ? [] : UnresolvedIds(p.Value, idMap, liveById, creates)),
        JsonArray a => a.SelectMany(e => UnresolvedIds(e, idMap, liveById, creates)),
        JsonValue v when v.TryGetValue<string>(out var s) && LooksLikeId(s) && !idMap.ContainsKey(s) && !liveById.ContainsKey(s) && !creates.ContainsKey(s) => [s],
        _ => [],
    };

    /// <summary>
    /// A copy with unresolved IDs removed from every list; <c>Single</c> is true when one sits where a list is not,
    /// so it cannot be removed without leaving the field without its value.
    /// </summary>
    private static (JsonNode? Node, bool Single) PruneUnresolved(JsonNode? node, Dictionary<string, string> idMap,
        Dictionary<string, (string Collection, JsonObject Object)> liveById, Dictionary<string, PlanOperation> creates)
    {
        bool Unresolved(JsonNode? n) => UnresolvedIds(n, idMap, liveById, creates).Any();
        switch (node)
        {
            case JsonArray arr:
            {
                var copy = new JsonArray();
                var single = false;
                foreach (var e in arr)
                {
                    if (e is JsonValue && Unresolved(e)) continue;
                    var (p, s) = PruneUnresolved(e, idMap, liveById, creates);
                    single |= s;
                    copy.Add(p);
                }
                return (copy, single);
            }
            case JsonObject obj:
            {
                var copy = new JsonObject();
                var single = false;
                foreach (var (k, v) in obj)
                {
                    var (p, s) = PruneUnresolved(v, idMap, liveById, creates);
                    single |= s;
                    copy[k] = p;
                }
                return (copy, single);
            }
            default:
                return (node?.DeepClone(), Unresolved(node));
        }
    }

    /// <summary>A UniFi object ID: a 24-hex ObjectId (Network 10) or a UUID (Network 11).</summary>
    private static bool LooksLikeId(string s) =>
        (s.Length == 24 && s.All(Uri.IsHexDigit)) || (s.Length == 36 && Guid.TryParse(s, out _));

    private static string FieldLabel(string field) => ChangeDescriber.LabelFor(field);

    private static JsonObject CreateBody(ConfigCollectionDef def, JsonObject desired, PlanDocument doc, string name)
    {
        var body = new JsonObject();
        foreach (var (k, v) in desired)
        {
            // UniFi's own bookkeeping IDs (Network 11's copy of a 10.x ObjectId, the cloud ID): never written.
            if (k is "_id" or "id" or "site_id" or "external_id" or ObjectMatcher.LegacyIdField || k == ConfigCollections.DeviceMetaField) continue;
            if (def.IsReadOnly(desired, k) && !(def.Write!.Allowlist?.Contains(k) ?? false)) continue;
            if (def.Write!.Allowlist != null && !def.Write.Allowlist.Contains(k)) continue;
            if (ConfigCollections.IsSecretField(def, k) && v is JsonValue sv && sv.TryGetValue<string>(out var s) && s == KeepSecret)
            {
                doc.Errors.Add($"{def.Noun} {name}: {ChangeDescriber.LabelFor(k)} is redacted in the source. Enter a value for it in the file.");
                continue;
            }
            body[k] = v?.DeepClone();
        }
        DropAnyMatchType(def, body);
        return body;
    }

    /// <summary>
    /// UniFi drops matching_target_type SPECIFIC from a firewall policy's ANY source or destination
    /// (none of the main site's ANY matches carry it, Network 11.0.97), though its own create defaults
    /// send it; sent, the create read back as "Not applied by UniFi: Destination". OBJECT stays.
    /// </summary>
    internal static void DropAnyMatchType(ConfigCollectionDef def, JsonObject body)
    {
        if (def.Key != "firewall_policy") return;
        foreach (var side in new[] { "source", "destination" })
            if (body[side] is JsonObject match && match["matching_target"]?.ToString() == "ANY"
                && match["matching_target_type"]?.ToString() == "SPECIFIC")
                match.Remove("matching_target_type");
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
            DropAnyMatchType(def, partial);
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
                if (!write.Allowlist.Contains(key) && write.KeepOnUpdate?.Contains(key) != true) merged.Remove(key);
        DropAnyMatchType(def, merged);
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
            // The slot is the desired index when there is one (a Roll Back, a restore: from a snapshot),
            // else the console's: an edited file carries no index, and without this every policy above
            // UniFi's predefined ones would be moved below them. A new one goes before them, where UniFi
            // puts a custom policy: after them it sits below Allow All Traffic and never matches.
            var liveIndex = live.Get("firewall_policy").ToDictionary(ConfigState.IdOf,
                p => p["index"] is JsonValue lv && lv.TryGetValue<long>(out var li) ? li : long.MaxValue, StringComparer.OrdinalIgnoreCase);
            foreach (var p in group.OrderBy(p => p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue))
            {
                var id = Map(ConfigState.IdOf(p));
                // Only policies the console has or the plan creates: a Roll Back's snapshot can name one
                // deleted since, and UniFi refuses a reorder that lists it.
                if (!liveIndex.ContainsKey(id) && !id.StartsWith(RefPrefix, StringComparison.Ordinal)) continue;
                var idx = p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i
                    : liveIndex.TryGetValue(id, out var li) ? li : long.MinValue;
                (idx < predefinedMin ? before : after).Add(id);
            }
            // The reorder runs before the deletes, and UniFi refuses one that leaves out a policy the
            // pair still has (ShouldIncludeFirewallPolicyInBatchUpdate): a policy about to be deleted
            // keeps its slot until then.
            foreach (var p in live.Get("firewall_policy").Where(p => deleted.Contains(ConfigState.IdOf(p))
                         && p["source"]?["zone_id"]?.ToString() == src && p["destination"]?["zone_id"]?.ToString() == dst))
            {
                var idx = p["index"] is JsonValue v && v.TryGetValue<long>(out var i) ? i : long.MaxValue;
                (idx < predefinedMin ? before : after).Add(ConfigState.IdOf(p));
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
                    // A reference in a field UniFi manages (Etherlighting's network colors) is UniFi's to clean up.
                    if (!References(WithoutReadOnly(def, o), del.ObjectId!)) continue;
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

    private static JsonObject WithoutReadOnly(ConfigCollectionDef? def, JsonObject o)
    {
        if (def == null || !o.Any(p => def.IsReadOnly(o, p.Key))) return o;
        var copy = new JsonObject();
        foreach (var (k, v) in o)
            if (!def.IsReadOnly(o, k)) copy[k] = v?.DeepClone();
        return copy;
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
        doc.Operations = AfterTheirCreates(doc.Operations
            .OrderBy(Rank)
            .ThenBy(o => o.Action == PlanAction.Delete ? -o.Order : o.Order)
            .ThenBy(o => o.ObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList());

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

    /// <summary>
    /// Moves each operation after the creates it references (a setting naming a network the plan
    /// creates), keeping the given order otherwise. Collection order alone writes settings first.
    /// </summary>
    private static List<PlanOperation> AfterTheirCreates(List<PlanOperation> ops)
    {
        var byKey = ops.ToDictionary(o => o.Key, StringComparer.Ordinal);
        // A body naming a create, or an update of an object this plan creates (a new WLAN's WPA3 step).
        var needs = ops.ToDictionary(o => o.Key, o => RefsIn(o.Body)
            .Concat(o.ObjectId is { } id && id.StartsWith(RefPrefix, StringComparison.Ordinal) ? [id[RefPrefix.Length..]] : [])
            .Concat(o.After)
            .Where(k => k != o.Key && byKey.ContainsKey(k)).ToHashSet(), StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PlanOperation>(ops.Count);
        var pending = new List<PlanOperation>(ops);
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(o => needs[o.Key].All(placed.Contains)) ?? pending[0];
            pending.Remove(next);
            placed.Add(next.Key);
            result.Add(next);
        }
        return result;
    }

    /// <summary>
    /// A zone that gives up a network writes after the zone that takes it. UniFi refuses to leave a
    /// network in no zone (api.err.NetworkCantBeRemovedFromDefaultZone), so the name order is not safe.
    /// </summary>
    private static void ZoneMovesAfterTheirTarget(PlanDocument doc, ConfigState live)
    {
        static HashSet<string> Ids(JsonNode? node) =>
            (node?["network_ids"] as JsonArray ?? []).Select(n => n?.ToString()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var zones = doc.Operations.Where(o => o.Collection == "firewall_zone" && o.Action != PlanAction.Delete && o.Body?["network_ids"] is JsonArray).ToList();
        foreach (var op in zones.Where(o => o.Action == PlanAction.Update))
        {
            var liveZone = live.Get("firewall_zone").FirstOrDefault(z => ConfigState.IdOf(z).Equals(op.ObjectId, StringComparison.OrdinalIgnoreCase));
            var given = Ids(liveZone);
            given.ExceptWith(Ids(op.Body));
            if (given.Count == 0) continue;
            op.After.AddRange(zones.Where(t => t != op && Ids(t.Body).Overlaps(given)).Select(t => t.Key).Except(op.After));
        }
    }

    private static IEnumerable<string> RefsIn(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => RefsIn(p.Value)),
        JsonArray a => a.SelectMany(RefsIn),
        JsonValue v when v.TryGetValue<string>(out var s) && s.StartsWith(RefPrefix, StringComparison.Ordinal) => [s[RefPrefix.Length..]],
        _ => [],
    };

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
        var groups = ops.GroupBy(o => o.Collection)
            .Select(g => new WriteStepGroup { Collection = ConfigCollections.Get(g.Key)?.DisplayName ?? g.Key, Names = g.Select(o => o.ObjectName).ToList() })
            .ToList();
        return new WriteStep
        {
            Title = string.Join(", ", groups.Select(g => g.Collection)),
            Detail = detail,
            OperationKeys = ops.Select(o => o.Key).ToList(),
            Groups = groups,
        };
    }
}
