using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Custom app definitions: parsing what the form takes, matching rows, and how the store applies
/// them to Live Traffic. Addresses are RFC 5737 documentation ranges.
/// </summary>
public class CustomAppMatcherTests
{
    private const string Laptop = "aa:bb:cc:dd:ee:02";

    private static CustomAppRule Rule(string ports, CustomAppProtocol proto = CustomAppProtocol.Udp,
        CustomAppPortOn on = CustomAppPortOn.Destination, CustomAppScope scope = CustomAppScope.Anywhere,
        string? remote = null, int id = 1, string name = "ARGB Controller", string? icon = null)
    {
        CustomAppMatcher.TryParsePorts(ports, out var parsed, out _).Should().BeTrue();
        return new CustomAppRule(id, name, proto, parsed, on, scope, remote, icon);
    }

    private static LiveFlowRow Row(string proto = "UDP", int clientPort = 50000, int remotePort = 21324,
        LiveRouteKind kind = LiveRouteKind.Lan, string destination = "192.0.2.30", string destinationIp = "192.0.2.30",
        string? app = null) => new()
    {
        Key = Guid.NewGuid().ToString("N"),
        Proto = proto,
        ClientPort = clientPort,
        RemotePort = remotePort,
        RouteKind = kind,
        Destination = destination,
        DestinationIp = destinationIp,
        App = app ?? $"{proto} {remotePort}",
    };

    // ---------------------------------------------------------------- ports and remotes

    [Theory]
    [InlineData("21324", "21324")]
    [InlineData(" 5000 - 5010 ", "5000-5010")]
    [InlineData("80, 8080,443", "80, 8080, 443")]
    [InlineData("1,65535", "1, 65535")]
    public void PortsParse(string text, string normalized)
    {
        CustomAppMatcher.TryParsePorts(text, out var ports, out var error).Should().BeTrue();
        error.Should().BeNull();
        string.Join(", ", ports).Should().Be(normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("http")]
    [InlineData("80-")]
    [InlineData("1-2-3")]
    public void BadPortsAreRefusedWithTheFormMessage(string text)
    {
        CustomAppMatcher.TryParsePorts(text, out _, out var error).Should().BeFalse();
        error.Should().StartWith("Enter a port from 1 to 65535");
    }

    [Fact]
    public void ABackwardRangeSaysSo()
    {
        CustomAppMatcher.TryParsePorts("5010-5000", out _, out var error).Should().BeFalse();
        error.Should().Be("That range runs backward. Put the lower port first.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("*.example.com")]
    [InlineData("192.0.2.5")]
    [InlineData("192.0.2.0/24")]
    [InlineData("2001:db8::/32")]
    public void RemotesThatValidate(string? remote) => CustomAppMatcher.ValidateRemote(remote).Should().BeNull();

    [Theory]
    [InlineData("example")]
    [InlineData("192.0.2.0/33")]
    [InlineData("not a domain.com")]
    [InlineData("192.0.2.0/x")]
    public void RemotesThatDoNot(string remote) => CustomAppMatcher.ValidateRemote(remote).Should().NotBeNull();

    // ---------------------------------------------------------------- the seed in a link

    [Fact]
    public void SeedRoundTripsThroughTheUrl()
    {
        var seed = new CustomAppSeed(CustomAppProtocol.Udp, 21324, CustomAppScope.Lan);
        seed.Encode().Should().Be("udp-21324-lan");
        CustomAppSeed.Decode("udp-21324-lan").Should().Be(seed);
        CustomAppSeed.Decode("TCP-443-internet").Should().Be(new CustomAppSeed(CustomAppProtocol.Tcp, 443, CustomAppScope.Internet));
        CustomAppSeed.Decode("tcp-443-any")!.Scope.Should().Be(CustomAppScope.Anywhere);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("icmp-0-lan")]
    [InlineData("udp-70000-lan")]
    [InlineData("udp-21324")]
    public void ABadSeedIsIgnored(string? value) => CustomAppSeed.Decode(value).Should().BeNull();

    [Fact]
    public void OnlyTheBareProtocolAndPortFallbackIsUnnamed()
    {
        Row().UnnamedPort.Should().Be(new CustomAppSeed(CustomAppProtocol.Udp, 21324, CustomAppScope.Lan));
        Row(kind: LiveRouteKind.Wan).UnnamedPort!.Scope.Should().Be(CustomAppScope.Internet);
        Row(app: "SSH").UnnamedPort.Should().BeNull("a service name is a fallback, but the card is for a bare port");
        Row(app: "UDP 9999").UnnamedPort.Should().BeNull("the label must be this row's own port");
        Row(proto: "ICMP", remotePort: 0, app: "ICMP").UnnamedPort.Should().BeNull();
    }

    // ---------------------------------------------------------------- matching

    [Fact]
    public void ProtocolMustMatch()
    {
        CustomAppMatcher.Matches(Rule("21324"), Row()).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", CustomAppProtocol.Tcp), Row()).Should().BeFalse();
        CustomAppMatcher.Matches(Rule("21324", CustomAppProtocol.Both), Row(proto: "TCP")).Should().BeTrue();
    }

    [Fact]
    public void PortOnPicksTheEnd()
    {
        var inbound = Row(clientPort: 21324, remotePort: 61000);
        CustomAppMatcher.Matches(Rule("21324"), inbound).Should().BeFalse();
        CustomAppMatcher.Matches(Rule("21324", on: CustomAppPortOn.Source), inbound).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", on: CustomAppPortOn.Either), inbound).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", on: CustomAppPortOn.Source), Row()).Should().BeFalse();
    }

    [Fact]
    public void RangesAndListsMatch()
    {
        CustomAppMatcher.Matches(Rule("21000-22000"), Row()).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("80, 21324"), Row()).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("80, 443"), Row()).Should().BeFalse();
    }

    [Fact]
    public void WhereSplitsLanFromTheInternet()
    {
        CustomAppMatcher.Matches(Rule("21324", scope: CustomAppScope.Lan), Row()).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", scope: CustomAppScope.Lan), Row(kind: LiveRouteKind.Wan)).Should().BeFalse();
        CustomAppMatcher.Matches(Rule("21324", scope: CustomAppScope.Internet), Row(kind: LiveRouteKind.Vpn)).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", scope: CustomAppScope.Internet), Row()).Should().BeFalse();
    }

    [Fact]
    public void RemoteMatchesASuffixAnAddressOrACidr()
    {
        var toCloud = Row(kind: LiveRouteKind.Wan, destination: "api.lights.example.com", destinationIp: "203.0.113.9");
        CustomAppMatcher.Matches(Rule("21324", remote: "example.com"), toCloud).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", remote: "lights.example.com"), toCloud).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", remote: "ample.com"), toCloud).Should().BeFalse("a suffix matches whole labels only");
        CustomAppMatcher.Matches(Rule("21324", remote: "203.0.113.9"), toCloud).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", remote: "203.0.113.0/24"), toCloud).Should().BeTrue();
        CustomAppMatcher.Matches(Rule("21324", remote: "198.51.100.0/24"), toCloud).Should().BeFalse();
    }

    [Fact]
    public void NetworkOptimizersOwnTrafficKeepsItsName()
    {
        CustomAppMatcher.Matches(Rule("21324"), Row(app: "Network Optimizer")).Should().BeFalse();
    }

    [Fact]
    public void TheFirstMatchInListOrderNamesTheRow()
    {
        var rows = new List<LiveFlowRow> { Row(), Row(remotePort: 80, app: "HTTP") };
        CustomAppMatcher.Apply(rows, [Rule("21324", id: 7, name: "Lights"), Rule("21324", id: 8, name: "Later")]);

        rows[0].App.Should().Be("Lights");
        rows[0].CustomAppId.Should().Be(7);
        rows[0].AppIsFallback.Should().BeFalse();
        rows[0].AppIconClass.Should().Be(CustomAppMatcher.InitialOnlyIcon, "no glyph chosen draws the initial, never a brand found by name");
        rows[0].UnnamedPort.Should().BeNull();
        rows[1].App.Should().Be("HTTP");
    }

    [Fact]
    public void AChosenGlyphIsTheIcon()
    {
        var rows = new List<LiveFlowRow> { Row() };
        CustomAppMatcher.Apply(rows, [Rule("21324", icon: "fa-solid fa-lightbulb")]);
        rows[0].AppIconClass.Should().Be("fa-solid fa-lightbulb");
    }

    [Fact]
    public void DescribeReadsLikeTheList()
    {
        CustomAppMatcher.Describe(Rule("21324", on: CustomAppPortOn.Either, scope: CustomAppScope.Lan))
            .Should().Be("UDP 21324, either end, between my devices");
        CustomAppMatcher.Describe(Rule("1400", CustomAppProtocol.Tcp, scope: CustomAppScope.Internet))
            .Should().Be("TCP 1400, to the internet");
        CustomAppMatcher.Describe(Rule("443", CustomAppProtocol.Tcp, remote: "example.com"))
            .Should().Be("TCP 443, example.com");
        CustomAppMatcher.Describe(Rule("80, 8080", CustomAppProtocol.Both, on: CustomAppPortOn.Source))
            .Should().Be("TCP and UDP 80, 8080, source port");
    }

    [Fact]
    public void AStoredRowThatNoLongerParsesIsSkipped()
    {
        var rules = CustomAppMatcher.ToRules(
        [
            new CustomAppDefinition { Id = 2, SortOrder = 1, Name = "Second", Ports = "80" },
            new CustomAppDefinition { Id = 1, SortOrder = 0, Name = "First", Ports = "21324" },
            new CustomAppDefinition { Id = 3, SortOrder = 2, Name = "Broken", Ports = "nope" },
        ]);
        rules.Select(r => r.Name).Should().Equal("First", "Second");
    }

    // ---------------------------------------------------------------- the store

    private static LiveFlow LanFlow(string key, int port, string proto = "udp") => new()
    {
        Key = key,
        Proto = proto,
        ClientIp = "192.0.2.20",
        ClientMac = Laptop,
        ClientPort = 50000,
        RemoteIp = "192.0.2.30",
        RemotePort = port,
        Lan = true,
        DownBytes = 1_000_000,
        UpBytes = 10_000,
        TotalDownBytes = 1_000_000,
        TotalUpBytes = 10_000,
        AgeSeconds = 30,
    };

    private static LiveTrafficStore StoreWith(params LiveFlow[] flows)
    {
        var store = new LiveTrafficStore();
        store.SetSiteData(
            [new LiveClientInfo(Laptop, "Laptop", "192.0.2.20", "6 GHz", "Main")],
            ["192.0.2.1"], "Gateway",
            new Dictionary<string, LiveRouteInfo>(), new LiveRouteInfo("WAN", LiveRouteKind.Wan, null, null));
        var batch = new LiveTrafficBatch { WindowSeconds = 2, TotalFlows = flows.Length };
        batch.Flows.AddRange(flows);
        store.Ingest(batch);
        return store;
    }

    private static LiveTrafficQuery Query(LiveTrafficGroupBy by = LiveTrafficGroupBy.Flows) =>
        new() { ViewerId = "test", Filters = [], IncludeSankey = true, GroupBy = by };

    [Fact]
    public void AnUnnamedPortCarriesItsSeedToTheRowTheSankeyAndTheGroup()
    {
        var store = StoreWith(LanFlow("a", 21324));

        var view = store.BuildView(Query(), LiveTrafficState.Live);
        var row = view.Flows.Single();
        row.App.Should().Be("UDP 21324");
        row.RouteKind.Should().Be(LiveRouteKind.Lan);
        row.UnnamedPort.Should().Be(new CustomAppSeed(CustomAppProtocol.Udp, 21324, CustomAppScope.Lan));
        view.Sankey!.Nodes.Single(n => n.Column == 1).Unnamed.Should().Be(row.UnnamedPort);

        var groups = store.BuildView(Query(LiveTrafficGroupBy.App), LiveTrafficState.Live).Groups;
        groups.Single().Unnamed.Should().Be(row.UnnamedPort);
    }

    [Fact]
    public void ADefinitionRenamesTheLiveRowOnTheNextView()
    {
        var store = StoreWith(LanFlow("a", 21324), LanFlow("b", 21325));
        store.SetCustomApps([Rule("21324", scope: CustomAppScope.Lan)]);

        var view = store.BuildView(Query(), LiveTrafficState.Live);
        view.Flows.Single(f => f.Key == "a").App.Should().Be("ARGB Controller");
        view.Flows.Single(f => f.Key == "b").App.Should().Be("UDP 21325");
        view.Sankey!.Nodes.Where(n => n.Column == 1).Select(n => n.Name).Should().Contain("ARGB Controller");
        store.LiveCounts().Should().Equal(new Dictionary<int, int> { [1] = 1 });
    }

    [Fact]
    public void PreviewCountsLiveMatchesAndNamesTheDefinitionAboveThatWins()
    {
        var store = StoreWith(LanFlow("a", 21324), LanFlow("b", 21325));
        store.SetCustomApps([Rule("21324", id: 1, name: "Lights")]);

        var draft = Rule("21324-21325", id: 0, name: "Draft");
        store.Preview(draft, editingId: null).Should().Be(new CustomAppPreview(2, "Lights"));

        // Editing the first definition: nothing sits above it.
        store.Preview(draft with { Id = 1 }, editingId: 1).Should().Be(new CustomAppPreview(2, null));
        store.Preview(Rule("9999", id: 0), editingId: null).Should().Be(new CustomAppPreview(0, null));
    }
}
