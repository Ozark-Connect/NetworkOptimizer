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

        windows[TestSeries.Start].IsLoadedDown.Should().BeTrue();
        windows[TestSeries.Start].IsLoadedUp.Should().BeFalse();
        windows[TestSeries.Start].IsIdle.Should().BeFalse();
    }

    [Fact]
    public void Classifies_idle_when_both_directions_quiet()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 200_000_000, 20_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows[TestSeries.Start].IsIdle.Should().BeTrue();
        windows[TestSeries.Start].IsLoadedDown.Should().BeFalse();
        windows[TestSeries.Start].IsLoadedUp.Should().BeFalse();
    }

    [Fact]
    public void Moderate_load_is_neither_idle_nor_loaded()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 400_000_000, 20_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows[TestSeries.Start].IsIdle.Should().BeFalse();
        windows[TestSeries.Start].IsLoadedDown.Should().BeFalse();
    }

    [Fact]
    public void Both_directions_can_be_loaded_at_once()
    {
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 800_000_000, 90_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows[TestSeries.Start].IsLoadedDown.Should().BeTrue();
        windows[TestSeries.Start].IsLoadedUp.Should().BeTrue();
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
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 100_000_000, 1_000_000),
            new(TestSeries.Start.AddSeconds(10), 750_000_000, 1_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(1);
        windows[TestSeries.Start].IsLoadedDown.Should().BeTrue();
    }

    [Fact]
    public void Fifteen_second_burst_registers_as_loaded()
    {
        // A short speed-test burst lands in its own 15 s window instead of diluting
        // into a minute-level mean
        var rates = new List<ThroughputSample>
        {
            new(TestSeries.Start, 10_000_000, 1_000_000),
            new(TestSeries.Start.AddSeconds(15), 900_000_000, 2_000_000),
            new(TestSeries.Start.AddSeconds(30), 10_000_000, 1_000_000)
        };

        var windows = LoadClassifier.Classify(rates, expectedDownloadMbps: 1000, expectedUploadMbps: 100, Options);

        windows.Should().HaveCount(3);
        windows[TestSeries.Start.AddSeconds(15)].IsLoadedDown.Should().BeTrue();
        windows[TestSeries.Start].IsIdle.Should().BeTrue();
        windows[TestSeries.Start.AddSeconds(30)].IsIdle.Should().BeTrue();
    }
}
