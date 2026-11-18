using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Which end of a flow is the service, and what History calls it. The reply records are the shape a
/// console logs for every device answering the gateway's one-shot mDNS query. RFC 5737 addresses.
/// </summary>
public class FlowNamingTests
{
    private static readonly HashSet<string> Wans = new(StringComparer.OrdinalIgnoreCase) { "Fiber WAN" };

    [Theory]
    [InlineData("SSL/TLS", "HTTPS", "HTTPS")]
    [InlineData("SSL/TLS", "IMAPS", "IMAPS")]
    [InlineData("SSL/TLS", "POP3S", "POP3S")]
    [InlineData("SSL/TLS", "SMTP Submit", "SMTP Submit")]
    [InlineData("SSL/TLS", "RDP", "RDP")]
    [InlineData("SSL/TLS", "HTTPS-Alt", "HTTPS-Alt")]
    [InlineData("SSL/TLS", "HTTP", "HTTPS")]
    [InlineData("SSL/TLS", "HTTP-Alt", "HTTPS")]
    [InlineData("SSL/TLS", "OTHER", "SSL/TLS")]
    [InlineData("SSL/TLS", null, "SSL/TLS")]
    [InlineData("Netflix", "HTTPS", "Netflix")]
    public void Tls_on_a_known_service_reads_the_service(string app, string? service, string expected)
    {
        FlowNaming.TlsAsService(app, service).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("OTHER", true)]
    [InlineData("HTTPS", true)]
    [InlineData("HTTPS-Alt", true)]
    [InlineData("MQTT-TLS", false)]
    [InlineData("SMTP", false)]
    public void A_cloud_name_replaces_only_a_web_port(string? service, bool generic)
    {
        FlowNaming.IsGenericService(service).Should().Be(generic);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("OTHER", null)]
    [InlineData(" other ", null)]
    [InlineData("HTTPS", "HTTPS")]
    public void TheConsolesCatchAllIsNoName(string? service, string? expected) =>
        FlowNaming.ConsoleService(service).Should().Be(expected);

    [Theory]
    [InlineData("TELNET", "Telnet")]
    [InlineData("SSH", "SSH")]
    [InlineData("Bonjour", "Bonjour")]   // not a well-known port name: the console's spelling stays
    public void AConsoleServiceTakesOurSpelling(string service, string expected) =>
        FlowNaming.ConsoleService(service).Should().Be(expected);

    [Theory]
    [InlineData(5353, 50690, 5353)]   // a reply to a query from a throwaway port
    [InlineData(51000, 443, 443)]     // an ordinary outbound connection
    [InlineData(51000, 51820, 51820)] // both high: the remote end stays the service
    [InlineData(0, 0, 0)]
    public void TheServiceEndIsTheRemoteUnlessItIsTheThrowawayOne(int client, int remote, int expected) =>
        FlowNaming.ServicePort(client, remote).Should().Be(expected);

    [Fact]
    public void BarePortNamesTheServiceEnd()
    {
        FlowNaming.BarePort("UDP", 21324, 50690).Should().Be("UDP 21324");
        FlowNaming.BarePort("UDP", 50000, 21324).Should().Be("UDP 21324");
        FlowNaming.BarePort("ICMP", 0, 0).Should().Be("ICMP");
    }

    private static UniFiTrafficFlow Reply(int clientPort, int gatewayPort = 50690, string service = "OTHER") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Action = "allowed",
        Direction = "local",
        Protocol = "UDP",
        Service = service,
        Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        FlowStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        TrafficData = new UniFiTrafficFlowData { BytesTotal = 1250, PacketsTotal = 2 },
        Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.20", Port = clientPort, Mac = "aa:bb:cc:dd:ee:02", ClientName = "Laptop", ZoneName = "Internal" },
        Destination = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.1", Port = gatewayPort, Mac = "aa:bb:cc:dd:ee:01", ZoneName = "Gateway" },
        In = new UniFiTrafficFlowNetwork { NetworkName = "Main" },
    };

    [Fact]
    public void AnMdnsReplyToTheGatewayReadsAsMdns()
    {
        var row = FlowHistoryCache.ToRow(Reply(5353), Wans, "Gateway")!;

        row.App.Should().Be("mDNS", "the console's OTHER says less than the client's port name");
        row.DestinationSub.Should().Be("mDNS");
        row.Destination.Should().Be("Gateway");
        row.RouteKind.Should().Be(LiveRouteKind.Lan);
        row.ClientPort.Should().Be(5353);
        row.RemotePort.Should().Be(50690);
    }

    [Fact]
    public void AnUnknownServiceOnTheClientEndIsNamedByThatPort()
    {
        FlowHistoryCache.ToRow(Reply(21324), Wans, "Gateway")!.App.Should().Be("UDP 21324");
    }

    [Fact]
    public void BothEndsHighKeepTheRemotePort()
    {
        FlowHistoryCache.ToRow(Reply(51000, 51001), Wans, "Gateway")!.App.Should().Be("UDP 51001");
    }

    [Fact]
    public void ARealConsoleServiceStillWins()
    {
        FlowHistoryCache.ToRow(Reply(5353, service: "Bonjour"), Wans, "Gateway")!.App.Should().Be("Bonjour");
    }

    // ---------------------------------------------------------------- one client's cache

    [Fact]
    public void AClientCacheIsSharedByItsViewersAndGoesWithTheLast()
    {
        var registry = new LiveTrafficRegistry();
        var now = DateTime.UtcNow;
        var first = registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:02", "viewer-1", now);
        registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:02", "viewer-2", now).Should().BeSameAs(first);

        registry.ReleaseClientHistory("site", "viewer-1", now);
        registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:02", "viewer-2", now).Should().BeSameAs(first, "one viewer is still on it");

        registry.ReleaseClientHistory("site", "viewer-2", now);
        registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:02", "viewer-3", now).Should().NotBeSameAs(first);
    }

    [Fact]
    public void AnIdleClientCacheIsDroppedWhenAnotherIsAskedFor()
    {
        var registry = new LiveTrafficRegistry();
        var now = DateTime.UtcNow;
        var stale = registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:02", "viewer-1", now);

        registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:03", "viewer-2", now.AddMinutes(5));

        registry.ClientHistoryFor("site", "aa:bb:cc:dd:ee:02", "viewer-1", now.AddMinutes(5)).Should().NotBeSameAs(stale);
    }
}
