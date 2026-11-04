using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services;

public class NotificationTimeZoneResolverTests
{
    private const string Chicago = "America/Chicago";
    private const string Tokyo = "Asia/Tokyo";

    private int _consoleReads;

    private NotificationTimeZoneResolver Build(
        string? installDefault, Func<string, CancellationToken, Task<string?>>? readConsoleZone,
        (string?, string?) installFormat = default, bool formatReadThrows = false)
    {
        var source = new Mock<ITimeZonePreferenceSource>();
        source.Setup(s => s.GetInheritedAsync()).ReturnsAsync(installDefault);
        if (formatReadThrows)
            source.Setup(s => s.GetInheritedFormatAsync()).ThrowsAsync(new InvalidOperationException("settings unavailable"));
        else
            source.Setup(s => s.GetInheritedFormatAsync()).ReturnsAsync(installFormat);
        var services = new ServiceCollection().AddScoped(_ => source.Object).BuildServiceProvider();
        return new NotificationTimeZoneResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            BuildSiteZones(readConsoleZone),
            NullLogger<NotificationTimeZoneResolver>.Instance);
    }

    private SiteTimeZones BuildSiteZones(Func<string, CancellationToken, Task<string?>>? readConsoleZone) =>
        new(slug => readConsoleZone == null ? null : ct =>
            {
                _consoleReads++;
                return readConsoleZone(slug, ct);
            },
            NullLogger<SiteTimeZones>.Instance);

    private NotificationTimeZoneResolver Build(string? installDefault, Func<string, string?> consoleZone) =>
        Build(installDefault, (slug, _) => Task.FromResult(consoleZone(slug)));

    [Fact]
    public async Task With_no_install_default_format_alerts_read_the_fallback()
    {
        (await Build(null, readConsoleZone: null).ResolveFormatAsync()).Should().Be(DisplayFormat.Fallback);
    }

    [Fact]
    public async Task The_install_default_format_sets_the_notification_format_half_by_half()
    {
        (await Build(null, readConsoleZone: null, installFormat: ("12", "dmy")).ResolveFormatAsync())
            .Should().Be(new DisplayFormat(ClockStyle.TwelveHour, DateOrder.DayFirst));
        (await Build(null, readConsoleZone: null, installFormat: ("12", null)).ResolveFormatAsync())
            .Should().Be(new DisplayFormat(ClockStyle.TwelveHour, DisplayFormat.Fallback.Order));
    }

    [Fact]
    public async Task A_failed_format_read_falls_back_rather_than_throwing()
    {
        (await Build(null, readConsoleZone: null, formatReadThrows: true).ResolveFormatAsync()).Should().Be(DisplayFormat.Fallback);
    }

    [Fact]
    public async Task The_site_console_zone_wins_over_the_install_default()
    {
        var resolver = Build(Tokyo, _ => Chicago);

        (await resolver.ResolveAsync("branch")).Id.Should().Be(Chicago);
    }

    [Fact]
    public async Task Each_site_reads_its_own_console_zone()
    {
        var resolver = Build(null, slug => slug == "branch" ? Tokyo : Chicago);

        (await resolver.ResolveAsync("branch")).Id.Should().Be(Tokyo);
        (await resolver.ResolveAsync(null)).Id.Should().Be(Chicago);
    }

    [Fact]
    public async Task A_console_with_no_zone_reads_the_install_default()
    {
        (await Build(Tokyo, _ => null).ResolveAsync("branch")).Id.Should().Be(Tokyo);
        (await Build(Tokyo, readConsoleZone: null).ResolveAsync("branch")).Id.Should().Be(Tokyo);
    }

    [Fact]
    public async Task A_cached_read_never_waits_and_fills_for_the_next_caller()
    {
        var release = new TaskCompletionSource<string?>();
        var zones = BuildSiteZones((_, _) => release.Task);

        zones.Cached("branch").Should().BeNull();
        release.SetResult(Chicago);
        await zones.GetAsync("branch"); // joins the read Cached started

        zones.Cached("branch")!.Id.Should().Be(Chicago);
        _consoleReads.Should().Be(1);
    }

    [Fact]
    public async Task The_console_zone_is_cached_per_site()
    {
        var resolver = Build(null, _ => Chicago);

        await resolver.ResolveAsync("branch");
        await resolver.ResolveAsync("branch");

        _consoleReads.Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Not/AZone")]
    public async Task No_usable_zone_anywhere_reads_the_server_zone(string? consoleZone)
    {
        var resolver = Build(null, _ => consoleZone);

        (await resolver.ResolveAsync("branch")).Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task A_site_with_no_console_yet_reads_the_server_zone_and_is_asked_again()
    {
        var resolver = Build(null, readConsoleZone: null);

        (await resolver.ResolveAsync("branch")).Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task A_console_that_hangs_falls_back_within_the_read_timeout()
    {
        var resolver = Build(null, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Chicago;
        });

        var resolve = resolver.ResolveAsync("branch");

        (await resolve.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task A_burst_of_alerts_shares_one_console_read()
    {
        var release = new TaskCompletionSource<string?>();
        var resolver = Build(null, (_, _) => release.Task);

        var burst = Enumerable.Range(0, 20).Select(_ => resolver.ResolveAsync("branch")).ToList();
        release.SetResult(Chicago);

        (await Task.WhenAll(burst)).Should().OnlyContain(z => z.Id == Chicago);
        _consoleReads.Should().Be(1);
    }

    [Fact]
    public async Task A_read_that_ignores_its_token_is_still_bounded()
    {
        // The client's retry delays do not observe the token, so the bound cannot rely on it alone.
        var resolver = Build(null, (_, _) => new TaskCompletionSource<string?>().Task);

        (await resolver.ResolveAsync("branch").WaitAsync(TimeSpan.FromSeconds(15))).Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_zone_it_had()
    {
        var answer = Chicago;
        var zones = BuildSiteZones((_, _) => answer == null ? throw new HttpRequestException("console down") : Task.FromResult<string?>(answer));
        (await zones.GetAsync("branch"))!.Id.Should().Be(Chicago);

        // Force the entry stale, then fail the refresh: the site keeps its zone rather than the server's.
        answer = null;
        var stale = typeof(SiteTimeZones).GetField("_zones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(zones) as System.Collections.Concurrent.ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime CachedAt, bool Found)>;
        stale!["branch"] = (stale["branch"].Zone, DateTime.MinValue, true);

        (await zones.GetAsync("branch"))!.Id.Should().Be(Chicago);
        zones.Cached("branch")!.Id.Should().Be(Chicago);
    }

    [Fact]
    public async Task A_failed_first_read_is_retried_within_a_minute()
    {
        var calls = 0;
        var zones = BuildSiteZones((_, _) => ++calls == 1 ? throw new TaskCanceledException() : Task.FromResult<string?>(Chicago));
        (await zones.GetAsync("branch")).Should().BeNull();

        // Sixty-one seconds on, the failure has lapsed (a console that answered without a zone waits an hour).
        var entries = typeof(SiteTimeZones).GetField("_zones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(zones) as System.Collections.Concurrent.ConcurrentDictionary<string, (TimeZoneInfo? Zone, DateTime CachedAt, bool Found)>;
        entries!["branch"] = (null, entries["branch"].CachedAt - TimeSpan.FromSeconds(61), false);

        (await zones.GetAsync("branch"))!.Id.Should().Be(Chicago);
    }

    [Fact]
    public async Task Remembered_zones_serve_before_any_console_answers()
    {
        string? saved = null;
        var first = new SiteTimeZones(_ => _ => Task.FromResult<string?>(Tokyo), NullLogger<SiteTimeZones>.Instance,
            () => Task.FromResult<string?>(saved), json => { saved = json; return Task.CompletedTask; });
        await first.GetAsync("branch");
        saved.Should().Contain(Tokyo);

        // A restart with the console not connected yet: the remembered zone serves at once.
        var restarted = new SiteTimeZones(_ => null, NullLogger<SiteTimeZones>.Instance, () => Task.FromResult<string?>(saved), _ => Task.CompletedTask);
        await restarted.LoadRememberedAsync();

        restarted.Cached("branch")!.Id.Should().Be(Tokyo);
    }

    [Fact]
    public void Api_key_and_legacy_connections_are_never_asked()
    {
        using var apiKey = new UniFi.UniFiApiClient(NullLogger<UniFi.UniFiApiClient>.Instance, "192.0.2.1", "", "", apiKey: "key");
        using var local = new UniFi.UniFiApiClient(NullLogger<UniFi.UniFiApiClient>.Instance, "192.0.2.1", "admin", "secret");

        SiteTimeZones.ConsoleCanGiveZone(apiKey).Should().BeFalse();
        SiteTimeZones.ConsoleCanGiveZone(local).Should().BeTrue();
    }

    [Fact]
    public async Task A_console_that_throws_falls_back_and_is_not_asked_again_at_once()
    {
        var resolver = Build(null, (_, _) => throw new HttpRequestException("console down"));

        (await resolver.ResolveAsync("branch")).Should().Be(TimeZoneInfo.Local);
        await resolver.ResolveAsync("branch");

        _consoleReads.Should().Be(1);
    }
}
