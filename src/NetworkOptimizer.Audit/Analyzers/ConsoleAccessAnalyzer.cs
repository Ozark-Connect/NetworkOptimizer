using Microsoft.Extensions.Logging;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Audit.Analyzers;

/// <summary>
/// Flags console API keys and local admin accounts that have gone unused. Each kind raises at most
/// one issue that lists every unused credential, so several stale accounts do not stack points.
/// </summary>
public class ConsoleAccessAnalyzer
{
    /// <summary>Unused this long: Recommended.</summary>
    public const int RecommendedIdleDays = 7;

    /// <summary>Unused this long: Critical.</summary>
    public const int CriticalIdleDays = 30;

    public const int RecommendedScoreImpact = 2;
    public const int CriticalScoreImpact = 5;

    private readonly ILogger<ConsoleAccessAnalyzer> _logger;

    public ConsoleAccessAnalyzer(ILogger<ConsoleAccessAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyze console accounts and API keys. A null list means this connection could not read it,
    /// and that check is skipped.
    /// </summary>
    /// <param name="connectedUsername">The account Network Optimizer signs in as. Skipped: a long-lived
    /// session keeps it in use without a fresh sign-in, so its login time understates its use.</param>
    public List<AuditIssue> Analyze(
        IReadOnlyList<UniFiOsUser>? users,
        IReadOnlyList<UniFiOsApiKey>? apiKeys,
        DateTimeOffset now,
        string? connectedUsername = null)
    {
        var issues = new List<AuditIssue>();

        if (apiKeys != null)
        {
            var unused = apiKeys
                .Select(k => Describe(k.Name ?? "Unnamed key", k.LastUsedAt, k.CreatedAt, now, "never used"))
                .Where(c => c != null)
                .Select(c => c!)
                .ToList();

            var issue = BuildIssue(unused, IssueTypes.UnusedApiKeys, "CONSOLE-KEY-001",
                one: "API key", many: "API keys",
                "Revoke API keys nobody uses on the UniFi Console. A forgotten key keeps full access with no sign-in.");
            if (issue != null) issues.Add(issue);
        }

        if (users != null)
        {
            var unused = users
                .Where(IsActiveLocalAdmin)
                .Where(u => !IsConnectedAccount(u, connectedUsername))
                .Select(u => Describe(u.DisplayName, LastSignIn(u), FromUnix(u.CreateTime), now, "never signed in"))
                .Where(c => c != null)
                .Select(c => c!)
                .ToList();

            var issue = BuildIssue(unused, IssueTypes.UnusedLocalAdmins, "CONSOLE-ADMIN-001",
                one: "local admin account", many: "local admin accounts",
                "Remove local admin accounts nobody uses in UniFi OS - Admins & Users. A local account has no UI.com sign-in protection.");
            if (issue != null) issues.Add(issue);
        }

        _logger.LogInformation("Console access: {Keys} API keys, {Users} accounts checked, {Issues} issues",
            apiKeys?.Count.ToString() ?? "no access to", users?.Count.ToString() ?? "no access to", issues.Count);

        return issues;
    }

    /// <summary>An enabled account that exists only on this console and holds an admin role.</summary>
    private static bool IsActiveLocalAdmin(UniFiOsUser user) =>
        user.OnlyLocalAccount
        && string.Equals(user.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)
        && user.Roles is { Count: > 0 };

    private static bool IsConnectedAccount(UniFiOsUser user, string? connectedUsername) =>
        !string.IsNullOrEmpty(connectedUsername)
        && (string.Equals(user.Username, connectedUsername, StringComparison.OrdinalIgnoreCase)
            || string.Equals(user.Email, connectedUsername, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? LastSignIn(UniFiOsUser user)
    {
        var latest = Math.Max(user.LoginTime, user.LastActivityTime);
        return FromUnix(latest);
    }

    private static DateTimeOffset? FromUnix(long seconds) =>
        seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    private sealed record UnusedCredential(string Name, int IdleDays, string Detail);

    /// <summary>
    /// Null when the credential was used recently, or has no timestamp to judge it by. A credential
    /// never used is judged from its creation, so a new one gets the same grace period.
    /// </summary>
    private static UnusedCredential? Describe(string name, DateTimeOffset? lastUsed, DateTimeOffset? created, DateTimeOffset now, string neverText)
    {
        var since = lastUsed ?? created;
        if (since == null)
            return null;

        var idleDays = (int)Math.Floor((now - since.Value).TotalDays);
        if (idleDays < RecommendedIdleDays)
            return null;

        var detail = lastUsed != null
            ? $"{name} ({Days(idleDays)})"
            : $"{name} ({neverText}, created {Days(idleDays)} ago)";
        return new UnusedCredential(name, idleDays, detail);
    }

    private static AuditIssue? BuildIssue(List<UnusedCredential> unused, string type, string ruleId,
        string one, string many, string recommendation)
    {
        if (unused.Count == 0)
            return null;

        var critical = unused.Any(c => c.IdleDays >= CriticalIdleDays);
        var list = string.Join(", ", unused.OrderByDescending(c => c.IdleDays).Select(c => c.Detail));
        var noun = unused.Count == 1 ? one : many;

        return new AuditIssue
        {
            Type = type,
            Severity = critical ? AuditSeverity.Critical : AuditSeverity.Recommended,
            Message = $"{unused.Count} {noun} unused for {RecommendedIdleDays}+ days: {list}.",
            RuleId = ruleId,
            ScoreImpact = critical ? CriticalScoreImpact : RecommendedScoreImpact,
            RecommendedAction = recommendation,
            Metadata = new Dictionary<string, object>
            {
                ["unused_count"] = unused.Count,
                ["max_idle_days"] = unused.Max(c => c.IdleDays)
            }
        };
    }

    private static string Days(int n) => n == 1 ? "1 day" : $"{n} days";
}
