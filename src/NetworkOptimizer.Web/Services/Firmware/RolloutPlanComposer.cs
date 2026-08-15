using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Everything gathered from the live site before a plan can be ordered.</summary>
/// <param name="Context">Devices, coverage and console state, frozen at plan time.</param>
/// <param name="Estimator">Downtime estimates (seeds, this site's history, and the other sites').</param>
/// <param name="CurrentChannel">The release channel devices follow today.</param>
/// <param name="Console">The console as it answered at plan time, or null when it did not.</param>
public sealed record RolloutPlanInputs(
    RolloutPlanningContext Context,
    FirmwareTimingEstimator Estimator,
    string CurrentChannel,
    NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? Console = null,
    IReadOnlyList<PlanTargetImage>? TargetImages = null);

/// <summary>
/// The one path a plan is built through, wizard and autopilot alike: refresh the catalog, freeze
/// the site, compose the estimator, then order it. Shared so the unattended path cannot drift from
/// the one an admin sees in the preview.
/// </summary>
public static class RolloutPlanComposer
{
    /// <summary>
    /// Freezes the site for planning. The catalog refresh comes first and is not optional: it is
    /// UniFi's own "Check for Updates", so it stages the builds the plan is about to command.
    /// </summary>
    /// <param name="planning">The site's planning source.</param>
    /// <param name="siteTimings">This site's learned model timings.</param>
    /// <param name="commands">Firmware command surface.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<RolloutPlanInputs> GatherAsync(
        IRolloutPlanningSource planning,
        IReadOnlyList<FirmwareModelTiming> siteTimings,
        IFirmwareCommandClient commands,
        FirmwareRolloutSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(commands);

        var catalog = await commands.CheckForUpdatesAsync(cancellationToken);

        var context = await planning.GetContextAsync(cancellationToken);
        var estimator = await planning.GetEstimatorAsync(siteTimings ?? [], cancellationToken);
        var currentChannel = await commands.GetDeviceChannelAsync(cancellationToken);
        var console = await commands.GetConsoleSystemInfoAsync(cancellationToken);

        var images = new List<PlanTargetImage>();
        if (settings != null)
        {
            CaptureImages(images, context, settings, currentChannel, currentChannel, catalog);
            currentChannel = await StageEveryPlannedChannelAsync(
                planning, commands, context, settings, currentChannel, images, cancellationToken);
            console = await StageNetworkAppChannelAsync(commands, console, settings, cancellationToken);
        }

        return new RolloutPlanInputs(
            context,
            estimator,
            string.IsNullOrEmpty(currentChannel) ? FirmwareChannels.Release : currentChannel,
            console,
            images);
    }

    /// <summary>
    /// Gives every device the target version for the channel IT is planned on.
    ///
    /// The console holds one channel's catalog at a time - "change channel, re-run list-available,
    /// get that channel's URLs" - so with a per-model or per-type override in play, one refresh
    /// can only ever describe part of the plan. Each distinct channel is therefore visited once
    /// and the devices belonging to it take their target from that pass. The channel is not
    /// restored: planning on a channel is committing to it, and the rollout sets it again per wave.
    /// </summary>
    /// <returns>The channel the console was left on.</returns>
    private static async Task<string?> StageEveryPlannedChannelAsync(
        IRolloutPlanningSource planning,
        IFirmwareCommandClient commands,
        RolloutPlanningContext context,
        FirmwareRolloutSettings settings,
        string? currentChannel,
        List<PlanTargetImage> images,
        CancellationToken cancellationToken)
    {
        var wanted = context.Devices
            .Where(d => d.Upgradable)
            .Select(d => RolloutPlanner.ResolveChannel(d, settings))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Already-current channel first, so a single-channel plan costs nothing extra; the global
        // channel last, so the console is left somewhere predictable rather than on whichever
        // override happened to be visited last. Waves set their own channel at run time either way.
        var ordered = wanted
            .OrderByDescending(c => string.Equals(c, currentChannel, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => string.Equals(c, settings.GlobalChannel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var channel in ordered)
        {
            // The channel already active needs nothing: the context in hand was read on it.
            if (string.Equals(channel, currentChannel, StringComparison.OrdinalIgnoreCase)) continue;

            // A refused write is the Early Access gate being off. Those devices keep no target
            // rather than being quoted the channel we failed to leave.
            if (!await commands.SetDeviceChannelAsync(channel, cancellationToken)) continue;
            currentChannel = channel;
            var catalog = await commands.CheckForUpdatesAsync(cancellationToken);

            var staged = await planning.GetContextAsync(cancellationToken);
            var byMac = staged.Devices.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
            foreach (var device in context.Devices.Where(d =>
                d.Upgradable && string.Equals(RolloutPlanner.ResolveChannel(d, settings), channel, StringComparison.OrdinalIgnoreCase)))
            {
                device.ToVersion = byMac.TryGetValue(device.Mac, out var fresh) ? fresh.ToVersion : null;

                // A less aggressive channel can offer an OLDER build than the device is running,
                // and the console presents that as an available update. Moving a channel down is
                // not an instruction to roll firmware back, so nothing is planned for it.
                // TODO: a deliberate fleet-wide downgrade is a separate, opt-in mode - the
                // per-device rollback already exists, this is the broad version of it.
                if (!NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(device.ToVersion, device.FromVersion))
                {
                    device.ToVersion = null;
                    device.Upgradable = false;
                }
            }

            CaptureImages(images, context, settings, channel, channel, catalog);
        }

        return currentChannel;
    }

    /// <summary>
    /// Records the direct image URL for every device planned on <paramref name="channel"/>, from
    /// the catalog that channel just produced. Commanding by URL is what frees the rollout from
    /// having to put the console back on each channel as it goes.
    /// </summary>
    private static void CaptureImages(
        List<PlanTargetImage> images,
        RolloutPlanningContext context,
        FirmwareRolloutSettings settings,
        string channel,
        string? stagedChannel,
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> catalog)
    {
        if (!string.Equals(channel, stagedChannel, StringComparison.OrdinalIgnoreCase)) return;

        foreach (var device in context.Devices.Where(d => d.Upgradable
            && string.Equals(RolloutPlanner.ResolveChannel(d, settings), channel, StringComparison.OrdinalIgnoreCase)))
        {
            if (images.Any(i => string.Equals(i.Mac, device.Mac, StringComparison.OrdinalIgnoreCase))) continue;

            var entry = catalog.FirstOrDefault(e =>
                string.Equals(e.BaseModel, device.Model, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Device, device.Model, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(entry?.Url)) continue;

            images.Add(new PlanTargetImage { Mac = device.Mac, Version = entry.Version ?? device.ToVersion, Url = entry.Url });
        }
    }

    /// <summary>
    /// Puts the UniFi Network application on its planned channel before its update is read.
    ///
    /// The console describes the application with one releaseChannel and one updateAvailable, so
    /// that version is whatever the channel it is on offers - unlike UniFi OS, which publishes a
    /// latestByChannel map and needs no switching to be read correctly.
    /// </summary>
    /// <returns>The console as it reads on the planned channel, or as it was when nothing changed.</returns>
    private static async Task<NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo?> StageNetworkAppChannelAsync(
        IFirmwareCommandClient commands,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        FirmwareRolloutSettings settings,
        CancellationToken cancellationToken)
    {
        if (!settings.IncludeUniFiNetwork || !ConsoleReachable(console)) return console;

        var wanted = settings.EffectiveNetworkAppChannel;
        var current = console!.NetworkApplication?.ReleaseChannel;
        if (string.IsNullOrEmpty(wanted) || string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
            return console;

        if (!await commands.SetConsoleChannelsAsync(wanted, null, cancellationToken))
            return console;

        return await commands.GetConsoleSystemInfoAsync(cancellationToken) ?? console;
    }

    /// <summary>Orders a plan from the frozen inputs.</summary>
    /// <param name="inputs">What the site looked like at plan time.</param>
    /// <param name="settings">Settings to plan against.</param>
    /// <param name="additionalExcludedMacs">Devices excluded on top of the settings (the ripeness gate).</param>
    public static RolloutPlanResult Plan(
        RolloutPlanInputs inputs,
        FirmwareRolloutSettings settings,
        IReadOnlyCollection<string>? additionalExcludedMacs = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(settings);

        var result = PlanCore(inputs, settings, additionalExcludedMacs);
        if (inputs.TargetImages is { Count: > 0 })
            result.Document.TargetImages = inputs.TargetImages.ToList();
        return result;
    }

    private static RolloutPlanResult PlanCore(
        RolloutPlanInputs inputs,
        FirmwareRolloutSettings settings,
        IReadOnlyCollection<string>? additionalExcludedMacs)
    {
        return new RolloutPlanner().Plan(new RolloutPlanningInput
        {
            Devices = inputs.Context.Devices,
            Settings = settings,
            Estimator = inputs.Estimator,
            CurrentConsoleChannel = inputs.CurrentChannel,
            Neighbors = inputs.Context.Neighbors,
            AdditionalExcludedMacs = additionalExcludedMacs ?? [],
            NetworkAppUpdateAvailable = HasNetworkAppUpdate(inputs.Console),
            UniFiOsUpdateAvailable = HasUniFiOsUpdate(inputs.Console, settings.EffectiveUniFiOsChannel),
        });
    }

    /// <summary>
    /// Whether the console is offering an application update. A console we cannot reach at all
    /// (an API-key connection answers with nothing) cannot be updated, so that is a no; a console
    /// that answered but does not describe the application is an older shape, and that is a yes
    /// rather than a silently dropped step.
    /// </summary>
    private static bool HasNetworkAppUpdate(NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console)
    {
        if (!ConsoleReachable(console)) return false;

        var app = console!.NetworkApplication;
        if (app == null) return true;
        return !string.IsNullOrEmpty(app.UpdateAvailable);
    }

    /// <summary>Whether /api/system answered with anything at all.</summary>
    public static bool ConsoleReachable(NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console) =>
        console != null && (console.Firmware != null || console.Apps != null);

    /// <summary>Whether the chosen channel offers a UniFi OS build the console is not already on.</summary>
    private static bool HasUniFiOsUpdate(
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console, string channel)
    {
        if (!ConsoleReachable(console)) return false;
        if (console?.Firmware?.LatestByChannel is not { } byChannel) return true;
        if (!byChannel.TryGetValue(channel, out var release) || string.IsNullOrEmpty(release?.Version)) return true;

        var installed = console.InstalledOsVersion;
        if (string.IsNullOrEmpty(installed)) return true;

        return !string.Equals(
            NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.Short(release.Version),
            NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.Short(installed),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Steps that would actually be commanded, i.e. everything not excluded up front.</summary>
    /// <param name="result">A planned rollout.</param>
    public static int LiveStepCount(RolloutPlanResult result) =>
        result?.Steps.Count(s => s.State != FirmwareRolloutStepState.SkippedExcluded) ?? 0;
}
