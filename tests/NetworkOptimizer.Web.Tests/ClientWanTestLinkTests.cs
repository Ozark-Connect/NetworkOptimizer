using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The client WAN test link, shared by the Client WAN Test page and Client Performance's WAN Test:
/// an agent-backed site routes through the agent's /wan/ relay, a direct site links the server, and
/// a site that could not attribute the result gets no link.
/// </summary>
public class ClientWanTestLinkTests
{
    private static readonly ExternalSpeedTestServer DefaultServer = new()
    {
        Id = 1, Name = "Primary", ServerId = "primary", IsDefault = true, Scheme = "https", Host = "speed.example.com",
    };

    private static readonly ExternalSpeedTestServer OtherServer = new()
    {
        Id = 2, Name = "Backup", ServerId = "backup", IsDefault = false, Scheme = "https", Host = "speed2.example.com",
    };

    private static SiteSpeedTestTargetResolver.Result Agent(bool onGateway = false) => new(
        EffectiveTarget: "192.0.2.10",
        BaseUrl: onGateway ? null : "https://192.0.2.10:3005",
        Host: "192.0.2.10",
        UsesAgent: !onGateway,
        AgentOffline: false,
        AgentOnGateway: onGateway);

    [Fact]
    public void An_agent_site_routes_through_the_agent_relay()
    {
        ClientWanTestLink.RunUrl(Agent(), DefaultServer).Should().Be("https://192.0.2.10:3005/wan/?Run");
        ClientWanTestLink.BaseUrl(Agent(), OtherServer).Should().Be("https://192.0.2.10:3005/wan/backup/");
    }

    [Fact]
    public void A_direct_site_links_the_server()
    {
        ClientWanTestLink.BaseUrl(null, DefaultServer).Should().Be(DefaultServer.Url);
    }

    [Fact]
    public void The_default_server_is_picked_first()
    {
        ClientWanTestLink.DefaultServer(new[] { OtherServer, DefaultServer }).Should().BeSameAs(DefaultServer);
        ClientWanTestLink.DefaultServer(new[] { OtherServer }).Should().BeSameAs(OtherServer);
        ClientWanTestLink.DefaultServer(Array.Empty<ExternalSpeedTestServer>()).Should().BeNull();
    }

    [Fact]
    public void No_link_without_a_server_or_where_the_result_would_be_misattributed()
    {
        ClientWanTestLink.CanRun(isDefaultSite: true, null, DefaultServer).Should().BeTrue();
        ClientWanTestLink.CanRun(isDefaultSite: true, null, null).Should().BeFalse("no WAN server is configured");
        ClientWanTestLink.CanRun(isDefaultSite: false, null, DefaultServer).Should().BeFalse("the site's agent is offline");
        ClientWanTestLink.CanRun(isDefaultSite: false, Agent(), DefaultServer).Should().BeTrue();
        ClientWanTestLink.CanRun(isDefaultSite: false, Agent(onGateway: true), DefaultServer).Should().BeFalse("the agent runs on the gateway");
    }
}
