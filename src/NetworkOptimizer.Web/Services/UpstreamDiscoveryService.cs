using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class UpstreamDiscoveryService : IUpstreamDiscoveryService
{
    private readonly UpstreamTracerService _tracer;
    private readonly IAuditContext _audit;

    public UpstreamDiscoveryService(UpstreamTracerService tracer, IAuditContext audit)
    {
        _tracer = tracer;
        _audit = audit;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _tracer.StartDiscoveryAsync(ct);

        // Shape of the result, not its contents: how far it got and how much it found. The path
        // itself (WAN address, first-mile neighbor) is discovery output the panel already shows,
        // and is not what an audit trail is for.
        var s = _tracer.State;
        _audit.SetDetails(new
        {
            step = s.Step.ToString(),
            accessHops = s.AccessHops.Count,
            transitAsns = s.TransitAsns.Count,
            pendingRemovals = s.PendingRemovalTransitAsns.Count,
            failed = s.FailureMessage is not null
        });
    }

    /// <inheritdoc />
    public async Task CommitAsync(CancellationToken ct = default)
    {
        // Counted before the commit: committing clears the review lists, so reading them
        // afterwards would report every run as having applied nothing.
        var s = _tracer.State;
        var detail = new
        {
            accessHops = s.AccessHops.Count,
            transitAsns = s.TransitAsns.Count,
            removals = s.PendingRemovalTransitAsns.Count,
            addedAsns = s.DiscoveryAddedAsns.Count
        };

        await _tracer.CommitResultsAsync(ct);
        _audit.SetDetails(detail);
    }
}
