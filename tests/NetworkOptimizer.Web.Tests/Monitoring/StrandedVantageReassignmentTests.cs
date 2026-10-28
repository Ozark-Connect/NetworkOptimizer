using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;
using EnrolledAgent = NetworkOptimizer.Web.Services.Monitoring.StrandedVantageReassignment.EnrolledAgent;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class StrandedVantageReassignmentTests
{
    private const int GatewayAgent = 1;
    private const int OtherAgent = 2;
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static WanContext Vantage(int id, int? agentId, string wan = "wan2") =>
        new() { Id = id, Name = $"v{id}", AgentId = agentId, WanInterface = wan };

    private static List<WanContext> Select(
        IEnumerable<WanContext> contexts, IReadOnlyCollection<EnrolledAgent> enrolled, params int[] connected) =>
        StrandedVantageReassignment.SelectStranded(contexts, enrolled, connected, GatewayAgent, Now, LongAgo);

    private static readonly DateTime LongAgo = Now - TimeSpan.FromDays(30);

    [Fact]
    public void RemovedAgent_IsStrandedImmediately()
    {
        var stranded = Select(new[] { Vantage(1, OtherAgent) },
            new[] { new EnrolledAgent(GatewayAgent, Now) }, GatewayAgent);

        stranded.Should().ContainSingle(c => c.Id == 1);
    }

    [Fact]
    public void OfflineAgent_WithinGrace_IsLeftAlone()
    {
        var stranded = Select(new[] { Vantage(1, OtherAgent) },
            new[] { new EnrolledAgent(OtherAgent, Now - TimeSpan.FromMinutes(5)) }, GatewayAgent);

        stranded.Should().BeEmpty();
    }

    [Fact]
    public void OfflineAgent_PastGrace_IsStranded()
    {
        var stranded = Select(new[] { Vantage(1, OtherAgent) },
            new[] { new EnrolledAgent(OtherAgent, Now - StrandedVantageReassignment.OfflineGrace) }, GatewayAgent);

        stranded.Should().ContainSingle();
    }

    [Fact]
    public void ConnectedAgent_IsNeverStranded_EvenWithAStaleLastSeen()
    {
        var stranded = Select(new[] { Vantage(1, OtherAgent) },
            new[] { new EnrolledAgent(OtherAgent, Now - TimeSpan.FromDays(1)) }, GatewayAgent, OtherAgent);

        stranded.Should().BeEmpty();
    }

    [Fact]
    public void ServerBoundVantage_AndTheGatewayAgentsOwn_AreNeverStranded()
    {
        var stranded = Select(new[] { Vantage(1, null), Vantage(2, GatewayAgent) },
            Array.Empty<EnrolledAgent>(), GatewayAgent);

        stranded.Should().BeEmpty();
    }

    [Fact]
    public void ServerDowntime_DoesNotCountAsAgentDowntime()
    {
        // Last heartbeat an hour ago because the server was down; it came back 5 minutes ago.
        var stranded = StrandedVantageReassignment.SelectStranded(new[] { Vantage(1, OtherAgent) },
            new[] { new EnrolledAgent(OtherAgent, Now - TimeSpan.FromHours(1)) },
            new[] { GatewayAgent }, GatewayAgent, Now, watchingSince: Now - TimeSpan.FromMinutes(5));

        stranded.Should().BeEmpty();
    }

    [Fact]
    public void RemovedAgent_MovesEvenRightAfterAServerStart()
    {
        var stranded = StrandedVantageReassignment.SelectStranded(new[] { Vantage(1, OtherAgent) },
            Array.Empty<EnrolledAgent>(), new[] { GatewayAgent }, GatewayAgent, Now, watchingSince: Now);

        stranded.Should().ContainSingle();
    }

    [Fact]
    public void NeverSeenAgent_IsStranded()
    {
        var stranded = Select(new[] { Vantage(1, OtherAgent) },
            new[] { new EnrolledAgent(OtherAgent, null) }, GatewayAgent);

        stranded.Should().ContainSingle();
    }

    [Fact]
    public void MoveFromOfflineAgent_ThenRestore_ReturnsTheOriginalBinding()
    {
        var context = Vantage(1, OtherAgent);
        context.ProbeSourceIp = "192.0.2.10";

        StrandedVantageReassignment.MoveToGateway(context, GatewayAgent, "ppp0", agentRemoved: false);
        context.AgentId.Should().Be(GatewayAgent);
        context.InterfaceName.Should().Be("ppp0");
        context.ProbeSourceIp.Should().BeNull();

        StrandedVantageReassignment.Restore(context).Should().Be(GatewayAgent);
        context.AgentId.Should().Be(OtherAgent);
        context.InterfaceName.Should().BeNull();
        context.ProbeSourceIp.Should().Be("192.0.2.10");
        context.ReassignedFromAgentId.Should().BeNull();
    }

    [Fact]
    public void MoveFromRemovedAgent_IsPermanent()
    {
        var context = Vantage(1, OtherAgent);

        StrandedVantageReassignment.MoveToGateway(context, GatewayAgent, "eth8", agentRemoved: true);

        context.ReassignedFromAgentId.Should().BeNull();
    }

    [Fact]
    public void SecondMove_StillRemembersTheFirstOwner()
    {
        const int SecondGateway = 3;
        var context = Vantage(1, OtherAgent);
        context.InterfaceName = "eth9";

        StrandedVantageReassignment.MoveToGateway(context, GatewayAgent, "eth8", agentRemoved: false);
        StrandedVantageReassignment.MoveToGateway(context, SecondGateway, "eth8", agentRemoved: false);

        context.ReassignedFromAgentId.Should().Be(OtherAgent);
        context.ReassignedFromInterfaceName.Should().Be("eth9");
    }

    [Fact]
    public void OriginRemovedAfterTheMove_IsForgotten()
    {
        var context = Vantage(1, OtherAgent);
        StrandedVantageReassignment.MoveToGateway(context, GatewayAgent, "eth8", agentRemoved: false);

        StrandedVantageReassignment.ForgetRemovedOrigins(new[] { context },
            new[] { new EnrolledAgent(GatewayAgent, Now) }).Should().BeTrue();

        context.ReassignedFromAgentId.Should().BeNull();
        context.AgentId.Should().Be(GatewayAgent);
    }

    [Fact]
    public void OriginStillEnrolled_IsKept()
    {
        var context = Vantage(1, OtherAgent);
        StrandedVantageReassignment.MoveToGateway(context, GatewayAgent, "eth8", agentRemoved: false);

        StrandedVantageReassignment.ForgetRemovedOrigins(new[] { context },
            new[] { new EnrolledAgent(GatewayAgent, Now), new EnrolledAgent(OtherAgent, null) }).Should().BeFalse();

        context.ReassignedFromAgentId.Should().Be(OtherAgent);
    }

    [Theory]
    [InlineData("eth4")]
    [InlineData("eth4.100")]
    [InlineData("ppp0")]
    [InlineData("gre1")]
    public void DataPath_IsTheWansRecordedDataPathInterface(string dataPath)
    {
        var profiles = new[]
        {
            new WanProfile { WanNetworkgroup = "WAN", DataPathInterface = "eth9" },
            new WanProfile { WanNetworkgroup = "WAN2", DataPathInterface = dataPath, CounterInterface = "eth4" },
        };

        StrandedVantageReassignment.DataPathFor(profiles, "wan2").Should().Be(dataPath);
    }

    [Fact]
    public void DataPath_UnknownWan_IsNull()
    {
        var profiles = new[] { new WanProfile { WanNetworkgroup = "WAN", DataPathInterface = "eth9" } };

        StrandedVantageReassignment.DataPathFor(profiles, "wan3").Should().BeNull();
        StrandedVantageReassignment.DataPathFor(profiles, null).Should().BeNull();
    }
}
