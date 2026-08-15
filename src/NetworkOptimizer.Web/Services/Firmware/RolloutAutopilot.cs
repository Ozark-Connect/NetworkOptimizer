using System.Text.Json;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Autopilot's plan-building pass, driven by the site's executor tick.</summary>
public interface IRolloutAutopilot
{
    /// <summary>
    /// Builds and announces the next unattended rollout when the site is due one. Does nothing at
    /// all unless the site is on autopilot, nothing is already in flight, and there is something
    /// new to install.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new plan's id, or null when nothing was created.</returns>
    Task<int?> CreatePlanIfDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One site's autopilot: hourly, it re-checks the console for updates, plans the same way the
/// wizard does, and books the result into the next quiet window far enough out for the heads-up
/// alert to be worth anything.
/// <para>
/// Two rules keep it from nagging. Nothing is ever planned while a plan is in flight, so a
/// postponed run is not instantly replaced by a new one. And after an autopilot run is aborted, the
/// same set of target versions is never proposed again - the site said no to it - so autopilot
/// waits for genuinely new firmware before asking a second time.
/// </para>
/// </summary>
public class RolloutAutopilot : IRolloutAutopilot
{
    /// <summary>Plan author for unattended runs, and the marker the abort gate looks for.</summary>
    public const string Actor = "autopilot";

    /// <summary>How often the site is considered for a new plan.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    private readonly IFirmwareRolloutRepositoryAccessor _repositories;
    private readonly IRolloutPlanningScope _planning;
    private readonly IFirmwareCommandClient _commands;
    private readonly IReleaseMetadataSource _releases;
    private readonly IAlertEventBus _eventBus;
    private readonly TimeProvider _time;
    private readonly ILogger<RolloutAutopilot> _logger;
    private readonly string _siteSlug;
    private readonly string _siteSuffix;

    private DateTime _lastCheckedAt = DateTime.MinValue;

    /// <param name="repositories">Site-pinned rollout repository access.</param>
    /// <param name="planning">Site-pinned planning source access.</param>
    /// <param name="commands">Firmware command surface.</param>
    /// <param name="releases">Publish dates for the ripeness gate.</param>
    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance plans for.</param>
    public RolloutAutopilot(
        IFirmwareRolloutRepositoryAccessor repositories,
        IRolloutPlanningScope planning,
        IFirmwareCommandClient commands,
        IReleaseMetadataSource releases,
        IAlertEventBus eventBus,
        TimeProvider time,
        ILogger<RolloutAutopilot> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _repositories = repositories;
        _planning = planning;
        _commands = commands;
        _releases = releases;
        _eventBus = eventBus;
        _time = time;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSuffix = _siteSlug == SiteManagementService.DefaultSiteSlug ? "" : $" (site {_siteSlug})";
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<int?> CreatePlanIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (Now - _lastCheckedAt < CheckInterval)
            return null;

        var settings = await _repositories.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        if (settings.Mode != FirmwareRolloutMode.Autopilot)
            return null;

        _lastCheckedAt = Now;

        // Anything non-terminal owns the site: a scheduled, announced, postponed, running or
        // soaking plan all mean autopilot has nothing to add.
        var active = await _repositories.UseAsync((r, c) => r.GetActivePlanAsync(c), cancellationToken);
        if (active != null)
            return null;

        var timings = await _repositories.UseAsync((r, c) => r.GetModelTimingsAsync(c), cancellationToken);
        var inputs = await _planning.UseAsync(
            (p, c) => RolloutPlanComposer.GatherAsync(p, timings, _commands, settings, _logger, c), cancellationToken);

        var ripeness = await EvaluateRipenessAsync(inputs.Context.Devices, settings.MinReleaseAgeDays, cancellationToken);
        var result = RolloutPlanComposer.Plan(inputs, settings, ripeness.UnripeMacs);
        result.Document.Notes.AddRange(ripeness.Notes);

        // A console update is reason enough to run. On a Cloud Gateway the console's own UniFi OS
        // build waits while every device reports nothing pending, so counting devices alone would
        // leave that site behind for good.
        var devicesToUpgrade = RolloutPlanComposer.LiveStepCount(result);
        var consoleToUpgrade = result.Document.IncludesUniFiNetworkUpdate || result.Document.IncludesUniFiOsUpdate;
        if (devicesToUpgrade == 0 && !consoleToUpgrade)
        {
            _logger.LogDebug(
                "Autopilot found nothing to upgrade on site {Site} right now", _siteSlug);
            return null;
        }

        await ApplyUniFiOsRipenessAsync(result.Document, settings.MinReleaseAgeDays, cancellationToken);

        if (await WasAlreadyRefusedAsync(result, cancellationToken))
        {
            _logger.LogDebug(
                "Autopilot is holding off on site {Site}: the last unattended rollout was stopped and nothing new has been released since",
                _siteSlug);
            return null;
        }

        var lead = TimeSpan.FromHours(Math.Max(1, settings.NotifyHoursAhead));
        var window = await _planning.UseAsync(
            (p, c) => p.ProposeWindowAsync(inputs.Context, result.Document.TotalEstimatedSeconds, settings, lead, c),
            cancellationToken);
        // The proposal already carries the instant its site-local hour names, converted through the
        // SITE's zone. Re-deriving it here read those hours as the server's and fired a remote site
        // off by the offset between them.
        var startAtUtc = window.StartUtc;

        await _planning.UseAsync(
            (p, c) => p.PopulatePriorVersionsAsync(result.Document, result.Steps, c), cancellationToken);

        var plan = await _repositories.UseAsync((r, c) => r.CreatePlanAsync(new FirmwareRolloutPlan
        {
            Status = FirmwareRolloutStatus.Announced,
            ScheduledStartAt = startAtUtc,
            PlanJson = JsonSerializer.Serialize(result.Document),
            CreatedBy = Actor,
            CreatedAt = Now,
        }, c), cancellationToken);

        foreach (var step in result.Steps) step.PlanId = plan.Id;
        await _repositories.UseAsync((r, c) => r.AddStepsAsync(result.Steps, c), cancellationToken);

        await AnnounceAsync(result, window, startAtUtc, cancellationToken);

        _logger.LogInformation(
            "Autopilot scheduled firmware rollout {Id} on site {Site} for {When} ({Devices} devices, {Waves} waves)",
            plan.Id, _siteSlug, startAtUtc, RolloutPlanComposer.LiveStepCount(result), result.Document.Waves.Count);

        return plan.Id;
    }

    /// <summary>
    /// The site's own reading of the start, for the one place it matters most: an alert is read out
    /// of context, and whether a 3 AM reboot bothers anyone is a question about the site's hours.
    /// Empty when the site keeps the server's, which is every single-site install.
    /// </summary>
    private static string SiteAside(QuietWindowProposal window, DateTime startAtUtc)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(window.TimeZoneId);
            if (tz.BaseUtcOffset == TimeZoneInfo.Local.BaseUtcOffset) return string.Empty;

            var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(startAtUtc, DateTimeKind.Utc), tz);
            return $" ({at:h:mm tt} at-site)";
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return string.Empty;
        }
    }

    private async Task AnnounceAsync(
        RolloutPlanResult result, QuietWindowProposal window, DateTime startAtUtc, CancellationToken cancellationToken)
    {
        var devices = RolloutPlanComposer.LiveStepCount(result);
        var hours = Math.Max(0, (startAtUtc - Now).TotalHours);

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = RolloutAlerts.Upcoming,
            Source = RolloutAlerts.Source,
            Severity = AlertSeverity.Info,
            Title = $"Firmware Rollout Scheduled{_siteSuffix}",
            Message =
                $"{devices} device{(devices == 1 ? "" : "s")} will be upgraded starting "
                + $"{startAtUtc.ToLocalTime():ddd MMM d, h:mm tt}{SiteAside(window, startAtUtc)}, "
                + $"in about {hours:0} hour{(Math.Round(hours) == 1 ? "" : "s")} - chosen from {window.Basis}. "
                + "Open Firmware Rollout to postpone or stop it.",
            SourceUrl = RolloutAlerts.SourceUrl,
        }, cancellationToken);
    }

    /// <summary>Unripe devices and the notes explaining what they are waiting for.</summary>
    private sealed record RipenessOutcome(HashSet<string> UnripeMacs, List<string> Notes);

    /// <summary>
    /// Holds back devices whose target build is younger than the site's minimum release age. A
    /// publish date that cannot be resolved counts as ripe: a feed outage must never stall
    /// autopilot, and the plan records that it happened.
    /// </summary>
    private async Task<RipenessOutcome> EvaluateRipenessAsync(
        IReadOnlyList<PlannerDevice> devices, int minAgeDays, CancellationToken cancellationToken)
    {
        var unripe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();
        if (minAgeDays <= 0) return new RipenessOutcome(unripe, notes);

        var unresolved = 0;
        var groups = devices
            .Where(d => d.Upgradable && !string.IsNullOrWhiteSpace(d.ToVersion))
            .GroupBy(d => (d.Model, d.ToVersion), TargetComparer.Instance);

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReleaseMetadata? metadata = null;
            try
            {
                metadata = await _releases.GetAsync(group.Key.Model, group.Key.ToVersion, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Autopilot could not read the publish date for {Model} {Version}",
                    group.Key.Model, group.Key.ToVersion);
            }

            if (metadata?.PublishedAt == null)
            {
                unresolved++;
                continue;
            }

            if (ReleaseRipeness.IsRipe(metadata.PublishedAt, Now, minAgeDays))
                continue;

            var count = 0;
            foreach (var device in group)
            {
                unripe.Add(device.Mac);
                count++;
            }

            var age = ReleaseRipeness.AgeDays(metadata.PublishedAt, Now) ?? 0;
            notes.Add(
                $"{count} {group.Key.Model} device{(count == 1 ? " is" : "s are")} waiting for {group.Key.ToVersion} to age {minAgeDays} days; it was published {age} day{(age == 1 ? "" : "s")} ago.");
        }

        if (unresolved > 0)
        {
            notes.Add(
                $"No publish date could be read for {unresolved} build{(unresolved == 1 ? "" : "s")}, so nothing was held back for age on {(unresolved == 1 ? "it" : "them")}.");
        }

        return new RipenessOutcome(unripe, notes);
    }

    /// <summary>
    /// Drops the console's own UniFi OS step when the build on offer is too new. Its publish date
    /// comes from the console rather than the feed - the feed does not carry Cloud Gateway OS builds
    /// per console model.
    /// </summary>
    private async Task ApplyUniFiOsRipenessAsync(
        RolloutPlanDocument document, int minAgeDays, CancellationToken cancellationToken)
    {
        if (minAgeDays <= 0 || !document.IncludesUniFiOsUpdate)
            return;

        DateTime? created = null;
        string? version = null;
        try
        {
            var pending = await _commands.GetPendingUniFiOsUpdateAsync(cancellationToken);
            created = pending?.Created;
            version = pending?.Version;

            if (created == null)
            {
                var info = await _commands.GetConsoleSystemInfoAsync(cancellationToken);
                created = info?.Firmware?.Latest?.Created;
                version ??= info?.Firmware?.Latest?.Version;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Autopilot could not read the console's UniFi OS publish date on site {Site}", _siteSlug);
        }

        if (ReleaseRipeness.IsRipe(created, Now, minAgeDays))
            return;

        var age = ReleaseRipeness.AgeDays(created, Now) ?? 0;
        document.IncludesUniFiOsUpdate = false;
        document.Notes.Add(
            $"UniFi OS {version ?? "the pending build"} is waiting to age {minAgeDays} days; it was published {age} day{(age == 1 ? "" : "s")} ago, so the console keeps its current build.");
    }

    /// <summary>
    /// Whether this plan is the one the site already stopped. The comparison is the set of device
    /// targets: anything the aborted plan did not carry is new firmware, and worth asking about.
    /// </summary>
    private async Task<bool> WasAlreadyRefusedAsync(RolloutPlanResult result, CancellationToken cancellationToken)
    {
        var history = await _repositories.UseAsync((r, c) => r.GetPlanHistoryAsync(1, c), cancellationToken);
        var last = history.FirstOrDefault();
        if (last is not { Status: FirmwareRolloutStatus.Aborted } ||
            !string.Equals(last.CreatedBy, Actor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var refused = TargetsOf(ParseDocument(last));
        var proposed = TargetsOf(result.Document);
        return proposed.Count > 0 && proposed.IsSubsetOf(refused);
    }

    private static HashSet<string> TargetsOf(RolloutPlanDocument document) =>
        document.Waves
            .SelectMany(w => w.Steps)
            .Select(s => $"{s.Mac}|{s.ToVersion}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static RolloutPlanDocument ParseDocument(FirmwareRolloutPlan plan)
    {
        try
        {
            return JsonSerializer.Deserialize<RolloutPlanDocument>(plan.PlanJson) ?? new RolloutPlanDocument();
        }
        catch (JsonException)
        {
            return new RolloutPlanDocument();
        }
    }


    /// <summary>Groups devices by the exact build they are heading to, model included.</summary>
    private sealed class TargetComparer : IEqualityComparer<(string Model, string? ToVersion)>
    {
        public static readonly TargetComparer Instance = new();

        public bool Equals((string Model, string? ToVersion) x, (string Model, string? ToVersion) y) =>
            string.Equals(x.Model, y.Model, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.ToVersion, y.ToVersion, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Model, string? ToVersion) obj) =>
            HashCode.Combine(
                obj.Model?.ToLowerInvariant(),
                obj.ToVersion?.ToLowerInvariant());
    }
}
