using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class SqliteRepositoryTests : IDisposable
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly SqliteRepository _repository;
    private readonly Mock<ILogger<SqliteRepository>> _loggerMock;

    public SqliteRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new NetworkOptimizerDbContext(options);
        _loggerMock = new Mock<ILogger<SqliteRepository>>();
        _repository = new SqliteRepository(_context, _loggerMock.Object);
    }

    public void Dispose()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region AuditResult Tests

    [Fact]
    public async Task SaveAuditResultAsync_SavesAndReturnsId()
    {
        // Arrange
        var audit = new AuditResult
        {
            DeviceId = "device-1",
            DeviceName = "Test Switch",
            AuditDate = DateTime.UtcNow,
            TotalChecks = 10,
            PassedChecks = 8,
            FailedChecks = 2,
            ComplianceScore = 80.0
        };

        // Act
        var id = await _repository.SaveAuditResultAsync(audit);

        // Assert
        id.Should().BeGreaterThan(0);
        var saved = await _context.AuditResults.FindAsync(id);
        saved.Should().NotBeNull();
        saved!.DeviceId.Should().Be("device-1");
    }

    [Fact]
    public async Task SaveAuditResultAsync_SetsCreatedAt()
    {
        // Arrange
        var beforeSave = DateTime.UtcNow;
        var audit = new AuditResult
        {
            DeviceId = "device-1",
            DeviceName = "Test Switch"
        };

        // Act
        var id = await _repository.SaveAuditResultAsync(audit);

        // Assert
        var saved = await _context.AuditResults.FindAsync(id);
        saved!.CreatedAt.Should().BeOnOrAfter(beforeSave);
    }

    [Fact]
    public async Task GetAuditResultAsync_ReturnsCorrectResult()
    {
        // Arrange
        var audit = new AuditResult
        {
            DeviceId = "device-1",
            DeviceName = "Test Switch",
            ComplianceScore = 95.0
        };
        _context.AuditResults.Add(audit);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetAuditResultAsync(audit.Id);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceId.Should().Be("device-1");
        result.ComplianceScore.Should().Be(95.0);
    }

    [Fact]
    public async Task GetAuditResultAsync_ReturnsNullForNonExistent()
    {
        // Act
        var result = await _repository.GetAuditResultAsync(999);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAuditHistoryAsync_ReturnsAllResults()
    {
        // Arrange
        _context.AuditResults.AddRange(
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-1) },
            new AuditResult { DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAuditHistoryAsync();

        // Assert
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAuditHistoryAsync_FiltersByDevice()
    {
        // Arrange
        _context.AuditResults.AddRange(
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow },
            new AuditResult { DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow },
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-1) }
        );
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAuditHistoryAsync(deviceId: "device-1");

        // Assert
        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.DeviceId.Should().Be("device-1"));
    }

    [Fact]
    public async Task GetAuditHistoryAsync_OrdersByDateDescending()
    {
        // Arrange
        _context.AuditResults.AddRange(
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-2) },
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow },
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-1) }
        );
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAuditHistoryAsync();

        // Assert
        results.Should().BeInDescendingOrder(r => r.AuditDate);
    }

    [Fact]
    public async Task GetAuditHistoryAsync_RespectsLimit()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            _context.AuditResults.Add(new AuditResult
            {
                DeviceId = $"device-{i}",
                DeviceName = $"Switch {i}",
                AuditDate = DateTime.UtcNow.AddDays(-i)
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAuditHistoryAsync(limit: 5);

        // Assert
        results.Should().HaveCount(5);
    }

    [Fact]
    public async Task DeleteOldAuditsAsync_DeletesOldRecords()
    {
        // Arrange
        _context.AuditResults.AddRange(
            new AuditResult { DeviceId = "device-1", DeviceName = "Switch 1", AuditDate = DateTime.UtcNow.AddDays(-30) },
            new AuditResult { DeviceId = "device-2", DeviceName = "Switch 2", AuditDate = DateTime.UtcNow.AddDays(-10) },
            new AuditResult { DeviceId = "device-3", DeviceName = "Switch 3", AuditDate = DateTime.UtcNow }
        );
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteOldAuditsAsync(DateTime.UtcNow.AddDays(-15));

        // Assert
        var remaining = await _context.AuditResults.ToListAsync();
        remaining.Should().HaveCount(2);
        remaining.Should().NotContain(a => a.DeviceId == "device-1");
    }

    #endregion

    #region SqmBaseline Tests

    [Fact]
    public async Task SaveSqmBaselineAsync_SavesAndReturnsId()
    {
        // Arrange
        var baseline = CreateSqmBaseline("gateway-1", "eth0");

        // Act
        var id = await _repository.SaveSqmBaselineAsync(baseline);

        // Assert
        id.Should().BeGreaterThan(0);
        var saved = await _context.SqmBaselines.FindAsync(id);
        saved.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSqmBaselineAsync_ReturnsCorrectBaseline()
    {
        // Arrange
        var baseline = CreateSqmBaseline("gateway-1", "eth0", downloadMbps: 100.0, uploadMbps: 20.0);
        _context.SqmBaselines.Add(baseline);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetSqmBaselineAsync("gateway-1", "eth0");

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedDownloadMbps.Should().Be(100.0);
        result.RecommendedUploadMbps.Should().Be(20.0);
    }

    [Fact]
    public async Task GetSqmBaselineAsync_ReturnsNullWhenNotFound()
    {
        // Act
        var result = await _repository.GetSqmBaselineAsync("nonexistent", "eth0");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllSqmBaselinesAsync_ReturnsAll()
    {
        // Arrange
        _context.SqmBaselines.AddRange(
            CreateSqmBaseline("device-1", "eth0"),
            CreateSqmBaseline("device-2", "eth1")
        );
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAllSqmBaselinesAsync();

        // Assert
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAllSqmBaselinesAsync_FiltersByDevice()
    {
        // Arrange
        _context.SqmBaselines.AddRange(
            CreateSqmBaseline("device-1", "eth0"),
            CreateSqmBaseline("device-2", "eth1")
        );
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAllSqmBaselinesAsync(deviceId: "device-1");

        // Assert
        results.Should().HaveCount(1);
        results[0].DeviceId.Should().Be("device-1");
    }

    [Fact]
    public async Task DeleteSqmBaselineAsync_RemovesBaseline()
    {
        // Arrange
        var baseline = CreateSqmBaseline("device-1", "eth0");
        _context.SqmBaselines.Add(baseline);
        await _context.SaveChangesAsync();
        var id = baseline.Id;

        // Act
        await _repository.DeleteSqmBaselineAsync(id);

        // Assert
        var deleted = await _context.SqmBaselines.FindAsync(id);
        deleted.Should().BeNull();
    }

    private static SqmBaseline CreateSqmBaseline(
        string deviceId,
        string interfaceId,
        double downloadMbps = 100.0,
        double uploadMbps = 20.0)
    {
        return new SqmBaseline
        {
            DeviceId = deviceId,
            InterfaceId = interfaceId,
            InterfaceName = "WAN",
            BaselineStart = DateTime.UtcNow.AddDays(-7),
            BaselineEnd = DateTime.UtcNow,
            RecommendedDownloadMbps = downloadMbps,
            RecommendedUploadMbps = uploadMbps
        };
    }

    #endregion

    #region AgentConfiguration Tests

    [Fact]
    public async Task SaveAgentConfigAsync_SavesConfig()
    {
        // Arrange
        var config = new AgentConfiguration
        {
            AgentId = "agent-1",
            AgentName = "Test Agent",
            IsEnabled = true
        };

        // Act
        await _repository.SaveAgentConfigAsync(config);

        // Assert
        var saved = await _context.AgentConfigurations.FindAsync("agent-1");
        saved.Should().NotBeNull();
        saved!.AgentName.Should().Be("Test Agent");
    }

    [Fact]
    public async Task GetAgentConfigAsync_ReturnsCorrectConfig()
    {
        // Arrange
        var config = new AgentConfiguration
        {
            AgentId = "agent-1",
            AgentName = "Test Agent",
            IsEnabled = true
        };
        _context.AgentConfigurations.Add(config);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetAgentConfigAsync("agent-1");

        // Assert
        result.Should().NotBeNull();
        result!.AgentName.Should().Be("Test Agent");
    }

    [Fact]
    public async Task GetAllAgentConfigsAsync_ReturnsOrderedByName()
    {
        // Arrange
        _context.AgentConfigurations.AddRange(
            new AgentConfiguration { AgentId = "agent-1", AgentName = "Zebra", IsEnabled = true },
            new AgentConfiguration { AgentId = "agent-2", AgentName = "Alpha", IsEnabled = true },
            new AgentConfiguration { AgentId = "agent-3", AgentName = "Beta", IsEnabled = false }
        );
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAllAgentConfigsAsync();

        // Assert
        results.Should().HaveCount(3);
        results[0].AgentName.Should().Be("Alpha");
        results[1].AgentName.Should().Be("Beta");
        results[2].AgentName.Should().Be("Zebra");
    }

    [Fact]
    public async Task DeleteAgentConfigAsync_RemovesConfig()
    {
        // Arrange
        var config = new AgentConfiguration
        {
            AgentId = "agent-1",
            AgentName = "Test Agent",
            IsEnabled = true
        };
        _context.AgentConfigurations.Add(config);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAgentConfigAsync("agent-1");

        // Assert
        var deleted = await _context.AgentConfigurations.FindAsync("agent-1");
        deleted.Should().BeNull();
    }

    #endregion

    #region License Tests

    [Fact]
    public async Task SaveLicenseAsync_SavesAndReturnsId()
    {
        // Arrange
        var license = new LicenseInfo
        {
            LicenseKey = "LICENSE-KEY-123",
            LicensedTo = "Test Company",
            IsActive = true,
            ExpirationDate = DateTime.UtcNow.AddYears(1)
        };

        // Act
        var id = await _repository.SaveLicenseAsync(license);

        // Assert
        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetLicenseAsync_ReturnsActiveLicense()
    {
        // Arrange
        _context.Licenses.AddRange(
            new LicenseInfo { LicenseKey = "OLD-KEY", LicensedTo = "Old", IsActive = false },
            new LicenseInfo { LicenseKey = "NEW-KEY", LicensedTo = "Current", IsActive = true }
        );
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetLicenseAsync();

        // Assert
        result.Should().NotBeNull();
        result!.LicenseKey.Should().Be("NEW-KEY");
        result.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetLicenseAsync_ReturnsNullWhenNoActiveLicense()
    {
        // Arrange
        _context.Licenses.Add(new LicenseInfo
        {
            LicenseKey = "INACTIVE-KEY",
            LicensedTo = "Test",
            IsActive = false
        });
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetLicenseAsync();

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsOnNullContext()
    {
        // Act
        var act = () => new SqliteRepository(null!, _loggerMock.Object);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Act
        var act = () => new SqliteRepository(_context, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("logger");
    }

    #endregion
}
