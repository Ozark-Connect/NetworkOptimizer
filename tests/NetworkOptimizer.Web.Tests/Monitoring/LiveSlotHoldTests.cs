using FluentAssertions;
using NetworkOptimizer.Web.Components.Shared.Monitoring.LiveTraffic;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class LiveSlotHoldTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Grows_at_once_and_holds_through_a_short_dip()
    {
        var hold = new LiveSlotHold();
        hold.Rows("q", 4, T0).Should().Be(4);
        hold.Rows("q", 9, T0.AddSeconds(2)).Should().Be(9);
        hold.Rows("q", 5, T0.AddSeconds(4)).Should().Be(9);
        hold.Rows("q", 9, T0.AddSeconds(20)).Should().Be(9);
        hold.Rows("q", 5, T0.AddSeconds(40)).Should().Be(9, "the dip restarted when the rows came back");
    }

    [Fact]
    public void Settles_to_fewer_rows_once_they_have_held()
    {
        var hold = new LiveSlotHold();
        hold.Rows("q", 9, T0);
        hold.Rows("q", 5, T0.AddSeconds(2)).Should().Be(9);
        hold.Rows("q", 6, T0.AddSeconds(31)).Should().Be(9);
        hold.Rows("q", 6, T0.AddSeconds(32)).Should().Be(6, "fewer than the most for 30 s settles in one step");
    }

    [Fact]
    public void A_new_query_starts_over_at_once()
    {
        var hold = new LiveSlotHold();
        hold.Rows("q", 9, T0);
        hold.Rows("other", 3, T0.AddSeconds(1)).Should().Be(3);
    }
}
