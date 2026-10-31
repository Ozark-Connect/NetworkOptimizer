using System.Globalization;
using System.Text.Json.Nodes;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>A set of object diffs attributed to one actor: one row on the Changes timeline.</summary>
public sealed class AttributedChange
{
    public required ConfigChangeActorKind ActorKind { get; init; }
    public string? ActorName { get; init; }
    public string? ActorIp { get; init; }
    public string? ActorPlatform { get; init; }
    public DateTime OccurredAt { get; init; }

    /// <summary>The Change Control plan whose writes made this change, when it was one.</summary>
    public int? PlanId { get; init; }

    public List<ObjectDiff> Objects { get; } = [];
    public List<AuditLogEvent> Events { get; } = [];
}

/// <summary>
/// Joins a snapshot diff with the AUDIT events and Network Optimizer's write journal in the same
/// window (spec "Change detection", "NO-owned objects"). The AUDIT event says who and which object;
/// the diff says what. NO's own writes are recognized from the journal, never from the AUDIT admin
/// name, which is the API key owner's and differs by install.
/// </summary>
public static class ChangeAttributor
{
    /// <summary>How far apart a journal write and its AUDIT event may be.</summary>
    public static readonly TimeSpan JournalSlack = TimeSpan.FromMinutes(3);

    /// <summary>How long after a Network app version change unattributed changes count as likely UniFi.</summary>
    public static readonly TimeSpan UpgradeWindow = TimeSpan.FromMinutes(60);

    /// <summary>How long after a device's firmware changes its own unattributed changes count as likely UniFi.</summary>
    public static readonly TimeSpan DeviceUpgradeWindow = TimeSpan.FromMinutes(20);

    /// <summary>How long a network's AUDIT event can account for a side effect UniFi writes without one.</summary>
    public static readonly TimeSpan SideEffectWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Fields UniFi itself rewrites (Network app upgrade migrations; nightly channel optimization).
    /// From the 10.6 app's Auto-Optimize removal migration (network-app-10.6-dtos.md).
    /// </summary>
    private static readonly string[] LikelyUniFiFields =
    [
        "arp_cache_timeout", "arp_cache_base_reachable", "bc_filter", "minrate_", "no2ghz_oui", "network_optimization",
    ];

    /// <summary>True when the AUDIT event is one of NO's own writes (a journal entry on the same object near the same time).</summary>
    public static bool IsOwnEvent(AuditLogEvent e, IReadOnlyList<ConfigWriteJournalEntry> journal) =>
        journal.Any(j => Math.Abs((j.At - e.TimeUtc).TotalSeconds) <= JournalSlack.TotalSeconds
            && (Matches(j.ObjectId, e.ObjectId)
                // A device's AUDIT entry names it by MAC, our journal by its _id: match those on collection and time.
                || ((e.ObjectId == null || e.Section == "DEVICE") && SameCollection(j.Collection, e))
                // Our batch-reorder names no policy; UniFi audits it as an index change on each one it moved.
                || (IsReorder(j) && e.SettingField == OrderAuditField && SameCollection(j.Collection, e))));

    private static bool IsReorder(ConfigWriteJournalEntry j) => j.Path.Contains("batch-reorder", StringComparison.OrdinalIgnoreCase);

    /// <summary>The AUDIT setting UniFi names, once per moved policy, for a policy reorder.</summary>
    private const string OrderAuditField = "index";

    private static bool Matches(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool SameCollection(string? collection, AuditLogEvent e)
    {
        if (collection == null) return false;
        var def = ConfigCollections.Get(collection);
        return def != null && (def.AuditSections.Contains(e.Section ?? "") || def.AuditCollectionNames.Contains(e.SettingCollection ?? ""));
    }

    /// <summary>The journal entries that wrote this object in the window.</summary>
    public static bool WrittenByNo(ObjectDiff obj, IReadOnlyList<ConfigWriteJournalEntry> journal) =>
        journal.Any(j => Matches(j.ObjectId, obj.ObjectId)
            || (j.Collection == obj.Collection && j.ObjectId == null && j.Path.Contains(obj.ObjectId, StringComparison.OrdinalIgnoreCase))
            || (obj.OrderedIds != null && j.Collection == obj.Collection && IsReorder(j))
            || (obj.Action == ConfigChangeAction.Deleted && j.Collection == obj.Collection && IsBatchDelete(j)));

    // Its body is the list of IDs, so the journal names no object: it explains any delete in its
    // collection, held to writes after the diff's before read (Explaining).
    private static bool IsBatchDelete(ConfigWriteJournalEntry j) =>
        j.ObjectId == null && j.Path.EndsWith("batch-delete", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when an unattributed object diff is on a field UniFi rewrites by itself.</summary>
    public static bool IsLikelyUniFiField(ObjectDiff obj) =>
        obj.Churn
        || (obj.Fields.Count > 0 && obj.Fields.All(f =>
            LikelyUniFiFields.Any(p => f.Path.Contains(p, StringComparison.Ordinal))
            || (obj.Collection == "device" && f.Path.StartsWith("radio_table", StringComparison.Ordinal)
                && (f.Path.EndsWith(".channel", StringComparison.Ordinal) || f.Path.EndsWith(".ht", StringComparison.Ordinal)))));

    /// <summary>
    /// The device itself just changed firmware: UniFi re-provisions it after the upgrade. Scoped to that
    /// device, so an admin's edit elsewhere during a rollout still reads Unattributed.
    /// </summary>
    private static bool IsUpgradedDevice(ObjectDiff obj, IReadOnlySet<string>? macs) =>
        macs is { Count: > 0 } && obj.Collection == "device"
        && (obj.After ?? obj.Before)?["mac"]?.ToString() is { } mac && macs.Contains(mac);

    /// <summary>Groups a diff's objects by actor.</summary>
    /// <param name="objects">Changed objects from the snapshot diff.</param>
    /// <param name="events">AUDIT change events since the previous snapshot.</param>
    /// <param name="journal">NO's writes since the previous snapshot.</param>
    /// <param name="now">Detection time.</param>
    /// <param name="inUpgradeWindow">The Network app version changed recently.</param>
    /// <param name="beforeReadAt">When the diff's before state was read; null when unknown.</param>
    /// <param name="upgradedDevices">MACs of devices whose firmware changed within <see cref="DeviceUpgradeWindow"/>.</param>
    /// <param name="recentEvents">AUDIT change events of the last <see cref="SideEffectWindow"/>, earlier passes' included; used only by <see cref="SideEffectCause"/>.</param>
    public static List<AttributedChange> Attribute(
        IReadOnlyList<ObjectDiff> objects, IReadOnlyList<AuditLogEvent> events,
        IReadOnlyList<ConfigWriteJournalEntry> journal, DateTime now, bool inUpgradeWindow, DateTime? beforeReadAt = null,
        IReadOnlySet<string>? upgradedDevices = null, IReadOnlyList<AuditLogEvent>? recentEvents = null)
    {
        var result = new List<AttributedChange>();
        // A write already in the before state owns only events from before that read: an admin's edit in
        // UniFi a minute after a plan wrote the same object was dropped as ours, and read Unattributed.
        var humanEvents = events.Where(e => e.IsChange && !IsOwnEvent(e, beforeReadAt is { } read && e.TimeUtc > read
            ? journal.Where(j => j.At >= read).ToList() : journal)).ToList();
        var byActor = new Dictionary<string, AttributedChange>(StringComparer.Ordinal);
        var ownObjects = new List<ObjectDiff>();
        var unattributed = new List<ObjectDiff>();

        void CreditPerson(ObjectDiff obj, AuditLogEvent evt)
        {
            var key = ActorKey(evt);
            if (!byActor.TryGetValue(key, out var group))
            {
                byActor[key] = group = new AttributedChange
                {
                    ActorKind = ConfigChangeActorKind.Person,
                    ActorName = evt.AdminName,
                    ActorIp = evt.Ip,
                    ActorPlatform = evt.Platform,
                    OccurredAt = evt.TimeUtc,
                };
                result.Add(group);
            }
            group.Objects.Add(obj);
            if (!group.Events.Contains(evt)) group.Events.Add(evt);
        }

        foreach (var obj in objects)
        {
            var evt = FindEvent(obj, humanEvents);
            if (evt != null)
            {
                CreditPerson(obj, evt);
                continue;
            }

            // An object NO owns but did not write is NO's only outside an upgrade: the upgrade rewrites them too.
            if (WrittenByNo(obj, Explaining(journal, obj, beforeReadAt)) || (obj.OwnedBy != null && !inUpgradeWindow))
            {
                ownObjects.Add(obj);
                continue;
            }

            unattributed.Add(obj);
        }

        // A WLAN's own AP group (Specific APs) has no audit event of its own: it follows its WLAN. UniFi
        // deletes it a few seconds after the WLAN, often into the next pass, where only the journal shows who.
        var likelyUniFi = new List<ObjectDiff>();
        foreach (var obj in unattributed.Where(IsWlanApGroup).ToList())
        {
            unattributed.Remove(obj);
            var wlan = objects.FirstOrDefault(o => o.Collection == "wlanconf" && (ListsApGroup(o.Before, obj.ObjectId) || ListsApGroup(o.After, obj.ObjectId)));
            if (wlan != null && result.FirstOrDefault(g => g.Objects.Contains(wlan)) is { } group) group.Objects.Add(obj);
            else if (wlan != null && ownObjects.Contains(wlan)) ownObjects.Add(obj);
            else if (obj.Action == ConfigChangeAction.Deleted && journal.Any(j => j.Collection == "wlanconf" && j.Method == "DELETE")) ownObjects.Add(obj);
            else if (wlan == null && obj.Action == ConfigChangeAction.Deleted) likelyUniFi.Add(obj);
            else unattributed.Add(obj);
        }

        // Side effects UniFi writes with no AUDIT event of their own follow the network change that caused them.
        var causes = (recentEvents ?? []).Concat(events).Where(e => e.IsChange).DistinctBy(e => e.Id).ToList();
        foreach (var obj in unattributed.ToList())
        {
            if (SideEffectCause(obj, causes, now) is not { } cause) continue;
            unattributed.Remove(obj);
            if (IsOwnEvent(cause, journal)) ownObjects.Add(obj);
            else CreditPerson(obj, cause);
        }

        if (ownObjects.Count > 0)
        {
            // The newest write to these objects made the state this diff sees; the window can hold older ones.
            var writes = journal.Where(j => ownObjects.Any(o => WrittenByNo(o, Explaining([j], o, beforeReadAt)))).ToList();
            var noChange = new AttributedChange
            {
                ActorKind = ConfigChangeActorKind.NetworkOptimizer,
                ActorName = ownObjects.Select(o => o.OwnedBy).FirstOrDefault(n => n != null) ?? JournalSource(writes) ?? JournalSource(journal) ?? "Network Optimizer",
                OccurredAt = writes.Count > 0 ? writes.Max(j => j.At) : now,
                PlanId = writes.OrderByDescending(j => j.At).Select(j => PlanOf(j.Source)).FirstOrDefault(p => p != null),
            };
            noChange.Objects.AddRange(ownObjects);
            result.Add(noChange);
        }

        var likely = unattributed.Where(o => inUpgradeWindow || IsLikelyUniFiField(o) || IsUpgradedDevice(o, upgradedDevices))
            .Concat(likelyUniFi).ToList();
        var unknown = unattributed.Except(likely).ToList();
        if (likely.Count > 0)
        {
            var group = new AttributedChange { ActorKind = ConfigChangeActorKind.LikelyUniFi, OccurredAt = now };
            group.Objects.AddRange(likely);
            result.Add(group);
        }
        if (unknown.Count > 0)
        {
            var group = new AttributedChange { ActorKind = ConfigChangeActorKind.NoAdminRecorded, OccurredAt = now };
            group.Objects.AddRange(unknown);
            result.Add(group);
        }
        return result.OrderBy(g => g.OccurredAt).ToList();
    }

    private static string ActorKey(AuditLogEvent e) => $"{e.AdminId ?? e.AdminName}|{e.Ip}";

    private static readonly System.Text.RegularExpressions.Regex PortExclusionPath =
        new(@"^port_overrides\[port_idx=\d+\]\.excluded_networkconf_ids$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The AUDIT event behind a change UniFi writes with no event of its own (UDR7, Network 11, 2026-10-30), held to two shapes:
    /// <list type="bullet">
    /// <item>A zone whose only change is its networks: each network added or removed has a network event in the window
    /// that moves it (created, removed, or its zone changed: see <see cref="MovesZone"/>).</item>
    /// <item>A device whose only changes are port <c>excluded_networkconf_ids</c> lists: each network added or removed was
    /// created or removed in the window. UniFi logs the same edit on port profiles, not on devices.</item>
    /// </list>
    /// Every network must be accounted for, all by one actor; anything else stays unattributed. Returns the newest such event.
    /// </summary>
    internal static AuditLogEvent? SideEffectCause(ObjectDiff obj, IReadOnlyList<AuditLogEvent> events, DateTime now)
    {
        if (obj.Action != ConfigChangeAction.Updated || obj.Fields.Count == 0) return null;
        var zone = obj.Collection == "firewall_zone" && obj.Fields.All(f => f.Path == "network_ids");
        var device = obj.Collection == "device" && obj.Fields.All(f => PortExclusionPath.IsMatch(f.Path));
        if (!zone && !device) return null;

        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in obj.Fields)
        {
            if (f.Secret || IdList(f.Old) is not { } before || IdList(f.New) is not { } after) return null;
            changed.UnionWith(before.Except(after, StringComparer.OrdinalIgnoreCase));
            changed.UnionWith(after.Except(before, StringComparer.OrdinalIgnoreCase));
        }
        if (changed.Count == 0) return null;

        var networks = ConfigCollections.Get("networkconf")!;
        var found = new List<AuditLogEvent>();
        foreach (var id in changed)
        {
            var cause = events.Where(e => Matches(e.ObjectId, id)
                    && e.TimeUtc <= now + JournalSlack && now - e.TimeUtc <= SideEffectWindow
                    && (networks.AuditSections.Contains(e.Section ?? "") || networks.AuditCollectionNames.Contains(e.SettingCollection ?? ""))
                    && (e.Key is "GENERATED" or "REMOVED_ENTITY" || (zone && MovesZone(e))))
                .MaxBy(e => e.Timestamp);
            if (cause == null) return null;
            found.Add(cause);
        }
        return found.Select(ActorKey).Distinct(StringComparer.Ordinal).Count() == 1 ? found.MaxBy(e => e.Timestamp) : null;
    }

    /// <summary>
    /// A network event that can carry a zone move: one naming <c>firewall_zone_id</c> (zone picked on the network), or a
    /// multi-setting one, which names no setting (zone created holding the network logs one with COUNT 2).
    /// </summary>
    private static bool MovesZone(AuditLogEvent e) =>
        e.Key == "MADE_CHANGES_MULTIPLE_SETTING_OBJECTS" || (e.Key == "MADE_CHANGES_ONE_SETTING_OBJECT" && e.SettingField == "firewall_zone_id");

    /// <summary>A list of IDs (null or absent reads as empty); null when the value is anything else.</summary>
    private static List<string>? IdList(JsonNode? node) => node switch
    {
        null => [],
        JsonArray a when a.All(x => x is JsonValue v && v.TryGetValue<string>(out _)) => a.Select(x => x!.GetValue<string>()).ToList(),
        _ => null,
    };

    /// <summary>
    /// The writes that can account for an object's diff. One that finished before the diff's before
    /// state was read is already in it: it explains only fields it did not send (UniFi fills some in
    /// a pass later), never a field it sent, which only someone else can have changed since.
    /// </summary>
    internal static IReadOnlyList<ConfigWriteJournalEntry> Explaining(
        IReadOnlyList<ConfigWriteJournalEntry> journal, ObjectDiff obj, DateTime? beforeReadAt)
    {
        // A batch delete names no object, so only one after the before read can be this object's.
        if (beforeReadAt is { } since && obj.Action == ConfigChangeAction.Deleted)
            journal = journal.Where(j => !IsBatchDelete(j) || j.At >= since).ToList();
        if (beforeReadAt is not { } readAt || obj.Fields.Count == 0) return journal;
        var changed = obj.Fields.Select(f => TopLevel(f.Path)).ToHashSet(StringComparer.Ordinal);
        return journal.Where(j => j.At >= readAt || SentFields(j) is not { } sent || !changed.IsSubsetOf(sent)).ToList();
    }

    private static string TopLevel(string path)
    {
        var end = path.IndexOfAny(['.', '[']);
        return end < 0 ? path : path[..end];
    }

    private static HashSet<string>? SentFields(ConfigWriteJournalEntry j)
    {
        if (string.IsNullOrEmpty(j.FieldsJson)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(j.FieldsJson); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The plan ID in a write's source ("change-control:plan:12"), or null.</summary>
    internal static int? PlanOf(string? source) =>
        source != null && source.StartsWith(PlanSourcePrefix, StringComparison.Ordinal)
        && int.TryParse(source.AsSpan(PlanSourcePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>The write source a plan's apply runs under, followed by its ID.</summary>
    public const string PlanSourcePrefix = "change-control:plan:";

    private static bool IsWlanApGroup(ObjectDiff o) =>
        o.Collection == "apgroups" && (o.Before ?? o.After)?["for_wlanconf"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool ListsApGroup(JsonObject? wlan, string id) =>
        wlan?["ap_group_ids"] is JsonArray ids && ids.Any(i => string.Equals(i?.ToString(), id, StringComparison.OrdinalIgnoreCase));

    private static string? JournalSource(IReadOnlyList<ConfigWriteJournalEntry> journal) =>
        journal.Select(j => j.Source).FirstOrDefault(s => !string.IsNullOrEmpty(s)) switch
        {
            null => null,
            var s when s.StartsWith("change-control", StringComparison.Ordinal) => "Network Optimizer",
            var s => s,
        };

    /// <summary>The AUDIT event that names this object: by ID, else by name within the collection's sections.</summary>
    private static AuditLogEvent? FindEvent(ObjectDiff obj, IReadOnlyList<AuditLogEvent> events)
    {
        var def = ConfigCollections.Get(obj.Collection);
        if (obj.OrderedIds is { } ordered)
            return events.LastOrDefault(e => e.SettingField == OrderAuditField && ordered.Any(id => Matches(id, e.ObjectId)));
        var byId = events.LastOrDefault(e => Matches(e.ObjectId, obj.ObjectId));
        if (byId != null) return byId;
        if (def == null) return null;
        bool InCollection(AuditLogEvent e) =>
            def.AuditSections.Contains(e.Section ?? "") || def.AuditCollectionNames.Contains(e.SettingCollection ?? "");
        var byName = events.LastOrDefault(e => InCollection(e) && string.Equals(e.ObjectName, obj.Name, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        // A multi-object event in the same collection, when it is the only one: "made 3 changes to Firewall policies".
        var inSection = events.Where(e => InCollection(e) && e.ObjectId == null && e.ObjectName == null).ToList();
        return inSection.Count == 1 ? inSection[0] : null;
    }
}
