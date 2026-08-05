using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// A load window is seven seconds; an episode is however long the line actually stayed loaded.
/// Grouping by window made "the newest three" mean the last twenty seconds, so any brief lull
/// inside one bad evening read as a line that had been fixed.
/// </summary>
public class LoadEpisodeTests
{
    private const int WindowSeconds = 7;

    private static DateTime W(int index) => new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        .AddSeconds(index * WindowSeconds);

    [Fact]
    public void Consecutive_windows_share_one_episode_start()
    {
        var starts = SeriesStats.LoadEpisodeStarts(new[] { W(0), W(1), W(2) }, WindowSeconds);

        starts.Values.Should().AllBeEquivalentTo(W(0));
    }

    [Fact]
    public void A_gap_begins_a_new_episode()
    {
        var starts = SeriesStats.LoadEpisodeStarts(new[] { W(0), W(1), W(9), W(10) }, WindowSeconds);

        starts[W(0)].Should().Be(W(0));
        starts[W(1)].Should().Be(W(0));
        starts[W(9)].Should().Be(W(9));
        starts[W(10)].Should().Be(W(9));
        starts.Values.Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void A_long_saturation_is_one_episode_not_many()
    {
        // Five minutes of continuous load: one event, however many windows it spans.
        var windows = Enumerable.Range(0, 43).Select(W).ToArray();

        var starts = SeriesStats.LoadEpisodeStarts(windows, WindowSeconds);

        starts.Values.Distinct().Should().ContainSingle();
        SeriesStats.LoadEpisodeSeconds(windows, WindowSeconds).Values.Should().AllBeEquivalentTo(43 * 7.0);
    }

    [Fact]
    public void Unordered_input_still_groups_correctly()
    {
        var starts = SeriesStats.LoadEpisodeStarts(new[] { W(10), W(1), W(9), W(0) }, WindowSeconds);

        starts[W(1)].Should().Be(W(0));
        starts[W(10)].Should().Be(W(9));
    }

    [Fact]
    public void Nothing_loaded_is_an_empty_map_rather_than_a_throw()
    {
        SeriesStats.LoadEpisodeStarts(Array.Empty<DateTime>(), WindowSeconds).Should().BeEmpty();
    }
}
