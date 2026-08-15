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
        ILogger? logger = null,
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
            if (!string.IsNullOrEmpty(currentChannel))
                CaptureImages(images, context, settings, currentChannel, currentChannel, catalog);
            currentChannel = await StageEveryPlannedChannelAsync(
                planning, commands, context, settings, currentChannel, images, catalog, logger, cancellationToken);
            console = await StageConsoleChannelsAsync(commands, console, settings, cancellationToken);
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
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> priorCatalog,
        ILogger? logger,
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

            var catalog = await WaitForCatalogAsync(commands, priorCatalog, channel, logger, cancellationToken);
            priorCatalog = catalog;

            var staged = await planning.GetContextAsync(cancellationToken);
            var byMac = staged.Devices.ToDictionary(d => d.Mac, StringComparer.OrdinalIgnoreCase);
            foreach (var device in context.Devices.Where(d =>
                d.Upgradable && string.Equals(RolloutPlanner.ResolveChannel(d, settings), channel, StringComparison.OrdinalIgnoreCase)))
            {
                // Upgradable travels with the version: it was read on the OLD channel, and a device
                // this channel has nothing for is not a candidate however it looked before. Keeping
                // one and replacing the other is what put a device on a build from neither.
                var known = byMac.TryGetValue(device.Mac, out var fresh);
                device.ToVersion = known ? fresh!.ToVersion : null;
                device.Upgradable = known && fresh!.Upgradable;

            }

            CaptureImages(images, context, settings, channel, channel, catalog);
        }

        DropDowngrades(context);
        return currentChannel;
    }

    /// <summary>
    /// Drops every device the console is offering an OLDER build than it already runs.
    ///
    /// A channel is a line to follow, not a direction to move: selecting a less aggressive one
    /// makes the console present its build as an available update even when it is behind, so an
    /// Early Access console offered 6.5.87 to a bridge on 6.5.89 and 7.4.1 to a switch on 7.5.9.
    /// Applied to every device, not only ones whose channel was switched - the devices already on
    /// the console's channel are exactly the ones a plan is most likely to contain.
    /// TODO: a deliberate fleet-wide downgrade is a separate, opt-in mode. The per-device rollback
    /// already exists; this would be the broad version of it.
    /// </summary>
    private static void DropDowngrades(RolloutPlanningContext context)
    {
        foreach (var device in context.Devices.Where(d => d.Upgradable))
        {
            if (NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(device.ToVersion, device.FromVersion))
                continue;

            device.ToVersion = null;
            device.Upgradable = false;
        }
    }

    /// <summary>How long a channel change is given to appear in the catalog.</summary>
    private static readonly TimeSpan CatalogReflectWait = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Re-runs Check for Updates until the console has actually restaged for the channel just set.
    ///
    /// The console does not repopulate instantly. Reading the device list too early returns the
    /// PREVIOUS channel's offers, which then read as downgrades and are dropped - so a device with
    /// a real update on the new channel shows none at all, which is worse than showing the wrong
    /// one. The catalog changing is the console having restaged; unchanged means still working, or
    /// two channels genuinely offering the same builds, which is why this is bounded rather than
    /// waited on indefinitely.
    /// </summary>
    private static async Task<IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry>> WaitForCatalogAsync(
        IFirmwareCommandClient commands,
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> before,
        string channel,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        static string Fingerprint(IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> c) =>
            string.Join("|", c.Select(e => $"{e.BaseModel ?? e.Device}={e.Version}").OrderBy(x => x, StringComparer.Ordinal));

        var was = Fingerprint(before);
        var deadline = DateTime.UtcNow + CatalogReflectWait;
        var catalog = await commands.CheckForUpdatesAsync(cancellationToken);

        while (Fingerprint(catalog) == was && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            catalog = await commands.CheckForUpdatesAsync(cancellationToken);
        }

        if (Fingerprint(catalog) == was)
        {
            logger?.LogWarning(
                "The console did not restage after moving devices to {Channel} within {Seconds}s; "
                + "versions may still describe the previous channel",
                channel, CatalogReflectWait.TotalSeconds);
        }

        return catalog;
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
    /// Puts both console surfaces on their planned channel before their versions are read.
    /// </summary>
    /// <returns>The console as it reads on the planned channels, or as it was when nothing changed.</returns>
    private static async Task<NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo?> StageConsoleChannelsAsync(
        IFirmwareCommandClient commands,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        FirmwareRolloutSettings settings,
        CancellationToken cancellationToken)
    {
        if (!ConsoleReachable(console)) return console;

        // latestByChannel is only refreshed for a channel the console has actually been put on:
        // an atl console sitting on RC reported release 4.4.7 for weeks and produced 5.1.19 the
        // moment GA was selected. So both surfaces are put on their planned channel before their
        // version is read, and left there - planning on a channel is committing to it.
        var app = settings.IncludeUniFiNetwork ? settings.EffectiveNetworkAppChannel : null;
        if (!string.IsNullOrEmpty(app)
            && string.Equals(console!.NetworkApplication?.ReleaseChannel, app, StringComparison.OrdinalIgnoreCase))
            app = null;

        var os = settings.IncludeUniFiOs ? settings.EffectiveUniFiOsChannel : null;
        if (!string.IsNullOrEmpty(os)
            && string.Equals(console!.Firmware?.ReleaseChannel, os, StringComparison.OrdinalIgnoreCase))
            os = null;

        if (app == null && os == null) return console;
        if (!await commands.SetConsoleChannelsAsync(app, os, cancellationToken)) return console;

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
        if (string.IsNullOrEmpty(app.UpdateAvailable)) return false;

        // The console names the build its channel offers, which after a channel move down can be
        // behind what is running. Only newer is an update.
        // TODO: a deliberate downgrade is a separate opt-in mode, as for devices and the console.
        return !string.IsNullOrEmpty(app.Version)
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(app.UpdateAvailable, app.Version);
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

        // Newer, not merely different. A channel holds its own line, so a less aggressive one can
        // name a build far behind what is installed - GA at 4.4.7 against 5.1.28 - and treating any
        // difference as an update turned that into a console downgrade.
        // TODO: a deliberate downgrade is a separate opt-in mode, as for devices.
        return NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(release.Version, installed);
    }

    /// <summary>Steps that would actually be commanded, i.e. everything not excluded up front.</summary>
    /// <param name="result">A planned rollout.</param>
    public static int LiveStepCount(RolloutPlanResult result) =>
        result?.Steps.Count(s => s.State != FirmwareRolloutStepState.SkippedExcluded) ?? 0;
}
