using System.Text.Json;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Assembles the post-soak report from what the rollout already recorded: the step rows, their
/// before/after resource windows, and the plan document's console outcomes and notes. Pure - it
/// measures nothing of its own, so the report can never disagree with the live view.
/// </summary>
public static class RolloutReportBuilder
{
    /// <summary>Key a changelog lookup is offered under: model and target version.</summary>
    /// <param name="model">Device model / SKU.</param>
    /// <param name="version">Target firmware version.</param>
    public static string ChangelogKey(string? model, string? version) =>
        $"{model?.Trim().ToLowerInvariant()}|{version?.Trim().ToLowerInvariant()}";

    /// <summary>
    /// Builds the report for a finished plan.
    /// </summary>
    /// <param name="plan">The plan, at the end of its soak.</param>
    /// <param name="document">Its parsed plan document.</param>
    /// <param name="steps">Its steps, in execution order.</param>
    /// <param name="generatedAt">Report time (UTC).</param>
    /// <param name="changelogUrls">Changelog URLs by <see cref="ChangelogKey"/>, where resolvable.</param>
    public static RolloutReport Build(
        FirmwareRolloutPlan plan,
        RolloutPlanDocument document,
        IReadOnlyList<FirmwareRolloutStep> steps,
        DateTime generatedAt,
        IReadOnlyDictionary<string, string?>? changelogUrls = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(steps);

        var report = new RolloutReport
        {
            GeneratedAt = generatedAt,
            StartedAt = plan.StartedAt,
            CompletedAt = plan.CompletedAt,
            TotalSeconds = plan.StartedAt is DateTime started && plan.CompletedAt is DateTime completed
                ? (int)Math.Max(0, (completed - started).TotalSeconds)
                : 0,
            UniFiNetworkUpdateOutcome = document.IncludesUniFiNetworkUpdate ? document.NetworkAppUpdate.Outcome : null,
            UniFiNetworkFromVersion = document.NetworkAppUpdate.FromVersion,
            UniFiNetworkToVersion = document.NetworkAppUpdate.TargetVersion,
            UniFiOsUpdateOutcome = document.IncludesUniFiOsUpdate ? document.UniFiOsUpdate.Outcome : null,
            UniFiOsFromVersion = document.UniFiOsUpdate.FromVersion,
            UniFiOsToVersion = document.UniFiOsUpdate.TargetVersion,
            Notes = [.. document.Notes],
        };

        foreach (var step in steps)
        {
            var pre = ParseStats(step.PreStatsJson);
            var post = ParseStats(step.PostStatsJson);
            var outcome = OutcomeFor(step, document);

            var row = new RolloutReportRow
            {
                Mac = step.DeviceMac,
                Name = step.DeviceName,
                // The catalog SKU, the same name the rest of the app shows; the console's model
                // code is a key, never a label.
                Model = UniFiProductDatabase.GetBestProductName(step.Model, null),
                DeviceType = step.DeviceType,
                FromVersion = step.FromVersion,
                ToVersion = step.ToVersion,
                UpgradedAt = step.BackAt,
                DowntimeSeconds = step.DowntimeSeconds,
                Outcome = outcome,
                CpuBeforeMean = pre?.CpuPercent,
                CpuAfterMean = post?.CpuPercent,
                MemBeforeMean = pre?.MemoryUsedPercent,
                MemAfterMean = post?.MemoryUsedPercent,
                Issue = step.Error,
            };

            if (changelogUrls != null &&
                changelogUrls.TryGetValue(ChangelogKey(step.Model, step.ToVersion), out var url))
            {
                row.ChangelogUrl = url;
            }

            report.Rows.Add(row);
        }

        // Add the gateway as a row when the OS step ran and it isn't already a device step.
        if (document.IncludesUniFiOsUpdate && !string.IsNullOrWhiteSpace(document.ConsoleMac)
            && !report.Rows.Any(r => string.Equals(r.Mac, document.ConsoleMac, StringComparison.OrdinalIgnoreCase)))
        {
            var osPre = ParseStats(document.UniFiOsUpdate.PreStatsJson);
            var osPost = ParseStats(document.UniFiOsUpdate.PostStatsJson);
            var osOutcome = document.UniFiOsUpdate.Outcome switch
            {
                "updated" => RolloutOutcomes.Upgraded,
                "stuck" => RolloutOutcomes.Failed,
                _ => document.UniFiOsUpdate.Outcome ?? RolloutOutcomes.Skipped,
            };
            report.Rows.Add(new RolloutReportRow
            {
                Mac = document.ConsoleMac,
                Name = "Console (UniFi OS)",
                Model = "Cloud Gateway",
                DeviceType = "ugw",
                FromVersion = document.UniFiOsUpdate.FromVersion,
                ToVersion = document.UniFiOsUpdate.TargetVersion,
                UpgradedAt = document.UniFiOsUpdate.BackAt,
                DowntimeSeconds = document.UniFiOsUpdate is { WentDownAt: DateTime osDown, BackAt: DateTime osBack }
                    ? (int)Math.Max(0, (osBack - osDown).TotalSeconds)
                    : null,
                Outcome = osOutcome,
                CpuBeforeMean = osPre?.CpuPercent,
                CpuAfterMean = osPost?.CpuPercent,
                MemBeforeMean = osPre?.MemoryUsedPercent,
                MemAfterMean = osPost?.MemoryUsedPercent,
            });
        }

        report.DevicesUpgraded = report.Rows.Count(r => r.Outcome is RolloutOutcomes.Upgraded or RolloutOutcomes.RegressionFlagged);
        report.DevicesRolledBack = report.Rows.Count(r => r.Outcome == RolloutOutcomes.RolledBack);
        report.DevicesFailed = report.Rows.Count(r => r.Outcome == RolloutOutcomes.Failed);
        report.DevicesSkipped = report.Rows.Count(r => r.Outcome is RolloutOutcomes.Skipped or RolloutOutcomes.Held);

        report.SiteCpuBeforeMean = PairedMean(report.Rows, r => r.CpuBeforeMean, r => r.CpuAfterMean, before: true);
        report.SiteCpuAfterMean = PairedMean(report.Rows, r => r.CpuBeforeMean, r => r.CpuAfterMean, before: false);
        report.SiteMemBeforeMean = PairedMean(report.Rows, r => r.MemBeforeMean, r => r.MemAfterMean, before: true);
        report.SiteMemAfterMean = PairedMean(report.Rows, r => r.MemBeforeMean, r => r.MemAfterMean, before: false);

        AddIssues(report, document);
        return report;
    }

    /// <summary>
    /// What became of one device. A step whose target is the version it started the rollout on has
    /// been rolled back - the rollback path re-runs the step with the prior version as its target.
    /// </summary>
    private static string OutcomeFor(FirmwareRolloutStep step, RolloutPlanDocument document)
    {
        var upgraded = step.State is FirmwareRolloutStepState.LitmusPassed or FirmwareRolloutStepState.RegressionFlagged;
        if (upgraded && WasRolledBack(step, document))
            return RolloutOutcomes.RolledBack;

        return step.State switch
        {
            FirmwareRolloutStepState.LitmusPassed => RolloutOutcomes.Upgraded,
            FirmwareRolloutStepState.RegressionFlagged => RolloutOutcomes.RegressionFlagged,
            FirmwareRolloutStepState.Failed => RolloutOutcomes.Failed,
            FirmwareRolloutStepState.SkippedExcluded or FirmwareRolloutStepState.AbortedSku => RolloutOutcomes.Skipped,
            FirmwareRolloutStepState.Held or FirmwareRolloutStepState.Pending => RolloutOutcomes.Held,
            _ => RolloutOutcomes.InProgress,
        };
    }

    private static bool WasRolledBack(FirmwareRolloutStep step, RolloutPlanDocument document)
    {
        var prior = document.PriorVersions
            .FirstOrDefault(p => string.Equals(p.Mac, step.DeviceMac, StringComparison.OrdinalIgnoreCase));
        return prior?.Version != null
            && string.Equals(prior.Version, step.ToVersion, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(step.FromVersion, step.ToVersion, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Site mean over the devices measured on BOTH sides. A device that only reported afterwards
    /// would otherwise move one end of the comparison and not the other.
    /// </summary>
    private static double? PairedMean(
        List<RolloutReportRow> rows,
        Func<RolloutReportRow, double?> beforeOf,
        Func<RolloutReportRow, double?> afterOf,
        bool before)
    {
        var paired = rows.Where(r => beforeOf(r).HasValue && afterOf(r).HasValue).ToList();
        if (paired.Count == 0) return null;
        return paired.Average(r => (before ? beforeOf(r) : afterOf(r))!.Value);
    }

    private static void AddIssues(RolloutReport report, RolloutPlanDocument document)
    {
        foreach (var row in report.Rows)
        {
            if (!string.IsNullOrWhiteSpace(row.Issue))
            {
                report.Issues.Add($"{row.Name} ({row.Model}): {row.Issue}");
            }
            else if (row.Outcome == RolloutOutcomes.RegressionFlagged)
            {
                report.Issues.Add(
                    $"{row.Name} ({row.Model}) is working harder on {row.ToVersion ?? "its new firmware"} than it was on {row.FromVersion ?? "its previous firmware"}.");
            }
        }

        switch (report.UniFiNetworkUpdateOutcome)
        {
            case "stuck":
                report.Issues.Add("The UniFi Network application did not come back after its update.");
                break;
        }

        switch (report.UniFiOsUpdateOutcome)
        {
            case "stuck":
                report.Issues.Add("The console has not answered since its UniFi OS update.");
                break;
            case "unchanged":
                report.Issues.Add(
                    $"The console accepted UniFi OS {document.UniFiOsUpdate.TargetVersion ?? "its newest build"} but is still offering it.");
                break;
            case "refused":
                report.Issues.Add("The console would not take its UniFi OS update.");
                break;
        }
    }

    private static RolloutResourceStats? ParseStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RolloutResourceStats>(json); }
        catch (JsonException) { return null; }
    }
}
