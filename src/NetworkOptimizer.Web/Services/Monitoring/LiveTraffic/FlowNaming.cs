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
    /// DPI's SSL/TLS on a port whose service runs over TLS reads that service: TLS on 443 is HTTPS, on 993
    /// IMAPS, on 587 SMTP after STARTTLS, and the sub-line then has nothing to repeat. TLS on any other
    /// service (HTTP on 80) keeps the DPI name, which is the news there.
    /// </summary>
    public static string? TlsAsService(string? app, string? service) =>
        app == "SSL/TLS" && service != null && TlsServices.Contains(service) ? service : app;

    /// <summary>The port table's names for services that speak TLS from the first byte or upgrade to it.</summary>
    private static readonly HashSet<string> TlsServices = new(StringComparer.Ordinal)
    {
        "HTTPS", "HTTPS-Alt", "SMTPS", "LDAPS", "DNS over TLS", "IMAPS", "POP3S", "MQTT-TLS",
        "SMTP", "SMTP Submit", "IMAP", "POP3", "LDAP", "FTP",
    };

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
