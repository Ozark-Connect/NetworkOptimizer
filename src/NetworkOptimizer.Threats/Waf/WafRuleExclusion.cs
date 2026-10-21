using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.Threats.Interfaces;

namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// One managed exclusion: a CRS rule turned off for requests matching every field that is set.
/// Network Optimizer holds the list per site and pushes it to the site's WAF, which writes the rules.
/// </summary>
public sealed record WafRuleExclusion
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("ruleId")] public long RuleId { get; init; }
    [JsonPropertyName("host")] public string? Host { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    /// <summary>A path prefix, without the query string.</summary>
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("createdBy")] public string? CreatedBy { get; init; }
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The fields the WAF takes; the note and who added it stay in Network Optimizer.</summary>
    public object ForWaf() => new { id = Id, ruleId = RuleId, host = Host, method = Method, path = Path };

    /// <summary>The same rule and scope, whatever the note or author.</summary>
    public bool SameScope(WafRuleExclusion other) =>
        RuleId == other.RuleId
        && string.Equals(Host ?? "", other.Host ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Method ?? "", other.Method ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path ?? "", other.Path ?? "", StringComparison.Ordinal);
}

/// <summary>Reads and writes a site's managed exclusions in the threat settings.</summary>
public static class WafRuleExclusions
{
    /// <summary>The site's exclusions, oldest first; empty when none or unreadable.</summary>
    public static async Task<List<WafRuleExclusion>> LoadAsync(IThreatSettingsAccessor settings, string? site, CancellationToken ct)
    {
        var json = await settings.GetSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Exclusions, site), ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<WafRuleExclusion>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static Task SaveAsync(IThreatSettingsAccessor settings, string? site, IReadOnlyList<WafRuleExclusion> list, CancellationToken ct) =>
        settings.SaveSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Exclusions, site), JsonSerializer.Serialize(list), ct);

    /// <summary>Identifies what the WAF was last sent, so a sync pushes only on change.</summary>
    public static string Fingerprint(IEnumerable<WafRuleExclusion> list) =>
        string.Join(";", list.Select(x => $"{x.Id}|{x.RuleId}|{x.Host}|{x.Method}|{x.Path}"));
}

/// <summary>netopt-waf's GET/PUT /api/exclusions response.</summary>
public sealed class WafExclusionSet
{
    /// <summary>The built-in app exclusion sets the WAF loads, such as grafana.</summary>
    [JsonPropertyName("apps")] public List<string> Apps { get; set; } = [];
    [JsonPropertyName("exclusions")] public List<WafRuleExclusion> Exclusions { get; set; } = [];
}
