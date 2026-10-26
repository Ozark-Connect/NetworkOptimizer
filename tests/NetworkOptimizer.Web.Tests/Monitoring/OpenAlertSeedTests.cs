using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;
using static NetworkOptimizer.Web.Tests.Monitoring.OpenAlertTestScope;

namespace NetworkOptimizer.Web.Tests.Monitoring;

public class OpenAlertSeedTests
{
    private static readonly TimeSpan Ago = TimeSpan.FromMinutes(30);

    private static Task<OpenAlerts> Read(params AlertHistoryEntry[] stored) =>
        new OpenAlertSeed(With(stored), "default", ["device.offline"], a => OpenAlertSeed.MacSubject(a.DeviceId),
            NullLogger.Instance).OpenAsync();

    [Fact]
    public async Task OpeningWithoutRecovery_IsOpen()
    {
        var open = await Read(Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago));

        Assert.NotNull(open.Get("device.offline", "aabbccddee01"));
    }

    [Fact]
    public async Task RecoveryAfterTheOpening_ClosesIt()
    {
        var open = await Read(
            Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago),
            Alert("device.recovered", "AA-BB-CC-DD-EE-01", Ago - TimeSpan.FromMinutes(1)));

        Assert.Empty(open.All);
    }

    [Fact]
    public async Task RecoveryBeforeTheOpening_DoesNotCloseIt()
    {
        var open = await Read(
            Alert("device.recovered", "aa:bb:cc:dd:ee:01", Ago + TimeSpan.FromMinutes(5)),
            Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago));

        Assert.Single(open.All);
    }

    [Fact]
    public async Task ResolvedRecovery_StillClosesItsOpening()
    {
        var open = await Read(
            Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago),
            Alert("device.recovered", "aa:bb:cc:dd:ee:01", Ago - TimeSpan.FromMinutes(1)).Resolved());

        Assert.Empty(open.All);
    }

    [Fact]
    public async Task RecoveryForAnotherSubject_DoesNotCloseIt()
    {
        var open = await Read(
            Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago),
            Alert("device.recovered", "aa:bb:cc:dd:ee:02", Ago - TimeSpan.FromMinutes(1)));

        Assert.Single(open.All);
    }

    [Fact]
    public async Task ResolvedOpening_IsNotOpen()
    {
        var open = await Read(Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago).Resolved());

        Assert.Empty(open.All);
    }

    [Fact]
    public async Task OpeningPastTheLookback_IsStale()
    {
        var open = await Read(Alert("device.offline", "aa:bb:cc:dd:ee:01", OpenAlertSeed.Lookback + TimeSpan.FromHours(1)));

        Assert.Empty(open.All);
    }

    [Fact]
    public async Task Take_HandsEachOpenAlertOutOnce()
    {
        var open = await Read(Alert("device.offline", "aa:bb:cc:dd:ee:01", Ago));

        Assert.NotNull(open.Take("device.offline", "aabbccddee01"));
        Assert.Null(open.Take("device.offline", "aabbccddee01"));
    }

    [Fact]
    public async Task ReadFailure_SeedsNothing()
    {
        var repo = new Mock<IAlertRepository>();
        repo.Setup(r => r.GetUnresolvedAlertsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db"));
        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<IAlertSiteScope>());
        services.AddScoped(_ => repo.Object);
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var open = await new OpenAlertSeed(scopes, "default", ["device.offline"], a => a.DeviceId, NullLogger.Instance).OpenAsync();

        Assert.Empty(open.All);
    }

    [Fact]
    public async Task NoScopeFactory_SeedsNothing()
    {
        var open = await new OpenAlertSeed(null, "default", ["device.offline"], a => a.DeviceId, NullLogger.Instance).OpenAsync();

        Assert.Empty(open.All);
    }
}
