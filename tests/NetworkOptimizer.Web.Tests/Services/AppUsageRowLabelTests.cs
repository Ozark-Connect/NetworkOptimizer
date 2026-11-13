using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

/// <summary>
/// The Applications list's rows: catalog names for what the catalog knows, "Application N" (a gap
/// that is ours) for what it does not, and UniFi Network's own "Unidentified" for traffic its DPI
/// could not classify at all - each of the last two saying which in its tooltip.
/// </summary>
public class AppUsageRowLabelTests
{
    private static UniFiAppUsage Usage(int category, int application, long rx = 1000, long tx = 100, long activity = 60) => new()
    {
        Category = category,
        Application = application,
        BytesReceived = rx,
        BytesTransmitted = tx,
        ActivitySeconds = activity,
    };

    [Fact]
    public void A_catalog_application_takes_its_name_and_category_with_no_note()
    {
        // 13/7 is Speedtest.net in the embedded catalog.
        var row = ClientDashboardService.BuildAppRows(new[] { Usage(13, 7) }).Single();
        row.Name.Should().Be("Speedtest.net");
        row.Category.Should().Be("Web services");
        row.Note.Should().BeNull();
    }

    [Fact]
    public void Dpi_unidentified_traffic_is_unidentified_with_no_category()
    {
        var row = ClientDashboardService.BuildAppRows(new[] { Usage(255, 65535) }).Single();
        row.Name.Should().Be("Unidentified");
        row.Category.Should().Be("");
        row.Note.Should().Contain("could not identify");
    }

    [Fact]
    public void A_catalog_miss_is_named_by_id_and_keeps_its_real_category()
    {
        var row = ClientDashboardService.BuildAppRows(new[] { Usage(13, 64999) }).Single();
        row.Name.Should().Be("Application 64999");
        row.Category.Should().Be("Web services");
        row.Note.Should().Contain("no name for it yet");
    }

    [Fact]
    public void Two_ids_with_one_name_are_one_row_with_their_totals()
    {
        // 14/32 is "Apple"; 13/84 is the catalog's "Apple.com", folded into "Apple".
        var rows = ClientDashboardService.BuildAppRows(new[]
        {
            Usage(14, 32, rx: 5000, tx: 500, activity: 300),
            Usage(13, 84, rx: 1000, tx: 100, activity: 120),
            Usage(0, 27, rx: 10, tx: 10),   // "Apple iMessage" is its own app, never folded
        });
        rows.Select(r => r.Name).Should().BeEquivalentTo("Apple", "Apple iMessage");
        var apple = rows.Single(r => r.Name == "Apple");
        apple.DownloadBytes.Should().Be(6000);
        apple.UploadBytes.Should().Be(600);
        apple.ActivitySeconds.Should().Be(300, "the two overlap in time");
    }

    [Fact]
    public void Rows_come_largest_first_and_empty_rows_are_dropped()
    {
        var rows = ClientDashboardService.BuildAppRows(new[]
        {
            Usage(13, 7, rx: 10, tx: 0),
            Usage(255, 65535, rx: 900, tx: 100),
            Usage(13, 190, rx: 0, tx: 0),
        });
        rows.Should().HaveCount(2);
        rows[0].Name.Should().Be("Unidentified");
        rows[1].Name.Should().Be("Speedtest.net");
    }
}
