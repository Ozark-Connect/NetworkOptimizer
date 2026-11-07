using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class UpstreamDiscoveryService : IUpstreamDiscoveryService
{
    private readonly UpstreamTracerService _tracer;
    private readonly IAuditContext _audit;
    private readonly UpstreamDiscoveryGate? _healthGate;

    public UpstreamDiscoveryService(UpstreamTracerService tracer, IAuditContext audit, UpstreamDiscoveryGate? healthGate = null)
    {
        _tracer = tracer;
        _audit = audit;
        _healthGate = healthGate;
    }

    /// <inheritdoc />
    public async Task<UpstreamDiscoveryStart> StartAsync(Monitoring.UpstreamTracerService? tracer = null, bool overrideHealthGate = false,
        CancellationToken ct = default)
    {
        // The audit gate wraps whichever tracer runs; a per-WAN run is the same operator action
        // on another WAN's instance, not a different action.
        var t = tracer ?? _tracer;

        var verdict = _healthGate == null
            ? UpstreamDiscoveryGateVerdict.Clear
            : await _healthGate.EvaluateAsync(t.SiteSlug, t.Binding?.WanInterface, ct);
        if (!verdict.Ok && !overrideHealthGate)
        {
            // Nothing started: the caller warns and asks again.
            _audit.SuppressNoChange();
            return new UpstreamDiscoveryStart(false, verdict.Reason);
        }

        await t.StartDiscoveryAsync(verdict.Ok ? null : verdict.Reason, ct);

        // Shape of the result, not its contents: how far it got and how much it found. The path
        // itself (WAN address, first-mile neighbor) is discovery output the panel already shows,
        // and is not what an audit trail is for.
        var s = t.State;
        _audit.SetDetails(new
        {
            step = s.Step.ToString(),
            accessHops = s.AccessHops.Count,
            transitAsns = s.TransitAsns.Count,
            pendingRemovals = s.PendingRemovalTransitAsns.Count,
            failed = s.FailureMessage is not null,
            healthGateOverridden = !verdict.Ok,
            healthGateReason = verdict.Reason
        });
        return new UpstreamDiscoveryStart(true, verdict.Reason);
    }

    /// <inheritdoc />
    public async Task CommitAsync(Monitoring.UpstreamTracerService? tracer = null, CancellationToken ct = default)
    {
        var t = tracer ?? _tracer;
        // Counted before the commit: committing clears the review lists, so reading them
        // afterwards would report every run as having applied nothing.
        var s = t.State;
        var detail = new
        {
            accessHops = s.AccessHops.Count,
            transitAsns = s.TransitAsns.Count,
            removals = s.PendingRemovalTransitAsns.Count,
            addedAsns = s.DiscoveryAddedAsns.Count
        };

        await t.CommitResultsAsync(ct);
        _audit.SetDetails(detail);
    }
}
