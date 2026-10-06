using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Writes a band's recommended channels to the access points in UniFi Network. Site Admin, like
/// Pin Channel on the same table: it changes the site's radios, not just what it is advised.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IChannelPlanApplyService
{
    /// <summary>
    /// Moves each radio in turn, waiting for the AP to report the new channel before the next, so
    /// only one AP is mid-move at a time. Reports each outcome as it lands.
    /// </summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WiFiChannelPlanApplied, Category = AuditCategories.Action, TargetType = "channel_plan")]
    Task<List<ChannelApplyOutcome>> ApplyAsync(
        RadioBand band,
        IReadOnlyList<ChannelApplyItem> items,
        IProgress<ChannelApplyOutcome>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IChannelPlanApplyService" />
public class ChannelPlanApplyService : IChannelPlanApplyService
{
    /// <summary>The console takes a moment to start provisioning; a read before then still shows the old channel.</summary>
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(3);

    /// <summary>Covers a 60 s DFS channel availability check plus provisioning.</summary>
    private static readonly TimeSpan ArrivalDeadline = TimeSpan.FromSeconds(90);

    private readonly UniFiConnectionService _connectionService;
    private readonly IAuditContext _auditContext;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly string _siteSlug;
    private readonly ILogger<ChannelPlanApplyService> _logger;

    /// <param name="connectionService">This site's UniFi Network connection.</param>
    /// <param name="auditContext">Carries the per-radio outcomes into the audit entry.</param>
    /// <param name="licenseState">Writes are refused on a site whose license is not operational.</param>
    /// <param name="siteContext">The site this scope serves.</param>
    /// <param name="logger">Logger.</param>
    public ChannelPlanApplyService(
        UniFiConnectionService connectionService,
        IAuditContext auditContext,
        Licensing.LicenseStateService licenseState,
        SiteContextService siteContext,
        ILogger<ChannelPlanApplyService> logger)
    {
        _connectionService = connectionService;
        _auditContext = auditContext;
        _licenseState = licenseState;
        _siteSlug = siteContext.Slug;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<ChannelApplyOutcome>> ApplyAsync(
        RadioBand band,
        IReadOnlyList<ChannelApplyItem> items,
        IProgress<ChannelApplyOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Licensing.LicenseGuard.EnsureOperational(_licenseState, _siteSlug);

        var outcomes = new List<ChannelApplyOutcome>();
        var client = _connectionService.IsConnected ? _connectionService.Client : null;
        string? stopReason = client == null ? "Not connected to UniFi Network" : null;

        foreach (var item in items.Where(i => i.Band == band))
        {
            ChannelApplyOutcome outcome;
            if (stopReason != null || cancellationToken.IsCancellationRequested)
                outcome = new(item, ChannelApplyStatus.Skipped, stopReason ?? "Canceled");
            else
            {
                try
                {
                    outcome = await ApplyOneAsync(client!, item, cancellationToken);
                }
                catch (UniFiPermissionException ex)
                {
                    // The account cannot write at all, so every remaining radio would fail the same way.
                    outcome = new(item, ChannelApplyStatus.Failed, ex.Message);
                    stopReason = "Not attempted";
                }
                catch (OperationCanceledException)
                {
                    outcome = new(item, ChannelApplyStatus.Skipped, "Canceled");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Channel apply failed for {Ap} {Band}", item.ApMac, band);
                    outcome = new(item, ChannelApplyStatus.Failed, "UniFi Network could not be reached");
                }
            }

            outcomes.Add(outcome);
            progress?.Report(outcome);
        }

        _auditContext.SetTarget($"{outcomes.Count(o => o.Status is ChannelApplyStatus.Applied or ChannelApplyStatus.Unconfirmed)} radio(s)",
            band.ToDisplayString());
        _auditContext.SetDetails(new
        {
            Band = band.ToDisplayString(),
            Radios = outcomes.Select(o => new
            {
                Ap = o.Item.ApName,
                o.Item.ApMac,
                From = $"Ch {o.Item.CurrentChannel} / {o.Item.CurrentWidth} MHz",
                To = $"Ch {o.Item.Channel} / {o.Item.Width} MHz",
                Status = o.Status.ToString(),
                o.Reason
            }).ToList()
        });
        return outcomes;
    }

    private async Task<ChannelApplyOutcome> ApplyOneAsync(UniFiApiClient client, ChannelApplyItem item, CancellationToken ct)
    {
        var device = await client.GetDeviceAsync(item.ApMac, ct);
        var (update, stop) = ChannelPlanApply.Preflight(item, device);
        if (stop != null) return stop;

        if (!await client.UpdateDeviceRadioChannelsAsync(device!.Id, [update!], ct))
            return new(item, ChannelApplyStatus.Failed, "UniFi Network refused the change");

        _logger.LogInformation("Channel apply: {Ap} {Band} Ch {From}/{FromW} -> Ch {To}/{ToW} (site {Site})",
            item.ApName, item.Band, item.CurrentChannel, item.CurrentWidth, item.Channel, item.Width, _siteSlug);

        // The change is saved from here on, so a cancel or a failed read while waiting is Unconfirmed,
        // never Skipped or Failed.
        try
        {
            await Task.Delay(FirstCheckAfter, ct);
            var deadline = DateTime.UtcNow + ArrivalDeadline;
            while (true)
            {
                if (ChannelPlanApply.HasArrived(item, await client.GetDeviceAsync(item.ApMac, ct)))
                    return new(item, ChannelApplyStatus.Applied);
                if (DateTime.UtcNow >= deadline)
                    return new(item, ChannelApplyStatus.Unconfirmed,
                        $"Saved in UniFi Network; the AP had not reported the new channel after {ArrivalDeadline.TotalSeconds:0} seconds");
                await Task.Delay(CheckEvery, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return new(item, ChannelApplyStatus.Unconfirmed, "Saved in UniFi Network; stopped waiting for the AP");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel apply: could not read {Ap} back after the change", item.ApMac);
            return new(item, ChannelApplyStatus.Unconfirmed, "Saved in UniFi Network; could not read the AP back");
        }
    }
}
