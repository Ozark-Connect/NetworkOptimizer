using FluentAssertions;
using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class ChannelMemoryHelperTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    private static ChannelChangeEvent Change(DateTimeOffset at, int from, int to) => new()
    {
        Timestamp = at,
        ApMac = "aa:bb:cc:dd:ee:01",
        Band = RadioBand.Band2_4GHz,
        PreviousChannel = from,
        NewChannel = to
    };

    // --- GetChannelAtTime ---

    [Fact]
    public void GetChannelAtTime_NoEvents_ReturnsCurrentChannel()
    {
        var channel = ChannelMemoryHelper.GetChannelAtTime(Now, new List<ChannelChangeEvent>(), 6);
        channel.Should().Be(6);
    }

    [Fact]
    public void GetChannelAtTime_BeforeFirstEvent_ReturnsFirstEventPreviousChannel()
    {
        var events = new List<ChannelChangeEvent> { Change(Now.AddDays(-1), from: 1, to: 6) };

        var channel = ChannelMemoryHelper.GetChannelAtTime(Now.AddDays(-2), events, 6);

        channel.Should().Be(1);
    }

    [Fact]
    public void GetChannelAtTime_BetweenEvents_ReturnsChannelLiveAtThatTime()
    {
        var events = new List<ChannelChangeEvent>
        {
            Change(Now.AddDays(-5), from: 1, to: 6),
            Change(Now.AddDays(-2), from: 6, to: 11)
        };

        ChannelMemoryHelper.GetChannelAtTime(Now.AddDays(-3), events, 11).Should().Be(6);
        ChannelMemoryHelper.GetChannelAtTime(Now.AddDays(-1), events, 11).Should().Be(11);
    }

    // --- BuildSoakInfo ---

    [Fact]
    public void BuildSoakInfo_RecentChange_SoaksPreviousChannel()
    {
        var changedAt = Now - ChannelMemoryHelper.SoakPeriod / 2;
        var events = new[] { Change(changedAt, from: 1, to: 6) };

        var soak = ChannelMemoryHelper.BuildSoakInfo(events, currentChannel: 6, Now);

        soak.Should().NotBeNull();
        soak!.SoakedChannels.Should().BeEquivalentTo(new[] { 1 });
        soak.LastChangeAt.Should().Be(changedAt);
        soak.SoakEndsAt.Should().Be(changedAt + ChannelMemoryHelper.SoakPeriod);
    }

    [Fact]
    public void BuildSoakInfo_ChangeOlderThanSoakPeriod_ReturnsNull()
    {
        var events = new[] { Change(Now - ChannelMemoryHelper.SoakPeriod - TimeSpan.FromHours(1), from: 1, to: 6) };

        ChannelMemoryHelper.BuildSoakInfo(events, currentChannel: 6, Now).Should().BeNull();
    }

    [Fact]
    public void BuildSoakInfo_PreviousChannelIsCurrentAgain_NotSoaked()
    {
        // Hopped 6 -> 1 -> 6: channel 6 was "left" mid-window but the radio is back on it now.
        var events = new[]
        {
            Change(Now - ChannelMemoryHelper.SoakPeriod * 0.75, from: 6, to: 1),
            Change(Now - ChannelMemoryHelper.SoakPeriod * 0.25, from: 1, to: 6)
        };

        var soak = ChannelMemoryHelper.BuildSoakInfo(events, currentChannel: 6, Now);

        soak.Should().NotBeNull();
        soak!.SoakedChannels.Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void BuildSoakInfo_DuplicateEventsFromTwoSources_Tolerated()
    {
        // The same change can arrive from both the UniFi system log and the persisted change log.
        var events = new[]
        {
            Change(Now - ChannelMemoryHelper.SoakPeriod / 2, from: 1, to: 6),
            Change(Now - ChannelMemoryHelper.SoakPeriod / 2, from: 1, to: 6)
        };

        var soak = ChannelMemoryHelper.BuildSoakInfo(events, currentChannel: 6, Now);

        soak!.SoakedChannels.Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void BuildSoakInfo_UnknownPreviousChannel_Ignored()
    {
        var events = new[] { Change(Now - ChannelMemoryHelper.SoakPeriod / 2, from: 0, to: 6) };

        ChannelMemoryHelper.BuildSoakInfo(events, currentChannel: 6, Now).Should().BeNull();
    }

    // --- MergeLongTermOutcomes ---

    private static ChannelOutcomeBucket Bucket(
        int channel, int width, int samples, double util, double interf, double txRetry,
        DateTimeOffset? lastSampleAt = null) => new(
            channel, width,
            UtilizationSum: util * samples,
            InterferenceSum: interf * samples,
            TxRetrySum: txRetry * samples,
            SampleCount: samples,
            LastSampleAt: lastSampleAt ?? Now.AddDays(-10));

    [Fact]
    public void MergeLongTermOutcomes_FillsChannelMissingFromRecent()
    {
        var recent = new Dictionary<int, (double, double, double)> { [6] = (30, 20, 10) };
        var buckets = new[] { Bucket(1, 20, samples: 24, util: 50, interf: 40, txRetry: 15) };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(recent, buckets, currentWidthMhz: 20, Now);

        merged.Should().NotBeNull();
        merged![6].Should().Be((30d, 20d, 10d));
        // Decay scales weight and sums equally, so a single bucket's averages are unchanged
        merged[1].Utilization.Should().BeApproximately(50, 0.001);
        merged[1].Interference.Should().BeApproximately(40, 0.001);
        merged[1].TxRetryPct.Should().BeApproximately(15, 0.001);
    }

    [Fact]
    public void MergeLongTermOutcomes_RecentDataWins()
    {
        var recent = new Dictionary<int, (double, double, double)> { [1] = (10, 10, 10) };
        var buckets = new[] { Bucket(1, 20, samples: 100, util: 90, interf: 90, txRetry: 90) };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(recent, buckets, currentWidthMhz: 20, Now);

        merged![1].Should().Be((10d, 10d, 10d));
    }

    [Fact]
    public void MergeLongTermOutcomes_BelowMinSamples_Ignored()
    {
        var buckets = new[] { Bucket(1, 20, samples: ChannelMemoryHelper.MinLongTermSamples - 1, util: 50, interf: 40, txRetry: 15) };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(null, buckets, currentWidthMhz: 20, Now);

        merged.Should().BeNull();
    }

    [Fact]
    public void MergeLongTermOutcomes_OtherWidthExcluded_UnknownWidthIncluded()
    {
        var buckets = new[]
        {
            Bucket(1, 40, samples: 100, util: 90, interf: 90, txRetry: 90),
            Bucket(11, 0, samples: 24, util: 30, interf: 20, txRetry: 5)
        };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(null, buckets, currentWidthMhz: 20, Now);

        merged.Should().NotBeNull();
        merged!.Should().NotContainKey(1);
        merged.Should().ContainKey(11);
    }

    [Fact]
    public void MergeLongTermOutcomes_MultipleBuckets_WeightedAverage()
    {
        var buckets = new[]
        {
            Bucket(1, 20, samples: 10, util: 20, interf: 10, txRetry: 0),
            Bucket(1, 20, samples: 30, util: 60, interf: 50, txRetry: 20)
        };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(null, buckets, currentWidthMhz: 20, Now);

        // Same age, so decay cancels: (20*10 + 60*30) / 40 = 50; (10*10 + 50*30) / 40 = 40;
        // (0*10 + 20*30) / 40 = 15
        merged![1].Utilization.Should().BeApproximately(50, 0.001);
        merged[1].Interference.Should().BeApproximately(40, 0.001);
        merged[1].TxRetryPct.Should().BeApproximately(15, 0.001);
    }

    [Fact]
    public void MergeLongTermOutcomes_HalfLifeDecay_TiltsAverageTowardNewerEvidence()
    {
        // Equal sample counts, one bucket exactly one half-life older: its weight is halved,
        // so the average lands at (30*1.0 + 90*0.5) / 1.5 = 50, not the flat midpoint 60.
        var buckets = new[]
        {
            Bucket(1, 20, samples: 24, util: 30, interf: 30, txRetry: 30, lastSampleAt: Now),
            Bucket(1, 20, samples: 24, util: 90, interf: 90, txRetry: 90,
                lastSampleAt: Now - ChannelMemoryHelper.OutcomeHalfLife)
        };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(null, buckets, currentWidthMhz: 20, Now);

        merged![1].Utilization.Should().BeApproximately(50, 0.001);
        merged[1].Interference.Should().BeApproximately(50, 0.001);
        merged[1].TxRetryPct.Should().BeApproximately(50, 0.001);
    }

    [Fact]
    public void MergeLongTermOutcomes_FullyAgedEvidence_RevertsToUnknown()
    {
        // A day's worth of samples from ~three half-lives ago decays to ~3 effective samples,
        // below the minimum gate - stale memory must not speak with full authority.
        var buckets = new[]
        {
            Bucket(1, 20, samples: 24, util: 50, interf: 40, txRetry: 15,
                lastSampleAt: Now.AddDays(-170))
        };

        var merged = ChannelMemoryHelper.MergeLongTermOutcomes(null, buckets, currentWidthMhz: 20, Now);

        merged.Should().BeNull();
    }

    [Fact]
    public void MergeLongTermOutcomes_NoData_ReturnsNull()
    {
        ChannelMemoryHelper.MergeLongTermOutcomes(null, Array.Empty<ChannelOutcomeBucket>(), 20, Now)
            .Should().BeNull();
    }

    // --- MergeRememberedNeighbors ---

    private static ChannelScanResult Scan(
        string apMac, params NeighborNetwork[] neighbors) => new()
    {
        ApMac = apMac,
        Band = RadioBand.Band2_4GHz,
        ScanTime = Now,
        Neighbors = neighbors.ToList()
    };

    private static NeighborNetwork LiveNeighbor(string bssid, int channel, int signal = -70) => new()
    {
        Bssid = bssid,
        Channel = channel,
        Signal = signal
    };

    private static RememberedNeighborSighting Remembered(
        string apMac, string bssid, int channel, DateTimeOffset lastSeen, int signal = -65,
        int sightingCount = 10) => new(
            apMac, RadioBand.Band2_4GHz, bssid, channel, WidthMhz: 20, signal, sightingCount, lastSeen, Ssid: "Net");

    [Fact]
    public void MergeRememberedNeighbors_AddsUnseenNeighbor_WithDecayedConfidence()
    {
        var scans = new List<ChannelScanResult> { Scan("aa:bb:cc:dd:ee:01") };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 11,
                lastSeen: Now - ChannelMemoryHelper.NeighborHalfLife)
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        var nb = merged.Single().Neighbors.Single();
        nb.Bssid.Should().Be("11:22:33:44:55:66");
        nb.Channel.Should().Be(11);
        nb.Confidence.Should().BeApproximately(0.5, 0.001, "one half-life halves the confidence");
        scans[0].Neighbors.Should().BeEmpty("the input scan must not be mutated");
    }

    [Fact]
    public void MergeRememberedNeighbors_TransientSighting_ScaledDownByPersistence()
    {
        // Seen only once (a one-off): confidence is scaled by 1/MinSightings even though the
        // sighting is fresh, so a transient neighbor can't accumulate into full phantom load.
        var scans = new List<ChannelScanResult> { Scan("aa:bb:cc:dd:ee:01") };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 11,
                lastSeen: Now, sightingCount: 1)
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        var expected = 1.0 / ChannelMemoryHelper.MinNeighborSightingsForFullWeight;
        merged.Single().Neighbors.Single().Confidence.Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void MergeRememberedNeighbors_DurableSighting_FullPersistence()
    {
        // Seen well past the full-weight threshold and fresh: confidence is the age decay
        // (1.0) with no persistence penalty.
        var scans = new List<ChannelScanResult> { Scan("aa:bb:cc:dd:ee:01") };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 11,
                lastSeen: Now, sightingCount: ChannelMemoryHelper.MinNeighborSightingsForFullWeight + 5)
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        merged.Single().Neighbors.Single().Confidence.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void MergeRememberedNeighbors_TransientAndAged_DroppedBelowFloor()
    {
        // One-off (persistence 1/3) AND two age half-lives old (0.25): combined confidence
        // 0.083 falls below the 0.125 floor, so a stale transient is dropped entirely.
        var scans = new List<ChannelScanResult> { Scan("aa:bb:cc:dd:ee:01") };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 11,
                lastSeen: Now - ChannelMemoryHelper.NeighborHalfLife * 2, sightingCount: 1)
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        merged.Single().Neighbors.Should().BeEmpty();
    }

    [Fact]
    public void MergeRememberedNeighbors_FullyAgedSighting_Dropped()
    {
        var scans = new List<ChannelScanResult> { Scan("aa:bb:cc:dd:ee:01") };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 11,
                lastSeen: Now - ChannelMemoryHelper.NeighborMemoryWindow - TimeSpan.FromDays(1))
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        merged.Single().Neighbors.Should().BeEmpty();
    }

    [Fact]
    public void MergeRememberedNeighbors_BssidLiveOnDifferentChannel_Suppressed()
    {
        // Another AP currently sees the BSSID on ch1 - it moved, so the remembered ch11 row
        // is obsolete for everyone.
        var scans = new List<ChannelScanResult>
        {
            Scan("aa:bb:cc:dd:ee:01"),
            Scan("aa:bb:cc:dd:ee:02", LiveNeighbor("11:22:33:44:55:66", channel: 1))
        };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 11, lastSeen: Now.AddDays(-2))
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        merged[0].Neighbors.Should().BeEmpty();
    }

    [Fact]
    public void MergeRememberedNeighbors_BssidLiveOnSameChannel_KeptForOtherObserver()
    {
        // AP2 sees the neighbor live on ch6; AP1 can't currently hear it but remembers it on
        // the same channel - AP1's own vantage is kept (triangulation would understate it),
        // while a remembered sighting for AP2 itself is suppressed (its live sighting wins).
        var scans = new List<ChannelScanResult>
        {
            Scan("aa:bb:cc:dd:ee:01"),
            Scan("aa:bb:cc:dd:ee:02", LiveNeighbor("11:22:33:44:55:66", channel: 6))
        };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:01", "11:22:33:44:55:66", channel: 6, lastSeen: Now.AddDays(-2)),
            Remembered("aa:bb:cc:dd:ee:02", "11:22:33:44:55:66", channel: 6, lastSeen: Now.AddDays(-2))
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        merged[0].Neighbors.Should().ContainSingle()
            .Which.Confidence.Should().BeLessThan(1.0);
        merged[1].Neighbors.Should().ContainSingle("only the live sighting remains")
            .Which.Confidence.Should().Be(1.0);
    }

    [Fact]
    public void MergeRememberedNeighbors_NoScanEntryForRadio_SightingIgnored()
    {
        // No synthetic scan entries: the engine must not believe it has scan data it doesn't.
        var scans = new List<ChannelScanResult> { Scan("aa:bb:cc:dd:ee:01") };
        var remembered = new[]
        {
            Remembered("aa:bb:cc:dd:ee:99", "11:22:33:44:55:66", channel: 6, lastSeen: Now.AddDays(-1))
        };

        var merged = ChannelMemoryHelper.MergeRememberedNeighbors(scans, remembered, Now);

        merged.Should().HaveCount(1);
        merged[0].Neighbors.Should().BeEmpty();
    }
}
