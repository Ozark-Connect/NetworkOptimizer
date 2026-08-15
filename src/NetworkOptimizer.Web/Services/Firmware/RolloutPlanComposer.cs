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
    NetworkOptimizer.UniFi.Models.UniFiConsoleSystemInfo? Console = null);

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(commands);

        await commands.CheckForUpdatesAsync(cancellationToken);

        var context = await planning.GetContextAsync(cancellationToken);
        var estimator = await planning.GetEstimatorAsync(siteTimings ?? [], cancellationToken);
        var currentChannel = await commands.GetDeviceChannelAsync(cancellationToken);
        var console = await commands.GetConsoleSystemInfoAsync(cancellationToken);

        return new RolloutPlanInputs(
            context,
            estimator,
            string.IsNullOrEmpty(currentChannel) ? FirmwareChannels.Release : currentChannel,
            console);
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
