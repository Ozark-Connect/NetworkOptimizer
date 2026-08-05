using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class AlertRepositoryTests : IDisposable
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly NetworkOptimizerDbContext _context;
    private readonly AlertRepository _repository;

    public AlertRepositoryTests()
    {
        _context = CreateContext();
        _repository = new AlertRepository(_context, new Mock<ILogger<AlertRepository>>().Object);
    }

    private NetworkOptimizerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: _databaseName)
            .Options;
        return new NetworkOptimizerDbContext(options);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    private async Task<AlertHistoryEntry> SeedAlertAsync(
        string eventType,
        string? deviceId,
        AlertStatus status = AlertStatus.Active)
    {
        var alert = new AlertHistoryEntry
        {
            EventType = eventType,
            Severity = AlertSeverity.Critical,
            Status = status,
            Source = "monitoring",
            Title = "Test alert",
            DeviceId = deviceId,
            TriggeredAt = DateTime.UtcNow
        };

        _context.AlertHistory.Add(alert);
        await _context.SaveChangesAsync();
        return alert;
    }

    #region ResolveActiveAlertsAsync

    [Fact]
    public async Task ResolveActiveAlertsAsync_ResolvesOnlyMatchingEventTypeAndDevice()
    {
        var partialOnWan2 = await SeedAlertAsync("monitoring.wan_outage_partial", "wan2");
        var partialOnWan = await SeedAlertAsync("monitoring.wan_outage_partial", "wan");
        var outageOnWan2 = await SeedAlertAsync("monitoring.wan_outage", "wan2");
        var rollup = await SeedAlertAsync("monitoring.wan_outage", "all-wans");

        var resolved = await _repository.ResolveActiveAlertsAsync(["monitoring.wan_outage_partial"], "wan2");

        resolved.Should().ContainSingle();
        resolved[0].Id.Should().Be(partialOnWan2.Id);

        using var verify = CreateContext();
        var stored = await verify.AlertHistory.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Status);
        stored[partialOnWan2.Id].Should().Be(AlertStatus.Resolved);
        stored[partialOnWan.Id].Should().Be(AlertStatus.Active);
        stored[outageOnWan2.Id].Should().Be(AlertStatus.Active);
        stored[rollup.Id].Should().Be(AlertStatus.Active);
    }

    [Fact]
    public async Task ResolveActiveAlertsAsync_ResolvesEveryListedEventTypeOnTheDevice()
    {
        var outage = await SeedAlertAsync("monitoring.wan_outage", "wan2");
        var partial = await SeedAlertAsync("monitoring.wan_outage_partial", "wan2");

        var resolved = await _repository.ResolveActiveAlertsAsync(
            ["monitoring.wan_outage", "monitoring.wan_outage_partial"], "wan2");

        resolved.Select(a => a.Id).Should().BeEquivalentTo(new[] { outage.Id, partial.Id });
        resolved.Should().OnlyContain(a => a.Status == AlertStatus.Resolved);
    }

    [Fact]
    public async Task ResolveActiveAlertsAsync_LeavesAcknowledgedAndAlreadyResolvedEntriesAlone()
    {
        var acknowledged = await SeedAlertAsync("monitoring.wan_outage", "wan2", AlertStatus.Acknowledged);
        var alreadyResolved = await SeedAlertAsync("monitoring.wan_outage", "wan2", AlertStatus.Resolved);

        var resolved = await _repository.ResolveActiveAlertsAsync(["monitoring.wan_outage"], "wan2");

        resolved.Should().BeEmpty();

        using var verify = CreateContext();
        var stored = await verify.AlertHistory.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Status);
        stored[acknowledged.Id].Should().Be(AlertStatus.Acknowledged);
        stored[alreadyResolved.Id].Should().Be(AlertStatus.Resolved);
    }

    [Fact]
    public async Task ResolveActiveAlertsAsync_StampsResolvedAt()
    {
        var before = DateTime.UtcNow;
        await SeedAlertAsync("monitoring.wan_outage", "wan2");

        var resolved = await _repository.ResolveActiveAlertsAsync(["monitoring.wan_outage"], "wan2");

        resolved.Should().ContainSingle();
        resolved[0].ResolvedAt.Should().NotBeNull();
        resolved[0].ResolvedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public async Task ResolveActiveAlertsAsync_NoMatches_ReturnsEmpty()
    {
        await SeedAlertAsync("monitoring.wan_outage", "wan");

        var resolved = await _repository.ResolveActiveAlertsAsync(["monitoring.wan_outage"], "wan2");

        resolved.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveActiveAlertsAsync_NoEventTypesOrNoDevice_ReturnsEmpty()
    {
        var alert = await SeedAlertAsync("monitoring.wan_outage", "wan2");

        (await _repository.ResolveActiveAlertsAsync([], "wan2")).Should().BeEmpty();
        (await _repository.ResolveActiveAlertsAsync(["monitoring.wan_outage"], "")).Should().BeEmpty();

        using var verify = CreateContext();
        var stored = await verify.AlertHistory.AsNoTracking().FirstAsync(a => a.Id == alert.Id);
        stored.Status.Should().Be(AlertStatus.Active);
    }

    #endregion
}
