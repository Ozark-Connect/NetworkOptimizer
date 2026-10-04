using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Everything gathered from the live site before a plan can be ordered.</summary>
/// <param name="Context">Devices, coverage and console state, frozen at plan time.</param>
/// <param name="Estimator">Downtime estimates (seeds, this site's history, and the other sites').</param>
/// <param name="CurrentChannel">The release channel devices follow today.</param>
/// <param name="Console">The console as it answered at plan time, or null when it did not.</param>
/// <param name="UniFiOsPinned">True when the UniFi OS target is a build chosen by hand.</param>
/// <param name="NetworkAppPinned">True when the Network application target is a build chosen by hand.</param>
public sealed record RolloutPlanInputs(
    RolloutPlanningContext Context,
    FirmwareTimingEstimator Estimator,
    string CurrentChannel,
    NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? Console = null,
    IReadOnlyList<PlanTargetImage>? TargetImages = null,
    bool UniFiOsPinned = false,
    bool NetworkAppPinned = false);

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
    /// <param name="settings">Settings to stage channels from; null skips staging.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="sharedCatalog">
    /// The install-wide build store, fed from this refresh and consulted for devices the console
    /// offered nothing. Null skips both sides.
    /// </param>
    /// <param name="pin">A build chosen by hand, installed in place of anything newer on offer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<RolloutPlanInputs> GatherAsync(
        IRolloutPlanningSource planning,
        IReadOnlyList<FirmwareModelTiming> siteTimings,
        IFirmwareCommandClient commands,
        FirmwareRolloutSettings? settings = null,
        ILogger? logger = null,
        ISharedFirmwareCatalogRepository? sharedCatalog = null,
        UbiquitiReleaseFeedClient? feed = null,
        RolloutBuildPin? pin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(commands);

        await commands.TriggerDeviceFirmwareCheckAsync(cancellationToken);
        var catalog = await commands.CheckForUpdatesAsync(cancellationToken);

        var context = await planning.GetContextAsync(cancellationToken);
        var estimator = await planning.GetEstimatorAsync(siteTimings ?? [], cancellationToken);
        var currentChannel = await commands.GetDeviceChannelAsync(cancellationToken);
        var console = await commands.GetConsoleSystemInfoAsync(cancellationToken);

        if (sharedCatalog != null && !string.IsNullOrEmpty(currentChannel))
            await RecordSharedBuildsAsync(sharedCatalog, currentChannel, catalog, cancellationToken);

        var images = new List<PlanTargetImage>();
        if (settings != null)
        {
            if (!string.IsNullOrEmpty(currentChannel))
            {
                ReconcileWithCatalog(context, settings, currentChannel, catalog, logger);
                CaptureImages(images, context, settings, currentChannel, currentChannel, catalog);
            }
            currentChannel = await StageEveryPlannedChannelAsync(
                planning, commands, context, settings, currentChannel, images, catalog, sharedCatalog, logger, cancellationToken);
            console = await StageConsoleChannelsAsync(commands, console, settings, cancellationToken);

            if (feed != null && settings.IncludeUniFiOs)
                await PatchStaleGaFromFeedAsync(console, feed, logger, cancellationToken);
        }

        if (sharedCatalog != null)
        {
            await RecordSharedNetworkAppAsync(sharedCatalog, console, cancellationToken);
            await RecordSharedUniFiOsAsync(sharedCatalog, console, cancellationToken);
            await AdoptSharedBuildsAsync(
                sharedCatalog, context, settings, currentChannel, images, logger, cancellationToken);
            await AdoptSharedNetworkAppAsync(sharedCatalog, console, logger, cancellationToken);
            await AdoptSharedUniFiOsAsync(sharedCatalog, commands, console, settings, logger, cancellationToken);
        }

        var (osPinned, appPinned) = pin == null
            ? (false, false)
            : await ApplyPinAsync(pin, commands, context, console, settings, images, logger, cancellationToken);

        // Last word on every path, the read-only preview included. It used to run only inside the
        // channel staging above, so the drift check (which previews without staging) kept the
        // console's offer of an older build and Re-plan opened the wizard on a downgrade.
        DropDowngrades(context);

        return new RolloutPlanInputs(
            context,
            estimator,
            string.IsNullOrEmpty(currentChannel) ? FirmwareChannels.Release : currentChannel,
            console,
            images,
            osPinned,
            appPinned);
    }

    /// <summary>
    /// Puts a build chosen by hand in place of whatever newer one the console or the shared catalog
    /// offers. A pin no newer than what runs takes that surface out of the plan rather than letting
    /// the newest build through. UniFi OS and the Network application need gateway SSH, because
    /// the console's own install command only takes its newest build; a cellular modem keeps its
    /// offer, because it can only install the console-cached build.
    /// </summary>
    /// <returns>Whether the UniFi OS and the Network application targets are now pinned.</returns>
    private static async Task<(bool UniFiOs, bool NetworkApp)> ApplyPinAsync(
        RolloutBuildPin pin,
        IFirmwareCommandClient commands,
        RolloutPlanningContext context,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        FirmwareRolloutSettings? settings,
        List<PlanTargetImage> images,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        switch (pin.Kind)
        {
            case FirmwareUrlKind.Device:
                var models = new HashSet<string>(pin.DeviceModels, StringComparer.OrdinalIgnoreCase);
                foreach (var device in context.Devices.Where(d => models.Contains(d.Model)))
                {
                    if (FirmwareTimingEstimator.Classify(device) == FirmwareDeviceClass.CellularModem) continue;

                    images.RemoveAll(i => string.Equals(i.Mac, device.Mac, StringComparison.OrdinalIgnoreCase));
                    if (!NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(pin.Version, device.FromVersion)
                        || string.IsNullOrWhiteSpace(device.FromVersion))
                    {
                        device.ToVersion = null;
                        device.Upgradable = false;
                        continue;
                    }

                    device.ToVersion = pin.Version;
                    device.Upgradable = true;
                    images.Add(new PlanTargetImage { Mac = device.Mac, Version = pin.Version, Url = pin.UrlFor(device.Model), Pinned = true });
                }
                return (false, false);

            case FirmwareUrlKind.UniFiOs:
            {
                if (!ConsoleReachable(console) || console!.IsStandaloneConsole
                    || console.Firmware?.LatestByChannel is not { } byChannel
                    || !SamePlatform(console.Hardware?.Shortname, pin.Target))
                    return (false, false);

                // Nothing else may stand in for the pin. An unusable one leaves only the installed
                // build on offer: an empty list reads as "update available, details unknown".
                var channel = settings?.EffectiveUniFiOsChannel ?? console.Firmware.ReleaseChannel ?? FirmwareChannels.Beta;
                byChannel.Clear();
                if (!NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(pin.Version, console.InstalledOsVersion)
                    || !await commands.HasGatewaySshAsync(cancellationToken))
                {
                    byChannel[channel] = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease
                    {
                        Channel = channel,
                        Version = console.InstalledOsVersion,
                    };
                    logger?.LogInformation(
                        "Not planning UniFi OS {Version}: it is not newer than {Installed}, or gateway SSH is not configured",
                        pin.Version, console.InstalledOsVersion ?? "unknown");
                    return (false, false);
                }

                byChannel[channel] = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease
                {
                    Channel = channel,
                    Version = pin.Version,
                    Links = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLinks
                    {
                        Data = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLink { Href = pin.Url },
                    },
                };
                return (true, false);
            }

            case FirmwareUrlKind.NetworkApp:
            {
                if (!ConsoleReachable(console) || console!.IsStandaloneConsole
                    || console.NetworkApplication is not { } app)
                    return (false, false);

                if (!NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(pin.Version, app.Version)
                    || !await commands.HasGatewaySshAsync(cancellationToken))
                {
                    app.UpdateAvailable = null;
                    logger?.LogInformation(
                        "Not planning UniFi Network {Version}: it is not newer than {Installed}, or gateway SSH is not configured",
                        pin.Version, app.Version ?? "unknown");
                    return (false, false);
                }

                app.UpdateAvailable = pin.Version;
                return (false, true);
            }

            default:
                return (false, false);
        }
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
        ISharedFirmwareCatalogRepository? sharedCatalog,
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
            if (!await commands.SetDeviceChannelAsync(channel, cancellationToken))
            {
                DropChannel(context, settings, channel);
                continue;
            }
            currentChannel = channel;

            await commands.TriggerDeviceFirmwareCheckAsync(cancellationToken);
            var catalog = await WaitForCatalogAsync(commands, priorCatalog, channel, logger, cancellationToken);
            priorCatalog = catalog;

            if (sharedCatalog != null)
                await RecordSharedBuildsAsync(sharedCatalog, channel, catalog, cancellationToken);

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

            ReconcileWithCatalog(context, settings, channel, catalog, logger);
            CaptureImages(images, context, settings, channel, channel, catalog);
        }

        return currentChannel;
    }

    /// <summary>
    /// Holds every device planned on <paramref name="channel"/> to the build that channel's catalog
    /// carries for its model. A device record and the catalog restage independently after a channel
    /// change, so the record can still name the channel before this one.
    /// An empty catalog is a failed read rather than an empty channel, so it drops nothing.
    /// </summary>
    private static void ReconcileWithCatalog(
        RolloutPlanningContext context,
        FirmwareRolloutSettings settings,
        string channel,
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> catalog,
        ILogger? logger)
    {
        if (catalog.Count == 0)
        {
            logger?.LogWarning(
                "Not checking targets against the {Channel} catalog: the console returned no builds", channel);
            return;
        }

        foreach (var device in context.Devices.Where(d => d.Upgradable
            && string.Equals(RolloutPlanner.ResolveChannel(d, settings), channel, StringComparison.OrdinalIgnoreCase)))
        {
            var entry = FindCatalogEntry(catalog, device.Model);
            if (string.IsNullOrWhiteSpace(entry?.Version))
            {
                logger?.LogInformation(
                    "Dropping {Model} ({Mac}) from the plan: the {Channel} catalog carries no build for it",
                    device.Model, device.Mac, channel);
                device.ToVersion = null;
                device.Upgradable = false;
                continue;
            }

            if (NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.SameBuild(entry.Version, device.ToVersion))
                continue;

            logger?.LogInformation(
                "Retargeting {Model} ({Mac}) to {Version}, what {Channel} carries; the console still offered {Stale}",
                device.Model, device.Mac, entry.Version, channel, device.ToVersion ?? "nothing");
            device.ToVersion = entry.Version;
        }
    }

    /// <summary>Takes every device planned on a channel out of the plan.</summary>
    private static void DropChannel(
        RolloutPlanningContext context, FirmwareRolloutSettings settings, string channel)
    {
        foreach (var device in context.Devices.Where(d => d.Upgradable
            && string.Equals(RolloutPlanner.ResolveChannel(d, settings), channel, StringComparison.OrdinalIgnoreCase)))
        {
            device.ToVersion = null;
            device.Upgradable = false;
        }
    }

    /// <summary>The catalog entry for a device's model code, by either name the catalog uses.</summary>
    private static NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry? FindCatalogEntry(
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> catalog, string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? null
            : catalog.FirstOrDefault(e =>
                string.Equals(e.BaseModel, model, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Device, model, StringComparison.OrdinalIgnoreCase));

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
    ///
    /// An empty catalog is never a restage: it is this app's own "could not read it" answer.
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
        bool Restaged(IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> c) =>
            c.Count > 0 && Fingerprint(c) != was;

        var deadline = DateTime.UtcNow + CatalogReflectWait;
        var catalog = await commands.CheckForUpdatesAsync(cancellationToken);

        while (!Restaged(catalog) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            catalog = await commands.CheckForUpdatesAsync(cancellationToken);
        }

        if (!Restaged(catalog))
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

            var entry = FindCatalogEntry(catalog, device.Model);
            if (string.IsNullOrWhiteSpace(entry?.Url)) continue;

            images.Add(new PlanTargetImage { Mac = device.Mac, Version = entry.Version ?? device.ToVersion, Url = entry.Url });
        }
    }

    /// <summary>Feeds one channel's catalog into the install-wide build store. Best-effort.</summary>
    private static Task RecordSharedBuildsAsync(
        ISharedFirmwareCatalogRepository sharedCatalog,
        string channel,
        IReadOnlyList<NetworkOptimizer.UniFi.Models.UniFiFirmwareCatalogEntry> catalog,
        CancellationToken cancellationToken)
    {
        var builds = catalog
            .Select(e => new SharedFirmwareBuild
            {
                Model = e.BaseModel ?? e.Device ?? string.Empty,
                Channel = channel,
                Version = e.Version ?? string.Empty,
                Url = e.Url ?? string.Empty,
                Md5Sum = e.Md5Sum,
            })
            .Where(b => b.Model.Length > 0 && b.Version.Length > 0 && b.Url.Length > 0)
            .ToList();

        return builds.Count > 0
            ? sharedCatalog.UpsertDeviceBuildsAsync(builds, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Records the Network application build this console is being offered, when it is one.</summary>
    private static Task RecordSharedNetworkAppAsync(
        ISharedFirmwareCatalogRepository sharedCatalog,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        CancellationToken cancellationToken)
    {
        var app = console?.NetworkApplication;
        if (app == null
            || string.IsNullOrWhiteSpace(app.UpdateAvailable)
            || string.IsNullOrWhiteSpace(app.ReleaseChannel)
            || !NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(app.UpdateAvailable, app.Version))
        {
            return Task.CompletedTask;
        }

        return sharedCatalog.UpsertNetworkAppBuildAsync(
            app.ReleaseChannel, app.UpdateAvailable, NetworkAppDebUrl(console), cancellationToken);
    }

    /// <summary>
    /// Offers each device a build another site was already offered on the same model and channel -
    /// Ubiquiti ungates builds per console, not per channel - when it is newer than what the device
    /// runs and than what this console offers it. A device whose running version is unknown is
    /// never offered anything, because newer cannot be established for it. A cellular modem keeps
    /// its console's offer: it can only install the console-cached build.
    /// </summary>
    private static async Task AdoptSharedBuildsAsync(
        ISharedFirmwareCatalogRepository sharedCatalog,
        RolloutPlanningContext context,
        FirmwareRolloutSettings? settings,
        string? currentChannel,
        List<PlanTargetImage> images,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        foreach (var device in context.Devices)
        {
            if (string.IsNullOrWhiteSpace(device.Model) || string.IsNullOrWhiteSpace(device.FromVersion))
                continue;

            var offered = device.Upgradable
                && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(device.ToVersion, device.FromVersion)
                ? device.ToVersion
                : null;
            if (offered != null && FirmwareTimingEstimator.Classify(device) == FirmwareDeviceClass.CellularModem)
                continue;

            var channel = settings != null ? RolloutPlanner.ResolveChannel(device, settings) : currentChannel;
            if (string.IsNullOrEmpty(channel)) continue;

            var build = await sharedCatalog.FindNewerDeviceBuildAsync(
                device.Model, channel, offered ?? device.FromVersion, cancellationToken);
            if (build == null) continue;

            device.ToVersion = build.Version;
            device.Upgradable = true;
            // The image has to name the new target, or the step falls back to the console's own build.
            images.RemoveAll(i => string.Equals(i.Mac, device.Mac, StringComparison.OrdinalIgnoreCase));
            images.Add(new PlanTargetImage { Mac = device.Mac, Version = build.Version, Url = build.Url });

            logger?.LogInformation(
                "Offering {Model} ({Mac}) {Version} on {Channel} from the shared catalog; this console offered {Offered}",
                device.Model, device.Mac, build.Version, channel, offered ?? "nothing");
        }
    }

    /// <summary>
    /// Fills in a Network application update the console has not noticed yet - its updateAvailable
    /// is stale until its own background check runs. Only when the console offers nothing itself:
    /// a version the console has staged always wins over one it has not.
    /// </summary>
    private static async Task AdoptSharedNetworkAppAsync(
        ISharedFirmwareCatalogRepository sharedCatalog,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (!ConsoleReachable(console)) return;

        // On a standalone console the Network app runs on a separate UOS Server host: the only
        // install path is the console's own API trigger against a build it has staged itself, so a
        // shared-catalog version it has not noticed cannot be acted on there.
        if (console!.IsStandaloneConsole) return;

        var app = console.NetworkApplication;
        if (app == null
            || !string.IsNullOrEmpty(app.UpdateAvailable)
            || string.IsNullOrWhiteSpace(app.ReleaseChannel)
            || string.IsNullOrWhiteSpace(app.Version))
        {
            return;
        }

        var build = await sharedCatalog.FindNewerNetworkAppBuildAsync(
            app.ReleaseChannel, app.Version, cancellationToken);
        if (build == null) return;

        app.UpdateAvailable = build.Version;
        logger?.LogInformation(
            "Offering UniFi Network {Version} on {Channel} from the shared catalog; this console has not noticed it yet",
            build.Version, app.ReleaseChannel);
    }

    /// <summary>
    /// Records every UniFi OS build this Cloud Gateway lists, one per channel, under its hardware
    /// platform. A self-hosted UniFi OS Server is skipped: its OS is never ours to update.
    /// </summary>
    private static Task RecordSharedUniFiOsAsync(
        ISharedFirmwareCatalogRepository sharedCatalog,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        CancellationToken cancellationToken)
    {
        var platform = console?.Hardware?.Shortname;
        if (string.IsNullOrWhiteSpace(platform)
            || console!.IsStandaloneConsole
            || console.Firmware?.LatestByChannel is not { } byChannel)
        {
            return Task.CompletedTask;
        }

        var builds = byChannel
            .Select(kv => new SharedUniFiOsBuild
            {
                Platform = platform,
                Channel = kv.Key,
                Version = kv.Value?.Version ?? string.Empty,
                Url = kv.Value?.DownloadUrl ?? string.Empty,
                PublishedUtc = kv.Value?.Created,
            })
            .Where(b => b.Version.Length > 0 && b.Url.Length > 0)
            .ToList();

        return builds.Count > 0
            ? sharedCatalog.UpsertUniFiOsBuildsAsync(builds, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>
    /// Offers this Cloud Gateway a UniFi OS build another console of the same platform was already
    /// offered, when it is newer than anything this console runs or offers on the planned channel.
    /// The adopted build goes into latestByChannel, so the plan reads it like the console's own.
    /// Needs gateway SSH: the console has not staged the build, so <c>ubnt-systool fwupdate</c> by
    /// URL is the only install path.
    /// </summary>
    private static async Task AdoptSharedUniFiOsAsync(
        ISharedFirmwareCatalogRepository sharedCatalog,
        IFirmwareCommandClient commands,
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        FirmwareRolloutSettings? settings,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (!ConsoleReachable(console) || console!.IsStandaloneConsole) return;
        if (console.Firmware?.LatestByChannel is not { } byChannel) return;

        var platform = console.Hardware?.Shortname;
        var installed = console.InstalledOsVersion;
        var channel = settings?.EffectiveUniFiOsChannel ?? console.Firmware.ReleaseChannel;
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(channel))
            return;

        // Beat the console's own offer as well as what it runs: its own build installs without SSH.
        var offered = OfferedUniFiOsRelease(console, channel)?.Version;
        var floor = NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(offered, installed) ? offered : installed;

        // Matched by platform, not by code: an image link can name a model code (UDRULT) where the
        // console reports its shortname (UCG-ULTRA).
        var channels = ChannelsAtOrBelow(channel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SharedUniFiOsBuild? build = null;
        foreach (var row in await sharedCatalog.ListUniFiOsBuildsAsync(cancellationToken))
        {
            if (!channels.Contains(row.Channel) || !SamePlatform(row.Platform, platform)) continue;
            if (!NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(row.Version, floor)) continue;
            if (build == null || NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(row.Version, build.Version))
                build = row;
        }
        if (build == null) return;

        if (!await commands.HasGatewaySshAsync(cancellationToken))
        {
            logger?.LogInformation(
                "Not offering UniFi OS {Version} for {Platform} from the shared catalog: gateway SSH is not configured",
                build.Version, platform);
            return;
        }

        byChannel[build.Channel] = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease
        {
            Channel = build.Channel,
            Version = build.Version,
            Created = build.PublishedUtc,
            Links = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLinks
            {
                Data = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLink { Href = build.Url },
            },
        };
        logger?.LogInformation(
            "Offering UniFi OS {Version} on {Channel} for {Platform} from the shared catalog; this console offered {Offered}",
            build.Version, build.Channel, platform, offered ?? "nothing");
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
            NetworkAppFromVersion = inputs.Console?.NetworkApplication?.Version,
            NetworkAppToVersion = inputs.Console?.NetworkApplication?.UpdateAvailable,
            UniFiOsFromVersion = inputs.Console?.InstalledOsVersion,
            UniFiOsToVersion = OfferedUniFiOsVersion(inputs.Console, settings.EffectiveUniFiOsChannel),
            NetworkAppDownloadUrl = NetworkAppDebUrl(inputs.Console),
            UniFiOsDownloadUrl = OfferedUniFiOsRelease(inputs.Console, settings.EffectiveUniFiOsChannel)?.DownloadUrl,
            UniFiOsPublishedAt = OfferedUniFiOsRelease(inputs.Console, settings.EffectiveUniFiOsChannel)?.Created,
            IsStandaloneConsole = inputs.Console?.IsStandaloneConsole ?? false,
            UniFiOsPinned = inputs.UniFiOsPinned,
            NetworkAppPinned = inputs.NetworkAppPinned,
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

    /// <summary>
    /// Whether two hardware codes name the same console platform: equal, or both resolving to the
    /// same product. Image file names carry a model code (UDRULT) while the console reports its
    /// shortname (UCG-ULTRA); both are the UCG-Ultra.
    /// </summary>
    internal static bool SamePlatform(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        // An unknown code resolves to itself, so two unknown codes only match when equal (above).
        // A product name can differ from its shortname by case alone (UCG-Ultra / UCG-ULTRA).
        var nameA = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(a, a);
        var nameB = NetworkOptimizer.UniFi.UniFiProductDatabase.GetBestProductName(b, b);
        return string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether /api/system answered with anything at all.</summary>
    public static bool ConsoleReachable(NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console) =>
        console != null && (console.Firmware != null || console.Apps != null);

    /// <summary>The UniFi OS build this channel is offering, whatever its age.</summary>
    private static string? OfferedUniFiOsVersion(
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console, string channel) =>
        OfferedUniFiOsRelease(console, channel)?.Version;

    /// <summary>
    /// The newest UniFi OS release available at or below the configured channel's aggressiveness.
    /// A promoted version can leave its origin channel (RC reverts to the prior RC build) and
    /// the GA entry can go stale, so we walk all channels at or below and take the newest.
    /// </summary>
    internal static NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease? OfferedUniFiOsRelease(
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console, string channel)
    {
        if (console?.Firmware?.LatestByChannel is not { } byChannel)
            return null;

        NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease? best = null;
        foreach (var ch in ChannelsAtOrBelow(channel))
        {
            if (!byChannel.TryGetValue(ch, out var release) || string.IsNullOrEmpty(release?.Version))
                continue;
            if (best == null
                || NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(release.Version, best.Version!))
                best = release;
        }
        return best;
    }

    /// <summary>
    /// Channels at or below the given aggressiveness: beta sees all three, release-candidate
    /// sees RC and release, release sees only release.
    /// </summary>
    private static IEnumerable<string> ChannelsAtOrBelow(string channel) => channel switch
    {
        FirmwareChannels.Beta => [FirmwareChannels.Beta, FirmwareChannels.ReleaseCandidate, FirmwareChannels.Release],
        FirmwareChannels.ReleaseCandidate => [FirmwareChannels.ReleaseCandidate, FirmwareChannels.Release],
        _ => [channel],
    };

    /// <summary>
    /// The console's <c>latestByChannel["release"]</c> entry can go stale when the console sits
    /// on beta or RC: the GA entry only refreshes when the console is actually put on that channel.
    /// This checks Ubiquiti's public release feed and replaces the stale entry with the real GA
    /// build, so the channel-walking logic in <see cref="OfferedUniFiOsRelease"/> picks it up.
    /// </summary>
    private static async Task PatchStaleGaFromFeedAsync(
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console,
        UbiquitiReleaseFeedClient feed,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (console?.Firmware?.LatestByChannel is not { } byChannel)
            return;

        var platform = console.Hardware?.Shortname;
        if (string.IsNullOrWhiteSpace(platform))
            return;

        var installed = console.InstalledOsVersion;

        // Check what the console thinks GA is.
        byChannel.TryGetValue(FirmwareChannels.Release, out var consoleGa);
        var consoleGaVersion = consoleGa?.Version;

        // If the console's GA entry is already newer than installed, the channel walk will find it.
        if (!string.IsNullOrEmpty(consoleGaVersion)
            && !string.IsNullOrEmpty(installed)
            && NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(consoleGaVersion, installed))
        {
            logger?.LogDebug(
                "UniFi OS GA from console ({Version}) is already newer than installed ({Installed}), feed check skipped",
                consoleGaVersion, installed);
            return;
        }

        var feedGa = await feed.GetLatestAsync(platform, UbiquitiReleaseFeedClient.GaChannel,
            product: "unifi-dream", cancellationToken: cancellationToken);

        if (feedGa == null || string.IsNullOrEmpty(feedGa.Version))
        {
            logger?.LogDebug("No GA build on the public feed for platform {Platform}", platform);
            return;
        }

        // Only patch if the feed version is genuinely newer than what the console reported.
        if (!string.IsNullOrEmpty(consoleGaVersion)
            && !NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(feedGa.Version, consoleGaVersion))
        {
            logger?.LogDebug(
                "Public feed GA ({FeedVersion}) is not newer than console GA ({ConsoleVersion})",
                feedGa.Version, consoleGaVersion);
            return;
        }

        var release = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareRelease
        {
            Channel = FirmwareChannels.Release,
            Version = feedGa.Version,
            Created = feedGa.Created,
            Links = new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLinks
            {
                Data = feedGa.DownloadUrl != null
                    ? new NetworkOptimizer.UniFi.Models.UniFiConsoleFirmwareLink { Href = feedGa.DownloadUrl }
                    : null,
            },
        };

        byChannel[FirmwareChannels.Release] = release;
        logger?.LogInformation(
            "Patched stale UniFi OS GA entry: console reported {ConsoleVersion}, public feed has {FeedVersion} for {Platform}",
            consoleGaVersion ?? "(absent)", feedGa.Version, platform);
    }

    private static string? NetworkAppDebUrl(NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console)
    {
        var version = console?.NetworkApplication?.UpdateAvailable;
        if (string.IsNullOrWhiteSpace(version)) return null;
        var package = console!.IsStandaloneConsole ? "unifi_sysvinit_all" : "unifi-native_sysvinit";
        return $"https://dl.ui.com/unifi/{version}/{package}.deb";
    }

    private static bool HasUniFiOsUpdate(
        NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? console, string channel)
    {
        if (!ConsoleReachable(console)) return false;

        var offered = OfferedUniFiOsRelease(console, channel);
        if (offered == null || string.IsNullOrEmpty(offered.Version)) return true;

        var installed = console!.InstalledOsVersion;
        if (string.IsNullOrEmpty(installed)) return true;

        return NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.IsNewer(offered.Version, installed);
    }

    /// <summary>Steps that would actually be commanded, i.e. everything not excluded up front.</summary>
    /// <param name="result">A planned rollout.</param>
    public static int LiveStepCount(RolloutPlanResult result) =>
        result?.Steps.Count(s => s.State != FirmwareRolloutStepState.SkippedExcluded) ?? 0;
}
