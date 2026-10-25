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
            && (Matches(j.ObjectId, e.ObjectId) || (e.ObjectId == null && SameCollection(j.Collection, e))));

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
            || (j.Collection == obj.Collection && j.ObjectId == null && j.Path.Contains(obj.ObjectId, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when an unattributed object diff is on a field UniFi rewrites by itself.</summary>
    public static bool IsLikelyUniFiField(ObjectDiff obj) =>
        obj.Churn
        || (obj.Fields.Count > 0 && obj.Fields.All(f =>
            LikelyUniFiFields.Any(p => f.Path.Contains(p, StringComparison.Ordinal))
            || (obj.Collection == "device" && f.Path.StartsWith("radio_table", StringComparison.Ordinal)
                && (f.Path.EndsWith(".channel", StringComparison.Ordinal) || f.Path.EndsWith(".ht", StringComparison.Ordinal)))));

    /// <summary>Groups a diff's objects by actor.</summary>
    /// <param name="objects">Changed objects from the snapshot diff.</param>
    /// <param name="events">AUDIT change events since the previous snapshot.</param>
    /// <param name="journal">NO's writes since the previous snapshot.</param>
    /// <param name="now">Detection time.</param>
    /// <param name="inUpgradeWindow">The Network app version changed recently.</param>
    public static List<AttributedChange> Attribute(
        IReadOnlyList<ObjectDiff> objects, IReadOnlyList<AuditLogEvent> events,
        IReadOnlyList<ConfigWriteJournalEntry> journal, DateTime now, bool inUpgradeWindow)
    {
        var result = new List<AttributedChange>();
        var humanEvents = events.Where(e => e.IsChange && !IsOwnEvent(e, journal)).ToList();
        var byActor = new Dictionary<string, AttributedChange>(StringComparer.Ordinal);
        var ownObjects = new List<ObjectDiff>();
        var unattributed = new List<ObjectDiff>();

        foreach (var obj in objects)
        {
            var evt = FindEvent(obj, humanEvents);
            if (evt != null)
            {
                var key = $"{evt.AdminId ?? evt.AdminName}|{evt.Ip}";
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
                continue;
            }

            if (WrittenByNo(obj, journal) || obj.OwnedBy != null)
            {
                ownObjects.Add(obj);
                continue;
            }

            unattributed.Add(obj);
        }

        if (ownObjects.Count > 0)
        {
            // The newest write to these objects made the state this diff sees; the window can hold older ones.
            var writes = journal.Where(j => ownObjects.Any(o => WrittenByNo(o, [j]))).ToList();
            var noChange = new AttributedChange
            {
                ActorKind = ConfigChangeActorKind.NetworkOptimizer,
                ActorName = ownObjects.Select(o => o.OwnedBy).FirstOrDefault(n => n != null) ?? JournalSource(writes) ?? JournalSource(journal) ?? "Network Optimizer",
                OccurredAt = writes.Count > 0 ? writes.Max(j => j.At) : now,
            };
            noChange.Objects.AddRange(ownObjects);
            result.Add(noChange);
        }

        var likely = unattributed.Where(o => inUpgradeWindow || IsLikelyUniFiField(o)).ToList();
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
