using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class AuditRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly AuditRepository _repository;

    public AuditRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        var logger = new Mock<ILogger<AuditRepository>>();
        _repository = new AuditRepository(_context, logger.Object);
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    #region AuditResult Tests

    [Fact]
    public async Task SaveAuditResultAsync_SavesAndReturnsId()
    {
        var audit = new AuditResult
        {
            SiteId = 1,
            DeviceId = "device-1",
            DeviceName = "Test Switch",
            AuditDate = DateTime.UtcNow,
            TotalChecks = 10,
            PassedChecks = 8,
            FailedChecks = 2,
            ComplianceScore = 80.0
        };

        var id = await _repository.SaveAuditResultAsync(1, audit);

        id.Should().BeGreaterThan(0);
        var saved = await _context.AuditResults.FindAsync(id);
        saved.Should().NotBeNull();
        saved!.DeviceId.Should().Be("device-1");
    }

    [Fact]
    public async Task SaveAuditResultAsync_SetsCreatedAt()
    {
        var beforeSave = DateTime.UtcNow;
        var audit = new AuditResult
        {
            SiteId = 1,
            DeviceId = "device-1",
            DeviceName = "Test Switch"
        };

        var id = await _repository.SaveAuditResultAsync(1, audit);

        var saved = await _context.AuditResults.FindAsync(id);
        saved!.CreatedAt.Should().BeOnOrAfter(beforeSave);
    }

    [Fact]
    public async Task GetAuditResultAsync_ReturnsCorrectResult()
    {
        var audit = new AuditResult
        {
            SiteId = 1,
            DeviceId = "device-1",
            DeviceName = "Test Switch",
            ComplianceScore = 95.0
        };
        _context.AuditResults.Add(audit);
        await _context.SaveChangesAsync();

        var result = await _repository.GetAuditResultAsync(1, audit.Id);

        result.Should().NotBeNull();
        result!.DeviceId.Should().Be("device-1");
        result.ComplianceScore.Should().Be(95.0);
    }

    [Fact]
    public async Task GetAuditResultAsync_ReturnsNullForNonExistent()
    {
        var result = await _repository.GetAuditResultAsync(1, 999);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAuditHistoryAsync_ReturnsAllResults()
    {
        _context.AuditResults.AddRange(
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-1) },
            new AuditResult { SiteId = 1, DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetAuditHistoryAsync(1);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAuditHistoryAsync_FiltersByDevice()
    {
        _context.AuditResults.AddRange(
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow },
            new AuditResult { SiteId = 1, DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow },
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-1) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetAuditHistoryAsync(1, deviceId: "device-1");

        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.DeviceId.Should().Be("device-1"));
    }

    [Fact]
    public async Task GetAuditHistoryAsync_OrdersByDateDescending()
    {
        _context.AuditResults.AddRange(
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-2) },
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow },
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-1) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetAuditHistoryAsync(1);

        results.Should().BeInDescendingOrder(r => r.AuditDate);
    }

    [Fact]
    public async Task GetAuditHistoryAsync_RespectsLimit()
    {
        for (int i = 0; i < 10; i++)
        {
            _context.AuditResults.Add(new AuditResult
            {
                SiteId = 1,
                DeviceId = $"device-{i}",
                DeviceName = $"Switch {i}",
                AuditDate = DateTime.UtcNow.AddDays(-i)
            });
        }
        await _context.SaveChangesAsync();

        var results = await _repository.GetAuditHistoryAsync(1, limit: 5);

        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task DeleteOldAuditsAsync_DeletesOldRecords()
    {
        _context.AuditResults.AddRange(
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-30) },
            new AuditResult { SiteId = 1, DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow.AddDays(-10) },
            new AuditResult { SiteId = 1, DeviceId = "device-3", DeviceName = "Switch 3", AuditDate = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        await _repository.DeleteOldAuditsAsync(1, DateTime.UtcNow.AddDays(-15));

        var remaining = await _context.AuditResults.ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().NotContain(a => a.DeviceId == "device-1");
    }

    [Fact]
    public async Task GetLatestAuditResultAsync_ReturnsNewestResult()
    {
        _context.AuditResults.AddRange(
            new AuditResult { SiteId = 1, DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-2) },
            new AuditResult { SiteId = 1, DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow },
            new AuditResult { SiteId = 1, DeviceId = "device-3", DeviceName = "Switch 3", AuditDate = DateTime.UtcNow.AddDays(-1) }
        );
        await _context.SaveChangesAsync();

        var result = await _repository.GetLatestAuditResultAsync(1);

        result.Should().NotBeNull();
        result!.DeviceId.Should().Be("device-2");
    }

    [Fact]
    public async Task GetLatestAuditResultAsync_ReturnsNullWhenEmpty()
    {
        var result = await _repository.GetLatestAuditResultAsync(1);
        result.Should().BeNull();
    }

    #endregion

    #region DismissedIssue Tests

    [Fact]
    public async Task GetDismissedIssuesAsync_ReturnsAllDismissed()
    {
        _context.DismissedIssues.AddRange(
            new DismissedIssue { SiteId = 1, IssueKey = "issue-1", DismissedAt = DateTime.UtcNow },
            new DismissedIssue { SiteId = 1, IssueKey = "issue-2", DismissedAt = DateTime.UtcNow.AddMinutes(-5) }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetDismissedIssuesAsync(1);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetDismissedIssuesAsync_OrdersByDismissedAtDescending()
    {
        _context.DismissedIssues.AddRange(
            new DismissedIssue { SiteId = 1, IssueKey = "old-issue", DismissedAt = DateTime.UtcNow.AddDays(-1) },
            new DismissedIssue { SiteId = 1, IssueKey = "new-issue", DismissedAt = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        var results = await _repository.GetDismissedIssuesAsync(1);

        results[0].IssueKey.Should().Be("new-issue");
    }

    [Fact]
    public async Task SaveDismissedIssueAsync_AddsIssue()
    {
        var issue = new DismissedIssue { SiteId = 1, IssueKey = "test-issue" };

        await _repository.SaveDismissedIssueAsync(1, issue);

        var saved = await _context.DismissedIssues.FirstOrDefaultAsync(d => d.IssueKey == "test-issue");
        saved.Should().NotBeNull();
        saved!.DismissedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeleteDismissedIssueAsync_RemovesIssue()
    {
        _context.DismissedIssues.Add(new DismissedIssue { SiteId = 1, IssueKey = "to-delete", DismissedAt = DateTime.UtcNow });
        await _context.SaveChangesAsync();

        await _repository.DeleteDismissedIssueAsync(1, "to-delete");

        var deleted = await _context.DismissedIssues.FirstOrDefaultAsync(d => d.IssueKey == "to-delete");
        deleted.Should().BeNull();
    }

    [Fact]
    public async Task ClearAllDismissedIssuesAsync_RemovesAllIssues()
    {
        _context.DismissedIssues.AddRange(
            new DismissedIssue { SiteId = 1, IssueKey = "issue-1", DismissedAt = DateTime.UtcNow },
            new DismissedIssue { SiteId = 1, IssueKey = "issue-2", DismissedAt = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        await _repository.ClearAllDismissedIssuesAsync(1);

        var remaining = await _context.DismissedIssues.CountAsync();
        remaining.Should().Be(0);
    }

    #endregion
}
