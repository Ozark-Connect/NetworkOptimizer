using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class LoadClassifierTests
{
    private static readonly IspHealthOptions Options = new();

    [Fact]
    public void Classifies_download_load_at_seventy_percent_of_expected()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 700_000_000, 5_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(1);
        var w = windows.Values.Single();
        w.IsLoadedDown.Should().BeTrue();
        w.IsLoadedUp.Should().BeFalse();
        w.IsIdle.Should().BeFalse();
    }

    [Fact]
    public void Classifies_idle_when_both_directions_quiet()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 200_000_000, 20_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(1);
        var w = windows.Values.Single();
        w.IsIdle.Should().BeTrue();
        w.IsLoadedDown.Should().BeFalse();
        w.IsLoadedUp.Should().BeFalse();
    }

    [Fact]
    public void Moderate_load_is_neither_idle_nor_loaded()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 400_000_000, 20_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(1);
        var w = windows.Values.Single();
        w.IsIdle.Should().BeFalse();
        w.IsLoadedDown.Should().BeFalse();
    }

    [Fact]
    public void Both_directions_can_be_loaded_at_once()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 800_000_000, 90_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(1);
        var w = windows.Values.Single();
        w.IsLoadedDown.Should().BeTrue();
        w.IsLoadedUp.Should().BeTrue();
    }

    [Fact]
    public void No_expected_speeds_yields_no_classification()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 800_000_000, 90_000_000)
        };

        LoadClassifier.Classify(rates, null, null, Options).Should().BeEmpty();
    }

    [Fact]
    public void Uses_peak_rate_within_load_window()
    {
        // Both samples within one LoadWindowSeconds span so they group into one bucket
        var ws = Options.LoadWindowSeconds;
        var baseTime = CongestionDetector.FloorTime(TestSeries.Start, TimeSpan.FromSeconds(ws));
        var rates = new List<ThroughputSample>
        {
            new(baseTime, 100_000_000, 1_000_000),
            new(baseTime.AddSeconds(ws - 1), 750_000_000, 1_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(1);
        windows.Values.Single().IsLoadedDown.Should().BeTrue();
    }

    [Fact]
    public void Short_burst_registers_as_loaded_in_its_own_window()
    {
        var ws = Options.LoadWindowSeconds;
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 10_000_000, 1_000_000),
            new(TestSeries.Start.AddSeconds(ws), 900_000_000, 2_000_000),
            new(TestSeries.Start.AddSeconds(ws * 2), 10_000_000, 1_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(3);
        var keys = windows.Keys.OrderBy(k => k).ToList();
        windows[keys[1]].IsLoadedDown.Should().BeTrue();
        windows[keys[0]].IsIdle.Should().BeTrue();
        windows[keys[2]].IsIdle.Should().BeTrue();
    }

    [Fact]
    public void Exclusion_window_forces_non_loaded()
    {
        var ws = Options.LoadWindowSeconds;
        var baseTime = CongestionDetector.FloorTime(TestSeries.Start, TimeSpan.FromSeconds(ws));
        var rates = new List<ThroughputSample>
        {
            new(baseTime, 900_000_000, 90_000_000)
        };
        var exclusions = new List<(DateTime Start, DateTime End)>
        {
            (baseTime, baseTime.AddSeconds(20))
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options, exclusions);

        windows.Should().HaveCount(1);
        var w = windows.Values.Single();
        w.IsLoadedDown.Should().BeFalse();
        w.IsLoadedUp.Should().BeFalse();
        w.IsIdle.Should().BeFalse();
    }
}
