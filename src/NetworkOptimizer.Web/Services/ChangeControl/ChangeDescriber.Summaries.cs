using System.Text.RegularExpressions;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// One changed object as a change summary sees it. Built from a fresh diff at detection or from a stored
/// change's rows, so a summary rebuilt later reads the same as one written at detection.
/// <paramref name="Moves"/> is set (possibly empty) only on a policy order change.
/// </summary>
public sealed record SummaryObject(string Collection, string Name, ConfigChangeAction Action, List<FieldRow> Rows,
    List<OrderMove>? Moves = null);

public static partial class ChangeDescriber
{
    /// <summary>The label of an object's name field; a one-row name change reads "renamed from ...".</summary>
    public const string NameLabel = "Name";

    /// <summary>The label of a switch port's profile; a port given a profile reads "profile set to ...".</summary>
    public const string PortProfileLabel = "Port Profile";

    /// <summary>The name a policy order change goes by, as Review Plan names its reorder step.</summary>
    public static string PolicyOrderName(string zonePair) => $"Policy order, {zonePair}";

    /// <summary>
    /// A stored order object's name in today's wording. Changes recorded before <see cref="PolicyOrderName"/>
    /// stored "order, {pair}".
    /// </summary>
    public static string PolicyOrderDisplayName(string storedName) =>
        storedName.StartsWith("order, ", StringComparison.Ordinal) ? PolicyOrderName(storedName["order, ".Length..]) : storedName;

    /// <summary>A plan kind as Review Plan and the change summaries name it.</summary>
    public static string PlanKindName(ConfigPlanKind kind) => kind switch
    {
        ConfigPlanKind.RollbackSnapshot or ConfigPlanKind.RollbackChange => "Roll Back",
        ConfigPlanKind.Edit => "YAML edit",
        ConfigPlanKind.Restore => "Restore",
        ConfigPlanKind.ReplaceDevice => "Replace Device",
        ConfigPlanKind.Zones => "Set Up Zones",
        ConfigPlanKind.LegacyToZones => "Translate Legacy Rules",
        ConfigPlanKind.Template => "Template",
        ConfigPlanKind.Baseline => "Return to Baseline",
        ConfigPlanKind.Revert => "Revert",
        ConfigPlanKind.Readdress or ConfigPlanKind.ReaddressPrepare => "Re-address Networks",
        _ => kind.ToString(),
    };

    /// <summary>The lead of a summary for a change a plan made ("Re-address Networks applied").</summary>
    public static string PlanLead(ConfigPlanKind kind) => $"{PlanKindName(kind)} applied";

    /// <summary>
    /// The UniFi Network page a setting lives on, or null for a setting not mapped here (it keeps "Setting key").
    /// One setting, ips, holds both the honeypot addresses and Intrusion Prevention itself.
    /// </summary>
    public static string? SettingName(string collection, string key, IEnumerable<string> labels) => collection != "setting" ? null : key switch
    {
        "teleport" => TeleportPage,
        "ips_suppression" => SuppressedSignaturesPage,
        "ips" => labels.ToList() is { Count: > 0 } l && l.All(x => x.StartsWith("Honeypot", StringComparison.Ordinal))
            ? HoneypotPage : IntrusionPreventionPage,
        _ => null,
    };

    private const string TeleportPage = "VPN Server - Teleport";
    private const string SuppressedSignaturesPage = "CyberSecure - Suppressed Signatures";
    private const string HoneypotPage = "CyberSecure - Honeypot";
    private const string IntrusionPreventionPage = "CyberSecure - Intrusion Prevention";

    /// <summary>
    /// Every page <see cref="SettingName"/> returns. A setting's summary starts with its page and no noun,
    /// so the Changes list (CcChanges.Bold) bolds a summary's head only when it is one of these.
    /// </summary>
    public static readonly IReadOnlySet<string> SettingPages = new HashSet<string>(StringComparer.Ordinal)
    {
        TeleportPage, SuppressedSignaturesPage, HoneypotPage, IntrusionPreventionPage,
    };

    /// <summary>A fresh diff as a summary sees it.</summary>
    public static SummaryObject ForSummary(ObjectDiff diff, ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null) =>
        new(diff.Collection, diff.Name, diff.Action,
            diff.Action == ConfigChangeAction.Updated && diff.OrderedIds == null ? Rows(diff, beforeNames, afterNames) : [],
            diff.OrderedIds != null ? Moves([diff]) : null);

    /// <summary>A stored change object as a summary sees it, from its rows as the change detail shows them.</summary>
    public static SummaryObject ForSummary(ConfigChangeObject o, IReadOnlyList<StoredField> displayFields, List<OrderMove>? moves) =>
        new(o.Collection, o.ObjectName, o.Action,
            displayFields.Select(f => new FieldRow(f.Label, f.Old ?? "", f.New ?? "", f.Secret)).ToList(), moves);

    /// <summary>One summary line: "WLAN Home: security WPA2 to WPA2/WPA3", "Firewall Policy Block IoT created".</summary>
    public static string Summary(ObjectDiff diff, ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null) =>
        Summary(ForSummary(diff, beforeNames, afterNames));

    /// <summary>One object's summary line.</summary>
    public static string Summary(SummaryObject o)
    {
        if (o.Moves != null) return OrderSummary([], o.Moves, null);
        var (noun, name) = Subject(o);
        return o.Action switch
        {
            ConfigChangeAction.Created => $"{Named(noun, name)} created",
            ConfigChangeAction.Deleted => $"{Named(noun, name)} deleted",
            _ => noun == null ? $"{name}: {ChangeText(o.Rows)}" : SummaryLine(noun, name, ChangeText(o.Rows)),
        };
    }

    /// <summary>
    /// A group summary: the object's line for one, then names for up to three, then counts, with what a
    /// plan made led by the plan ("Re-address Networks applied: ..."). Ends with <paramref name="context"/>
    /// (<see cref="UpgradeContext"/>) when given.
    /// </summary>
    public static string GroupSummary(IReadOnlyList<ObjectDiff> objects, string? context = null,
        ConfigIdentityContext? beforeNames = null, ConfigIdentityContext? afterNames = null, string? lead = null) =>
        GroupSummary(objects.Select(o => ForSummary(o, beforeNames, afterNames)).ToList(), context, lead);

    /// <inheritdoc cref="GroupSummary(IReadOnlyList{ObjectDiff}, string?, ConfigIdentityContext?, ConfigIdentityContext?, string?)"/>
    public static string GroupSummary(IReadOnlyList<SummaryObject> objects, string? context = null, string? lead = null)
    {
        var orders = objects.Where(o => o.Moves != null).ToList();
        var rest = objects.Where(o => o.Moves == null).ToList();
        var text = orders.Count > 0 ? OrderSummary(rest, orders.SelectMany(o => o.Moves!).ToList(), lead) : Group(rest, lead);
        return context == null ? text : $"{text} {context}";
    }

    /// <summary>The Change column for one object: its change text, or its move for a policy order change.</summary>
    public static string ObjectChangeText(SummaryObject o) => o.Moves switch
    {
        { Count: 1 } m => $"{m[0].Name} moved from #{m[0].From} to #{m[0].To}",
        { Count: > 1 } m => $"{m.Count} {Plural(PolicyNoun)} moved",
        { } => "Order changed",
        null => o.Action switch
        {
            ConfigChangeAction.Created => "Created",
            ConfigChangeAction.Deleted => "Deleted",
            _ => ChangeText(o.Rows),
        },
    };

    private static string PolicyNoun => ConfigCollections.Get("firewall_policy")?.Noun ?? "Firewall Policy";

    /// <summary>The noun and name a summary names an object by; a mapped setting reads as its page, with no noun.</summary>
    private static (string? Noun, string Name) Subject(SummaryObject o) =>
        SettingName(o.Collection, o.Name, o.Rows.Select(r => r.Label)) is { } page
            ? (null, page)
            : (ConfigCollections.Get(o.Collection)?.Noun ?? o.Collection, o.Name);

    private static string Named(string? noun, string name) => noun == null ? name : $"{noun} {name}";

    private static string Group(List<SummaryObject> list, string? lead)
    {
        if (list.Count == 1) return Summary(list[0]);
        var byCollection = list.GroupBy(o => o.Collection).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        string body;
        if (list.Count <= 3 && byCollection.Count == 1)
        {
            // Name them: "Firewall Policies A and B: Name". One shared change text is said once; different ones are not listed.
            var noun = Subject(list[0]).Noun;
            var names = JoinAnd(list.Select(o => Subject(o).Name).ToList());
            var head = noun == null ? names : $"{Plural(noun)} {names}";
            var texts = list.Select(o => o.Action switch
            {
                ConfigChangeAction.Created => " created",
                ConfigChangeAction.Deleted => " deleted",
                _ => $": {ChangeText(o.Rows)}",
            }).Distinct().ToList();
            body = texts.Count == 1 ? head + texts[0] : $"{head} changed";
        }
        else if (list.Count <= 3)
            body = $"{JoinAnd(list.Select(o => Named(Subject(o).Noun, Subject(o).Name)).ToList())} changed";
        else if (byCollection.Count == 1)
            body = $"{list.Count} {Plural(Subject(list[0]).Noun ?? "Setting")} changed";
        else
            body = $"{list.Count} changes: {Breakdown(byCollection)}";

        if (lead == null) return body;
        return list.Count > 3 && byCollection.Count > 1 ? $"{lead}: {Breakdown(byCollection)}" : $"{lead}: {body}";
    }

    /// <summary>"40 Clients, 8 NAT Rules, 6 Networks", largest first; past four, "and N more".</summary>
    private static string Breakdown(List<IGrouping<string, SummaryObject>> byCollection)
    {
        string Part(IGrouping<string, SummaryObject> g)
        {
            var noun = ConfigCollections.Get(g.Key)?.Noun ?? g.Key;
            return g.Count() == 1 ? $"1 {noun}" : $"{g.Count()} {Plural(noun)}";
        }
        return byCollection.Count > 4
            ? $"{string.Join(", ", byCollection.Take(3).Select(Part))}, and {byCollection.Count - 3} more"
            : string.Join(", ", byCollection.Select(Part));
    }

    private static string JoinAnd(List<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))}, and {items[^1]}",
    };

    /// <summary>
    /// A group with policy order changes. The order is a pseudo object, so it is told as moves rather
    /// than counted as a policy: one rule edited and dragged in one save reads as that one rule.
    /// </summary>
    private static string OrderSummary(List<SummaryObject> rest, List<OrderMove> moves, string? lead)
    {
        var noun = PolicyNoun;
        var moved = moves.Count switch
        {
            0 => $"{noun} order changed",
            1 => $"{moves[0].Name} moved from #{moves[0].From} to #{moves[0].To}",
            _ => $"{moves.Count} {Plural(noun)} moved",
        };
        if (rest.Count == 0)
        {
            var only = moves.Count == 1 ? SummaryLine(noun, moves[0].Name, $"moved from #{moves[0].From} to #{moves[0].To}") : moved;
            return lead != null && moves.Count > 1 ? $"{lead}: {only}" : only;
        }
        if (rest.Count == 1 && rest[0].Action == ConfigChangeAction.Updated && moves.Count == 1 && moves[0].Name == rest[0].Name)
            return $"{Summary(rest[0])}, moved from #{moves[0].From} to #{moves[0].To}";
        return $"{Group(rest, lead)}; {moved}";
    }

    private static List<OrderMove> Moves(IEnumerable<ObjectDiff> orders) =>
        orders.SelectMany(o => o.Fields.Where(f => f.Path == "order")
                .SelectMany(f => PolicyOrderMoves.Compute(Names(f.Old), Names(f.New))))
            .ToList();

    private static List<string> Names(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonArray arr ? arr.Select(e => e?.ToString() ?? "").ToList() : [];

    private static string Plural(string noun) =>
        noun.EndsWith("y", StringComparison.Ordinal) && !noun.EndsWith("ey", StringComparison.Ordinal) ? noun[..^1] + "ies"
        : noun.EndsWith("s", StringComparison.Ordinal) ? noun : noun + "s";

    /// <summary>
    /// A missing value. Value writes "none" for null, empty text, and an empty list, and the display path
    /// title-cases it to "None"; UniFi's own "none" option reads the same, so it is told as removed too.
    /// </summary>
    private static bool IsAbsent(string value) => string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Port 4" or "Port 5 (CloudKey)" at the start of a switch port's label, then the field.</summary>
    [GeneratedRegex(@"^(Port \d+(?: \([^)]*\))?) (.+)$")]
    private static partial Regex PortLabel();
}
