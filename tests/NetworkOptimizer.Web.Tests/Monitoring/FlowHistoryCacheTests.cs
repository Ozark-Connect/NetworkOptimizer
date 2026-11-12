using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Live Traffic History: the console's flow records as rows, the Timeline, and the filters.
/// Addresses are RFC 5737 / documentation ranges.
/// </summary>
public class FlowHistoryCacheTests
{
    private static readonly HashSet<string> Wans = new(StringComparer.OrdinalIgnoreCase) { "Fiber WAN" };

    private static UniFiTrafficFlow Record(
        string id, DateTime seen, string direction = "outgoing", string action = "allowed",
        string clientIp = "192.0.2.20", string remoteIp = "203.0.113.5", int port = 443, string proto = "TCP",
        string remoteZone = "External", string? outNetwork = "Fiber WAN", long bytes = 1000) => new()
    {
        Id = id,
        Action = action,
        Direction = direction,
        Protocol = proto,
        Time = new DateTimeOffset(seen, TimeSpan.Zero).ToUnixTimeMilliseconds(),
        FlowStartTime = new DateTimeOffset(seen.AddSeconds(-30), TimeSpan.Zero).ToUnixTimeMilliseconds(),
        DurationMilliseconds = 30_000,
        TrafficData = new UniFiTrafficFlowData { BytesTotal = bytes, PacketsTotal = 10 },
        Source = direction == "incoming"
            ? new UniFiTrafficFlowEndpoint { Ip = remoteIp, Port = 40000, ZoneName = remoteZone }
            : new UniFiTrafficFlowEndpoint { Ip = clientIp, Port = 50000, ClientName = "Laptop ", Mac = "AA-BB-CC-DD-EE-02", ZoneName = "Internal" },
        Destination = direction == "incoming"
            ? new UniFiTrafficFlowEndpoint { Ip = clientIp, Port = port, ClientName = "NAS", ZoneName = "Internal" }
            : new UniFiTrafficFlowEndpoint { Ip = remoteIp, Port = port, ZoneName = remoteZone, Domains = ["api.example.com"] },
        In = new UniFiTrafficFlowNetwork { NetworkName = direction == "incoming" ? "Fiber WAN" : "Main" },
        Out = new UniFiTrafficFlowNetwork { NetworkName = direction == "incoming" ? "Main" : outNetwork },
        Policies = action == "blocked" ? [new UniFiTrafficFlowPolicy { Name = "Block IoT", Type = "FIREWALL" }] : null,
    };

    [Fact]
    public void OutgoingRecordIsInTheClientsFrame()
    {
        var row = FlowHistoryCache.ToRow(Record("a", DateTime.UtcNow, action: "blocked", bytes: 4096), Wans, "Gateway")!;

        row.ClientName.Should().Be("Laptop", "the console pads some names");
        row.ClientMac.Should().Be("aa:bb:cc:dd:ee:02");
        row.ClientVia.Should().Be("Main");
        row.Destination.Should().Be("api.example.com");
        row.Route.Should().Be("Fiber WAN");
        row.RouteKind.Should().Be(LiveRouteKind.Wan);
        row.Action.Should().Be("Blocked");
        row.PolicyName.Should().Be("Block IoT");
        row.TotalBytes.Should().Be(4096);
        row.Duration.Should().Be(TimeSpan.FromSeconds(30));
        row.App.Should().Be("HTTPS");
    }

    [Fact]
    public void AnExternalExitThatIsNotAWanIsAVpn()
    {
        var row = FlowHistoryCache.ToRow(Record("a", DateTime.UtcNow, outNetwork: "VPN Exit"), Wans, "Gateway")!;

        row.Route.Should().Be("VPN Exit");
        row.RouteKind.Should().Be(LiveRouteKind.Vpn);
    }

    [Fact]
    public void IncomingRecordTakesTheDestinationAsTheClient()
    {
        var row = FlowHistoryCache.ToRow(Record("a", DateTime.UtcNow, direction: "incoming", port: 8443), Wans, "Gateway")!;

        row.ClientName.Should().Be("NAS");
        row.ClientIp.Should().Be("192.0.2.20");
        row.DestinationIp.Should().Be("203.0.113.5");
        row.RemotePort.Should().Be(40000);
        row.Route.Should().Be("Fiber WAN");
    }

    [Fact]
    public void LocalRecordToTheGatewayIsLan()
    {
        var record = Record("a", DateTime.UtcNow, direction: "local", remoteIp: "192.0.2.1", port: 53, proto: "UDP", remoteZone: "Gateway", outNetwork: null);
        record.Destination!.Domains = null;

        var row = FlowHistoryCache.ToRow(record, Wans, "Home Gateway")!;

        row.Destination.Should().Be("Home Gateway");
        row.Route.Should().Be("LAN");
        row.RouteKind.Should().Be(LiveRouteKind.Lan);
    }

    [Fact]
    public void UniFiDevicesAreNamedByTheSitesDeviceList()
    {
        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aa:bb:cc:dd:ee:10"] = "Office AP",
            ["192.0.2.30"] = "Core Switch",
        };
        // To a switch, known by its LAN address: the record names neither end's device.
        var toSwitch = Record("a", DateTime.UtcNow, remoteIp: "192.0.2.30", port: 22, remoteZone: "Internal", outNetwork: null);
        toSwitch.Destination!.Domains = null;
        // From an AP, known by its MAC.
        var fromAp = Record("b", DateTime.UtcNow, clientIp: "192.0.2.40");
        fromAp.Source!.ClientName = null;
        fromAp.Source.Mac = "AA-BB-CC-DD-EE-10";

        FlowHistoryCache.ToRow(toSwitch, Wans, "Gateway", devices)!.Destination.Should().Be("Core Switch");
        FlowHistoryCache.ToRow(fromAp, Wans, "Gateway", devices)!.ClientName.Should().Be("Office AP");
        FlowHistoryCache.ToRow(toSwitch, Wans, "Gateway")!.Destination.Should().Be("192.0.2.30", "no device list: the address");
    }

    [Fact]
    public void FiltersWindowAndSortShapeTheView()
    {
        var now = DateTime.UtcNow;
        var cache = new FlowHistoryCache();
        cache.SetSiteNames(Wans, "Gateway");
        cache.Merge(
        [
            Record("a", now.AddMinutes(-20), bytes: 100),
            Record("b", now.AddMinutes(-10), action: "blocked", port: 23, bytes: 50),
            Record("c", now.AddMinutes(-2), proto: "UDP", port: 51820, bytes: 9000),
        ], now, cache.Generation);

        View(cache, now).Flows.Select(f => f.Key).Should().Equal("c", "b", "a");
        View(cache, now, sort: LiveHistorySort.Data).Flows.Select(f => f.Key).Should().Equal("c", "a", "b");
        View(cache, now, new LiveTrafficFilter(LiveTrafficField.Action, "Blocked")).Flows.Select(f => f.Key).Should().Equal("b");
        View(cache, now, new LiveTrafficFilter(LiveTrafficField.Protocol, "udp")).Flows.Select(f => f.Key).Should().Equal("c");
        View(cache, now, new LiveTrafficFilter(LiveTrafficField.Port, "23", Exclude: true)).Flows.Select(f => f.Key).Should().Equal("c", "a");

        var windowed = cache.BuildView(new LiveHistoryQuery { ViewerId = "v", From = now.AddMinutes(-15), To = now.AddMinutes(-5) }, LiveHistoryState.Ready, now);
        windowed.Flows.Select(f => f.Key).Should().Equal("b");
        windowed.MatchingBlocked.Should().Be(1);
        windowed.TotalFlows.Should().Be(3);
    }

    [Fact]
    public void TimelineHasOneBucketPerMinuteWithEmptyMinutes()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 30, DateTimeKind.Utc);
        var cache = new FlowHistoryCache();
        cache.Merge(
        [
            Record("a", now.AddMinutes(-5)),
            Record("b", now.AddMinutes(-5), action: "blocked", bytes: 70),
            Record("c", now.AddMinutes(-1)),
        ], now, cache.Generation);

        var buckets = View(cache, now).Buckets;

        buckets.Should().HaveCount(6);
        buckets[0].Start.Should().Be(new DateTime(2026, 1, 1, 11, 55, 0, DateTimeKind.Utc));
        (buckets[0].Allowed, buckets[0].Blocked, buckets[0].BlockedBytes).Should().Be((1, 1, 70));
        buckets.Skip(1).Take(3).Should().OnlyContain(b => b.Allowed + b.Blocked == 0);
        buckets[4].Allowed.Should().Be(1);
    }

    [Fact]
    public void LastViewerLeavingEmptiesTheCacheAndVoidsARefreshInFlight()
    {
        var now = DateTime.UtcNow;
        var cache = new FlowHistoryCache();
        cache.Touch("v1", now);
        var inFlight = cache.Generation;

        cache.Release("v1", now).Should().BeTrue();
        cache.Merge([Record("a", now)], now, inFlight);
        cache.MarkRefreshed(true, inFlight);

        cache.Loaded.Should().BeFalse();
        View(cache, now).TotalFlows.Should().Be(0);
    }

    [Fact]
    public void RecordsOlderThanTheWindowAreDropped()
    {
        var now = DateTime.UtcNow;
        var cache = new FlowHistoryCache();
        cache.Merge([Record("old", now - FlowHistoryCache.Window - TimeSpan.FromMinutes(1)), Record("new", now)], now, cache.Generation);

        View(cache, now).Flows.Select(f => f.Key).Should().Equal("new");
    }

    private static LiveHistoryView View(FlowHistoryCache cache, DateTime now, LiveTrafficFilter? filter = null, LiveHistorySort sort = LiveHistorySort.Time) =>
        cache.BuildView(new LiveHistoryQuery
        {
            ViewerId = "v",
            Filters = filter == null ? Array.Empty<LiveTrafficFilter>() : [filter],
            Sort = sort,
        }, LiveHistoryState.Ready, now);
}
