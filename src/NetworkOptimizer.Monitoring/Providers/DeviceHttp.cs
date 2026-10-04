namespace NetworkOptimizer.Monitoring.Providers;

/// <summary>HTTP plumbing shared by device providers.</summary>
public static class DeviceHttp
{
    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> for a device client. Every connection,
    /// redirect hops included, is opened by <paramref name="dialer"/>, so it lands in the device's
    /// network whatever address it names. Never leave it off a device handler: the default dials
    /// from this server's network, which is not the device's on an agent site.
    /// </summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> Via(IDeviceDialer dialer)
        => (context, ct) => dialer.DialAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
}
