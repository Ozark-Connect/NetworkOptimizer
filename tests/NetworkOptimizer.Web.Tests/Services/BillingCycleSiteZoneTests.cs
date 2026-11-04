using FluentAssertions;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class BillingCycleSiteZoneTests
{
    private static readonly TimeZoneInfo Denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

    [Fact]
    public void A_cycle_starts_at_the_site_midnight()
    {
        // 03:00 UTC on Nov 15 is still Nov 14 in Denver: the cycle that started Oct 15 there.
        var now = new DateTime(2026, 11, 15, 3, 0, 0, DateTimeKind.Utc);

        var (start, end) = WanDataUsageService.GetBillingCycleDates(15, now, Denver);

        start.Should().Be(new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc)); // Oct 15 00:00 MDT
        end.Should().Be(new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc));   // Nov 14 00:00 MST
    }

    [Fact]
    public void The_same_cycle_read_on_two_clocks_is_one_cycle()
    {
        var onSite = new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
        var onUtcServer = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);

        WanDataUsageService.SameCycle(onSite, onUtcServer).Should().BeTrue();
        WanDataUsageService.SameCycle(onSite, onSite.AddMonths(1)).Should().BeFalse();
    }

    [Fact]
    public void A_boundary_on_the_site_midnight_reads_on_the_site_date()
    {
        var tokyoMidnight = new DateTime(2026, 10, 31, 15, 0, 0, DateTimeKind.Utc); // Nov 1 00:00 JST

        WanDataUsageService.BillingDate(tokyoMidnight, Tokyo).Should().Be(new DateTime(2026, 11, 1));
    }

    [Fact]
    public void A_row_recorded_on_the_server_midnight_keeps_its_date()
    {
        // Rows from before cycles moved to the site's zone sit on the server's midnight.
        var serverMidnight = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, 1), TimeZoneInfo.Local);
        var site = TimeZoneInfo.Local.BaseUtcOffset == Tokyo.BaseUtcOffset ? Denver : Tokyo;

        WanDataUsageService.BillingDate(serverMidnight, site).Should().Be(new DateTime(2026, 11, 1));
    }
}
