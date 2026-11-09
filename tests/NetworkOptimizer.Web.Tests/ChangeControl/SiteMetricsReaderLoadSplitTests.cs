using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The WAN Latency and WAN Loss lanes draw every window on one series; before/after keeps the strict loaded split.</summary>
public class SiteMetricsReaderLoadSplitTests
{
    private static readonly DateTime T = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);

    private static readonly LoadWindow Idle = new(true, false, false);
    private static readonly LoadWindow LoadedDown = new(false, true, false);
    private static readonly LoadWindow LoadedUp = new(false, false, true);
    private static readonly LoadWindow MiddleBand = new(false, false, false);
    // An isolated loaded window after demotion: classified loaded, live flags cleared.
    private static readonly LoadWindow Demoted = new(false, false, false, ClassifiedLoadedDown: true);

    private static DateTime At(int minute) => T.AddMinutes(minute);

    [Theory]
    [InlineData("idle", false)]
    [InlineData("loadedDown", true)]
    [InlineData("loadedUp", true)]
    [InlineData("middleBand", false)]
    [InlineData("sqmExcluded", false)]
    [InlineData("demoted", false)]
    [InlineData("noRateSample", false)]
    public void EachWindow_LandsOnExactlyOneSeries(string kind, bool loaded)
    {
        var byStamp = new Dictionary<DateTime, LoadWindow>();
        var w = kind switch
        {
            "idle" => Idle,
            "loadedDown" => LoadedDown,
            "loadedUp" => LoadedUp,
            "middleBand" or "sqmExcluded" => MiddleBand,
            "demoted" => Demoted,
            _ => null,
        };
        if (w != null) byStamp[T] = w;

        var (idle, load) = SiteMetricsReader.SplitByLoad([(T, 9.4)], byStamp);

        idle.Single().Value.Should().Be(loaded ? null : 9.4);
        load.Single().Value.Should().Be(loaded ? 9.4 : null);
        SiteMetricsReader.OnIdleSeries(byStamp, T).Should().Be(!SiteMetricsReader.OnLoadedSeries(byStamp, T));
    }

    [Fact]
    public void MixedRange_HasNoGap()
    {
        var byStamp = new Dictionary<DateTime, LoadWindow>
        {
            [At(0)] = Idle, [At(2)] = MiddleBand, [At(4)] = LoadedDown, [At(6)] = Demoted,
        };
        var points = new List<(DateTime, double?)> { (At(0), 8.1), (At(2), 8.4), (At(4), 12.0), (At(6), 8.2), (At(8), 8.0) };

        var (idle, load) = SiteMetricsReader.SplitByLoad(points, byStamp);

        idle.Zip(load, (a, b) => a.Value ?? b.Value).Should().Equal(8.1, 8.4, 12.0, 8.2, 8.0);
        idle.Zip(load).Should().OnlyContain(x => x.First.Value == null || x.Second.Value == null);
    }

    [Fact]
    public void AWindowWithNoValue_StaysNullOnBoth()
    {
        var (idle, load) = SiteMetricsReader.SplitByLoad([(T, null)], new Dictionary<DateTime, LoadWindow> { [T] = Idle });

        idle.Single().Value.Should().BeNull();
        load.Single().Value.Should().BeNull();
    }

    [Fact]
    public void BeforeAfter_LoadedLoss_LeavesMiddleBandWindowsOut()
    {
        // The after hour's 14% loss sits on a middle-band window: the lane draws it on idle, the comparison skips it.
        static List<TimelineLane> Lanes(params (int Minute, double Loss, LoadWindow Load)[] windows)
        {
            var byStamp = windows.ToDictionary(w => At(w.Minute), w => w.Load);
            var (idle, load) = SiteMetricsReader.SplitByLoad(windows.Select(w => (At(w.Minute), (double?)w.Loss)).ToList(), byStamp);
            return [new TimelineLane { Key = "loss", SeriesA = idle, SeriesB = load }];
        }
        var before = Lanes((0, 0.5, LoadedDown), (2, 0.0, Idle));
        var after = Lanes((60, 0.5, LoadedDown), (62, 14.0, MiddleBand));

        var loss = SiteMetricsReader.Deltas(before, after).Single(d => d.Label == "Loaded loss");

        loss.Before.Should().Be("0.5");
        loss.After.Should().Be("0.5%");
        loss.Worse.Should().BeFalse();
        after[0].SeriesA.Should().Contain((At(62), 14.0));
    }
}
