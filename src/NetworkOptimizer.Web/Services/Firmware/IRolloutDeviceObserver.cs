namespace NetworkOptimizer.Web.Services.Firmware;

/// <summary>
/// One device as the rollout sees it on a polling pass: enough to decide whether it went down,
/// came back, and came back on the version the step asked for.
/// </summary>
public sealed record RolloutDeviceObservation
{
    /// <summary>Normalized (lowercase, colon-separated) device MAC.</summary>
    public required string Mac { get; init; }

    /// <summary>Device name as the console shows it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Model / SKU code.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Address for the SSH escalation and mesh re-pair paths.</summary>
    public string? IpAddress { get; init; }

    /// <summary>Firmware version the console reports right now - the step's success criterion.</summary>
    public string? Firmware { get; init; }

    /// <summary>Raw UniFi <c>state</c> value.</summary>
    public int State { get; init; }

    /// <summary>Whether the console still offers an upgrade.</summary>
    public bool Upgradable { get; init; }

    /// <summary>Version the console has staged for this device.</summary>
    public string? UpgradeToFirmware { get; init; }
}

/// <summary>
/// The rollout's view of live device state. Reads the same console polling surface the rest of the
/// app does, behind an interface so the executor can be driven by a scripted device timeline.
/// </summary>
public interface IRolloutDeviceObserver
{
    /// <summary>
    /// Every adopted device the console currently reports. An empty list means the console did not
    /// answer, which the orchestrator treats as "no information", never as devices being down.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<RolloutDeviceObservation>> ObserveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The real observer: the site's console connection, polled on the orchestrator's cadence.
/// </summary>
public class RolloutDeviceObserver : IRolloutDeviceObserver
{
    private readonly UniFiConnectionService _connection;
    private readonly ILogger<RolloutDeviceObserver> _logger;
    private readonly string _siteSlug;

    /// <param name="siteConnections">Per-site console connections.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site to observe.</param>
    public RolloutDeviceObserver(
        SiteConnectionRegistry siteConnections,
        ILogger<RolloutDeviceObserver> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _connection = siteConnections.GetFor(_siteSlug);
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RolloutDeviceObservation>> ObserveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var devices = await _connection.GetDiscoveredDevicesAsync(cancellationToken);
            return devices
                .Where(d => !string.IsNullOrEmpty(d.Mac))
                .Select(d => new RolloutDeviceObservation
                {
                    Mac = MacNormalizer.Normalize(d.Mac),
                    Name = string.IsNullOrEmpty(d.Name) ? d.FriendlyModelName : d.Name,
                    Model = d.Model,
                    IpAddress = string.IsNullOrEmpty(d.DisplayIpAddress) ? null : d.DisplayIpAddress,
                    Firmware = string.IsNullOrEmpty(d.Firmware) ? null : d.Firmware,
                    State = d.State,
                    Upgradable = d.Upgradable,
                    UpgradeToFirmware = d.UpgradeToFirmware,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Rollout device poll failed for site {Site}", _siteSlug);
            return [];
        }
    }
}
