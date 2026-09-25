using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class DashboardLayoutMergeTests
{
    private static DashboardLayout Saved(params string[] cardIds) => new()
    {
        Cards = cardIds.Select(id => new DashboardCardConfig { Id = id }).ToList()
    };

    [Fact]
    public void NewCard_InsertedAfterItsDefaultPredecessor_NotAppended()
    {
        var layout = Saved(DashboardCards.All.Where(id => id != DashboardCards.ActiveAlerts).ToArray());

        DashboardLayoutService.MergeDefaults(layout);

        layout.Cards.Select(c => c.Id).Should().Equal(DashboardCards.All);
    }

    [Fact]
    public void NewCard_FollowsPredecessorInUserOrder()
    {
        // The user moved Quick Stats below Device Status.
        var ids = DashboardCards.All.Where(id => id != DashboardCards.ActiveAlerts && id != DashboardCards.StatsRow).ToList();
        ids.Insert(ids.IndexOf(DashboardCards.DeviceStatus) + 1, DashboardCards.StatsRow);
        var layout = Saved(ids.ToArray());

        DashboardLayoutService.MergeDefaults(layout);

        var order = layout.Cards.Select(c => c.Id).ToList();
        order.IndexOf(DashboardCards.ActiveAlerts).Should().Be(order.IndexOf(DashboardCards.StatsRow) + 1);
    }

    [Fact]
    public void NewCard_NoPredecessorPresent_GoesFirst()
    {
        var layout = Saved(DashboardCards.SecurityPosture);

        DashboardLayoutService.MergeDefaults(layout);

        layout.Cards[0].Id.Should().Be(DashboardCards.StatsRow);
        layout.Cards[1].Id.Should().Be(DashboardCards.ActiveAlerts);
        layout.Cards[2].Id.Should().Be(DashboardCards.SecurityPosture);
    }

    [Fact]
    public void MergedCards_KeepDefaultVisibilityAndWidth()
    {
        var layout = Saved(DashboardCards.StatsRow);

        DashboardLayoutService.MergeDefaults(layout);

        var activeAlerts = layout.Cards.Single(c => c.Id == DashboardCards.ActiveAlerts);
        activeAlerts.Visible.Should().BeTrue();
        activeAlerts.FullWidth.Should().BeFalse();
        var liveView = layout.Cards.Single(c => c.Id == DashboardCards.LiveView);
        liveView.Visible.Should().BeFalse();
        liveView.FullWidth.Should().BeTrue();
    }
}
