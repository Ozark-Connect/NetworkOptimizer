using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Where a surfaced rollout is, coarser than <see cref="FirmwareRolloutStatus"/>.</summary>
public enum SiteRolloutStage
{
    Scheduled,
    Running,
    Paused,
    Soaking,
    Completed,
    Partial,
    Failed,
}

/// <summary>
/// A site's rollout worth showing outside the Firmware Rollout page: one scheduled, running,
/// paused, or soaking, or one whose report landed in the last <see cref="RecentWindow"/>.
/// </summary>
/// <param name="PlanId">The plan, for the report link on a finished rollout.</param>
/// <param name="Stage">Where the rollout is.</param>
/// <param name="At">Scheduled start, start, or report time, matching <paramref name="Stage"/>.</param>
public sealed record SiteRolloutStatus(int PlanId, SiteRolloutStage Stage, DateTime? At)
{
    /// <summary>How long a finished rollout stays surfaced after its report.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);

    /// <summary>How soon a scheduled rollout has to start to count as <see cref="NeedsAttention"/>.</summary>
    public static readonly TimeSpan ImminentWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// True while the rollout is running, paused, or soaking, or scheduled to start within
    /// <see cref="ImminentWindow"/>: the site ranks above minor health issues on the Dashboard.
    /// </summary>
    public bool NeedsAttention(DateTime nowUtc) => Stage switch
    {
        SiteRolloutStage.Running or SiteRolloutStage.Paused or SiteRolloutStage.Soaking => true,
        SiteRolloutStage.Scheduled => At is not { } at || at - nowUtc <= ImminentWindow,
        _ => false,
    };

    /// <summary>True once the soak is over and the report exists.</summary>
    public bool IsFinished => Stage is SiteRolloutStage.Completed or SiteRolloutStage.Partial or SiteRolloutStage.Failed;

    /// <summary>"Rollout scheduled", "Rollout running": a short label for pills and tiles.</summary>
    public string Label => Stage switch
    {
        SiteRolloutStage.Scheduled => "Rollout scheduled",
        SiteRolloutStage.Running => "Rollout running",
        SiteRolloutStage.Paused => "Rollout paused",
        SiteRolloutStage.Soaking => "Rollout soaking",
        SiteRolloutStage.Completed => "Rollout complete",
        SiteRolloutStage.Partial => "Rollout partial",
        _ => "Rollout failed",
    };

    /// <summary>"info", "warning", "danger", "success", or "muted", for color.</summary>
    public string Tone => Stage switch
    {
        SiteRolloutStage.Running or SiteRolloutStage.Soaking => "info",
        SiteRolloutStage.Paused or SiteRolloutStage.Partial => "warning",
        SiteRolloutStage.Failed => "danger",
        SiteRolloutStage.Completed => "success",
        _ => "muted",
    };

    /// <summary>The Firmware Rollout page; a finished rollout opens its report.</summary>
    public string Href => IsFinished ? $"/firmware-rollout?report={PlanId}" : "/firmware-rollout";

    /// <summary>The label with its time, relative to <paramref name="nowUtc"/>: "Rollout scheduled, starts in 5 hours".</summary>
    public string Describe(DateTime nowUtc)
    {
        if (At is not { } at) return Label;
        var span = Span((at - nowUtc).Duration());
        return Stage switch
        {
            SiteRolloutStage.Scheduled => at > nowUtc ? $"{Label}, starts in {span}" : $"{Label}, starting now",
            SiteRolloutStage.Running or SiteRolloutStage.Paused => $"{Label}, started {span} ago",
            SiteRolloutStage.Soaking => $"{Label}, upgrades finished {span} ago",
            _ => $"{Label}, reported {span} ago",
        };
    }

    /// <summary>
    /// The rollout to surface from a site's plans: its active plan unless that is still a Draft,
    /// else its newest report within <see cref="RecentWindow"/>. Aborted plans are not surfaced.
    /// </summary>
    /// <param name="active">The site's non-terminal plan, or null.</param>
    /// <param name="recent">The site's plans, newest first.</param>
    /// <param name="nowUtc">The current time.</param>
    public static SiteRolloutStatus? From(FirmwareRolloutPlan? active, IEnumerable<FirmwareRolloutPlan> recent, DateTime nowUtc)
    {
        if (active is { Status: not FirmwareRolloutStatus.Draft } a)
        {
            return a.Status switch
            {
                FirmwareRolloutStatus.Scheduled or FirmwareRolloutStatus.Announced => new(a.Id, SiteRolloutStage.Scheduled, a.ScheduledStartAt),
                FirmwareRolloutStatus.Paused => new(a.Id, SiteRolloutStage.Paused, a.StartedAt),
                FirmwareRolloutStatus.SoakWait => new(a.Id, SiteRolloutStage.Soaking, a.CompletedAt),
                _ => new(a.Id, SiteRolloutStage.Running, a.StartedAt),
            };
        }

        foreach (var plan in recent)
        {
            if (plan.Status is not (FirmwareRolloutStatus.Reported or FirmwareRolloutStatus.Failed)) continue;
            var report = RolloutReport.Parse(plan.ReportJson);
            var at = report?.GeneratedAt is { } g && g != default ? g : plan.CompletedAt;
            if (at is not { } finished || nowUtc - finished > RecentWindow) continue;
            var failed = report != null && (report.DevicesFailed > 0 || report.DevicesRolledBack > 0);
            var stage = plan.Status == FirmwareRolloutStatus.Failed || (failed && report!.TotalUpgraded == 0) ? SiteRolloutStage.Failed
                : failed ? SiteRolloutStage.Partial : SiteRolloutStage.Completed;
            return new(plan.Id, stage, finished);
        }
        return null;
    }

    /// <summary>Reads a site's rollout to surface from its repository.</summary>
    /// <param name="repository">The site's rollout store.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<SiteRolloutStatus?> ReadAsync(IFirmwareRolloutRepository repository, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var active = await repository.GetActivePlanAsync(cancellationToken);
        if (active is { Status: not FirmwareRolloutStatus.Draft }) return From(active, [], nowUtc);
        return From(null, await repository.GetPlanHistoryAsync(5, cancellationToken), nowUtc);
    }

    private static string Span(TimeSpan t) => TimeFormatHelper.FormatDuration(t < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : t);
}
