using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// Which end of a flow is the service, and what to call it, for Live and History alike. A flow in the
/// client's frame can have the service on the client's own port: a reply to a query the far end sent
/// from a throwaway port (every device's mDNS answer to the gateway's one-shot query), or a connection
/// made to the client. Its remote port then names nothing.
/// </summary>
public static class FlowNaming
{
    /// <summary>
    /// DPI's SSL/TLS on a known service reads the service, the more specific name: IMAPS on 993, RDP on
    /// 3389. HTTP inside TLS is HTTPS. Only a port with no name keeps SSL/TLS.
    /// </summary>
    public static string? TlsAsService(string? app, string? service)
    {
        if (app != "SSL/TLS" || ConsoleService(service) is not { } name) return app;
        return name is "HTTP" or "HTTP-Alt" ? "HTTPS" : name;
    }

    /// <summary>A web or bare-TLS protocol name, which a cloud's name (AWS) is more specific than.</summary>
    public static bool IsGenericApp(string? app) => app is "HTTP" or "HTTPS" or "HTTP/3" or "QUIC" or "SSL/TLS";

    /// <summary>A web port or no named port: a cloud's name may replace it. MQTT-TLS on 8883 keeps its name.</summary>
    public static bool IsGenericService(string? service) =>
        ConsoleService(service) is null or "HTTP" or "HTTPS" or "HTTP-Alt" or "HTTPS-Alt";

    /// <summary>Ports from here up are treated as ephemeral, as Threat Intelligence treats them.</summary>
    public const int EphemeralFloor = NetworkOptimizer.Threats.Analysis.FlowInterestFilter.EphemeralPortFloor;

    /// <summary>The console's own catch-all service label, which says less than the port's name.</summary>
    private const string ConsoleCatchAll = "OTHER";

    /// <summary>The service name a console record carries, or null when it has none or only its catch-all.</summary>
    public static string? ConsoleService(string? service)
    {
        var value = service?.Trim();
        return string.IsNullOrEmpty(value) || value.Equals(ConsoleCatchAll, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>The remote port is a throwaway one and the client's is not: the client's end is the service.</summary>
    public static bool ServiceOnClient(int clientPort, int remotePort) =>
        remotePort >= EphemeralFloor && clientPort > 0 && clientPort < EphemeralFloor;

    /// <summary>The port at the service end of the flow.</summary>
    public static int ServicePort(int clientPort, int remotePort) =>
        ServiceOnClient(clientPort, remotePort) ? clientPort : remotePort;

    /// <summary>The well-known name of the remote port, else of the client's when the remote one is unprivileged.</summary>
    public static string? PortName(int clientPort, int remotePort) =>
        NetworkUtilities.GetPortServiceName(remotePort)
        ?? (remotePort >= 1024 ? NetworkUtilities.GetPortServiceName(clientPort) : null);

    /// <summary>"UDP 21324": the protocol and the service end's port, or the protocol alone when there is no port.</summary>
    public static string BarePort(string protoLabel, int clientPort, int remotePort)
    {
        var port = ServicePort(clientPort, remotePort);
        return port > 0 ? $"{protoLabel} {port}" : protoLabel;
    }
}
