using NetworkOptimizer.Core;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders.Uci;

namespace NetworkOptimizer.Web.Services.CableModemProviders;

/// <summary>
/// Cable modem provider for the UniFi Cable Internet (UCI). The UCI has no status page: its DOCSIS
/// tables travel only inside the encrypted inform frames it sends through the gateway, which the
/// On-Site Agent on the gateway captures and <see cref="UciInformService"/> decrypts. A poll
/// therefore reads the latest decoded inform instead of reaching the device. The configuration's
/// Host holds the UCI's MAC, not an address.
/// </summary>
[VendorSpecific("UniFi", "UCI stats come from captured inform frames, not an HTTP status page")]
public sealed class UniFiCableInternetProvider : ICableModemProvider
{
    private readonly UciInformService _informs;

    public UniFiCableInternetProvider(UciInformService informs)
    {
        _informs = informs;
    }

    /// <inheritdoc/>
    public string ProviderKey => UciInformService.ProviderKey;

    /// <inheritdoc/>
    public string DisplayName => "UniFi Cable Internet (UCI)";

    /// <inheritdoc/>
    public CmCredentialRequirement Credentials => CmCredentialRequirement.None;

    /// <inheritdoc/>
    public bool BlankUsernameMeansAdmin => false;

    /// <inheritdoc/>
    public Task<PollResult<CableModemStats>> PollAsync(CmPollContext context, CancellationToken cancellationToken = default)
    {
        var mac = context.ConfiguredHost ?? context.Host;
        var snapshot = _informs.GetSnapshot(context.SiteSlug, mac);
        if (snapshot == null || DateTime.UtcNow - snapshot.ReceivedAt > UciInformService.StaleAfter)
            return Task.FromResult(PollResult<CableModemStats>.Failed(_informs.DescribeMissing(context.SiteSlug, mac)));

        var stats = UciStatsMapper.Map(snapshot.Payload, context.Name, UciInformService.NormalizeMac(mac) ?? mac, snapshot.ReceivedAt);
        return Task.FromResult(PollResult<CableModemStats>.Ok(stats));
    }

    /// <inheritdoc/>
    public Task<(bool Success, string Message)> TestConnectionAsync(CmPollContext context, CancellationToken cancellationToken = default)
    {
        var mac = context.ConfiguredHost ?? context.Host;
        if (UciInformService.NormalizeMac(mac) == null)
            return Task.FromResult((false, "Pick the UCI to monitor."));

        var snapshot = _informs.GetSnapshot(context.SiteSlug, mac);
        if (snapshot != null && DateTime.UtcNow - snapshot.ReceivedAt <= UciInformService.StaleAfter)
            return Task.FromResult((true, "Receiving updates from the UCI."));

        // Capture starts only once the config is saved, so an unsaved UCI has nothing to show yet.
        // A capable agent on the gateway is everything a test can prove before then.
        return Task.FromResult(_informs.HasCapableAgent(context.SiteSlug)
            ? (true, "The On-Site Agent on the Gateway can capture this UCI. Stats appear about a minute after saving.")
            : (false, _informs.DescribeMissing(context.SiteSlug, mac)));
    }
}
