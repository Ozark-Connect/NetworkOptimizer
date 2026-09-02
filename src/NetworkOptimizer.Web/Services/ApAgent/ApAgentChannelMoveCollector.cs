using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// The post-move loop for one site. A channel_change event goes into the change log at once,
/// with the block centers the console never has, so a Channel AI move starts soaking at its
/// real time instead of hours later; the next radio reading checks where the radio landed
/// against the guess; and an hour on, the destination is measured against the origin.
/// </summary>
public sealed class ApAgentChannelMoveCollector
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ApAgentChannelMoveCollector> _logger;
    private readonly string _siteSlug;

    /// <summary>What has been learned about each move; read by the recommender and the Channels card.</summary>
    public ApAgentChannelMoveTracker Tracker { get; } = new();

    /// <summary>Creates the collector for one site.</summary>
    public ApAgentChannelMoveCollector(
        IServiceProvider serviceProvider,
        ILogger<ApAgentChannelMoveCollector> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
    }

    /// <summary>Records one channel_change event: the change log, then the tracker.</summary>
    public async Task RecordAsync(string apMac, string? apName, ApAgentEvent e, CancellationToken ct = default)
    {
        if (e.Channel is not { } change || change.ToChannel <= 0 || change.FromChannel <= 0) return;
        var band = ApAgentAirtimeAggregator.MapBandCode(change.Band);
        if (band.Length == 0) return;
        var radioBand = RadioBandExtensions.FromUniFiCode(band);
        var mac = apMac.Trim().ToLowerInvariant();

        int? fromCenter = change.FromCenterMhz > 0 ? ChannelSpanHelper.CenterChannelFromMhz(radioBand, change.FromCenterMhz) : null;
        int? toCenter = change.ToCenterMhz > 0 ? ChannelSpanHelper.CenterChannelFromMhz(radioBand, change.ToCenterMhz) : null;

        try
        {
            using var scope = CreateSiteScope();
            var repository = scope.ServiceProvider.GetRequiredService<IChannelMemoryRepository>();
            await repository.AddChangesAsync(new[]
            {
                new ApChannelChange
                {
                    ApMac = mac,
                    Band = band,
                    PreviousChannel = change.FromChannel,
                    PreviousWidthMhz = change.FromBw > 0 ? change.FromBw : null,
                    PreviousCenterChannel = fromCenter,
                    NewChannel = change.ToChannel,
                    NewWidthMhz = change.ToBw > 0 ? change.ToBw : null,
                    NewCenterChannel = toCenter,
                    ChangedAtUtc = e.At,
                    Source = ApChannelChangeSource.Agent
                }
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent channel change could not be stored for {Ap} (site {Site})", apMac, _siteSlug);
        }

        Tracker.Record(new ApAgentChannelMove
        {
            ApMac = mac, Band = band,
            FromChannel = change.FromChannel, FromWidth = change.FromBw, FromCenter = fromCenter,
            ToChannel = change.ToChannel, ToWidth = change.ToBw, ToCenter = toCenter,
            At = e.At
        });
        _logger.LogInformation("[ChannelMove] {Ap} {Band}: ch {From}/{FromWidth} -> ch {To}/{ToWidth} (agent, site {Site})",
            apName ?? apMac, radioBand, change.FromChannel, change.FromBw, change.ToChannel, change.ToBw, _siteSlug);
    }

    /// <summary>The landing check, from one radio reading: did the destination block match the guess?</summary>
    public void NoteRadios(string apMac, string? apName, IReadOnlyList<ApAgentRadioAirtime> radios)
    {
        foreach (var r in radios)
        {
            if (r.CenterMhz is not { } mhz || r.Channel <= 0) continue;
            var band = ApAgentAirtimeAggregator.MapBandCode(r.Band);
            if (band.Length == 0) continue;
            var radioBand = RadioBandExtensions.FromUniFiCode(band);
            if (ChannelSpanHelper.CenterChannelFromMhz(radioBand, mhz) is not { } center) continue;

            var landing = Tracker.CheckLanding(apMac, band, r.Channel, r.Width, center);
            if (landing == null) continue;
            var (predicted, landed) = landing.Value;
            // The observation that decides whether "lower block" stays the guess.
            _logger.LogInformation("[ChannelMove] {Ap} {Band}: predicted block {PLow}-{PHigh}, landed {LLow}-{LHigh}{Match} (site {Site})",
                apName ?? apMac, radioBand, predicted.Low, predicted.High, landed.Low, landed.High,
                predicted == landed ? "" : " (guess was wrong)", _siteSlug);
        }
    }

    /// <summary>Reaches any verdict that is due, from the agent's finalized airtime hours.</summary>
    public void EvaluateOutcomes(ApAgentAirtimeAggregator airtime, DateTime nowUtc)
    {
        Tracker.Prune(nowUtc);
        foreach (var move in Tracker.All())
        {
            if (move.Outcome != null || nowUtc < move.VerdictDueAt) continue;
            var hours = airtime.GetFinalizedHours(move.At.AddHours(-2), nowUtc);
            if (Tracker.TryEvaluate(move, hours, nowUtc))
                _logger.LogInformation("[ChannelMove] {Ap} {Band}: ch {From} -> ch {To} measured {Outcome} after an hour (interference {Before:F0}% -> {After:F0}%, site {Site})",
                    move.ApMac, RadioBandExtensions.FromUniFiCode(move.Band), move.FromChannel, move.ToChannel,
                    move.Outcome, move.InterferenceBefore, move.InterferenceAfter, _siteSlug);
        }
    }

    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }
}
