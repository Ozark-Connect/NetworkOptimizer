using System.Globalization;
using System.Text.RegularExpressions;
using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;

namespace NetworkOptimizer.Web.Services.CableModemProviders.Uci;

/// <summary>
/// Maps a decoded UCI inform into the shared <see cref="CableModemStats"/> model, following the
/// conventions the other providers set: OFDM rows carry <c>Modulation = "OFDM"</c> and leave
/// their codeword counters out of the aggregates (they run to billions), OFDMA rows carry
/// <c>ChannelType = "OFDMA"</c>.
/// </summary>
[VendorSpecific("UniFi", "UCI inform payload to CableModemStats")]
public static partial class UciStatsMapper
{
    /// <summary>Anything logged before this is a pre-clock-sync stamp, not a real time.</summary>
    private static readonly DateTime EarliestPlausible = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static CableModemStats Map(UciInformPayload payload, string name, string mac, DateTime capturedAt)
    {
        var stats = new CableModemStats
        {
            Timestamp = capturedAt,
            DeviceHost = mac,
            DeviceName = name,
            DeviceModel = string.IsNullOrWhiteSpace(payload.Model) ? "UCI" : payload.Model!,
            FirmwareVersion = string.IsNullOrWhiteSpace(payload.Version) ? null : payload.Version,
            UptimeSeconds = payload.Uptime > 0 ? payload.Uptime : null,
        };

        foreach (var row in payload.DsTable ?? [])
        {
            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = row.ChannelId ?? 0,
                LockStatus = LockStatus(row.State),
                Modulation = row.Modulation?.Trim() ?? "",
                Frequency = ParseFrequencyHz(row.Frequency),
                Power = row.Power,
                Snr = row.Snr.HasValue ? Math.Abs(row.Snr.Value) : null,
                Correctables = row.Correctable,
                Uncorrectables = row.Uncorrectable,
                ReportedSpeedMbps = row.Speed,
            });
        }

        foreach (var row in payload.OfdmTable ?? [])
        {
            stats.DownstreamChannels.Add(new DsChannel
            {
                ChannelId = row.ChannelId ?? 0,
                LockStatus = LockStatus(row.State),
                Modulation = "OFDM",
                Frequency = ParseFrequencyHz(row.Frequency),
                Power = row.Power,
                Snr = row.Snr.HasValue ? Math.Abs(row.Snr.Value) : null,
                ReportedSpeedMbps = row.Speed,
            });
        }

        foreach (var row in payload.UsTable ?? [])
        {
            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = row.ChannelId ?? 0,
                LockStatus = LockStatus(row.State),
                ChannelType = "SC-QAM",
                Modulation = string.IsNullOrWhiteSpace(row.Modulation) ? null : row.Modulation.Trim(),
                Frequency = ParseFrequencyHz(row.Frequency),
                Power = row.Power,
                ReportedSpeedMbps = row.Speed,
            });
        }

        foreach (var row in payload.OfdmaTable ?? [])
        {
            stats.UpstreamChannels.Add(new UsChannel
            {
                ChannelId = row.ChannelId ?? 0,
                LockStatus = LockStatus(row.State),
                ChannelType = "OFDMA",
                Modulation = string.IsNullOrWhiteSpace(row.Modulation) ? null : row.Modulation.Trim(),
                Frequency = ParseFrequencyHz(row.Frequency),
                Power = row.Power,
                ReportedSpeedMbps = row.Speed,
            });
        }

        if (payload.StateTable is { } state)
            stats.DocsisState = new CmDocsisState(state.Mode, state.State, state.ReinitReason);

        stats.Events = ParseEventLog(payload.EventLog);
        return stats;
    }

    /// <summary>"Locked" stays "Locked" (the aggregates match on it); anything else passes through.</summary>
    private static string LockStatus(string? state) =>
        string.IsNullOrWhiteSpace(state) ? "" : state.Trim();

    /// <summary>
    /// Frequency text to Hz. The UCI reports MHz; a bare value above 100,000 is already Hz. A unit
    /// suffix ("567 MHz", "36.2 GHz") is honored. Unreadable = 0, which the UI shows as blank.
    /// </summary>
    public static long ParseFrequencyHz(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var match = LeadingNumberRx().Match(text);
        if (!match.Success || !double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return 0;
        var unit = text[(match.Index + match.Length)..].Trim().ToLowerInvariant();
        var hz = unit switch
        {
            var u when u.StartsWith("ghz") => value * 1e9,
            var u when u.StartsWith("mhz") => value * 1e6,
            var u when u.StartsWith("khz") => value * 1e3,
            var u when u.StartsWith("hz") => value,
            _ => value > 100_000 ? value : value * 1e6,
        };
        return (long)Math.Round(hz);
    }

    /// <summary>
    /// Splits the event log into entries. A line reads "&lt;time&gt; [&lt;Level&gt;] &lt;text&gt;", where the
    /// time is "Time Not Established" before the modem has synced its clock.
    /// </summary>
    public static List<CmEvent> ParseEventLog(string? log)
    {
        var events = new List<CmEvent>();
        if (string.IsNullOrWhiteSpace(log)) return events;

        foreach (var rawLine in log.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var entry = new CmEvent { Raw = line, Text = line };
            var level = LevelRx().Match(line);
            if (level.Success)
            {
                entry.Level = level.Groups[1].Value.Trim();
                entry.Text = line[(level.Index + level.Length)..].Trim();
                entry.Time = ParseEventTime(line[..level.Index].Trim());
            }
            entry.Kind = Classify(entry.Text);
            events.Add(entry);
        }
        return events;
    }

    /// <summary>
    /// Recognises the DOCSIS events worth an alert. The log carries no event IDs, so this goes
    /// by the standard CableLabs message text. T3/T4 come first: a ranging message can name them.
    /// </summary>
    public static string? Classify(string text)
    {
        if (T3Rx().IsMatch(text)) return CmEventKinds.T3Timeout;
        if (T4Rx().IsMatch(text)) return CmEventKinds.T4Timeout;
        if (text.Contains("Ranging Request Retries exhausted", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unicast Ranging Received Abort Response", StringComparison.OrdinalIgnoreCase))
            return CmEventKinds.RangingFailure;
        return null;
    }

    private static DateTime? ParseEventTime(string text)
    {
        if (text.Length == 0 || text.Contains("Not Established", StringComparison.OrdinalIgnoreCase))
            return null;
        // The weekday adds nothing the date does not say, and a mismatched one fails the whole parse.
        text = LeadingWeekdayRx().Replace(text, "");
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var time)
            && !DateTime.TryParseExact(text, EventTimeFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces,
                out time))
            return null;
        return time < EarliestPlausible ? null : time;
    }

    private static readonly string[] EventTimeFormats =
    [
        "MMM d HH:mm:ss yyyy",
        "MMM dd HH:mm:ss yyyy",
        "MM/dd/yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
    ];

    [GeneratedRegex(@"^(Mon|Tue|Wed|Thu|Fri|Sat|Sun)[a-z]*,?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingWeekdayRx();

    [GeneratedRegex(@"^[-+]?\d+(\.\d+)?")]
    private static partial Regex LeadingNumberRx();

    [GeneratedRegex(@"\[(Emergency|Alert|Critical|Error|Warning|Notice|Information|Informational|Debug)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRx();

    [GeneratedRegex(@"\bT3\s*time[- ]?out", RegexOptions.IgnoreCase)]
    private static partial Regex T3Rx();

    [GeneratedRegex(@"\bT4\s*time[- ]?out", RegexOptions.IgnoreCase)]
    private static partial Regex T4Rx();
}
