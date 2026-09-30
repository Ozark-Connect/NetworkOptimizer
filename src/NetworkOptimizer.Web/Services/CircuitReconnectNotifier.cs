using Microsoft.AspNetCore.Components.Server.Circuits;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Tells a page that its circuit came back on a new connection. A reconnect keeps component state,
/// so anything a page read from the old connection (the viewer's own address) stays stale unless
/// the page looks again.
/// </summary>
public sealed class CircuitReconnectNotifier : CircuitHandler
{
    private bool _connectedOnce;

    /// <summary>Raised on each connection after the circuit's first.</summary>
    public event Action? Reconnected;

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_connectedOnce)
            Reconnected?.Invoke();
        _connectedOnce = true;
        return Task.CompletedTask;
    }
}
