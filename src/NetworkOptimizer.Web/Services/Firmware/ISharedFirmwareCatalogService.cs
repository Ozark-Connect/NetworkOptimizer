using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// The outcome of adding a firmware URL by hand.
/// </summary>
/// <param name="Error">Why nothing was added, or null on success.</param>
/// <param name="Kind">What the URL installs.</param>
/// <param name="Target">Device model or hardware platform the build is for; null for the Network application.</param>
/// <param name="Version">Version the build carries.</param>
/// <param name="MatchedExisting">True when the target came from a build already in the catalog rather than the file name.</param>
/// <param name="Url">The canonical URL that was stored.</param>
public sealed record FirmwareUrlAddResult(
    string? Error,
    FirmwareUrlKind Kind = FirmwareUrlKind.Unknown,
    string? Target = null,
    string? Version = null,
    bool MatchedExisting = false,
    string? Url = null)
{
    /// <summary>True when the build was added.</summary>
    public bool Succeeded => Error == null;

    /// <summary>The added build as a pin for the rollout planned from it, or null when nothing was added.</summary>
    public RolloutBuildPin? ToPin() =>
        Succeeded && Version != null && Url != null ? new RolloutBuildPin(Kind, Target, Version, Url) : null;
}

/// <summary>
/// Changes to the install-wide shared firmware catalog, made from a site's Firmware Rollout page.
/// Site-scoped like the rest of Firmware Rollout: Admin means Site Admin on the site in context.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISharedFirmwareCatalogService
{
    /// <summary>
    /// Adds a Ubiquiti firmware URL to the shared catalog on the Early Access channel, stamped with
    /// the time it was added. The surface and target come from the URL's path; a layout no rule
    /// knows is matched against builds already in the catalog.
    /// </summary>
    /// <param name="url">The download link as pasted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.FirmwareSharedBuildAdded, TargetType = "firmware_build")]
    Task<FirmwareUrlAddResult> AddFirmwareUrlAsync(string url, CancellationToken cancellationToken = default);
}
