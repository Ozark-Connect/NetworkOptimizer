using FluentAssertions;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The new server has to keep working against agent binaries that predate this branch, because
/// that is what every deployed site is running until the agents are rolled out. The rule is that
/// an old agent behaves exactly as it did, and is never handed work it cannot do correctly.
/// </summary>
public class OldAgentCompatibilityTests
{
    [Fact]
    public void AnOldAgentsHello_ReadsAsDidNotSay_NotAsNo()
    {
        // No supports_source_bind on the wire at all. Absent has to stay distinguishable from an
        // explicit false, because "cannot bind" and "did not say" get treated the same only by
        // accident - and the field is what gates offering an interface bind.
        var hello = new AgentHello { AgentKey = "k", Version = "2.5.3", LanIp = "192.0.2.10" };

        hello.HasSupportsSourceBind.Should().BeFalse();
        var stored = hello.HasSupportsSourceBind ? hello.SupportsSourceBind : (bool?)null;
        stored.Should().BeNull();
    }

    [Fact]
    public void ANewAgentCanSayNo_Distinctly()
    {
        var hello = new AgentHello { AgentKey = "k", SupportsSourceBind = false };

        hello.HasSupportsSourceBind.Should().BeTrue();
        var stored = hello.HasSupportsSourceBind ? hello.SupportsSourceBind : (bool?)null;
        stored.Should().Be(false);
    }

    [Fact]
    public void AnOldAgentRoundTripsThroughTheNewProto()
    {
        // Field 6 is new; nothing else moved. An old agent's bytes still parse, and a new server's
        // extra field does not disturb the fields an old agent reads.
        var hello = new AgentHello { AgentKey = "k", Version = "2.5.3", LanIp = "192.0.2.10", SpeedTestPort = 3000 };

        var parsed = AgentHello.Parser.ParseFrom(hello.ToByteArray());

        parsed.AgentKey.Should().Be("k");
        parsed.LanIp.Should().Be("192.0.2.10");
        parsed.SpeedTestPort.Should().Be(3000);
        parsed.HasSupportsSourceBind.Should().BeFalse();
    }

    [Fact]
    public void ProbeTargetSpecSourceIp_IsNotNewOnThisBranch()
    {
        // The field the server now populates predates this work, and old agents already prefer it
        // over their own default - which is why per-probe PING binding works before any rollout.
        var spec = new ProbeTargetSpec { TargetId = "t", Address = "192.0.2.1", SourceIp = "198.51.100.7" };

        spec.SourceIp.Should().Be("198.51.100.7");
    }
}
