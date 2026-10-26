using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One impact line: an audit issue a change added (regression) or removed (fix).</summary>
public sealed class ImpactLine
{
    /// <summary>"Regression" or "Fix".</summary>
    public string Kind { get; set; } = "Regression";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>The audit issue type (IssueTypes), for checks that match on the rule rather than its title.</summary>
    public string Type { get; set; } = "";
    public string? Network { get; set; }
    public string? Device { get; set; }
}

/// <summary>The result of a before/after audit.</summary>
public sealed class ImpactResult
{
    public List<ImpactLine> Regressions { get; } = [];
    public List<ImpactLine> Fixes { get; } = [];
    public int BeforeIssueCount { get; set; }
    public int AfterIssueCount { get; set; }
    public List<ImpactLine> AfterIssues { get; } = [];

    /// <summary>The highest severity among the regressions, or null.</summary>
    public AuditSeverity? WorstRegression { get; set; }
}

/// <summary>
/// Builds the Security Audit's config inputs from a <see cref="ConfigState"/> over live telemetry,
/// and turns two audit results into impact lines (spec "Impact diff").
/// </summary>
[VendorSpecific("UniFi", "overlays snapshot device config onto live stat/device telemetry")]
public static class ImpactAnalyzer
{
    private static readonly JsonSerializerOptions Lenient = new() { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

    /// <summary>True when any changed collection feeds the audit.</summary>
    public static bool Triggers(IEnumerable<string> collections) =>
        collections.Any(c => AuditConfigInputs.TriggerCollections.Contains(c));

    /// <summary>Config inputs for one state. <paramref name="liveDevicesJson"/> is stat/device as read now.</summary>
    public static AuditConfigInputs BuildInputs(ConfigState state, string liveDevicesJson)
    {
        return new AuditConfigInputs
        {
            DeviceDataJson = OverlayDevices(state, liveDevicesJson),
            SettingsData = Element(new JsonObject { ["meta"] = new JsonObject { ["rc"] = "ok" }, ["data"] = Array(state.Get("setting")) }),
            FirewallPoliciesData = state.Collections.ContainsKey("firewall_policy") ? Element(Array(state.Get("firewall_policy"))) : null,
            LegacyFirewallRulesData = state.Collections.ContainsKey("firewallrule") ? Element(Array(state.Get("firewallrule"))) : null,
            FirewallGroups = Typed<UniFiFirewallGroup>(state, "firewallgroup"),
            NatRulesData = state.Collections.ContainsKey("nat") ? Element(Array(state.Get("nat"))) : null,
            PortProfiles = Typed<UniFiPortProfile>(state, "portconf"),
            NetworkConfigs = Typed<UniFiNetworkConfig>(state, "networkconf"),
            FirewallZones = Typed<UniFiFirewallZone>(state, "firewall_zone"),
            PortForwardRules = Typed<UniFiPortForwardRule>(state, "portforward"),
            UpnpEnabled = state.Get("setting").FirstOrDefault(s => s["key"]?.ToString() == "usg")?["upnp_enabled"] is JsonValue v
                          && v.TryGetValue<bool>(out var b) ? b : null,
        };
    }

    private static JsonArray Array(IEnumerable<JsonObject> objects) =>
        new(objects.Select(o => (JsonNode?)o.DeepClone()).ToArray());

    private static JsonElement Element(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private static List<T>? Typed<T>(ConfigState state, string collection)
    {
        if (!state.Collections.ContainsKey(collection)) return null;
        var list = new List<T>();
        foreach (var o in state.Get(collection))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(o.ToJsonString(), Lenient) is { } item) list.Add(item);
            }
            catch (JsonException)
            {
                // An object the typed model cannot hold is left out of the audit, not fatal.
            }
        }
        return list;
    }

    /// <summary>Live stat/device with each device's config fields replaced by the state's.</summary>
    public static string OverlayDevices(ConfigState state, string liveDevicesJson)
    {
        var live = ConfigSnapshotReader.ExtractList(liveDevicesJson) ?? [];
        var byMac = state.Get("device")
            .Where(d => d["mac"] != null)
            .GroupBy(d => d["mac"]!.ToString().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var device in live)
        {
            var mac = device["mac"]?.ToString().ToLowerInvariant();
            if (mac == null || !byMac.TryGetValue(mac, out var config)) continue;
            foreach (var (key, value) in config)
            {
                if (key is "_id" or "mac" or "model" or "type" or "version" or "adopted" or ConfigCollections.DeviceMetaField) continue;
                if (key == "radio_table" && value is JsonArray radios && device["radio_table"] is JsonArray liveRadios)
                {
                    foreach (var r in radios.OfType<JsonObject>())
                    {
                        var match = liveRadios.OfType<JsonObject>().FirstOrDefault(l => l["radio"]?.ToString() == r["radio"]?.ToString());
                        if (match == null) continue;
                        foreach (var (rk, rv) in r) match[rk] = rv?.DeepClone();
                    }
                    continue;
                }
                device[key] = value?.DeepClone();
            }
        }
        return new JsonObject { ["meta"] = new JsonObject { ["rc"] = "ok" }, ["data"] = Array(live) }.ToJsonString();
    }

    /// <summary>Issue identity across two runs: title and what it is about.</summary>
    public static string IssueKey(AuditIssue issue) =>
        $"{issue.Title}|{issue.DeviceMac ?? issue.DeviceName}|{issue.Port}|{issue.CurrentNetwork}|{issue.ClientMac}";

    /// <summary>Regressions (issues added) and fixes (issues removed) between two audit results.</summary>
    public static ImpactResult Compare(AuditResult before, AuditResult after)
    {
        // Informational findings are not issues: they are counted and compared nowhere.
        var beforeIssues = before.Issues.Where(i => i.Severity != AuditSeverity.Informational).ToList();
        var afterIssues = after.Issues.Where(i => i.Severity != AuditSeverity.Informational).ToList();
        var result = new ImpactResult { BeforeIssueCount = beforeIssues.Count, AfterIssueCount = afterIssues.Count };
        var beforeKeys = beforeIssues.Select(IssueKey).ToHashSet(StringComparer.Ordinal);
        var afterKeys = afterIssues.Select(IssueKey).ToHashSet(StringComparer.Ordinal);

        foreach (var issue in afterIssues.DistinctBy(IssueKey))
        {
            var line = Line(issue, "Regression");
            result.AfterIssues.Add(line);
            if (beforeKeys.Contains(line.Key)) continue;
            result.Regressions.Add(line);
            if (result.WorstRegression == null || issue.Severity > result.WorstRegression) result.WorstRegression = issue.Severity;
        }
        foreach (var issue in beforeIssues.DistinctBy(IssueKey).Where(i => !afterKeys.Contains(IssueKey(i))))
            result.Fixes.Add(Line(issue, "Fix"));
        return result;
    }

    private static ImpactLine Line(AuditIssue issue, string kind) => new()
    {
        Kind = kind,
        Title = issue.CurrentNetwork is { Length: > 0 } net && !issue.Title.Contains(net, StringComparison.OrdinalIgnoreCase)
            ? $"{issue.Title} ({net})" : issue.Title,
        Detail = issue.Description ?? "",
        Severity = issue.Severity.ToString(),
        Key = IssueKey(issue),
        Type = issue.Type,
        Network = issue.CurrentNetwork,
        Device = issue.DeviceName,
    };
}
