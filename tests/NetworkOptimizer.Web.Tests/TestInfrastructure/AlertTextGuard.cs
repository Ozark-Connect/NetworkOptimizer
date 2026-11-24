using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NetworkOptimizer.Alerts.Events;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Alert text has no viewer, so every clock time in it names its zone ("8:47 AM EST", "14:42 UTC"), as
/// <c>TimestampFormatter</c> writes them. Context values may also be round-trip instants ("o"): evaluators read
/// them back, and the channels format them for display.
/// </summary>
internal static partial class AlertTextGuard
{
    // A clock time standing alone: "8:47", "14:42:50", "8:47 AM". MACs and IPv6 groups never match: their
    // neighbors are hex digits or colons. An offset ("UTC+01:00") is a label, not a time.
    [GeneratedRegex(@"(?<![\w:.+-])\d{1,2}:\d{2}(?::\d{2})?(?:\s?[AaPp]\.?[Mm]\.?)?(?![\w:])")]
    private static partial Regex ClockTime();

    // What must follow it: what DateTimeUtilities.ZoneAbbreviation writes ("CDT", "UTC", "UTC+01:00"), or
    // RolloutScopeCopy's site aside ("(03:00 at-site)", beside a labeled time).
    [GeneratedRegex(@"^\s+(?:UTC(?:[+-]\d{2}:\d{2})?|GMT|[A-Z]{2,5}|at-site)(?![\w])")]
    private static partial Regex ZoneLabel();

    // A machine timestamp ("2026-11-24 13:47:00Z", "2026-11-24T13:47:00.0000000Z") is never reader text.
    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}")]
    private static partial Regex MachineTimestamp();

    /// <summary>Throws when the event's title, message, or a context value shows a time a reader cannot place.</summary>
    public static void AssertTimesLabeled(AlertEvent e)
    {
        Check(e.Title, nameof(e.Title), e);
        Check(e.Message, nameof(e.Message), e);
        foreach (var (key, value) in e.Context)
        {
            if (DateTimeOffset.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) continue;
            Check(value, $"Context[{key}]", e);
        }
    }

    /// <summary>The first unlabeled time in <paramref name="text"/>, or null.</summary>
    public static string? FirstUnlabeledTime(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (MachineTimestamp().Match(text) is { Success: true } m) return m.Value;
        foreach (Match t in ClockTime().Matches(text))
            if (!ZoneLabel().IsMatch(text[(t.Index + t.Length)..])) return t.Value;
        return null;
    }

    private static void Check(string? text, string field, AlertEvent e)
    {
        if (FirstUnlabeledTime(text) is { } bad)
            throw new InvalidOperationException(
                $"Alert {e.EventType}: {field} shows \"{bad}\" without its zone. Alert times are on the notification clock and "
                + $"always name the zone (TimeFormatHelper.FormatLabeledDateTime). Text: {text}");
    }
}

/// <summary>
/// The alert bus for tests: keeps everything published, and fails the test on any event whose text shows a
/// time without its zone (<see cref="AlertTextGuard"/>). Every alert test publishes through it.
/// </summary>
internal sealed class CapturingBus : IAlertEventBus
{
    public List<AlertEvent> Published { get; } = [];

    /// <summary>The same list as <see cref="Published"/>, for tests written against the older name.</summary>
    public List<AlertEvent> Events => Published;

    public ValueTask PublishAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default)
    {
        AlertTextGuard.AssertTimesLabeled(alertEvent);
        Published.Add(alertEvent);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<AlertEvent> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
