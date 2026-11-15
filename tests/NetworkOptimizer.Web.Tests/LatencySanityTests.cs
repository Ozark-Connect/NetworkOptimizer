using FluentAssertions;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class LatencySanityTests
{
    [Fact]
    public void Clean_PlausibleBurst_IsUnchanged()
    {
        LatencySanity.Clean(2.3, 2.7, 3.0, 0.2).Should().Be((2.3, 2.7, 3.0, 0.2));
    }

    [Fact]
    public void Clean_AbsentValues_AreUnchanged()
    {
        LatencySanity.Clean(null, null, null, null).Should().Be(((double?)null, (double?)null, (double?)null, (double?)null));
    }

    [Fact]
    public void Clean_OneImpossibleReply_KeepsOnlyTheMinimum()
    {
        // What ping printed for a six-reply burst with one mangled reply.
        var cleaned = LatencySanity.Clean(2.308, 692818870035183.5, 4156913220211088.5, 823110.887);

        cleaned.Should().Be((2.308, (double?)null, (double?)null, (double?)null));
    }

    [Fact]
    public void Clean_EveryReplyImpossible_KeepsNothing()
    {
        var cleaned = LatencySanity.Clean(4e15, 4e15, 4e15, 0);

        cleaned.Should().Be(((double?)null, (double?)null, (double?)null, (double?)null));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    [InlineData(LatencySanity.MaxPlausibleRttMs + 1)]
    public void IsPlausible_OutOfRange_False(double ms)
    {
        LatencySanity.IsPlausible(ms).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1999.9)]
    [InlineData(LatencySanity.MaxPlausibleRttMs)]
    public void IsPlausible_InRange_True(double ms)
    {
        LatencySanity.IsPlausible(ms).Should().BeTrue();
    }

    [Fact]
    public void ScrubImplausibleRtt_ClearsThePoisonedFieldsAndKeepsLoss()
    {
        var result = new ProbeResult
        {
            TargetId = "t1",
            Sent = 6,
            Received = 6,
            Success = true,
            RttMinMs = 2.308,
            RttAvgMs = 692818870035183.5,
            RttMaxMs = 4156913220211088.5,
            JitterMs = 823110.887
        };

        AgentProbeResultSink.ScrubImplausibleRtt(result).Should().BeTrue();

        result.HasRttMinMs.Should().BeTrue();
        result.RttMinMs.Should().Be(2.308);
        result.HasRttAvgMs.Should().BeFalse();
        result.HasRttMaxMs.Should().BeFalse();
        result.HasJitterMs.Should().BeFalse();
        result.Received.Should().Be(6);
        result.Success.Should().BeTrue();
    }

    [Fact]
    public void ScrubImplausibleRtt_PlausibleResult_IsUntouched()
    {
        var result = new ProbeResult { TargetId = "t1", RttMinMs = 2.3, RttAvgMs = 2.7, RttMaxMs = 3.0, JitterMs = 0.2 };

        AgentProbeResultSink.ScrubImplausibleRtt(result).Should().BeFalse();

        result.RttAvgMs.Should().Be(2.7);
        result.HasRttMaxMs.Should().BeTrue();
    }
}
