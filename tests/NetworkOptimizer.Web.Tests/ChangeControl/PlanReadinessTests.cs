using FluentAssertions;
using NetworkOptimizer.Web.Services.ChangeControl;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ChangeControl;

/// <summary>The commit-confirmed watches follow the items still ticked.</summary>
public class PlanReadinessTests
{
    private static PlanDocument Plan() => new()
    {
        WatchDevices = true,
        WatchInternet = true,
        Operations =
        [
            new PlanOperation { Key = "port", Collection = "device", ManagementPath = true },
            new PlanOperation { Key = "dns", Collection = "static_dns", WanPath = true },
            new PlanOperation { Key = "wlan", Collection = "wlanconf" },
        ],
    };

    [Fact]
    public void Watches_NothingUnticked_KeepsBoth() =>
        PlanReadiness.Watches(Plan(), new HashSet<string>()).Should().Be((true, true));

    [Fact]
    public void Watches_WanItemUnticked_DropsTheInternetWatch() =>
        PlanReadiness.Watches(Plan(), new HashSet<string> { "dns" }).Should().Be((true, false));

    [Fact]
    public void Watches_OnlyAPlainItemTicked_WatchesNothing() =>
        PlanReadiness.Watches(Plan(), new HashSet<string> { "port", "dns" }).Should().Be((false, false));

    [Fact]
    public void Watches_NeverAddsAWatchThePlanDidNotHave()
    {
        var doc = Plan();
        doc.WatchInternet = false;
        PlanReadiness.Watches(doc, new HashSet<string> { "wlan" }).Should().Be((true, false));
    }

    [Fact]
    public void Exclude_ClearsTheWatchesOfWhatLeft()
    {
        var doc = Plan();
        PlanReadiness.Exclude(doc, new HashSet<string> { "port", "dns" });

        doc.Operations.Select(o => o.Key).Should().Equal("wlan");
        doc.WatchDevices.Should().BeFalse();
        doc.WatchInternet.Should().BeFalse();
    }
}
