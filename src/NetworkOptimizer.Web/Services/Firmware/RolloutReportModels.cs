using System.Text.Json;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The post-soak report, persisted as <c>FirmwareRolloutPlan.ReportJson</c> and rendered by both
/// the page and the PDF export. Property names are part of the persisted contract: adding is safe,
/// renaming orphans every report already written.
/// </summary>
public class RolloutReport
{
    /// <summary>When the report was built (soak end).</summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary>When the rollout started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the last step settled.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Wall-clock length of the rollout in seconds.</summary>
    public int TotalSeconds { get; set; }

    /// <summary>Devices that came back on their target version.</summary>
    public int DevicesUpgraded { get; set; }

    /// <summary>Devices whose upgrade did not take.</summary>
    public int DevicesFailed { get; set; }

    /// <summary>Devices put back on the firmware they came from.</summary>
    public int DevicesRolledBack { get; set; }

    /// <summary>Devices never attempted: excluded, held, or dropped with their SKU.</summary>
    public int DevicesSkipped { get; set; }

    /// <summary>Mean CPU across devices measured on both sides, before the rollout.</summary>
    public double? SiteCpuBeforeMean { get; set; }

    /// <summary>Mean CPU across devices measured on both sides, after the rollout.</summary>
    public double? SiteCpuAfterMean { get; set; }

    /// <summary>Mean memory use across devices measured on both sides, before the rollout.</summary>
    public double? SiteMemBeforeMean { get; set; }

    /// <summary>Mean memory use across devices measured on both sides, after the rollout.</summary>
    public double? SiteMemAfterMean { get; set; }

    /// <summary>How the UniFi Network application update ended, when one was included.</summary>
    public string? UniFiNetworkUpdateOutcome { get; set; }
    public string? UniFiNetworkFromVersion { get; set; }
    public string? UniFiNetworkToVersion { get; set; }

    /// <summary>How the console's UniFi OS update ended, when one was included.</summary>
    public string? UniFiOsUpdateOutcome { get; set; }
    public string? UniFiOsFromVersion { get; set; }
    public string? UniFiOsToVersion { get; set; }

    /// <summary>One row per device the plan covered.</summary>
    public List<RolloutReportRow> Rows { get; set; } = [];

    /// <summary>
    /// Devices upgraded plus the console when it had any update (Network, OS, or both = one).
    /// The console is one device regardless of how many surfaces were updated on it.
    /// </summary>
    /// <summary>
    /// Both the Network app and OS updates are Rows entries when they ran. Reports built before
    /// the Network app row was added lack it, so the fallback adds +1 when the outcome says
    /// "updated" but no row accounts for it (and the OS row doesn't already cover the console).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TotalUpgraded
    {
        get
        {
            var hasNetworkRow = Rows.Any(r => r.Name.Contains("UniFi Network", StringComparison.OrdinalIgnoreCase));
            var networkAppNeedsCount = UniFiNetworkUpdateOutcome == "updated"
                && !hasNetworkRow
                && UniFiOsUpdateOutcome is null or "skipped";
            return DevicesUpgraded + (networkAppNeedsCount ? 1 : 0);
        }
    }

    /// <summary>Everything that went wrong, in the order the plan met it.</summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>Assumptions and exclusions the plan was built with, carried from its notes.</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>
    /// Reads a persisted report back, or null when there is none (or it is unreadable). The typed
    /// accessor lives here rather than on the view model so the view model stays a plain carrier.
    /// </summary>
    /// <param name="reportJson">The stored report JSON.</param>
    public static RolloutReport? Parse(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return null;
        try
        {
            var report = JsonSerializer.Deserialize<RolloutReport>(reportJson);
            if (report != null) BackfillNetworkAppRow(report);
            return report;
        }
        catch (JsonException) { return null; }
    }

    private static void BackfillNetworkAppRow(RolloutReport report)
    {
        if (report.UniFiNetworkUpdateOutcome is null or "skipped" or "nothing-to-update") return;
        if (report.Rows.Any(r => r.Name.Contains("UniFi Network", StringComparison.OrdinalIgnoreCase))) return;
        if (report.UniFiOsUpdateOutcome is not null and not "skipped"
            && report.Rows.Any(r => r.Name.Contains("UniFi OS", StringComparison.OrdinalIgnoreCase)))
            return;

        var outcome = report.UniFiNetworkUpdateOutcome switch
        {
            "updated" => "Upgraded",
            "stuck" => "Failed",
            _ => "Skipped",
        };
        report.Rows.Insert(0, new RolloutReportRow
        {
            Name = "Console (UniFi Network)",
            Model = "Console",
            FromVersion = report.UniFiNetworkFromVersion,
            ToVersion = report.UniFiNetworkToVersion,
            Outcome = outcome,
        });
    }
}

/// <summary>One device's line in the post-soak report.</summary>
public class RolloutReportRow
{
    /// <summary>Colonized device MAC.</summary>
    public string Mac { get; set; } = "";

    /// <summary>Device name as the console showed it at plan time.</summary>
    public string Name { get; set; } = "";

    /// <summary>Model / SKU.</summary>
    public string Model { get; set; } = "";

    /// <summary>UniFi device type code (uap / usw / ugw).</summary>
    public string DeviceType { get; set; } = "";

    /// <summary>Version the device was on before its step ran.</summary>
    public string? FromVersion { get; set; }

    /// <summary>Version the step aimed at.</summary>
    public string? ToVersion { get; set; }

    /// <summary>Changelog for the target version where the public feed carries one (GA only).</summary>
    public string? ChangelogUrl { get; set; }

    /// <summary>When the device came back on its new firmware.</summary>
    public DateTime? UpgradedAt { get; set; }

    /// <summary>Measured offline window.</summary>
    public int? DowntimeSeconds { get; set; }

    /// <summary>Human label: Upgraded, Regression flagged, Failed, Skipped, Held, Rolled back.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Mean CPU over the hour before the upgrade.</summary>
    public double? CpuBeforeMean { get; set; }

    /// <summary>Mean CPU over the hour after the cool-down.</summary>
    public double? CpuAfterMean { get; set; }

    /// <summary>Mean memory use over the hour before the upgrade.</summary>
    public double? MemBeforeMean { get; set; }

    /// <summary>Mean memory use over the hour after the cool-down.</summary>
    public double? MemAfterMean { get; set; }

    /// <summary>What went wrong with this device, when something did.</summary>
    public string? Issue { get; set; }
}

/// <summary>The outcome labels a report row carries. One spelling, used by the page and the PDF.</summary>
public static class RolloutOutcomes
{
    public const string Upgraded = "Upgraded";
    public const string RegressionFlagged = "Regression flagged";
    public const string RolledBack = "Rolled back";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string Held = "Held";

    /// <summary>A step that had not settled when the report was built.</summary>
    public const string InProgress = "In progress";
}
