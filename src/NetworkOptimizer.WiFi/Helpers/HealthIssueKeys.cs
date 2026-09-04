using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// Builds <see cref="HealthIssue.Key"/> values. A key is what an acknowledgment is stored
/// against, so it must survive every refresh: same rule, same subject, same key.
/// </summary>
public static class HealthIssueKeys
{
    /// <summary>Scope for an issue about the site as a whole.</summary>
    public const string Site = "site";

    /// <summary>
    /// <c>{ruleId}|{scope}</c>, or <c>{ruleId}|site</c> with no scope. Scope parts are joined
    /// with <c>|</c> in the order given, so pass them in a fixed order.
    /// </summary>
    public static string For(string ruleId, params string[] scope) =>
        scope.Length == 0 ? $"{ruleId}|{Site}" : $"{ruleId}|{string.Join("|", scope)}";

    /// <summary>A set of MACs as one scope part: lowercased, de-duplicated, sorted, joined with <c>+</c>.</summary>
    public static string Macs(IEnumerable<string> macs) => Names(macs);

    /// <summary>A set of names as one scope part: trimmed, lowercased, de-duplicated, sorted, joined with <c>+</c>.</summary>
    public static string Names(IEnumerable<string> names) =>
        string.Join("+", names
            .Select(n => (n ?? string.Empty).Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>An AP radio as one scope part: <c>{mac}/{band code}</c>.</summary>
    public static string Radio(string apMac, RadioBand band) =>
        $"{(apMac ?? string.Empty).Trim().ToLowerInvariant()}/{band.ToUniFiCode()}";
}
