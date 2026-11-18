using System.Net;

namespace NetworkOptimizer.Monitoring.Conntrack;

/// <summary>Turns a handshake, timed in the flow's original direction, into Live Traffic's client frame.</summary>
public static class FlowRoundTrips
{
    /// <summary>
    /// The remote and client round trips in ms. A flow the client opened answers first from the remote
    /// end; one opened toward the client (a port forward) answers first from the client. The Gateway's own
    /// flows have no client round trip: that end is the Gateway itself.
    /// </summary>
    public static (double? Remote, double? Client) Orient(FlowHandshake h, string clientIp, bool gateway)
    {
        var clientOpened = IPAddress.TryParse(clientIp, out var ip) && ip.Equals(h.OrigSrc);
        var (remote, client) = clientOpened ? (h.OrigLegMs, h.ReplyLegMs) : (h.ReplyLegMs, (double?)h.OrigLegMs);
        return (remote, gateway ? null : client);
    }

    /// <summary>The wire value: whole microseconds, at least 1, or 0 for not measured.</summary>
    public static int ToMicroseconds(double? ms) =>
        ms is { } v and >= 0 ? (int)Math.Clamp(Math.Round(v * 1000), 1, int.MaxValue) : 0;
}
