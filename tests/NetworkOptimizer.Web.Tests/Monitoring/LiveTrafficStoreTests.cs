using FluentAssertions;
using Moq;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Monitoring.Tailscale;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using NetworkOptimizer.Web.Services.Tailscale;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The server half of Live Traffic: what the store makes of the agent's batches, the console's
/// labels, and a viewer's filters. Addresses are RFC 5737 / documentation ranges.
/// </summary>
public class LiveTrafficStoreTests
{
    [Fact]
    public void Tailscale_app_filter_keeps_Tailscale_devices_traffic_too()
    {
        LiveTrafficFilter[] tailscale = [new(LiveTrafficField.App, "Tailscale")];
        var fromDevice = new LiveFlowRow { Key = "a", App = "HTTPS", ClientIsTailnet = true };
        var toDevice = new LiveFlowRow { Key = "b", App = "SSH", RemoteClientVia = "Tailscale" };
        var tunnel = new LiveFlowRow { Key = "c", App = "Tailscale" };
        var other = new LiveFlowRow { Key = "d", App = "HTTPS" };

        LiveTrafficStore.Matches(fromDevice, tailscale, null).Should().BeTrue();
        LiveTrafficStore.Matches(toDevice, tailscale, null).Should().BeTrue();
        LiveTrafficStore.Matches(tunnel, tailscale, null).Should().BeTrue();
        LiveTrafficStore.Matches(other, tailscale, null).Should().BeFalse();
        LiveTrafficStore.Matches(fromDevice, [new(LiveTrafficField.App, "Tailscale", Exclude: true)], null).Should().BeFalse();
    }

    [Fact]
    public void ChipsOnOneFieldOrTogetherAndFieldsAnd()
    {
        var laptopHttps = new LiveFlowRow { Key = "a", ClientName = "Laptop", App = "HTTPS" };
        var tvHttps = new LiveFlowRow { Key = "b", ClientName = "TV", App = "HTTPS" };
        var tvDns = new LiveFlowRow { Key = "c", ClientName = "TV", App = "DNS" };
        var phone = new LiveFlowRow { Key = "d", ClientName = "Phone", App = "HTTPS" };
        LiveTrafficFilter[] two = [new(LiveTrafficField.Client, "Laptop"), new(LiveTrafficField.Client, "TV")];
        LiveTrafficFilter[] twoHttps = [.. two, new(LiveTrafficField.App, "HTTPS")];
        LiveTrafficFilter[] notTv = [new(LiveTrafficField.Client, "TV", Exclude: true), new(LiveTrafficField.App, "HTTPS")];

        new[] { laptopHttps, tvHttps, tvDns, phone }.Where(r => LiveTrafficStore.Matches(r, two, null))
            .Select(r => r.Key).Should().BeEquivalentTo(["a", "b", "c"]);
        new[] { laptopHttps, tvHttps, tvDns, phone }.Where(r => LiveTrafficStore.Matches(r, twoHttps, null))
            .Select(r => r.Key).Should().BeEquivalentTo(["a", "b"]);
        new[] { laptopHttps, tvHttps, tvDns, phone }.Where(r => LiveTrafficStore.Matches(r, notTv, null))
            .Select(r => r.Key).Should().BeEquivalentTo(["a", "d"], "an exclude chip that hits drops the row");
    }

    private const string Nas = "aa:bb:cc:dd:ee:01";
    private const string Laptop = "aa:bb:cc:dd:ee:02";

    private static LiveDnsRow Lookup(DateTime at, string type, string name = "example.com", string mac = Laptop,
        bool blocked = false, int ms = 5) =>
        new(at, "Laptop", "192.0.2.20", name, type, "Gateway", "192.0.2.1", false, false, blocked ? "NXDOMAIN" : "NOERROR", ms, true,
            blocked, mac);

    [Fact]
    public void LookupsOfOneNameSentTogetherAreOneRow()
    {
        var t = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var rows = LiveTrafficStore.CombineTypes(
        [
            Lookup(t.AddMilliseconds(40), "HTTPS", ms: 9),
            Lookup(t.AddMilliseconds(20), "AAAA", blocked: true),
            Lookup(t.AddMilliseconds(10), "other.example", name: "other.example"),
            Lookup(t, "A"),
        ]);

        rows.Should().HaveCount(2);
        rows[0].QType.Should().Be("A · AAAA · HTTPS");
        rows[0].At.Should().Be(t.AddMilliseconds(40), "the row keeps its newest lookup's time and place");
        rows[0].Blocked.Should().BeTrue();
        rows[0].LatencyMs.Should().Be(9);
        rows[1].Name.Should().Be("other.example");
    }

    [Fact]
    public void LookupsApartOrFromAnotherClientStaySeparate()
    {
        var t = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
        var rows = LiveTrafficStore.CombineTypes(
        [
            Lookup(t.AddSeconds(10), "A"),
            Lookup(t.AddSeconds(10), "AAAA", mac: Nas),
            Lookup(t, "AAAA"),
        ]);

        rows.Select(r => r.QType).Should().Equal("A", "AAAA", "AAAA");
    }

    private static LiveFlow Flow(string key, string mac, string clientIp, string remote, int port, string ifname,
        long down, long up, long totalDown = 0, string proto = "tcp", bool lan = false) => new()
    {
        Key = key,
        Proto = proto,
        ClientIp = clientIp,
        ClientMac = mac,
        ClientPort = 50000,
        RemoteIp = remote,
        RemotePort = port,
        WanIfname = ifname,
        Lan = lan,
        DownBytes = down,
        UpBytes = up,
        TotalDownBytes = totalDown == 0 ? down : totalDown,
        TotalUpBytes = up,
        AgeSeconds = 30,
    };

    private static LiveTrafficStore SeededStore()
    {
        var store = new LiveTrafficStore();
        store.SetSiteData(
            [
                new LiveClientInfo(Nas, "NAS", "192.0.2.10", "Port 9", "Main"),
                new LiveClientInfo(Laptop, "Laptop", "192.0.2.20", "6 GHz", "Main"),
            ],
            ["192.0.2.1", "198.51.100.7"],
            "Gateway",
            new Dictionary<string, LiveRouteInfo> { ["eth4"] = new("Fiber WAN", LiveRouteKind.Wan, 2_000_000_000, 1_000_000_000) },
            new LiveRouteInfo("Fiber WAN", LiveRouteKind.Wan, 2_000_000_000, 1_000_000_000));
        return store;
    }

    private static LiveTrafficBatch Batch(params LiveFlow[] flows)
    {
        var batch = new LiveTrafficBatch { WindowSeconds = 2, TotalFlows = 1309 };
        batch.Flows.AddRange(flows);
        return batch;
    }

    private static LiveTrafficQuery Query(params LiveTrafficFilter[] filters) =>
        new() { ViewerId = "test", Filters = filters };

    [Fact]
    public void RanksByDownloadRateAndNamesTheClient()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1_000_000, 10_000),
            Flow("b", Nas, "192.0.2.10", "203.0.113.9", 51820, "eth4", 120_000_000, 5_000_000, totalDown: 587_000_000_000, proto: "udp")));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Select(f => f.Key).Should().Equal("b", "a");
        var top = view.Flows[0];
        top.ClientName.Should().Be("NAS");
        top.ClientVia.Should().Be("Port 9");
        top.DownBps.Should().Be(480_000_000);
        top.App.Should().Be("WireGuard");
        top.DestinationSub.Should().Be("WireGuard");
        top.Route.Should().Be("Fiber WAN");
        view.TotalFlows.Should().Be(1309);
    }

    [Fact]
    public void WireGuardClientTunnelTakesItsNetworkNameWithoutAConsoleRecord()
    {
        var store = SeededStore();
        store.SetTunnelNames(LiveTrafficService.WireGuardClientNames(
        [
            new UniFiNetworkConfig { Name = "Office VPN", Purpose = "vpn-client", VpnType = "wireguard-client", WireguardId = 1 },
            new UniFiNetworkConfig { Name = "Road Warriors", Purpose = "remote-user-vpn", VpnType = "wireguard", WireguardId = 2 },
        ]));
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "wgclt1", 1000, 10),
            Flow("b", Laptop, "192.0.2.20", "203.0.113.6", 443, "wgclt2", 1000, 10)));

        var rows = store.BuildView(Query(), LiveTrafficState.Live).Flows.ToDictionary(f => f.Key);

        rows["a"].Route.Should().Be("Office VPN");
        rows["a"].RouteKind.Should().Be(LiveRouteKind.Vpn);
        rows["b"].Route.Should().Be("wgclt2", "a remote-user VPN server is not a route out");
    }

    [Fact]
    public void DnsAnswerNamesTheDestination()
    {
        var store = SeededStore();
        var batch = Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10));
        var dns = new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "192.0.2.1",
            Name = "api.example.com",
            Qtype = "A",
            Rcode = "NOERROR",
            LatencyMs = 4,
            Answered = true,
        };
        dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = "203.0.113.5", Ttl = 300 });
        batch.Dns.Add(dns);
        store.Ingest(batch);

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single().Destination.Should().Be("api.example.com");
        view.Flows.Single().DnsDetail.Should().Contain("Gateway");
        view.Dns.Single().Resolver.Should().Be("Gateway");
        view.Dns.Single().Bypass.Should().BeFalse();
    }

    [Theory]
    [InlineData(LiveFlowSort.Upload, true, new[] { "b", "a" })]
    [InlineData(LiveFlowSort.Upload, false, new[] { "a", "b" })]
    [InlineData(LiveFlowSort.Client, false, new[] { "a", "b" })]
    [InlineData(LiveFlowSort.Client, true, new[] { "b", "a" })]
    public void SortsFlowsByColumnBothWays(LiveFlowSort sort, bool descending, string[] expected)
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 9_000_000, 1_000),
            Flow("b", Nas, "192.0.2.10", "203.0.113.9", 443, "eth4", 1_000, 9_000_000)));

        var view = store.BuildView(Query() with { Sort = sort, Descending = descending }, LiveTrafficState.Live);

        view.Flows.Select(f => f.Key).Should().Equal(expected);
    }

    [Fact]
    public void SiteAddressIsBadgedAndNamesTheApp()
    {
        var store = SeededStore();
        store.SetSiteAddresses([("203.0.113.77", "Branch Office"), ("2001:db8:5::/48", "Branch Office")]);
        store.Ingest(Batch(
            Flow("v4", Laptop, "192.0.2.20", "203.0.113.77", 40000, "eth4", 1000, 10),
            Flow("v6", Laptop, "192.0.2.20", "2001:db8:5::10", 40001, "eth4", 1000, 10)));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Should().OnlyContain(f => f.RemoteSite == "Branch Office" && f.App == "Branch Office");
        view.Flows.Single(f => f.Key == "v4").Destination.Should().Be("203.0.113.77");
    }

    [Fact]
    public void OwnerNamesAnUnnamedAddressAndPtrWins()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("o", Laptop, "192.0.2.20", "203.0.113.40", 40000, "eth4", 1000, 10),
            Flow("p", Laptop, "192.0.2.20", "203.0.113.41", 40001, "eth4", 1000, 10)));
        store.AddressesWithoutOwner(10).Should().BeEquivalentTo(["203.0.113.40", "203.0.113.41"]);
        store.SetOwners(new Dictionary<string, string?> { ["203.0.113.40"] = "Example Org", ["203.0.113.41"] = "Example Org" });
        store.SetPtrNames(new Dictionary<string, string?> { ["203.0.113.40"] = null, ["203.0.113.41"] = "host.example.net" });

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single(f => f.Key == "o").Destination.Should().Be("Example Org");
        view.Flows.Single(f => f.Key == "p").Destination.Should().Be("host.example.net");
        view.Flows.Should().OnlyContain(f => f.App == "Example Org" && f.RemoteOrg == "Example Org");
    }

    private const string Printer = "aa:bb:cc:dd:ee:03";

    private static UniFiClientDetailResponse Seen(string mac, string ip, DateTime lastSeen, string? name = null,
        string? displayName = null, string? hostname = null, string? oui = null) => new()
    {
        Mac = mac,
        LastIp = ip,
        Name = name,
        DisplayName = displayName,
        Hostname = hostname,
        Oui = oui,
        LastSeen = new DateTimeOffset(lastSeen).ToUnixTimeSeconds(),
    };

    [Fact]
    public void AConnectedClientWithNoAddressTakesItsLastOneFromHistory()
    {
        var now = DateTime.UtcNow;
        var store = new LiveTrafficStore();
        store.SetSiteData(
            [
                new LiveClientInfo(Laptop, "Laptop", "192.0.2.20", "6 GHz", "Main"),
                new LiveClientInfo(Printer, "Living Room TV", "", "Port 4", "Main", Vendor: "LG Electronics"),
            ],
            ["192.0.2.1"], "Gateway", new Dictionary<string, LiveRouteInfo>(), null);
        store.SetRecentClients(LiveTrafficStore.RecentClients(
            [Seen(Printer, "192.0.2.30", now.AddMinutes(-5), name: "Living Room TV", oui: "LG Electronics")], now));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "192.0.2.30", 3001, "", 1000, 10, lan: true)));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();
        row.Destination.Should().Be("Living Room TV");
        row.App.Should().Be("LG webOS TV");
    }

    [Fact]
    public void AnOfflineDeviceKeepsItsVendorForTheAppName()
    {
        var now = DateTime.UtcNow;
        var store = SeededStore();
        store.SetRecentClients(LiveTrafficStore.RecentClients(
            [Seen(Printer, "192.0.2.30", now.AddHours(-2), name: "Living Room TV", oui: "LG Electronics")], now));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "192.0.2.30", 3000, "", 1000, 10, lan: true)));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();
        row.Destination.Should().Be("Living Room TV");
        row.App.Should().Be("LG webOS TV");
    }

    [Fact]
    public void AnOfflineClientStillNamesTheDestination()
    {
        var now = DateTime.UtcNow;
        var store = SeededStore();
        store.SetRecentClients(LiveTrafficStore.RecentClients([Seen(Printer, "192.0.2.30", now.AddHours(-3), name: "Office Printer")], now));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "192.0.2.30", 9100, "", 1000, 10, lan: true)));

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().Destination.Should().Be("Office Printer");
    }

    [Fact]
    public void AConnectedClientOnTheAddressBeatsTheOfflineOneThatHadIt()
    {
        var now = DateTime.UtcNow;
        var store = SeededStore();
        store.SetRecentClients(LiveTrafficStore.RecentClients([Seen(Printer, "192.0.2.10", now.AddHours(-1), name: "Old Printer")], now));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "192.0.2.10", 445, "", 1000, 10, lan: true)));

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().Destination.Should().Be("NAS");
    }

    [Fact]
    public void AConnectedClientKeepsItsOwnRecordOverItsHistory()
    {
        var now = DateTime.UtcNow;
        var store = SeededStore();
        // The NAS's history entry is an older address and name; neither replaces the connected record.
        store.SetRecentClients(LiveTrafficStore.RecentClients([Seen(Nas, "192.0.2.99", now.AddHours(-1), name: "Old NAS")], now));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "192.0.2.99", 445, "", 1000, 10, lan: true)));

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().Destination.Should().Be("192.0.2.99");
    }

    [Fact]
    public void RecentClientsTakeOnlyAddressesSeenWithinTheWindowNewestFirst()
    {
        var now = DateTime.UtcNow;
        var recent = LiveTrafficStore.RecentClients(
        [
            Seen("aa:bb:cc:dd:ee:10", "192.0.2.40", now.AddHours(-60), name: "Stale"),
            Seen("aa:bb:cc:dd:ee:11", "192.0.2.41", now.AddHours(-5), displayName: "Vendor, Inc. ee:11"),
            Seen("AA-BB-CC-DD-EE-12", "192.0.2.41", now.AddHours(-1), hostname: "sensor"),
        ], now);

        recent.Select(c => (c.Mac, c.Name, c.Ip)).Should().Equal(
            ("aa:bb:cc:dd:ee:12", "sensor", "192.0.2.41"),
            ("aa:bb:cc:dd:ee:11", "Vendor, Inc. ee:11", "192.0.2.41"));
    }

    [Fact]
    public void AppleOwnedAddressIsAppleAndStillHttpsUnderAnAppFilter()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("apple", Laptop, "192.0.2.20", "203.0.113.50", 443, "eth4", 1000, 10),
            Flow("other", Laptop, "192.0.2.20", "203.0.113.51", 443, "eth4", 1000, 10),
            Flow("ping", Laptop, "192.0.2.20", "203.0.113.52", 0, "eth4", 1000, 10, proto: "icmp")));
        store.SetOwners(new Dictionary<string, string?>
        {
            ["203.0.113.50"] = "APPLE-ENGINEERING", ["203.0.113.51"] = null, ["203.0.113.52"] = "Example Org",
        });
        store.SetPtrNames(new Dictionary<string, string?> { ["203.0.113.50"] = null, ["203.0.113.51"] = null, ["203.0.113.52"] = null });

        var all = store.BuildView(Query(), LiveTrafficState.Live);
        all.Flows.Single(f => f.Key == "apple").App.Should().Be("Apple");
        all.Flows.Single(f => f.Key == "other").App.Should().Be("HTTPS");
        all.Flows.Single(f => f.Key == "ping").App.Should().Be("ICMP", "a ping is not the app of whoever answers it");

        // HTTPS is a layer under the app: filtering by it keeps the Apple flow.
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.App, "HTTPS")), LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().BeEquivalentTo(["apple", "other"]);
    }

    [Fact]
    public void ReturningAfterTheLeaseEndedWaitsForRatesInsteadOfShowingOldRows()
    {
        var store = SeededStore();
        var now = DateTime.UtcNow;
        store.TouchViewer("v", now);
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.HasRates(DateTime.UtcNow).Should().BeTrue();

        store.ReleaseViewer("v", DateTime.UtcNow).Should().BeTrue();
        store.EndLease();
        store.HasRates(DateTime.UtcNow).Should().BeFalse("the lease ended, so the agent's next batch only seeds");
        store.BuildView(Query(), LiveTrafficState.Starting).Flows.Should().BeEmpty("rows from before are not live");

        // Back again: the agent's seeding batch carries no moving flows and is not yet rates.
        store.TouchViewer("v", DateTime.UtcNow);
        store.Ingest(Batch());
        store.HasRates(DateTime.UtcNow).Should().BeFalse();
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 2000, 10)));
        store.HasRates(DateTime.UtcNow).Should().BeTrue();
        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().DownBps.Should().Be(8000);
    }

    [Fact]
    public void FacetsSetTheirOwnFieldsChipsAsideAndKeepTheOthers()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("b", Laptop, "192.0.2.20", "203.0.113.6", 443, "eth4", 1000, 10),
            Flow("c", Nas, "192.0.2.10", "203.0.113.7", 22, "eth4", 1000, 10)));
        var laptopName = store.BuildView(Query(), LiveTrafficState.Live).Flows.First(f => f.ClientMac == Laptop).ClientName;

        // A Client chip is open: its own chip is set aside, so every client is offered with its count.
        var clients = store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Client, laptopName)) with { FacetField = LiveTrafficField.Client },
            LiveTrafficState.Live).Facets;
        clients.Where(f => !f.Idle).Select(f => f.Count).Should().Equal(2, 1);
        clients.First().Value.Should().Be(laptopName);

        // Another field's chip still applies: Ports under the laptop chip are only the laptop's, and the
        // port only the NAS uses is listed as hidden by that chip.
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Client, laptopName)) with { FacetField = LiveTrafficField.Port },
            LiveTrafficState.Live).Facets.Take(2).Should().Equal(new LiveFacetValue("443", 2), new LiveFacetValue("22", 0, Hidden: true));

        store.BuildView(Query(), LiveTrafficState.Live).Facets.Should().BeEmpty("no editor is open");
    }

    [Fact]
    public void TheGatewaysTcMonitorIsAdaptiveSqm()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("sqm", Nas, "192.0.2.10", "192.0.2.1", 8088, "", 1000, 10),         // to the Gateway
            Flow("proxy", Nas, "192.0.2.10", "203.0.113.9", 8088, "eth4", 1000, 10)));  // 8088 anywhere else
        store.Ingest(Batch(
            Flow("sqm", Nas, "192.0.2.10", "192.0.2.1", 8088, "", 1000, 10),
            Flow("proxy", Nas, "192.0.2.10", "203.0.113.9", 8088, "eth4", 1000, 10)));

        var rows = store.BuildView(Query(), LiveTrafficState.Live).Flows.ToDictionary(f => f.Key);

        rows["sqm"].App.Should().Be("Adaptive SQM");
        rows["proxy"].App.Should().NotBe("Adaptive SQM");
    }

    [Fact]
    public void RoutesCarryingNothingAreOfferedQuietlyLanIncluded()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));

        var routes = store.BuildView(Query() with { FacetField = LiveTrafficField.Route }, LiveTrafficState.Live).Facets;

        routes.First().Count.Should().Be(1, "the WAN the flow uses is moving");
        routes.Should().Contain(new LiveFacetValue("LAN", 0, Idle: true), "no traffic between VLANs right now");

        var protocols = store.BuildView(Query() with { FacetField = LiveTrafficField.Protocol }, LiveTrafficState.Live).Facets;
        protocols.Select(f => (f.Value, f.Idle)).Should().Equal(("TCP", false), ("ICMP", true), ("UDP", true));

        var ports = store.BuildView(Query() with { FacetField = LiveTrafficField.Port }, LiveTrafficState.Live).Facets;
        ports.First().Should().Be(new LiveFacetValue("443", 1));
        ports.Skip(1).Select(f => int.Parse(f.Value)).Should().BeInAscendingOrder("well-known ports in number order");
        ports.Should().NotContain(f => f.Value == "443" && f.Idle);
    }

    [Fact]
    public void FirewallBlocksCountYourClientsBlockedConnectionsInTheLastTenMinutes()
    {
        static UniFiTrafficFlow Blocked(string id, string direction, DateTime at) => new()
        {
            Id = id,
            Action = "blocked",
            Direction = direction,
            Protocol = "TCP",
            Time = new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            Source = new UniFiTrafficFlowEndpoint { Ip = direction == "incoming" ? "203.0.113.9" : "192.0.2.20", Port = 50000 },
            Destination = new UniFiTrafficFlowEndpoint { Ip = direction == "incoming" ? "192.0.2.1" : "203.0.113.5", Port = 443 },
        };
        var store = SeededStore();
        var now = DateTime.UtcNow;
        store.MergeUniFiFlows([
            Blocked("out", "outgoing", now.AddMinutes(-2)),
            Blocked("lan", "local", now.AddMinutes(-5)),
            Blocked("scan", "incoming", now.AddMinutes(-1)),   // the internet's scanners: not counted
            Blocked("old", "outgoing", now.AddMinutes(-11)),   // past the window
            Blocked("out", "outgoing", now.AddMinutes(-2)),    // the same record read again
        ]);

        store.BuildView(Query(), LiveTrafficState.Live).BlockedRecently.Should().Be(2);
    }

    [Fact]
    public void LeavingLetsTheLeaseRunOutSoAQuickReturnIsStillLive()
    {
        var store = SeededStore();
        var now = DateTime.UtcNow;
        store.TouchViewer("v", now).Should().BeTrue();
        store.NoteLeasePushed(now);
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));

        store.ReleaseViewer("v", now.AddSeconds(5)).Should().BeTrue();
        store.HasRates(DateTime.UtcNow).Should().BeTrue("leaving does not end the lease, so the stream keeps its rows");

        // Back within the lease: the same stream, live at once, and the lease is renewed.
        store.TouchViewer("v", now.AddSeconds(20)).Should().BeTrue();
        store.HasRates(DateTime.UtcNow).Should().BeTrue();
        store.BuildView(Query(), LiveTrafficState.Live).Flows.Should().ContainSingle();
    }

    [Fact]
    public void AReturnAfterTheLeaseLapsedForgetsTheOldStreamBeforeTheSeedBatch()
    {
        var store = SeededStore();
        var now = DateTime.UtcNow;
        store.TouchViewer("v", now);
        store.NoteLeasePushed(now);
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.ReleaseViewer("v", now);

        // Past the lease the agent's tracker reset: the old rows must not read as live.
        store.TouchViewer("v", now + LiveTrafficStore.LeaseLength + TimeSpan.FromSeconds(1)).Should().BeTrue();
        store.HasRates(DateTime.UtcNow).Should().BeFalse();
        store.BuildView(Query(), LiveTrafficState.Starting).Flows.Should().BeEmpty();
    }

    [Fact]
    public void TurnedOffGrantsNoLeaseKeepsNoBatchAndDropsWhatItHeld()
    {
        var store = SeededStore();
        store.TouchViewer("v", DateTime.UtcNow).Should().BeTrue();
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        store.HasRates(DateTime.UtcNow).Should().BeTrue();

        store.SetTurnedOff(true);
        store.BuildView(Query(), LiveTrafficState.TurnedOff).Flows.Should().BeEmpty("turning it off drops the rows held");
        store.TouchViewer("v", DateTime.UtcNow).Should().BeFalse("no lease is granted while off");
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 2000, 10)));
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 2000, 10)));
        store.HasRates(DateTime.UtcNow).Should().BeFalse("a batch already in flight is not kept");

        store.SetTurnedOff(false);
        store.TouchViewer("v", DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task OptOutIsReadOnceAndASaveDuringTheReadWins()
    {
        var settings = new Mock<NetworkOptimizer.Web.Services.ISystemSettingsService>();
        var registry = new LiveTrafficRegistry();
        settings.Setup(s => s.GetAsync("site-a", LiveTrafficRegistry.EnabledKey))
            .ReturnsAsync(() => { registry.SetTurnedOff("site-a", false); return "false"; });
        settings.Setup(s => s.GetAsync("site-b", LiveTrafficRegistry.EnabledKey)).ReturnsAsync("false");
        settings.Setup(s => s.GetAsync("site-c", LiveTrafficRegistry.EnabledKey)).ThrowsAsync(new InvalidOperationException());

        (await registry.IsTurnedOffAsync(settings.Object, "site-a")).Should().BeFalse("the save that landed during the read wins");
        (await registry.IsTurnedOffAsync(settings.Object, "site-b")).Should().BeTrue();
        registry.GetFor("site-b").TurnedOff.Should().BeTrue();
        (await registry.IsTurnedOffAsync(settings.Object, "site-b")).Should().BeTrue();
        settings.Verify(s => s.GetAsync("site-b", LiveTrafficRegistry.EnabledKey), Times.Once());
        (await registry.IsTurnedOffAsync(settings.Object, "site-c")).Should().BeTrue("a failed read never captures a site that may have opted out");
        (await registry.IsTurnedOffAsync(settings.Object, "site-d")).Should().BeFalse("unset is on");
    }

    [Fact]
    public void WireGuardToThisInstallsHostIsTailscale()
    {
        var store = SeededStore();
        store.SetServerHosts(new HashSet<string>(["203.0.113.80"], StringComparer.OrdinalIgnoreCase));
        store.Ingest(Batch(Flow("wg", Laptop, "192.0.2.20", "203.0.113.80", 41641, "eth4", 1000, 10, proto: "udp")));

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().App.Should().Be("Tailscale");
    }

    [Fact]
    public void AnAwsHostIsAwsOnAWebPortAndKeepsAKnownPortsName()
    {
        var store = SeededStore();
        var batch = Batch(
            Flow("web", Laptop, "192.0.2.20", "203.0.113.70", 443, "eth4", 1000, 10),
            Flow("mqtt", Laptop, "192.0.2.20", "203.0.113.70", 8883, "eth4", 1000, 10));
        var dns = new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "192.0.2.1",
            Name = "a1b2c3.iot.us-east-1.amazonaws.com",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        };
        dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = "203.0.113.70", Ttl = 300 });
        batch.Dns.Add(dns);
        store.Ingest(batch);

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single(f => f.Key == "web").App.Should().Be("AWS");
        view.Flows.Single(f => f.Key == "mqtt").App.Should().Be("MQTT-TLS");
    }

    [Fact]
    public void FlowsToThisInstallAreNetworkOptimizerAndApPollsAreApAgent()
    {
        var store = SeededStore();
        store.SetServerHosts(new HashSet<string>(["optimizer.example.com", "203.0.113.80"], StringComparer.OrdinalIgnoreCase));
        var batch = Batch(
            Flow("tunnel", Laptop, "192.0.2.20", "203.0.113.70", 443, "eth4", 1000, 10),
            Flow("byip", Laptop, "192.0.2.20", "203.0.113.80", 8042, "eth4", 1000, 10),
            Flow("poll", Nas, "192.0.2.10", "192.0.2.30", 8899, "", 1000, 10, lan: true));
        var dns = new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "192.0.2.1",
            Name = "Optimizer.Example.com",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        };
        dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = "203.0.113.70", Ttl = 300 });
        batch.Dns.Add(dns);
        store.Ingest(batch);

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single(f => f.Key == "tunnel").App.Should().Be("Network Optimizer", "the destination's DNS name is this install's host");
        view.Flows.Single(f => f.Key == "byip").App.Should().Be("Network Optimizer", "HOST_IP, reached by address");
        view.Flows.Single(f => f.Key == "poll").App.Should().Be("AP Agent");
        view.Flows.Single(f => f.Key == "tunnel").AppIconClass.Should().Contain("app-mark-netopt");
    }

    [Fact]
    public void UnlistedVpnAndTailscaleClientsSayHowTheyConnect()
    {
        var store = SeededStore();
        store.SetNetworkPurposes([("192.0.2.0/24", "corporate"), ("198.51.100.0/24", "remote-user-vpn")]);
        store.Ingest(Batch(
            Flow("vpn", "", "198.51.100.2", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("ts", "", "100.64.1.2", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("peer", "", "192.168.88.20", "203.0.113.5", 443, "eth4", 1000, 10)));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        // The kind is in the name, so the diagram and Group by Client say it; the address keeps two apart.
        view.Flows.Single(f => f.Key == "vpn").ClientName.Should().Be("VPN Client (198.51.100.2)");
        view.Flows.Single(f => f.Key == "ts").ClientName.Should().Be("Tailscale (100.64.1.2)");
        view.Flows.Single(f => f.Key == "peer").ClientName.Should().Be("192.168.88.20", "a site-to-site peer is not guessed to be Teleport");
        view.Sankey!.Nodes.Should().Contain(n => n.Column == 0 && n.Name == "VPN Client (198.51.100.2)");

        // A filter by the bare address still finds it.
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Client, "198.51.100.2")), LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("vpn");
    }

    [Fact]
    public void SiblingStreamsOnAPortTakeTheNamedOnesApp()
    {
        var store = SeededStore();
        var batch = Batch(
            Flow("named", Laptop, "192.0.2.20", "203.0.113.90", 8080, "eth4", 1000, 10),
            Flow("sibling", Laptop, "192.0.2.20", "203.0.113.91", 8080, "eth4", 1000, 10),
            Flow("other-client", Nas, "192.0.2.10", "203.0.113.92", 8080, "eth4", 1000, 10),
            Flow("web", Laptop, "192.0.2.20", "203.0.113.93", 443, "eth4", 1000, 10));
        var dns = new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "192.0.2.1",
            Name = "speedtest.example.net",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        };
        dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = "203.0.113.90", Ttl = 300 });
        batch.Dns.Add(dns);
        store.Ingest(batch);

        var flows = store.BuildView(Query(), LiveTrafficState.Live).Flows;

        flows.Single(f => f.Key == "named").App.Should().Be("Speedtest.net");
        flows.Single(f => f.Key == "sibling").App.Should().Be("Speedtest.net", "same client, same protocol and port, no name of its own");
        flows.Single(f => f.Key == "other-client").App.Should().NotBe("Speedtest.net", "another client's 8080 is not this test");
        flows.Single(f => f.Key == "web").App.Should().Be("HTTPS");
    }

    [Fact]
    public void SiblingNamesNeverCrossASharedPort()
    {
        var rows = new List<LiveFlowRow>
        {
            new() { Key = "a", ClientIp = "192.0.2.20", Proto = "tcp", RemotePort = 443, App = "Netflix" },
            new() { Key = "b", ClientIp = "192.0.2.20", Proto = "tcp", RemotePort = 443, App = "HTTPS", AppIsFallback = true },
        };
        LiveTrafficStore.InheritSiblingApps(rows).Single(r => r.Key == "b").App.Should().Be("HTTPS");
    }

    private const string Phone = "192.0.2.70";
    private const string Console = "192.0.2.5";
    private const string OtherClient = "192.0.2.220";
    private static bool UniFiConsoleOnly(string ip) => ip == Console;

    private static LiveFlowRow Https(string client, string dest) => new()
    {
        Key = $"https-{client}-{dest}", ClientIp = client, DestinationIp = dest, Proto = "TCP",
        ClientPort = 53473, RemotePort = 443, RouteKind = LiveRouteKind.Lan, App = "HTTPS", AppIsFallback = true,
    };

    private static LiveFlowRow Udp(string client, string dest, int clientPort, int remotePort, string app = "UDP", bool fallback = true) => new()
    {
        Key = $"udp-{client}-{dest}-{remotePort}", ClientIp = client, DestinationIp = dest, Proto = "UDP",
        ClientPort = clientPort, RemotePort = remotePort, RouteKind = LiveRouteKind.Lan, App = app, AppIsFallback = fallback,
    };

    [Fact]
    public void EphemeralUdpToAUniFiHostTheClientHoldsHttpsToIsUniFiWebRtc()
    {
        var rows = new List<LiveFlowRow> { Https(Phone, Console), Udp(Phone, Console, 61234, 35826, "UDP 35826") };
        var named = LiveTrafficStore.NameUniFiWebRtc(rows, rows, UniFiConsoleOnly).Single(r => r.Proto == "UDP");
        named.App.Should().Be(LiveTrafficStore.UniFiWebRtcApp);
        named.AppIsFallback.Should().BeFalse();
    }

    [Fact]
    public void UniFiWebRtcCountsAConsoleOriginatedUdpFlow()
    {
        var rows = new List<LiveFlowRow> { Https(Phone, Console), Udp(Console, Phone, 48821, 61234) };
        LiveTrafficStore.NameUniFiWebRtc(rows, rows, UniFiConsoleOnly).Single(r => r.Proto == "UDP")
            .App.Should().Be(LiveTrafficStore.UniFiWebRtcApp);
    }

    [Fact]
    public void UniFiWebRtcNeedsTheSameClientsHttpsSession()
    {
        var rows = new List<LiveFlowRow> { Https(OtherClient, Console), Udp(Phone, Console, 61234, 35826) };
        LiveTrafficStore.NameUniFiWebRtc(rows, rows, UniFiConsoleOnly).Single(r => r.Proto == "UDP").App.Should().Be("UDP");
    }

    [Fact]
    public void UniFiWebRtcNeedsAUniFiHost()
    {
        var rows = new List<LiveFlowRow> { Https(Phone, Console), Udp(Phone, Console, 61234, 35826) };
        LiveTrafficStore.NameUniFiWebRtc(rows, rows, _ => false).Single(r => r.Proto == "UDP").App.Should().Be("UDP");
    }

    [Fact]
    public void UniFiWebRtcLeavesServicePortsAndNamedFlowsAlone()
    {
        var rows = new List<LiveFlowRow>
        {
            Https(Phone, Console),
            Udp(Phone, Console, 61234, 10001, "UniFi Discovery", fallback: false),
            Udp(Phone, Console, 61234, 3478),
            Udp(Phone, Console, 61234, 40000, "STUN", fallback: false),
        };
        LiveTrafficStore.NameUniFiWebRtc(rows, rows, UniFiConsoleOnly).Where(r => r.Proto == "UDP")
            .Select(r => r.App).Should().Equal("UniFi Discovery", "UDP", "STUN");
    }

    [Fact]
    public void AnOwnersHostOnTheIspsAsnIsAnIspCache()
    {
        var store = SeededStore();
        var batch = Batch(
            Flow("ggc", Laptop, "192.0.2.20", "203.0.113.110", 443, "eth4", 1000, 10, proto: "udp"),
            Flow("google", Laptop, "192.0.2.20", "203.0.113.111", 443, "eth4", 1000, 10, proto: "udp"));
        foreach (var (ip, name) in new[] { ("203.0.113.110", "rr1---sn-abc.googlevideo.com"), ("203.0.113.111", "rr2---sn-def.googlevideo.com") })
        {
            var dns = new LiveDnsEvent
            {
                AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClientIp = "192.0.2.20",
                ServerIp = "192.0.2.1",
                Name = name,
                Qtype = "A",
                Rcode = "NOERROR",
                Answered = true,
            };
            dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = ip, Ttl = 300 });
            batch.Dns.Add(dns);
        }
        store.Ingest(batch);
        store.SetOwners(
            new Dictionary<string, string?> { ["203.0.113.110"] = "Example ISP", ["203.0.113.111"] = "Google" },
            new Dictionary<string, int> { ["203.0.113.110"] = 64496, ["203.0.113.111"] = 15169 });

        var flows = store.BuildView(Query(), LiveTrafficState.Live).Flows;

        var ggc = flows.Single(f => f.Key == "ggc");
        ggc.App.Should().Be("YouTube", "the app stays what it is");
        ggc.IspCache.Should().Be("Google Global Cache inside Example ISP (AS64496)");
        flows.Single(f => f.Key == "google").IspCache.Should().BeNull();
        store.BuildView(Query() with { Search = "ISP cache" }, LiveTrafficState.Live).Flows.Select(f => f.Key).Should().Equal("ggc");
    }

    [Fact]
    public void AStreamTheDpiNeverNamedTakesWhatItNamedItsAddress()
    {
        var store = SeededStore();
        UniFiTrafficFlow Record(string client, int port, string remote, int? app) => new()
        {
            Protocol = "UDP",
            Source = new UniFiTrafficFlowEndpoint { Ip = client, Port = port },
            Destination = new UniFiTrafficFlowEndpoint { Ip = remote, Port = 443 },
            Application = app is { } a ? new UniFiTrafficFlowApplication { App = a, Category = 4 } : null,
        };
        store.MergeUniFiFlows(
        [
            Record("192.0.2.10", 40001, "203.0.113.120", 112),   // another client's side connection: YouTube
            Record("192.0.2.20", 50000, "203.0.113.120", null),  // the stream itself: the DPI never named it
            Record("192.0.2.10", 40002, "203.0.113.121", 112),   // a front end the records disagree on
            Record("192.0.2.10", 40003, "203.0.113.121", 132),
        ]);
        store.Ingest(Batch(
            Flow("stream", Laptop, "192.0.2.20", "203.0.113.120", 443, "eth4", 900_000, 10, proto: "udp"),
            Flow("mixed", Laptop, "192.0.2.20", "203.0.113.121", 443, "eth4", 1000, 10, proto: "udp")));

        var flows = store.BuildView(Query(), LiveTrafficState.Live).Flows;

        flows.Single(f => f.Key == "stream").App.Should().Be("YouTube");
        flows.Single(f => f.Key == "mixed").App.Should().Be("HTTPS", "records that disagree name nothing");
    }

    [Fact]
    public void AnotherClientsRecordNamesTheAddressByItsDomain()
    {
        var store = SeededStore();
        store.MergeUniFiFlows(
        [
            new UniFiTrafficFlow
            {
                Protocol = "UDP",
                Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.10", Port = 40001 },
                Destination = new UniFiTrafficFlowEndpoint { Ip = "203.0.113.130", Port = 443, Domains = ["rr4---sn-abc.googlevideo.com"] },
            },
        ]);
        store.Ingest(Batch(Flow("stream", Laptop, "192.0.2.20", "203.0.113.130", 443, "eth4", 900_000, 10, proto: "udp")));
        store.SetOwners(new Dictionary<string, string?> { ["203.0.113.130"] = "Example ISP" }, new Dictionary<string, int> { ["203.0.113.130"] = 64496 });

        var f = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        f.Destination.Should().Be("rr4---sn-abc.googlevideo.com");
        f.App.Should().Be("YouTube");
        f.IspCache.Should().Be("Google Global Cache inside Example ISP (AS64496)");
    }

    [Fact]
    public void HttpsTakesInQuicButQuicStaysQuic()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("tcp", Laptop, "192.0.2.20", "203.0.113.60", 443, "eth4", 1000, 10),
            Flow("quic", Laptop, "192.0.2.20", "203.0.113.61", 443, "eth4", 1000, 10, proto: "udp")));

        Keys(Query(new LiveTrafficFilter(LiveTrafficField.App, "HTTPS"))).Should().BeEquivalentTo(["tcp", "quic"]);
        Keys(Query(new LiveTrafficFilter(LiveTrafficField.App, "QUIC"))).Should().BeEquivalentTo(["quic"]);
        Keys(Query() with { Search = "https" }).Should().BeEquivalentTo(["tcp", "quic"]);
        Keys(Query() with { Search = "quic" }).Should().BeEquivalentTo(["quic"]);

        IEnumerable<string> Keys(LiveTrafficQuery q) => store.BuildView(q, LiveTrafficState.Live).Flows.Select(f => f.Key);
    }

    [Fact]
    public void ResolverOnTheSameSwitchPortIsNotABypass()
    {
        const string resolverMac = "aa:bb:cc:dd:ee:53";
        const string hostMac = "aa:bb:cc:dd:ee:54";
        var store = new LiveTrafficStore();
        store.SetSiteData(
            [
                new LiveClientInfo(Laptop, "Laptop", "192.0.2.20", "6 GHz", "Main"),
                new LiveClientInfo(resolverMac, "Pi-hole", "198.51.100.53", "Port 10", "DNS", "aa:bb:cc:00:00:01", 10),
                new LiveClientInfo(hostMac, "Host", "192.0.2.53", "Port 10", "Main", "aa:bb:cc:00:00:01", 10),
            ],
            ["192.0.2.1"], "Gateway", new Dictionary<string, LiveRouteInfo>(), null);
        var batch = Batch(Flow("up", hostMac, "192.0.2.53", "203.0.113.9", 853, "eth4", 1000, 1000));
        batch.Dns.Add(new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "198.51.100.53",
            Name = "a.example",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        });
        store.Ingest(batch);

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        var up = view.Flows.Single();
        up.EncryptedDns.Should().BeTrue();
        up.DnsBypass.Should().BeFalse();
        view.Dns.Should().Contain(d => d.Encrypted && !d.Bypass);
    }

    [Theory]
    [InlineData("NXDOMAIN", null, true)]
    [InlineData("NOERROR", "0.0.0.0", true)]
    [InlineData("NOERROR", "::", true)]
    [InlineData("NOERROR", "203.0.113.5", false)]
    public void NullAddressAnswerCountsAsBlocked(string rcode, string? answer, bool blocked)
    {
        var e = new LiveDnsEvent { Rcode = rcode, Answered = true, Qtype = answer == "::" ? "AAAA" : "A" };
        if (answer != null) e.Answers.Add(new LiveDnsAnswer { Type = answer.Contains(':') ? "AAAA" : "A", Data = answer, Ttl = 60 });

        LiveTrafficStore.IsBlocked(e).Should().Be(blocked);
    }

    [Fact]
    public void BackfillStepsBackUntilTheOldestUnmatchedFlowIsCovered()
    {
        var store = SeededStore();
        var old = Flow("vpn", Nas, "192.0.2.10", "203.0.113.9", 51820, "eth4", 1000, 1000, proto: "udp");
        old.AgeSeconds = (long)TimeSpan.FromMinutes(55).TotalSeconds;
        store.Ingest(Batch(old));
        var now = DateTime.UtcNow;
        store.NextBackfillWindow(now).Should().BeNull("nothing is read back before the first console read");
        store.MergeUniFiFlows([]);
        store.MergeUniFiFlows([new UniFiTrafficFlow { Time = new DateTimeOffset(now).ToUnixTimeMilliseconds() }]);

        var windows = new List<(DateTime From, DateTime To)>();
        while (store.NextBackfillWindow(now) is { } w) windows.Add(w);

        windows.Should().HaveCount(3, "30 to 40, 40 to 50, and 50 to 56 minutes back");
        windows[0].To.Should().Be(now - TimeSpan.FromMinutes(30));
        windows[^1].From.Should().BeCloseTo(now - TimeSpan.FromMinutes(56), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ParallelStreamsToOneDestinationAreOneRow()
    {
        var store = SeededStore();
        var a = Flow("s1", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 3_000_000, 10_000);
        var b = Flow("s2", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1_000_000, 10_000);
        b.ClientPort = 50001;
        var other = Flow("x", Laptop, "192.0.2.20", "203.0.113.6", 443, "eth4", 500_000, 10_000);
        other.ClientPort = 50002;
        store.Ingest(Batch(a, b, other));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Should().HaveCount(2);
        var bundle = view.Flows.Single(f => f.Streams == 2);
        bundle.DownBps.Should().Be(16_000_000, "4 MB over a 2 s window, summed");
        bundle.TotalDownBytes.Should().Be(4_000_000);
        view.Flows.Single(f => f.Streams == 1).DestinationIp.Should().Be("203.0.113.6");
        view.MatchingFlows.Should().Be(3, "the count is still of flows");
    }

    [Fact]
    public void InterVlanPeerIsNamedAsTheClientItIs()
    {
        var store = SeededStore();
        var batch = Batch(Flow("lan", Laptop, "192.0.2.20", "192.0.2.10", 445, "", 1_000_000, 10_000, lan: true));
        var dns = new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "192.0.2.1",
            Name = "nas.home.example",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        };
        dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = "192.0.2.10", Ttl = 300 });
        batch.Dns.Add(dns);
        store.Ingest(batch);

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        row.Destination.Should().Be("NAS", "the UniFi name wins over local DNS");
        row.RemoteClientMac.Should().Be(Nas);
        row.RemoteClientVia.Should().Be("Port 9");
        row.RemoteNetwork.Should().Be("Main");
        row.DnsDetail.Should().Contain("Gateway", "the lookup is still shown in the detail");
    }

    [Fact]
    public void GatewayTunnelIsLeftOutOfWhereItGoes()
    {
        var store = SeededStore();
        var tunnel = Flow("wg", "", "198.51.100.7", "203.0.113.20", 51820, "eth4", 5_000_000, 5_000_000, proto: "udp");
        tunnel.Gateway = true;
        store.Ingest(Batch(tunnel, Flow("web", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1_000_000, 10_000)));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Should().Contain(f => f.Key == "wg", "the table keeps the tunnel");
        view.Sankey!.Nodes.Should().NotContain(n => n.Name == "WireGuard");
        view.Sankey.Nodes.Should().Contain(n => n.Name == "Laptop");
        view.Clients.Should().Be(1, "the gateway is not a client");
    }

    [Fact]
    public void ClientEncryptedDnsIsABypass()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("dot", Laptop, "192.0.2.20", "203.0.113.9", 853, "eth4", 1000, 1000)));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single().DnsBypass.Should().BeTrue();
        view.BypassingClients.Should().Be(1);
    }

    [Fact]
    public void SiteResolverEncryptedUpstreamIsNotABypass()
    {
        var store = SeededStore();
        var batch = Batch(Flow("dot", Nas, "192.0.2.10", "203.0.113.9", 853, "eth4", 1000, 1000));
        batch.Dns.Add(new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ClientMac = Laptop,
            ServerIp = "192.0.2.10",
            Name = "a.example",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        });
        store.Ingest(batch);

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single().DnsBypass.Should().BeFalse();
        view.BypassingClients.Should().Be(0);
    }

    [Fact]
    public void GatewayEncryptedUpstreamIsNotABypass()
    {
        var store = SeededStore();
        var doh = Flow("doh", "", "198.51.100.7", "1.0.0.2", 443, "eth4", 1000, 1000);
        doh.Gateway = true;
        store.Ingest(Batch(doh));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single().EncryptedDns.Should().BeTrue();
        view.Flows.Single().DnsBypass.Should().BeFalse();
        view.BypassingClients.Should().Be(0);
        view.Dns.Single(d => d.Encrypted).Bypass.Should().BeFalse();
    }

    [Fact]
    public void EncryptedDnsToAnUnlistedAddressIsFoundByItsPtr()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("doh", Laptop, "192.0.2.20", "203.0.113.9", 443, "eth4", 1000, 1000)));
        store.SetPtrNames(new Dictionary<string, string?> { ["203.0.113.9"] = "dns.nextdns.io" });

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows.Single().DnsBypass.Should().BeTrue();
        view.Dns.Single(d => d.Encrypted).Name.Should().Be("NextDNS");
    }

    [Fact]
    public void SiteResolverPlainUpstreamIsABypass()
    {
        var store = SeededStore();
        var batch = Batch();
        foreach (var (client, server) in new[] { ("192.0.2.20", "192.0.2.10"), ("192.0.2.10", "203.0.113.53") })
        {
            batch.Dns.Add(new LiveDnsEvent
            {
                AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClientIp = client,
                ServerIp = server,
                Name = "a.example",
                Qtype = "A",
                Rcode = "NOERROR",
                Answered = true,
            });
        }
        store.Ingest(batch);

        var view = store.BuildView(Query() with { DnsMode = LiveDnsMode.Bypass }, LiveTrafficState.Live);

        view.Dns.Should().ContainSingle().Which.ClientIp.Should().Be("192.0.2.10");
    }

    [Fact]
    public void OutsideResolverIsABypass()
    {
        var store = SeededStore();
        var batch = Batch();
        batch.Dns.Add(new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ClientMac = Laptop,
            ServerIp = "203.0.113.53",
            Name = "a.example",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        });
        store.Ingest(batch);

        var view = store.BuildView(Query() with { DnsMode = LiveDnsMode.Bypass }, LiveTrafficState.Live);

        view.Dns.Should().ContainSingle().Which.ClientName.Should().Be("Laptop");
        view.BypassingClients.Should().Be(1);
    }

    [Fact]
    public void UniFiFlowLabelsAppPolicyAndTeachesTheTunnelName()
    {
        var store = SeededStore();
        store.MergeUniFiFlows(
        [
            new UniFiTrafficFlow
            {
                Protocol = "TCP",
                FlowStartTime = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds(),
                Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.20", Port = 50000 },
                Destination = new UniFiTrafficFlowEndpoint { Ip = "203.0.113.70", Port = 443 },
                Out = new UniFiTrafficFlowNetwork { NetworkName = "VPN Exit" },
                Policies = [new UniFiTrafficFlowPolicy { Name = "Allow All Traffic", Type = "FIREWALL" }],
            },
        ]);
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.70", 443, "wgclt1", 1000, 10),
            Flow("b", Laptop, "192.0.2.20", "203.0.113.71", 443, "wgclt1", 500, 10)));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        var matched = view.Flows.Single(f => f.Key == "a");
        matched.PolicyName.Should().Be("Allow All Traffic");
        matched.Route.Should().Be("VPN Exit");
        matched.RouteKind.Should().Be(LiveRouteKind.Vpn);
        matched.StartedIsUpperBound.Should().BeFalse();
        view.Flows.Single(f => f.Key == "b").Route.Should().Be("VPN Exit", "one matched flow names the interface for all");
    }

    [Fact]
    public void LongQuicStreamTakesTheAppAnEndedSideConnectionToTheAddressHad()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("s", Laptop, "192.0.2.20", "203.0.113.80", 443, "eth4", 5_000_000, 10_000, proto: "udp")));
        store.MergeUniFiFlows(
        [
            new UniFiTrafficFlow
            {
                Protocol = "UDP",
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                FlowStartTime = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                Application = new UniFiTrafficFlowApplication { App = 112, Category = 4 },
                Service = "HTTPS",
                Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.20", Port = 49374 },
                Destination = new UniFiTrafficFlowEndpoint { Ip = "203.0.113.80", Port = 443, Domains = ["rr1---sn-test.googlevideo.com"] },
            },
        ]);
        store.Ingest(Batch(Flow("s", Laptop, "192.0.2.20", "203.0.113.80", 443, "eth4", 5_000_000, 10_000, proto: "udp")));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        row.App.Should().Be("YouTube");
        row.Destination.Should().Be("rr1---sn-test.googlevideo.com");
    }

    [Theory]
    [InlineData("1.1.1.1", 443, "tcp", "DNS over HTTPS")]
    [InlineData("1.1.1.1", 853, "tcp", "DNS over TLS")]
    [InlineData("1.1.1.1", 853, "udp", "DNS over QUIC")]
    public void EncryptedDnsTakesTheDnsMarkNotTheLock(string remote, int port, string proto, string expected)
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("s", Laptop, "192.0.2.20", remote, port, "eth4", 5_000, 1_000, proto: proto)));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        row.App.Should().Be(expected);
        row.AppIconClass.Should().Be("fa-solid fa-server");
    }

    [Fact]
    public void ADomainsAppReplacesDpisGenericTls()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("s", Laptop, "192.0.2.20", "203.0.113.90", 8443, "eth4", 5_000, 1_000)));
        store.MergeUniFiFlows(
        [
            new UniFiTrafficFlow
            {
                Protocol = "TCP",
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Application = new UniFiTrafficFlowApplication { App = 185, Category = 20 },
                Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.20", Port = 49374 },
                Destination = new UniFiTrafficFlowEndpoint { Ip = "203.0.113.90", Port = 8443, Domains = ["rsyslog.ooma.com"] },
            },
        ]);
        store.Ingest(Batch(Flow("s", Laptop, "192.0.2.20", "203.0.113.90", 8443, "eth4", 5_000, 1_000)));

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().App.Should().Be("Ooma", "SSL/TLS on HTTPS-Alt is generic");
    }

    [Theory]
    [InlineData("203.0.113.14", 443, 7029, "YouTube")]       // a neighbor in the ISP's cache block
    [InlineData("203.0.113.14", 443, 15169, "HTTPS")]        // Google's own network: not an embedded cache
    [InlineData("203.0.113.14", 8443, 7029, null)]           // not the cache's port
    [InlineData("198.51.100.14", 443, 7029, "HTTPS")]        // another /24
    public void CacheNodeResolvedOutOfViewTakesItsBlocksApp(string remote, int port, int asn, string? expected)
    {
        var store = SeededStore();
        var batch = Batch(Flow("s", Laptop, "192.0.2.20", remote, port, "eth4", 5_000_000, 10_000, proto: "udp"));
        var dns = new LiveDnsEvent
        {
            AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ClientIp = "192.0.2.20",
            ServerIp = "192.0.2.1",
            Name = "rr1---sn-test.googlevideo.com",
            Qtype = "A",
            Rcode = "NOERROR",
            Answered = true,
        };
        dns.Answers.Add(new LiveDnsAnswer { Type = "A", Data = "203.0.113.12", Ttl = 300 });
        batch.Dns.Add(dns);
        store.Ingest(batch);
        store.SetOwners(new Dictionary<string, string?> { [remote] = "Windstream" }, new Dictionary<string, int> { [remote] = asn });

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        if (expected == null) row.App.Should().NotBe("YouTube");
        else row.App.Should().Be(expected);
        if (expected == "YouTube") row.IspCache.Should().Contain("Google Global Cache").And.Contain("AS7029");
        else row.IspCache.Should().BeNull();
    }

    [Fact]
    public void DeviceProtocolByPortAndVendorBeatsTheDpiGuess()
    {
        var store = new LiveTrafficStore();
        store.SetSiteData(
            [
                new LiveClientInfo(Laptop, "Apple TV", "192.0.2.20", "Port 3", "Main"),
                new LiveClientInfo("aa:bb:cc:00:00:80", "IKEA Hub", "192.0.2.80", "Port 4", "IoT", null, null, "IKEA of Sweden AB"),
            ],
            ["192.0.2.1"], "Gateway", new Dictionary<string, LiveRouteInfo>(), null);
        store.MergeUniFiFlows(
        [
            new UniFiTrafficFlow
            {
                Protocol = "TCP",
                Application = new UniFiTrafficFlowApplication { App = 112, Category = 4 },
                Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.20", Port = 50000 },
                Destination = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.80", Port = 8000 },
            },
        ]);
        store.Ingest(Batch(Flow("h", Laptop, "192.0.2.20", "192.0.2.80", 8000, "", 1000, 1000, lan: true)));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        row.App.Should().Be("HomeKit");
        row.Category.Should().BeNull("the DPI's category belongs to its guess");
    }

    [Fact]
    public void ConsoleRecordForAnotherConnectionDoesNotDateThisOne()
    {
        var store = SeededStore();
        store.MergeUniFiFlows(
        [
            new UniFiTrafficFlow
            {
                Protocol = "TCP",
                FlowStartTime = DateTimeOffset.UtcNow.AddMinutes(-45).ToUnixTimeMilliseconds(),
                Source = new UniFiTrafficFlowEndpoint { Ip = "192.0.2.20", Port = 41000 },
                Destination = new UniFiTrafficFlowEndpoint { Ip = "203.0.113.5", Port = 443 },
            },
        ]);
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        row.Started.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(-30), TimeSpan.FromSeconds(5), "the kernel's age is the flow's own");
    }

    [Fact]
    public void ClientScopeMatchesByMacOrAnyOfItsAddresses()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("b", Nas, "192.0.2.10", "203.0.113.9", 443, "eth4", 5000, 10),
            // The laptop's IPv6 address, which the console's IP map does not hold.
            Flow("c", "", "2001:db8::20", "2001:db8:ff::5", 443, "eth4", 2000, 20),
            // A VPN peer the console knows only by its tunnel address.
            Flow("v", "", "192.0.2.200", "203.0.113.7", 443, "eth4", 3000, 30)));

        var laptop = store.BuildView(Query() with { Client = new LiveClientScope(Laptop, ["2001:db8::20"]) }, LiveTrafficState.Live);
        laptop.Flows.Select(f => f.Key).Should().BeEquivalentTo("a", "c");
        laptop.MatchingFlows.Should().Be(2);
        laptop.MatchingDownBps.Should().Be((1000 + 2000) * 8 / 2);
        laptop.MatchingUpBps.Should().Be((10 + 20) * 8 / 2);

        store.BuildView(Query() with { Client = new LiveClientScope("AA-BB-CC-DD-EE-02", []) }, LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("a");
        store.BuildView(Query() with { Client = new LiveClientScope(null, ["192.0.2.200"]) }, LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("v");
        // Filters still apply inside the scope.
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Destination, "2001:db8:ff::5", Exclude: true))
                with { Client = new LiveClientScope(Laptop, ["2001:db8::20"]) }, LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("a");
    }

    [Fact]
    public void ClientScopeKeepsOnlyTheClientsLookups()
    {
        var store = SeededStore();
        var batch = Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10));
        foreach (var (ip, name) in new[] { ("192.0.2.20", "v4.example.com"), ("2001:db8::20", "v6.example.com"), ("192.0.2.10", "nas.example.com") })
        {
            batch.Dns.Add(new LiveDnsEvent
            {
                AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ClientIp = ip,
                ServerIp = "192.0.2.1",
                Name = name,
                Qtype = "A",
                Rcode = "NOERROR",
                LatencyMs = 4,
                Answered = true,
            });
        }
        store.Ingest(batch);

        var view = store.BuildView(Query() with { Client = new LiveClientScope(Laptop, ["2001:db8::20"]) }, LiveTrafficState.Live);

        view.Dns.Select(d => d.Name).Should().BeEquivalentTo("v4.example.com", "v6.example.com");
        // A lookup from an address in the console's map carries the client's MAC, so the Client filter matches it by MAC.
        view.Dns.Single(d => d.Name == "v4.example.com").ClientMac.Should().Be(Laptop);
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Client, Nas)), LiveTrafficState.Live)
            .Dns.Select(d => d.Name).Should().Equal("nas.example.com");
    }

    [Fact]
    public void FiltersKeepAndExclude()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("b", Nas, "192.0.2.10", "203.0.113.9", 51820, "eth4", 2000, 10, proto: "udp")));

        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Client, "Laptop")), LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("a");
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.App, "WireGuard", Exclude: true)), LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("a");
        store.BuildView(Query() with { Search = "51820" }, LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("b");
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Protocol, "UDP")), LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("b");
        store.BuildView(Query(new LiveTrafficFilter(LiveTrafficField.Port, "443")), LiveTrafficState.Live)
            .Flows.Select(f => f.Key).Should().Equal("a");
    }

    [Fact]
    public void GroupsByAppAndRoute()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("b", Nas, "192.0.2.10", "203.0.113.6", 443, "eth4", 3000, 10),
            Flow("c", Nas, "192.0.2.10", "192.0.2.20", 8001, "", 100, 10, lan: true)));

        var byRoute = store.BuildView(Query() with { GroupBy = LiveTrafficGroupBy.Route }, LiveTrafficState.Live).Groups;
        byRoute.Select(g => (g.Name, g.Flows)).Should().Equal(("Fiber WAN", 2), ("LAN", 1));

        var wanOnly = store.BuildView(Query() with { WanOnly = true }, LiveTrafficState.Live);
        wanOnly.Flows.Should().NotContain(f => f.RouteKind == LiveRouteKind.Lan);
    }

    [Fact]
    public void EndedFlowsMoveToTheEndedTab()
    {
        var store = SeededStore();
        var flow = Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10);
        store.Ingest(Batch(flow));
        var ending = Batch();
        ending.Ended.Add(flow);
        store.Ingest(ending);

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Should().BeEmpty();
        store.BuildView(Query() with { Ended = true }, LiveTrafficState.Live).Flows.Should().ContainSingle().Which.Ended.Should().BeTrue();
    }

    [Fact]
    public void LanRouteNodeListsItsBusiestHostsFirst()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("small", Laptop, "192.0.2.20", "192.0.2.10", 445, "", 1000, 10, lan: true),
            Flow("big", Laptop, "192.0.2.20", "192.0.2.30", 8443, "", 50_000, 10, lan: true),
            Flow("web", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 9_000, 10)));

        var lan = store.BuildView(Query(), LiveTrafficState.Live).Sankey!.Nodes.Single(n => n.Column == 2 && n.Name == "LAN");

        lan.Destinations!.Select(d => d.Ip).Should().Equal("192.0.2.30", "192.0.2.10");
        lan.MoreDestinations.Should().Be(0);
    }

    [Fact]
    public void WanRouteCardNamesASitesPublicAddressBySite()
    {
        var store = SeededStore();
        store.SetSiteAddresses([("203.0.113.77", "Branch Office")]);
        store.Ingest(Batch(Flow("site", Laptop, "192.0.2.20", "203.0.113.77", 443, "eth4", 5000, 10)));

        var wan = store.BuildView(Query(), LiveTrafficState.Live).Sankey!.Nodes.Single(n => n.Column == 2);

        var dest = wan.Destinations!.Single();
        dest.Site.Should().Be("Branch Office");
        dest.Ip.Should().Be("203.0.113.77");
    }

    [Fact]
    public void SankeyLinksClientsAppsAndRoutes()
    {
        var store = SeededStore();
        store.Ingest(Batch(
            Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10),
            Flow("b", Nas, "192.0.2.10", "203.0.113.9", 51820, "eth4", 2000, 10, proto: "udp")));

        var sankey = store.BuildView(Query(), LiveTrafficState.Live).Sankey!;

        sankey.Nodes.Where(n => n.Column == 0).Select(n => n.Name).Should().BeEquivalentTo("NAS", "Laptop");
        sankey.Nodes.Should().Contain(n => n.Column == 2 && n.Name == "Fiber WAN");
        sankey.Links.Should().Contain(l => l.Column == 1 && l.From == "WireGuard" && l.To == "Fiber WAN");
    }

    [Fact]
    public void SankeyHeldColumnKeepsAnIdleNodeAndLeavesANewcomerOut()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("a", Laptop, "192.0.2.20", "203.0.113.5", 443, "eth4", 1000, 10)));
        var nas = new LiveSankeyNode(0, "NAS", 500, LiveTrafficField.Client, null, false, ClientIp: "192.0.2.10");
        var hold = new LiveSankeyHold(nas, [nas], HasOther: false);

        var clients = store.BuildView(Query() with { SankeyHold = hold }, LiveTrafficState.Live).Sankey!
            .Nodes.Where(n => n.Column == 0).ToList();

        clients.Should().ContainSingle().Which.Should().Match<LiveSankeyNode>(n => n.Name == "NAS" && n.Value == 0,
            "the held NAS went idle and the Laptop arrived while the column was held");
    }

    [Fact]
    public void SankeyHeldColumnKeepsAQuietClientOutsideTheBusiest()
    {
        var store = SeededStore();
        store.Ingest(Batch(Enumerable.Range(0, 7)
            .Select(i => Flow($"f{i}", $"00:11:22:33:44:{i:x2}", $"192.0.2.{30 + i}", "203.0.113.5", 443, "eth4", 1000 * (i + 1), 10))
            .ToArray()));
        var view = store.BuildView(Query(), LiveTrafficState.Live);
        var quietest = view.Flows.MinBy(f => f.DownBps)!.ClientName;
        var column = view.Sankey!.Nodes.Where(n => n.Column == 0 && !n.IsOther).ToList();
        var keep = new LiveSankeyNode(0, quietest, 1, LiveTrafficField.Client, null, false);

        var sankey = store.BuildView(Query() with { SankeyHold = new LiveSankeyHold(keep, column, HasOther: true) }, LiveTrafficState.Live).Sankey!;

        sankey.Nodes.Should().Contain(n => n.Column == 0 && n.Name == quietest && !n.IsOther && n.Value > 0);
    }

    [Fact]
    public void LeaseIsPushedForTheFirstViewerAndRenewedLater()
    {
        var store = new LiveTrafficStore();
        var t0 = DateTime.UtcNow;

        store.TouchViewer("v1", t0).Should().BeTrue();
        store.NoteLeasePushed(t0);
        store.TouchViewer("v1", t0.AddSeconds(2)).Should().BeFalse();
        store.TouchViewer("v1", t0.AddSeconds(16)).Should().BeTrue();

        store.ReleaseViewer("v1", t0.AddSeconds(17)).Should().BeTrue("nobody is left watching");
    }

    private static TailnetSnapshot Tailnet(params TailnetSnatEntry[] snat) => new(
        [new TailnetPeer { Name = "phone", Os = "iOS", Addresses = ["100.100.1.2", "fd7a:115c:a1e0::2"] }],
        new TailnetPeer { Name = "nas", Addresses = ["100.100.1.1"], IsExitNode = true, LanAddresses = ["192.0.2.10"] },
        snat);

    [Fact]
    public void RoutedTrafficIsAttributedToTheTailscaleDevice()
    {
        var store = SeededStore();
        // The NAS is the exit node: it NATed the phone's connection to its own port 50000.
        store.SetTailnet(Tailnet(new TailnetSnatEntry("tcp", "100.100.1.2", 51515, "203.0.113.5", 443, "192.0.2.10", 50000)), true);
        store.Ingest(Batch(
            Flow("routed", Nas, "192.0.2.10", "203.0.113.5", 443, "eth4", 1_000_000, 10_000),
            Flow("own", Nas, "192.0.2.10", "203.0.113.9", 443, "eth4", 500_000, 10_000)));

        var rows = store.BuildView(Query(), LiveTrafficState.Live).Flows.ToDictionary(f => f.Key);

        rows["routed"].ClientName.Should().Be("phone");
        rows["routed"].ClientIp.Should().Be("100.100.1.2");
        rows["routed"].ClientVia.Should().Be("Tailscale through NAS");
        rows["own"].ClientName.Should().Be("NAS", "the router's own traffic stays its own");
    }

    [Fact]
    public void TailscaleClientsAndDestinationsTakeTheirTailnetNames()
    {
        var store = SeededStore();
        store.SetTailnet(Tailnet(), true);
        store.Ingest(Batch(
            Flow("in", "", "100.100.1.2", "203.0.113.5", 443, "eth4", 1_000_000, 10_000),
            Flow("to", Laptop, "192.0.2.20", "100.100.1.2", 22, "", 1_000, 1_000, lan: true)));

        var rows = store.BuildView(Query(), LiveTrafficState.Live).Flows.ToDictionary(f => f.Key);

        rows["in"].ClientName.Should().Be("phone");
        rows["in"].ClientVia.Should().Be("Tailscale");
        rows["to"].Destination.Should().Be("phone");
    }

    [Fact]
    public void WithoutATailnetTheTailscaleClientKeepsItsAddress()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("in", "", "100.100.1.2", "203.0.113.5", 443, "eth4", 1_000_000, 10_000)));

        var view = store.BuildView(Query(), LiveTrafficState.Live);

        view.Flows[0].ClientName.Should().Be("Tailscale (100.100.1.2)");
        view.TailnetConnected.Should().BeFalse();
    }

    [Fact]
    public void WireGuardFlowsAreNamedByThePeersEndpoint()
    {
        var store = SeededStore();
        store.SetTailnet(new TailnetSnapshot(
            [
                new TailnetPeer { Name = "phone", Addresses = ["100.100.1.2"], Endpoints = ["203.0.113.40:40000", "192.168.1.50:41641"] },
                new TailnetPeer { Name = "laptop", Addresses = ["100.100.1.3"], Endpoints = ["198.51.100.60:41641"] },
                new TailnetPeer { Name = "tablet", Addresses = ["100.100.1.4"], Endpoints = ["198.51.100.60:5000"] },
            ], null, []), true);
        var flows = new[]
        {
            Flow("exact", Nas, "192.0.2.10", "192.168.1.50", 41641, "eth4", 1_000, 1_000, proto: "udp"),
            Flow("remapped", Nas, "192.0.2.10", "203.0.113.40", 40001, "eth4", 1_000, 1_000, proto: "udp"),
            Flow("shared", Nas, "192.0.2.10", "198.51.100.60", 9999, "eth4", 1_000, 1_000, proto: "udp"),
            Flow("notwg", Nas, "192.0.2.10", "203.0.113.40", 443, "eth4", 1_000, 1_000),
        };
        foreach (var f in flows.Take(3)) f.ClientPort = 41641;
        store.Ingest(Batch(flows));

        var rows = store.BuildView(Query(), LiveTrafficState.Live).Flows.ToDictionary(f => f.Key);

        rows["exact"].Destination.Should().Be("phone");
        rows["remapped"].Destination.Should().Be("phone", "a remapped port still matches the only peer at that address");
        rows["shared"].Destination.Should().NotBe("laptop").And.NotBe("tablet");
        rows["notwg"].Destination.Should().NotBe("phone", "the address alone names only Tailscale's own traffic");
    }

    [Fact]
    public void WireGuardToAPeersEndpointOffTailscalesPortIsTailscale()
    {
        // tailscaled on a random port, the peer's NAT on another, and the peer behind this install's WAN.
        var store = SeededStore();
        store.SetServerHosts(new HashSet<string>(["203.0.113.80"], StringComparer.OrdinalIgnoreCase));
        store.SetTailnet(new TailnetSnapshot(
            [new TailnetPeer { Name = "desktop", Addresses = ["100.100.1.5"], Endpoints = ["203.0.113.80:10222"] }], null, []), true);
        var flow = Flow("wg", Nas, "192.0.2.10", "203.0.113.80", 10222, "eth4", 1_000, 1_000, proto: "udp");
        flow.ClientPort = 59464;
        store.Ingest(Batch(flow));

        var row = store.BuildView(Query(), LiveTrafficState.Live).Flows.Single();

        row.Destination.Should().Be("desktop");
        row.App.Should().Be("Tailscale");
    }

    [Theory]
    [InlineData("derp9e.tailscale.com", true)]
    [InlineData("derp12e.tailscale.com.", true)]
    [InlineData("login.tailscale.com", false)]
    [InlineData("derp.example.com", false)]
    public void DerpHosts(string host, bool expected)
    {
        LiveTrafficStore.IsDerpHost(host).Should().Be(expected);
    }

    [Fact]
    public void DerpRelaysAreNamedByTheirRegion()
    {
        var derp = new DerpMapClient.DerpMap(
            new Dictionary<string, DerpRegion> { ["203.0.113.60"] = new("dfw", "Dallas") },
            new Dictionary<string, DerpRegion> { ["dfw"] = new("dfw", "Dallas") });
        var store = SeededStore();
        store.SetTailnet(new TailnetSnapshot([], null, [], derp: derp), true);
        store.Ingest(Batch(
            Flow("stun", Nas, "192.0.2.10", "203.0.113.60", 3478, "eth4", 100, 100, proto: "udp"),
            Flow("relay", Nas, "192.0.2.10", "203.0.113.60", 443, "eth4", 1_000, 1_000)));

        var rows = store.BuildView(Query(), LiveTrafficState.Live).Flows.ToDictionary(f => f.Key);

        rows["stun"].Destination.Should().Be("Tailscale DERP (Dallas)");
        rows["stun"].DestinationSub.Should().Be("STUN");
        rows["stun"].App.Should().Be("Tailscale", "Tailscale's STUN is Tailscale's, ahead of the port's name");
        rows["relay"].DestinationSub.Should().Be("DERP relay");
    }

    [Fact]
    public void StunToAnyoneElseIsNamedByItsPort()
    {
        var store = SeededStore();
        store.Ingest(Batch(Flow("stun", Laptop, "192.0.2.20", "203.0.113.70", 3478, "eth4", 100, 100, proto: "udp")));

        store.BuildView(Query(), LiveTrafficState.Live).Flows.Single().App.Should().Be("STUN");
    }}
