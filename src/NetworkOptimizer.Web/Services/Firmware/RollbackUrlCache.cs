using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// Fills a plan's prior-version image URLs at plan time.
/// <para>
/// This has to happen while the devices are still on those versions: the console catalog carries
/// the LATEST build per model only, so once a device is upgraded there is nowhere left to look up
/// the image it came from. Anonymous feed access is GA-only, so an RC or EA build resolves to
/// nothing and that absence is recorded rather than silently dropped - a rollback offer must not
/// appear for a device that has no image to go back to.
/// </para>
/// </summary>
public static class RollbackUrlCache
{
    /// <summary>
    /// Resolves each step's current firmware to a direct image URL and writes the results onto the
    /// plan document. Failures are recorded per device, never thrown: a feed that will not answer
    /// costs the rollback offer, not the rollout.
    /// </summary>
    /// <param name="document">Plan document to fill.</param>
    /// <param name="steps">The plan's steps, carrying the versions devices are on now.</param>
    /// <param name="feed">Public release feed client.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task PopulateAsync(
        RolloutPlanDocument document,
        IEnumerable<FirmwareRolloutStep> steps,
        UbiquitiReleaseFeedClient feed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(feed);

        document.PriorVersions.Clear();

        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.DeviceMac) || string.IsNullOrWhiteSpace(step.Model))
                continue;

            var entry = new PlanPriorVersion { Mac = step.DeviceMac, Version = step.FromVersion };

            if (string.IsNullOrWhiteSpace(step.FromVersion))
            {
                entry.UnavailableReason = "the console did not report which version this device is on";
            }
            else
            {
                try
                {
                    var release = await feed.GetByVersionAsync(step.Model, step.FromVersion, cancellationToken);
                    entry.Url = release?.DownloadUrl;
                    if (entry.Url == null)
                        entry.UnavailableReason = "the public release feed carries no such build (it serves GA only)";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    entry.UnavailableReason = $"the release feed could not be read ({ex.Message})";
                }
            }

            document.PriorVersions.Add(entry);
        }
    }
}
