using FluentAssertions;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// A rollout can be console-only, so alert copy that leads with a device count read as "0 devices
/// across 0 waves" on a real rollout. A UXG-class gateway is an ordinary device step and sets no
/// console flag, so its rollouts must keep reading exactly as they did.
/// </summary>
public class RolloutScopeCopyTests
{
    private static RolloutPlanDocument Doc(bool network = false, bool os = false) => new()
    {
        IncludesUniFiNetworkUpdate = network,
        IncludesUniFiOsUpdate = os,
    };

    [Fact]
    public void AConsoleOnlyRollout_NamesTheConsoleInsteadOfCountingNothing()
    {
        RolloutScopeCopy.Scope(Doc(os: true), devices: 0, waves: 0).Should().Be("UniFi OS");
        RolloutScopeCopy.Scope(Doc(network: true), 0, 0).Should().Be("the UniFi Network application");
        RolloutScopeCopy.Scope(Doc(network: true, os: true), 0, 0)
            .Should().Be("the UniFi Network application and UniFi OS");
    }

    [Fact]
    public void ADeviceOnlyRollout_ReadsExactlyAsItDidBefore()
    {
        // A UXG gateway lands here: an ordinary device step, no console flag.
        RolloutScopeCopy.Scope(Doc(), devices: 1, waves: 1).Should().Be("1 device across 1 wave");
        RolloutScopeCopy.Scope(Doc(), devices: 3, waves: 2).Should().Be("3 devices across 2 waves");
        RolloutScopeCopy.Subject(Doc(), devices: 1).Should().Be("1 device");
        RolloutScopeCopy.Subject(Doc(), devices: 3).Should().Be("3 devices");
    }

    [Fact]
    public void ARolloutCoveringBoth_NamesBoth()
    {
        RolloutScopeCopy.Scope(Doc(os: true), devices: 3, waves: 2)
            .Should().Be("3 devices across 2 waves, plus UniFi OS");
        RolloutScopeCopy.Subject(Doc(network: true, os: true), devices: 2)
            .Should().Be("2 devices and the UniFi Network application and UniFi OS");
    }

    [Fact]
    public void ARolloutCoveringNothing_StillCounts_RatherThanSayingNothingAtAll()
    {
        RolloutScopeCopy.Scope(Doc(), devices: 0, waves: 0).Should().Be("0 devices across 0 waves");
        RolloutScopeCopy.ConsoleSurfaces(Doc()).Should().BeNull();
        RolloutScopeCopy.IncludesConsole(Doc()).Should().BeFalse();
    }

    [Theory]
    [InlineData(0.2, "in under an hour")]
    [InlineData(1, "in about 1 hour")]
    [InlineData(12, "in about 12 hours")]
    [InlineData(23.6, "in about 1 day")]
    [InlineData(25, "in about 1 day and 1 hour")]
    [InlineData(168, "in about 7 days")]
    [InlineData(191, "in about 7 days and 23 hours")]
    public void AWeekOutReadsAsAWeek_NotAsNearlyTwoHundredHours(double hours, string expected)
    {
        RolloutScopeCopy.StartsIn(TimeSpan.FromHours(hours)).Should().Be(expected);
    }

    [Fact]
    public void AStartAlreadyBehindUs_ReadsAsImminentRatherThanNegative()
    {
        RolloutScopeCopy.StartsIn(TimeSpan.FromMinutes(-5)).Should().Be("in under an hour");
    }

    [Fact]
    public void SentenceCapitalizesWithoutTouchingTheRest()
    {
        RolloutScopeCopy.Sentence("the UniFi Network application").Should().Be("The UniFi Network application");
        RolloutScopeCopy.Sentence("UniFi OS").Should().Be("UniFi OS");
        RolloutScopeCopy.Sentence("").Should().BeEmpty();
    }

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    [Fact]
    public void ASiteAnHourAheadOfTheServer_ReadsTheStartInSiteHours()
    {
        var start = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);

        RolloutScopeCopy.SiteAside("America/New_York", start, Zone("America/Chicago"))
            .Should().Be(" (07:00 at-site)");
        RolloutScopeCopy.SiteAside("America/New_York", start, Zone("America/Chicago"),
                new DisplayFormat(ClockStyle.TwelveHour, DateOrder.MonthFirst))
            .Should().Be(" (7:00 AM at-site)");
    }

    [Fact]
    public void ASiteOnTheServersClock_SaysNothing()
    {
        var start = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);

        RolloutScopeCopy.SiteAside("America/Chicago", start, Zone("America/Chicago")).Should().BeEmpty();
    }

    [Fact]
    public void ZonesSharingABaseOffset_DifferOnlyWhileOneIsOnDst()
    {
        // Phoenix keeps MST all year; Denver is on MDT in July, so the clocks differ by an hour.
        var july = new DateTime(2026, 7, 15, 14, 0, 0, DateTimeKind.Utc);
        var january = new DateTime(2026, 1, 15, 14, 0, 0, DateTimeKind.Utc);

        RolloutScopeCopy.SiteAside("America/Phoenix", july, Zone("America/Denver"))
            .Should().Be(" (07:00 at-site)");
        RolloutScopeCopy.SiteAside("America/Phoenix", january, Zone("America/Denver")).Should().BeEmpty();
    }

    [Fact]
    public void AnAlertStart_IsLabeledWithItsZone_AndTheSiteAside()
    {
        var start = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);

        RolloutScopeCopy.AlertStartLabel(start, "America/New_York", Zone("America/Chicago"), DisplayFormat.Fallback)
            .Should().Be("Sun Oct 11, 06:00 CDT (07:00 at-site)");
        RolloutScopeCopy.AlertStartLabel(start, "America/New_York", Zone("America/New_York"), DisplayFormat.Fallback)
            .Should().Be("Sun Oct 11, 07:00 EDT");
    }

    [Fact]
    public void AnAlertStart_FollowsTheNotificationFormat_AsideIncluded()
    {
        var start = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);
        var twelveDayFirst = new DisplayFormat(ClockStyle.TwelveHour, DateOrder.DayFirst);

        RolloutScopeCopy.AlertStartLabel(start, "America/New_York", Zone("America/Chicago"), twelveDayFirst)
            .Should().Be("Sun 11 Oct, 6:00 AM CDT (7:00 AM at-site)");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void AMissingOrUnknownSiteZone_SaysNothing(string? zone)
    {
        RolloutScopeCopy.SiteAside(zone, DateTime.UtcNow, Zone("America/Chicago")).Should().BeEmpty();
    }

    [Fact]
    public void Moved_StatesTheNewStartAndTheOneItReplaced_OnTheNotificationClock()
    {
        var now = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);
        var previous = now.AddDays(2);
        var start = now.AddDays(3);

        var message = RolloutScopeCopy.Moved(Doc(), devices: 3, start, previous, now, Zone("America/Chicago"), DisplayFormat.Fallback);

        message.Should().Be(
            "3 devices will be upgraded starting Wed Oct 14, 06:00 CDT, in about 3 days. "
            + "The start moved from Tue Oct 13, 06:00 CDT. Open Firmware Rollout to postpone or stop it.");
    }

    [Fact]
    public void Moved_CarriesTheSiteAsideWhenTheSiteKeepsOtherHours()
    {
        var now = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);
        var doc = Doc();
        doc.TimeZoneId = "America/New_York";

        RolloutScopeCopy.Moved(doc, devices: 1, now.AddDays(1), now, now, Zone("America/Chicago"), DisplayFormat.Fallback)
            .Should().Contain("Mon Oct 12, 06:00 CDT (07:00 at-site)")
            .And.Contain("moved from Sun Oct 11, 06:00 CDT (07:00 at-site)");
    }

    [Fact]
    public void Moved_ForAConsoleOnlyRollout_NamesTheConsole()
    {
        var now = new DateTime(2026, 10, 11, 11, 0, 0, DateTimeKind.Utc);

        RolloutScopeCopy.Moved(Doc(os: true), devices: 0, now.AddHours(5), now.AddHours(2), now, Zone("America/Chicago"), DisplayFormat.Fallback)
            .Should().StartWith("UniFi OS will be upgraded starting ");
    }
}
