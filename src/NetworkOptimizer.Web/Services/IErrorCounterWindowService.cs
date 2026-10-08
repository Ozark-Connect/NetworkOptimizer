using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Error counter growth over the last 24 hours for the Dashboard's ONT and cable modem cards,
/// read from InfluxDB history. A device's own counters are cumulative since its last reboot, so
/// one PON maintenance window or power blip stays in them for months; the window drops it.
///
/// Site-scoped, Viewer: the Dashboard cards that show these are open to any role.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IErrorCounterWindowService
{
    /// <summary>Error growth for a standalone ONT, or null with no history to read.</summary>
    [RequireRole(Roles.Viewer)]
    Task<PonErrorTotals?> GetOntTotalsAsync(int ontId);

    /// <summary>Error growth for an SFP ONT module, or null with no history to read.</summary>
    [RequireRole(Roles.Viewer)]
    Task<PonErrorTotals?> GetSfpTotalsAsync(string deviceMac, string portName);

    /// <summary>Codeword growth for a cable modem, or null with no history to read.</summary>
    [RequireRole(Roles.Viewer)]
    Task<CmErrorTotals?> GetCmTotalsAsync(int cmId);
}

/// <summary>PON error counter growth over the window. A null field was not reported.</summary>
public sealed record PonErrorTotals(long? Bip, long? Fec, long? HecUncorrected, long? GemRxDropped);

/// <summary>Cable modem codeword counts over the window.</summary>
public sealed record CmErrorTotals(long Correctables, long Uncorrectables);
