using Microsoft.JSInterop;

namespace NetworkOptimizer.Web;

/// <summary>
/// A trivial round-trip the client can invoke to tell a live Blazor circuit
/// from a dead-but-open socket. On PWA/mobile resume the WebSocket can be dead
/// while still looking open to the client, so the resume handler in App.razor
/// calls <see cref="Ping"/> with a short timeout: a fast reply means the
/// circuit is fine (pick up seamlessly), and a timeout means it genuinely needs
/// a reload. This replaces a blind time-based reload that fired even when the
/// circuit had survived the backgrounding.
/// </summary>
public static class CircuitLiveness
{
    /// <summary>Returns immediately over the circuit; the value is unused - only that it round-trips.</summary>
    [JSInvokable]
    public static bool Ping() => true;
}
