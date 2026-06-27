using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ChannelSpanHelperTests
{
    // --- GetChannelSpan ---

    [Fact]
    public void GetChannelSpan_2_4GHz_20MHz_ReturnsPlusMinus2()
    {
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band2_4GHz, 6, 20);
        span.Should().Be((4, 8));
    }

    [Fact]
    public void GetChannelSpan_2_4GHz_40MHz_ReturnsPlusMinus4()
    {
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band2_4GHz, 6, 40);
        span.Should().Be((2, 10));
    }

    [Fact]
    public void GetChannelSpan_2_4GHz_ClampsToValidRange()
    {
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band2_4GHz, 1, 20);
        span.Low.Should().Be(1);
        span.High.Should().Be(3);
    }

    [Fact]
    public void GetChannelSpan_5GHz_20MHz_ReturnsSingleChannel()
    {
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 36, 20);
        span.Should().Be((36, 36));
    }

    [Fact]
    public void GetChannelSpan_5GHz_80MHz_ReturnsBondingGroup()
    {
        // Ch 36/80 spans 36-48 (4 channels * 4 = 12 channel numbers)
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 36, 80);
        span.Should().Be((36, 48));

        // Ch 44/80 should also span 36-48 (same bonding group)
        span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 44, 80);
        span.Should().Be((36, 48));
    }

    [Fact]
    public void GetChannelSpan_5GHz_160MHz_ReturnsBondingGroup()
    {
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 36, 160);
        span.Should().Be((36, 64));
    }

    [Fact]
    public void GetChannelSpan_6GHz_80MHz_ReturnsBondingGroup()
    {
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 5, 80);
        span.Should().Be((1, 13));
    }

    [Fact]
    public void GetChannelSpan_6GHz_320MHz_RespectsUNIIBoundary()
    {
        // UNII-5: channels 1-61
        var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 29, 320);
        span.Should().Be((1, 61));

        // UNII-6/7: channels 97-157
        span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 117, 320);
        span.Should().Be((97, 157));
    }

    // --- SpansOverlap ---

    [Fact]
    public void SpansOverlap_IdenticalSpans_ReturnsTrue()
    {
        ChannelSpanHelper.SpansOverlap((36, 48), (36, 48)).Should().BeTrue();
    }

    [Fact]
    public void SpansOverlap_NonOverlapping_ReturnsFalse()
    {
        ChannelSpanHelper.SpansOverlap((36, 48), (52, 64)).Should().BeFalse();
    }

    [Fact]
    public void SpansOverlap_PartialOverlap_ReturnsTrue()
    {
        ChannelSpanHelper.SpansOverlap((36, 48), (44, 56)).Should().BeTrue();
    }

    // --- SignalToInterferenceWeight ---

    [Fact]
    public void SignalToInterferenceWeight_StrongSignal_Returns1()
    {
        ChannelSpanHelper.SignalToInterferenceWeight(-50).Should().Be(1.0);
    }

    [Fact]
    public void SignalToInterferenceWeight_BelowCca_ReturnsZero()
    {
        // -90 dBm is below the -82 CCA threshold: the radio doesn't defer, so no contention.
        ChannelSpanHelper.SignalToInterferenceWeight(-90).Should().Be(0.0);
    }

    [Fact]
    public void SignalToInterferenceWeight_AtCca_ReturnsZero()
    {
        ChannelSpanHelper.SignalToInterferenceWeight(-82).Should().Be(0.0);
    }

    [Fact]
    public void SignalToInterferenceWeight_TypicalSpacing_Returns0_531()
    {
        // -65 dBm, CCA-anchored: (-65 + 82) / 32 = 0.531
        ChannelSpanHelper.SignalToInterferenceWeight(-65).Should().BeApproximately(0.531, 0.01);
    }

    [Fact]
    public void SignalToInterferenceWeight_ClampsAbove()
    {
        ChannelSpanHelper.SignalToInterferenceWeight(-30).Should().Be(1.0);
    }

    // --- ComputeOverlapFactor ---

    [Fact]
    public void ComputeOverlapFactor_2_4GHz_SameChannel_Returns1()
    {
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band2_4GHz, 6, 20, 6, 20)
            .Should().Be(1.0);
    }

    [Fact]
    public void ComputeOverlapFactor_2_4GHz_Adjacent_Returns0_7()
    {
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band2_4GHz, 6, 20, 7, 20)
            .Should().Be(0.7);
    }

    [Fact]
    public void ComputeOverlapFactor_2_4GHz_NonOverlapping_Returns0()
    {
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band2_4GHz, 1, 20, 11, 20)
            .Should().Be(0.0);
    }

    [Fact]
    public void ComputeOverlapFactor_5GHz_SameChannel_Returns1()
    {
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band5GHz, 36, 80, 36, 80)
            .Should().Be(1.0);
    }

    [Fact]
    public void ComputeOverlapFactor_5GHz_SameBondingGroupSameSpan_Returns1()
    {
        // Ch 36/80 and Ch 44/80 occupy the identical 80 MHz block (36-48), so they
        // time-share the whole channel - full co-channel even though primaries differ.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band5GHz, 36, 80, 44, 80)
            .Should().Be(1.0);
    }

    [Fact]
    public void ComputeOverlapFactor_5GHz_160MHz_SameBlockDifferentPrimary_Returns1()
    {
        // Ch 100/160 and Ch 112/160 both span the single 100-128 block: full co-channel.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band5GHz, 100, 160, 112, 160)
            .Should().Be(1.0);
    }

    [Fact]
    public void ComputeOverlapFactor_5GHz_PartialOverlap_Returns0_7()
    {
        // Ch 44/80 (span 36-48) partially overlaps Ch 52/160 (span 36-64): shared
        // sub-channels but not the same block, so it is secondary/partial overlap.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band5GHz, 44, 80, 52, 160)
            .Should().Be(0.7);
    }

    [Fact]
    public void ComputeOverlapFactor_5GHz_DifferentGroups_Returns0()
    {
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band5GHz, 36, 80, 149, 80)
            .Should().Be(0.0);
    }

    // --- GetChannelWidthSpan ---

    [Fact]
    public void GetChannelWidthSpan_5GHz_80MHz_Returns4Channels()
    {
        var channels = ChannelSpanHelper.GetChannelWidthSpan(RadioBand.Band5GHz, 36, 80);
        channels.Should().BeEquivalentTo(new[] { 36, 40, 44, 48 });
    }

    [Fact]
    public void GetChannelWidthSpan_2_4GHz_20MHz_ReturnsOverlappingRange()
    {
        var channels = ChannelSpanHelper.GetChannelWidthSpan(RadioBand.Band2_4GHz, 6, 20);
        channels.Should().BeEquivalentTo(new[] { 4, 5, 6, 7, 8 });
    }

    [Fact]
    public void GetChannelWidthSpan_2_4GHz_40MHz_WithExtChannel()
    {
        // Ch 6 with HT40+ (ext above) → secondary=10, span = 4-12
        var channels = ChannelSpanHelper.GetChannelWidthSpan(RadioBand.Band2_4GHz, 6, 40, extChannel: 1);
        channels.Should().Contain(4).And.Contain(12);
    }

    // --- Bonding Group Start helpers ---

    [Fact]
    public void GetBondingGroupStart5GHz_40MHz_ReturnsCorrectStart()
    {
        ChannelSpanHelper.GetBondingGroupStart5GHz(40, 40).Should().Be(36);
        ChannelSpanHelper.GetBondingGroupStart5GHz(153, 40).Should().Be(149);
    }

    [Fact]
    public void GetBondingGroupStart6GHz_40MHz_UsesFormula()
    {
        // Ch 5/40: offset=4, groupIndex=0, start=1
        ChannelSpanHelper.GetBondingGroupStart6GHz(5, 40).Should().Be(1);
        // Ch 9/40: offset=8, groupIndex=1, start=9
        ChannelSpanHelper.GetBondingGroupStart6GHz(9, 40).Should().Be(9);
    }
}
