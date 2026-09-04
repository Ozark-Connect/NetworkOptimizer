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
    public void GetChannelSpan_6GHz_320MHz_GuessesTheLowerBlock()
    {
        // Every primary above 29 is valid in one block of each 320 MHz channelization. Without a
        // measured center the guess is the lower block, which is what UniFi chose in every case
        // measured (primary 69 in 33-93, 5 in 1-61, 165 in 129-189).
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 29, 320).Should().Be((1, 61));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 5, 320).Should().Be((1, 61));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 69, 320).Should().Be((33, 93));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 117, 320).Should().Be((65, 125));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 165, 320).Should().Be((129, 189));
    }

    [Fact]
    public void GetChannelSpan_6GHz_320MHz_NoPrimaryFallsInAGap()
    {
        // The old table (1-61, 97-157, 161-221) mixed the two channelizations and left 62-96
        // uncovered, so primary 69 got a span of 69-129 that exists nowhere.
        for (int primary = 1; primary <= 233; primary += 4)
        {
            var span = ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, primary, 320);
            (span.High - span.Low).Should().Be(60, $"primary {primary} must sit in a full 320 MHz block");
            span.Low.Should().BeLessThanOrEqualTo(primary);
            span.High.Should().BeGreaterThanOrEqualTo(primary);
        }
    }

    [Fact]
    public void GetChannelSpan_WithCenter_UsesTheMeasuredBlock()
    {
        // The three main-site radios as iw dev reported them on 2026-09-01.
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 69, 320, centerChannel: 63).Should().Be((33, 93));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 5, 320, centerChannel: 31).Should().Be((1, 61));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 165, 320, centerChannel: 159).Should().Be((129, 189));

        // A center overrides the guess where the two disagree.
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 69, 320, centerChannel: 95).Should().Be((65, 125));

        // Narrower widths and 5 GHz take the same rule.
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 37, 160, centerChannel: 47).Should().Be((33, 61));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 100, 160, centerChannel: 114).Should().Be((100, 128));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band5GHz, 44, 80, centerChannel: 42).Should().Be((36, 48));
    }

    [Fact]
    public void GetChannelSpan_WithCenter_IgnoresACenterThatDoesNotContainThePrimary()
    {
        // A center from another radio's block is not this radio's; fall back to the guess.
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 5, 320, centerChannel: 159).Should().Be((1, 61));
        // 20 MHz and 2.4 GHz never take a center.
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band6GHz, 5, 20, centerChannel: 5).Should().Be((5, 5));
        ChannelSpanHelper.GetChannelSpan(RadioBand.Band2_4GHz, 6, 40, centerChannel: 8).Should().Be((2, 10));
    }

    [Fact]
    public void GetChannelWidthSpan_WithCenter_ListsTheMeasuredBlock()
    {
        var channels = ChannelSpanHelper.GetChannelWidthSpan(RadioBand.Band6GHz, 69, 320, centerChannel: 63);
        channels.Should().HaveCount(16);
        channels.First().Should().Be(33);
        channels.Last().Should().Be(93);
    }

    [Fact]
    public void CenterChannelFromMhz_ConvertsOnTheBandsGrid()
    {
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band6GHz, 6745).Should().Be(159);
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band6GHz, 6265).Should().Be(63);
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band6GHz, 6105).Should().Be(31);
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band5GHz, 5570).Should().Be(114);
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band2_4GHz, 2462).Should().BeNull();
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band6GHz, 6747).Should().BeNull("off the 5 MHz grid");
        ChannelSpanHelper.CenterChannelFromMhz(RadioBand.Band6GHz, 0).Should().BeNull();
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

    [Fact]
    public void ComputeOverlapFactor_6GHz_320MHz_WithCenters_UsesTheMeasuredBlocks()
    {
        // Main Kitchen (69, block 33-93) and the yard pair (5, block 1-61) share 33-61: partial.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 69, 320, 5, 320, center1: 63, center2: 31)
            .Should().Be(0.7);
        // Main Kitchen and Tiny Home (165, block 129-189) share nothing.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 69, 320, 165, 320, center1: 63, center2: 159)
            .Should().Be(0.0);
        // Same measured block on different primaries is full co-channel.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 37, 320, 69, 320, center1: 63, center2: 63)
            .Should().Be(1.0);
        // One side measured, the other guessed: 69 in the upper block against 37's guessed 1-61.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 69, 320, 37, 320, center1: 95)
            .Should().Be(0.0);
    }

    [Fact]
    public void ComputeOverlapFactor_SamePrimary_DifferentMeasuredBlocks_IsPartial()
    {
        // Primary 37 is valid in both 1-61 and 33-93; two radios that chose differently share
        // 33-61, not the whole channel.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 37, 320, 37, 320, center1: 31, center2: 63)
            .Should().Be(0.7);
        // Same primary with no centers, or matching ones, stays full co-channel.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 37, 320, 37, 320).Should().Be(1.0);
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band6GHz, 37, 320, 37, 320, center1: 63, center2: 63)
            .Should().Be(1.0);
        // Below 320 MHz a primary pins the block, so one measured side changes nothing.
        ChannelSpanHelper.ComputeOverlapFactor(RadioBand.Band5GHz, 36, 80, 36, 80, center1: 42).Should().Be(1.0);
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
