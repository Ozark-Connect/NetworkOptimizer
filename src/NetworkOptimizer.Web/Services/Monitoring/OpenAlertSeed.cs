using System.Text.Json;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Which paired alerts were still open when this process started. Evaluators keep their open state
/// in memory, so without this a restart mid-outage leaves the alert open for good: the evaluator
/// starts clean and never publishes the recovery. One instance per evaluator; the read happens once.
/// </summary>
public sealed class OpenAlertSeed
{
    /// <summary>An unresolved alert older than this is stale, not a condition still running.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory? _scopes;
    private readonly string _siteSlug;
    private readonly IReadOnlyCollection<string> _openTypes;
    private readonly Func<AlertHistoryEntry, string?> _subject;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Task<OpenAlerts>? _read;

    /// <param name="scopes">Scope factory for the site's alert repository; null seeds nothing.</param>
    /// <param name="siteSlug">Site whose alerts to read.</param>
    /// <param name="openTypes">Opening event types to look for; each must be in <see cref="AlertPairs.RecoveryFor"/>.</param>
    /// <param name="subject">
    /// The key an alert is about, applied to openings and recoveries alike, so both must produce the
    /// same key for the same subject, and it must match the key the evaluator uses at runtime. Null
    /// skips the alert.
    /// </param>
    /// <param name="logger">Logger for a failed read.</param>
    public OpenAlertSeed(IServiceScopeFactory? scopes, string siteSlug, IReadOnlyCollection<string> openTypes,
        Func<AlertHistoryEntry, string?> subject, ILogger logger)
    {
        _scopes = scopes;
        _siteSlug = siteSlug;
        _openTypes = openTypes;
        _subject = subject;
        _logger = logger;
    }

    /// <summary>The conditions open at start-up, read on first call. Never throws; empty on failure.</summary>
    public Task<OpenAlerts> OpenAsync()
    {
        if (_scopes == null) return Task.FromResult(OpenAlerts.None);
        lock (_lock) return _read ??= ReadAsync();
    }

    private async Task<OpenAlerts> ReadAsync()
    {
        try
        {
            using var scope = _scopes!.CreateScope();
            scope.ServiceProvider.GetRequiredService<IAlertSiteScope>().UseSite(_siteSlug);
            var repo = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

            var floor = DateTime.UtcNow - Lookback;
            var openings = (await repo.GetUnresolvedAlertsAsync())
                .Where(a => _openTypes.Contains(a.EventType) && a.TriggeredAt >= floor && _subject(a) != null)
                .ToList();
            if (openings.Count == 0) return OpenAlerts.None;

            // Recoveries are read whatever their status: a resolved recovery still closed its opening.
            var recoveryTypes = openings.Select(a => AlertPairs.RecoveryFor[a.EventType]).ToHashSet();
            var recoveries = (await repo.GetAlertsForDigestAsync(openings.Min(a => a.TriggeredAt)))
                .Where(a => recoveryTypes.Contains(a.EventType) && _subject(a) != null)
                .ToList();

            var open = new OpenAlerts(Open(openings, recoveries, _subject), _subject);
            if (open.All.Count > 0)
                _logger.LogInformation("{Count} alert condition(s) were open before start-up for site {Site}; their recovery will be published",
                    open.All.Count, _siteSlug);
            return open;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read open alerts for site {Site}; conditions open before start-up will not recover", _siteSlug);
            return OpenAlerts.None;
        }
    }

    /// <summary>Openings with no recovery for the same subject at or after them, newest per subject and type.</summary>
    internal static List<AlertHistoryEntry> Open(
        IEnumerable<AlertHistoryEntry> openings, IReadOnlyCollection<AlertHistoryEntry> recoveries, Func<AlertHistoryEntry, string?> subject) =>
        openings
            .GroupBy(a => (Key: subject(a)!, a.EventType))
            .Select(g => g.MaxBy(a => a.TriggeredAt)!)
            .Where(open => !recoveries.Any(r =>
                r.EventType == AlertPairs.RecoveryFor[open.EventType]
                && r.TriggeredAt >= open.TriggeredAt
                && subject(r) == subject(open)))
            .ToList();

    /// <summary>An alert's device as a subject key, or null when it names none.</summary>
    public static string? MacSubject(string? mac) =>
        string.IsNullOrWhiteSpace(mac) ? null : DeviceOfflineDeduplicator.NormalizeMac(mac);

    /// <summary>A round-trip ("o") timestamp from an alert's stored context, else when the alert fired.</summary>
    public static DateTime ContextTime(AlertHistoryEntry alert, string key) =>
        DateTime.TryParse(Context(alert, key), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var at)
            ? at
            : alert.TriggeredAt;

    /// <summary>One string value from an alert's stored context, or null.</summary>
    public static string? Context(AlertHistoryEntry alert, string key)
    {
        if (string.IsNullOrEmpty(alert.ContextJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(alert.ContextJson);
            return doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The alerts open at start-up, looked up by type and subject.</summary>
public sealed class OpenAlerts
{
    /// <summary>Nothing open.</summary>
    public static OpenAlerts None => new([], _ => null);

    private readonly Func<AlertHistoryEntry, string?> _subject;
    private readonly List<AlertHistoryEntry> _untaken;

    internal OpenAlerts(IReadOnlyList<AlertHistoryEntry> all, Func<AlertHistoryEntry, string?> subject)
    {
        All = all;
        _subject = subject;
        _untaken = all.ToList();
    }

    /// <summary>Every open alert.</summary>
    public IReadOnlyList<AlertHistoryEntry> All { get; }

    /// <summary>The open alert of this type for this subject, or null. Does not consume it.</summary>
    public AlertHistoryEntry? Get(string eventType, string? subject) =>
        subject == null ? null : All.FirstOrDefault(a => a.EventType == eventType && _subject(a) == subject);

    /// <summary>
    /// The open alert of this type for this subject, handed out once: a state dropped and rebuilt
    /// later (an agent that reconnected, a check disabled and re-enabled) must not re-seed from it.
    /// </summary>
    public AlertHistoryEntry? Take(string eventType, string? subject)
    {
        if (subject == null) return null;
        lock (_untaken)
        {
            var index = _untaken.FindIndex(a => a.EventType == eventType && _subject(a) == subject);
            if (index < 0) return null;
            var alert = _untaken[index];
            _untaken.RemoveAt(index);
            return alert;
        }
    }
}
