using NetworkOptimizer.AgentProtocol;

namespace NetworkOptimizer.Agent;

/// <summary>
/// HTTP handler for the agent's REST calls to the central server (enrollment, heartbeats,
/// speed test result relay). Connects through <see cref="DualStackConnector"/> so a broken
/// IPv6 path at the site falls back to IPv4 instead of timing out.
/// </summary>
public static class ServerHttpHandler
{
    public static SocketsHttpHandler Create(bool ignoreSslErrors)
    {
        var handler = new SocketsHttpHandler { ConnectCallback = DualStackConnector.ConnectAsync };
        if (ignoreSslErrors)
        {
            handler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            };
        }
        return handler;
    }
}
