using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class DisplayTimeZoneTests
{
    private const string Chicago = "America/Chicago";
    private const string Tokyo = "Asia/Tokyo";

    private static DisplayTimeZone Build(string? own, string? inherited)
    {
        var source = new Mock<ITimeZonePreferenceSource>();
        source.Setup(s => s.GetOwnAsync()).ReturnsAsync(own);
        source.Setup(s => s.GetInheritedAsync()).ReturnsAsync(inherited);
        return new DisplayTimeZone(source.Object, NullLogger<DisplayTimeZone>.Instance);
    }

    [Fact]
    public async Task Nothing_set_reads_the_server_zone()
    {
        var display = Build(null, null);
        await display.ResolveAsync();

        display.Zone.Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task Own_choice_wins_over_the_inherited_zone()
    {
        var display = Build(Tokyo, Chicago);
        await display.ResolveAsync();

        display.ZoneId.Should().Be(Tokyo);
    }

    [Fact]
    public async Task No_own_choice_reads_the_inherited_zone()
    {
        var display = Build(null, Chicago);
        await display.ResolveAsync();

        display.ZoneId.Should().Be(Chicago);
    }

    [Fact]
    public async Task An_unknown_id_falls_through_to_the_next_layer()
    {
        var display = Build("Not/AZone", Chicago);
        await display.ResolveAsync();

        display.ZoneId.Should().Be(Chicago);
    }

    [Fact]
    public async Task Unspecified_kind_is_read_as_utc()
    {
        var display = Build(Tokyo, null);
        await display.ResolveAsync();

        // EF returns SQLite timestamps as Unspecified; they are stored as UTC.
        var stored = new DateTime(2026, 1, 15, 3, 0, 0, DateTimeKind.Unspecified);

        display.ToLocal(stored).Should().Be(new DateTime(2026, 1, 15, 12, 0, 0));
    }

    [Fact]
    public async Task Entered_time_round_trips_through_utc()
    {
        var display = Build(Chicago, null);
        await display.ResolveAsync();

        var utc = display.ToUtc(new DateTime(2026, 7, 4, 9, 30, 0));

        utc.Should().Be(new DateTime(2026, 7, 4, 14, 30, 0, DateTimeKind.Utc));
        display.ToLocal(utc).Should().Be(new DateTime(2026, 7, 4, 9, 30, 0));
    }

    [Fact]
    public async Task Entered_time_in_the_spring_forward_gap_moves_past_it()
    {
        var display = Build(Chicago, null);
        await display.ResolveAsync();

        // 2:30 AM does not exist in Chicago on 2026-03-08; 3:00 AM CDT is 08:00 UTC.
        var utc = display.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0));

        utc.Should().Be(new DateTime(2026, 3, 8, 8, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Relative_time_past_a_week_shows_the_date_on_the_given_clock()
    {
        TimeZoneCatalog.TryResolve(Tokyo, out var tokyo).Should().BeTrue();
        var lateEvening = new DateTime(2026, 1, 15, 20, 0, 0, DateTimeKind.Utc);

        TimeFormatHelper.FormatRelativeTime(lateEvening, tokyo!).Should().Be("Jan 16, 2026");
    }

    [Fact]
    public void Catalog_offers_utc_first_and_only_iana_ids()
    {
        TimeZoneCatalog.Options[0].Id.Should().Be(TimeZoneCatalog.UtcId);
        TimeZoneCatalog.Options.Should().Contain(o => o.Id == Chicago && o.Region == "America" && o.Name == "Chicago");
        TimeZoneCatalog.Options.Should().OnlyContain(o => o.Id.Contains('/'));
    }

    [Fact]
    public void Every_catalog_id_resolves()
    {
        TimeZoneCatalog.Options.Where(o => !TimeZoneCatalog.TryResolve(o.Id, out _)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/localtime")]
    [InlineData("America/../America/Chicago")]
    [InlineData("America/Chicago\0")]
    [InlineData("America\\Chicago")]
    public void A_path_shaped_id_is_rejected(string id)
    {
        TimeZoneCatalog.TryResolve(id, out _).Should().BeFalse();
    }

    /// <summary>
    /// Razor renders for a person, so a time it shows is on that person's clock. The parameterless
    /// BCL conversions read the server's zone, which on a hosted install is nobody's.
    /// </summary>
    [Fact]
    public void Components_do_not_read_the_server_clock()
    {
        // Billing cycles are computed on the server's clock (WanDataUsageService), and the Adaptive
        // SQM schedule runs on the gateway's; both move to the site clock, not the viewer's.
        var allowed = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Alerts.razor"] = 3,
            ["Sqm.razor"] = 1,
        };
        var serverClock = new Regex(@"\.ToLocalTime\(\)|TimeZoneInfo\.Local\b|DateTime\.(Now|Today)\b");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(FindWebProjectRoot(), "Components"), "*.razor", SearchOption.AllDirectories)
            .Select(path => (Name: Path.GetFileName(path), Count: serverClock.Matches(File.ReadAllText(path)).Count))
            .Where(f => f.Count > allowed.GetValueOrDefault(f.Name))
            .Select(f => $"{f.Name}: {f.Count}")
            .ToList();

        offenders.Should().BeEmpty("a component shows times on the viewer's clock - inject IDisplayTimeZone");
    }

    private static string FindWebProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "NetworkOptimizer.Web");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate src/NetworkOptimizer.Web from the test output.");
    }
}
