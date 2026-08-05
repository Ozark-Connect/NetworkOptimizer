using FluentAssertions;
using Google.Protobuf;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

/// <summary>
/// The hello has to stay readable in both directions across a rollout: agents and servers update
/// on their own schedules, and a capability the server guesses at is worse than one it never
/// offers. Every capability on it is an explicitly optional field so "no" and "did not say" stay
/// distinguishable, and no field number is ever reused.
/// </summary>
public class AgentHelloCompatibilityTests
{
    [Fact]
    public void OldAgent_SaysNothingAboutSourceBinding()
    {
        // What an agent predating the field puts on the wire: the field simply is not there.
        var oldHello = new AgentHello { AgentKey = "key", Version = "2.5.0", LanIp = "192.0.2.20" };

        var parsed = AgentHello.Parser.ParseFrom(oldHello.ToByteArray());

        parsed.HasSupportsSourceBind.Should().BeFalse();
        parsed.SupportsSourceBind.Should().BeFalse();
    }

    [Fact]
    public void NewAgent_SayingNo_IsDistinguishableFromSayingNothing()
    {
        var windowsAgent = new AgentHello { AgentKey = "key", Version = "2.6.0", SupportsSourceBind = false };

        var parsed = AgentHello.Parser.ParseFrom(windowsAgent.ToByteArray());

        parsed.HasSupportsSourceBind.Should().BeTrue();
        parsed.SupportsSourceBind.Should().BeFalse();
    }

    [Fact]
    public void NewAgent_SayingYes_RoundTrips()
    {
        var linuxAgent = new AgentHello { AgentKey = "key", Version = "2.6.0", SupportsSourceBind = true };

        var parsed = AgentHello.Parser.ParseFrom(linuxAgent.ToByteArray());

        parsed.HasSupportsSourceBind.Should().BeTrue();
        parsed.SupportsSourceBind.Should().BeTrue();
    }

    [Fact]
    public void NewFieldDoesNotDisturbTheExistingOnes()
    {
        // An old SERVER parses a new agent's hello by skipping the unknown field, so everything it
        // already reads has to survive alongside it.
        var hello = new AgentHello
        {
            AgentKey = "key",
            Version = "2.6.0",
            LanIp = "192.0.2.20",
            SpeedTestPort = 24443,
            ServesSpeedTest = true,
            SupportsSourceBind = true,
        };

        var parsed = AgentHello.Parser.ParseFrom(hello.ToByteArray());

        parsed.AgentKey.Should().Be("key");
        parsed.LanIp.Should().Be("192.0.2.20");
        parsed.SpeedTestPort.Should().Be(24443);
        parsed.HasServesSpeedTest.Should().BeTrue();
        parsed.ServesSpeedTest.Should().BeTrue();
    }

    [Fact]
    public void ProbeTargetSpec_WithoutASource_LeavesTheAgentOnItsOwnDefault()
    {
        // Every target on a site with no WAN contexts carries an empty source, which ProbeRunner
        // reads as "use the agent's configured default" - the behavior before contexts existed.
        var spec = new ProbeTargetSpec { TargetId = "wan-1", Address = "192.0.2.1", ProbeMode = "icmp" };

        AgentProtocol.ProbeTargetSpec.Parser.ParseFrom(spec.ToByteArray()).SourceIp.Should().BeEmpty();
    }
}
