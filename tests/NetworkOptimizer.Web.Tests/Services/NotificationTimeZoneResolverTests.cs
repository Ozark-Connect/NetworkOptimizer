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
        string? installDefault, Func<string, CancellationToken, Task<string?>>? readConsoleZone)
    {
        var source = new Mock<ITimeZonePreferenceSource>();
        source.Setup(s => s.GetInheritedAsync()).ReturnsAsync(installDefault);
        var services = new ServiceCollection().AddScoped(_ => source.Object).BuildServiceProvider();
        return new NotificationTimeZoneResolver(
            services.GetRequiredService<IServiceScopeFactory>(),
            slug => readConsoleZone == null ? null : ct =>
            {
                _consoleReads++;
                return readConsoleZone(slug, ct);
            },
            NullLogger<NotificationTimeZoneResolver>.Instance);
    }

    private NotificationTimeZoneResolver Build(string? installDefault, Func<string, string?> consoleZone) =>
        Build(installDefault, (slug, _) => Task.FromResult(consoleZone(slug)));

    [Fact]
    public async Task The_install_default_wins_without_asking_the_console()
    {
        var resolver = Build(Tokyo, _ => Chicago);

        (await resolver.ResolveAsync("branch")).Id.Should().Be(Tokyo);
        _consoleReads.Should().Be(0);
    }

    [Fact]
    public async Task No_default_reads_the_site_console_zone()
    {
        var resolver = Build(null, slug => slug == "branch" ? Tokyo : Chicago);

        (await resolver.ResolveAsync("branch")).Id.Should().Be(Tokyo);
        (await resolver.ResolveAsync(null)).Id.Should().Be(Chicago);
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
    public async Task A_console_that_throws_falls_back_and_is_not_asked_again_at_once()
    {
        var resolver = Build(null, (_, _) => throw new HttpRequestException("console down"));

        (await resolver.ResolveAsync("branch")).Should().Be(TimeZoneInfo.Local);
        await resolver.ResolveAsync("branch");

        _consoleReads.Should().Be(1);
    }
}
