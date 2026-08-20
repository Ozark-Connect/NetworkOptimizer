using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The merge rule behind cross-site duration learning: a site that has measured a model for itself
/// keeps its own numbers, and only a site that cannot answer borrows the other sites' pooled window.
/// </summary>
public class CrossSiteTimingSourceTests
{
    private static FirmwareModelTiming Timing(string model, int samples, int median, int p90 = 0) => new()
    {
        Model = model,
        SampleCount = samples,
        MedianDowntimeSeconds = median,
        P90DowntimeSeconds = p90 == 0 ? median + 60 : p90,
        UpdatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Merge_KeepsTheSitesOwnNumbersOnceItHasEnoughSamples()
    {
        var merged = CrossSiteTimingSource.Merge(
            [Timing("U6PRO", samples: 3, median: 200)],
            [Timing("U6PRO", samples: 50, median: 500)]);

        merged.Should().ContainSingle();
        merged[0].MedianDowntimeSeconds.Should().Be(200);
        merged[0].SampleCount.Should().Be(3);
    }

    [Fact]
    public void Merge_BorrowsThePooledWindowWhenTheSiteHasTooLittleHistory()
    {
        var merged = CrossSiteTimingSource.Merge(
            [Timing("U6PRO", samples: 1, median: 900)],
            [Timing("U6PRO", samples: 2, median: 300), Timing("U6PRO", samples: 2, median: 500)]);

        // 2 samples at 300 s and 2 at 500 s pool to 400 s over 4 samples.
        merged.Should().ContainSingle();
        merged[0].MedianDowntimeSeconds.Should().Be(400);
        merged[0].SampleCount.Should().Be(4);
    }

    [Fact]
    public void Merge_WeightsThePoolByHowManyUpgradesEachSiteMeasured()
    {
        var merged = CrossSiteTimingSource.Merge(
            [],
            [Timing("USL24", samples: 9, median: 480), Timing("USL24", samples: 1, median: 900)]);

        merged.Should().ContainSingle();
        merged[0].MedianDowntimeSeconds.Should().Be(522);
    }

    [Fact]
    public void Merge_AddsAModelTheSiteHasNeverUpgraded()
    {
        var merged = CrossSiteTimingSource.Merge(
            [Timing("U6PRO", samples: 5, median: 240)],
            [Timing("USL24", samples: 4, median: 480)]);

        merged.Should().HaveCount(2);
        merged.Should().ContainSingle(t => t.Model == "USL24" && t.MedianDowntimeSeconds == 480);
    }

    [Fact]
    public void Merge_IgnoresAPoolThatIsItselfTooThin()
    {
        var merged = CrossSiteTimingSource.Merge(
            [Timing("U6PRO", samples: 1, median: 900)],
            [Timing("U6PRO", samples: 2, median: 300)]);

        merged.Should().ContainSingle();
        merged[0].MedianDowntimeSeconds.Should().Be(900);
        merged[0].SampleCount.Should().Be(1);
    }

    [Fact]
    public void Merge_LeavesTheEstimatorFreeToUseAPooledRow()
    {
        // The estimator only trusts a learned row at MinLearnedSamples, so a pooled row has to carry
        // the pool's sample count rather than the borrowing site's.
        var merged = CrossSiteTimingSource.Merge([], [Timing("U7PRO", samples: 6, median: 260)]);

        new FirmwareTimingEstimator(merged)
            .EstimateDowntimeSeconds("U7PRO", FirmwareDeviceClass.AccessPoint)
            .Should().Be(260);
    }

    [Fact]
    public void Merge_SkipsRowsThatMeasuredNothing()
    {
        var merged = CrossSiteTimingSource.Merge([], [Timing("U6PRO", samples: 4, median: 0)]);

        merged.Should().BeEmpty();
    }
}
