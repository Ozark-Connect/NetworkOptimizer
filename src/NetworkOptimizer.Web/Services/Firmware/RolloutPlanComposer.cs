using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>Everything gathered from the live site before a plan can be ordered.</summary>
/// <param name="Context">Devices, coverage and console state, frozen at plan time.</param>
/// <param name="Estimator">Downtime estimates (seeds, this site's history, and the other sites').</param>
/// <param name="CurrentChannel">The release channel devices follow today.</param>
public sealed record RolloutPlanInputs(
    RolloutPlanningContext Context,
    FirmwareTimingEstimator Estimator,
    string CurrentChannel);

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

        return new RolloutPlanInputs(
            context,
            estimator,
            string.IsNullOrEmpty(currentChannel) ? FirmwareChannels.Release : currentChannel);
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
        });
    }

    /// <summary>Steps that would actually be commanded, i.e. everything not excluded up front.</summary>
    /// <param name="result">A planned rollout.</param>
    public static int LiveStepCount(RolloutPlanResult result) =>
        result?.Steps.Count(s => s.State != FirmwareRolloutStepState.SkippedExcluded) ?? 0;
}
