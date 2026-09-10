using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

public class SfpDdmSpikeFilterTests
{
    // The event that raised a 67.8 °C alert on a stick whose real range is 40-49 °C.
    [Fact]
    public void TheLiveEventIsRecognised()
    {
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 42.85, prevRx: -22.37,
            midTemp: 67.85, midRx: -18.79,
            nextTemp: 44.85, nextRx: -22.29).Should().BeTrue();
    }

    [Fact]
    public void ADipIsRecognisedToo()
    {
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 46.85, prevRx: -22.29,
            midTemp: 24.85, midRx: -24.53,
            nextTemp: 46.85, nextRx: -22.29).Should().BeTrue();
    }

    [Fact]
    public void RealThermalDriftIsLeftAlone()
    {
        // 30 days of hourly means moved 40.5 to 48.7 °C with 0.5 dB of RX. One poll never does this.
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 47.85, prevRx: -22.29,
            midTemp: 48.85, midRx: -22.08,
            nextTemp: 49.85, nextRx: -22.08).Should().BeFalse();
    }

    [Fact]
    public void SmallCoMovementIsNotASignature()
    {
        // 427 samples in the 1-2 °C band already have RX moving with temperature.
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 43.85, prevRx: -22.29,
            midTemp: 45.85, midRx: -21.99,
            nextTemp: 43.85, nextRx: -22.29).Should().BeFalse();
    }

    [Fact]
    public void AThermalEventThatRampsAndStaysIsNotSuppressed()
    {
        // The protection against muting a real fault: it does not come back.
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 45.0, prevRx: -22.30,
            midTemp: 58.0, midRx: -21.40,
            nextTemp: 62.0, nextRx: -21.20).Should().BeFalse();
    }

    [Fact]
    public void TemperatureMovingAloneIsNotSuppressed()
    {
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 45.0, prevRx: -22.30,
            midTemp: 60.0, midRx: -22.28,
            nextTemp: 45.0, nextRx: -22.30).Should().BeFalse();
    }

    [Fact]
    public void OppositeDirectionsAreNotTheArtifact()
    {
        SfpDdmSpikeFilter.IsArtifact(
            prevTemp: 45.0, prevRx: -22.30,
            midTemp: 60.0, midRx: -23.50,
            nextTemp: 45.0, nextRx: -22.30).Should().BeFalse();
    }

    [Fact]
    public void JointJumpFlagsACandidateWithoutTheNextSample()
    {
        SfpDdmSpikeFilter.IsJointJump(42.85, -22.37, 67.85, -18.79).Should().BeTrue();
        SfpDdmSpikeFilter.IsJointJump(42.85, -22.37, 44.85, -22.29).Should().BeFalse();
    }
}
